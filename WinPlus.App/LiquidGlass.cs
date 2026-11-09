using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinPlus
{
    /// <summary>
    /// Apple's "Liquid Glass" for one rounded element of a window (the download bar):
    /// what's behind it on the screen, bent like a lens at the rounded edges, with
    /// the colors splitting a little there, a light frost, a dark tint so the text
    /// stays readable, and a shine on the rim.
    ///
    /// A picture of what's behind is taken when it starts (and again on Refresh): for
    /// that moment only, the window is left out of screen copies (WDA_EXCLUDEFROMCAPTURE),
    /// then it's back. So screen sharing (Discord, Teams, OBS…) and screenshots show the
    /// window with its glass; kept out all the time, they wouldn't show it at all.
    /// The bend for every pixel is worked out once; a picture is a copy, a blur and a
    /// lookup, done on its own thread.
    /// </summary>
    internal sealed class LiquidGlass : IDisposable
    {
        // Looks (in device-independent pixels where it's a size).
        private const double Bezel = 18;        // how far in from the edge the glass bends
        // The rest comes from the settings (Video download page).
        private readonly double Bend, Dispersion, TintAlpha, RimShine;   // from the settings
        private readonly int Frost;
        // The tint's color: dark gray, or the color asked for (e.g. the widgets' background color).
        private readonly byte TintR = 0x1C, TintG = 0x1C, TintB = 0x1E;
        private readonly double TintLight = 0.11;   // how light the tint color is (0–1)

        private readonly Window _window;
        private readonly FrameworkElement _host;     // not transformed (the element's parent)
        private readonly FrameworkElement _target;   // the element that gets the glass
        private readonly Thickness _offset;           // where the element sits in the host
        private readonly double _radius;

        private int _w, _h, _margin, _cw, _ch;        // output size, capture margin, capture size (pixels)
        private int[] _src = Array.Empty<int>(), _tmp = Array.Empty<int>(), _out = Array.Empty<int>();
        private int[] _mapR = Array.Empty<int>(), _mapG = Array.Empty<int>(), _mapB = Array.Empty<int>();
        private byte[] _rim = Array.Empty<byte>();
        private WriteableBitmap? _bitmap;
        private ImageBrush? _brush;
        private int[] _front = Array.Empty<int>();    // the finished frame, handed to the window
        private int _pending;                          // a frame is waiting for the window
        private IntPtr _screenDc, _memDc, _dib, _bits, _oldObj;
        private volatile bool _running;
        private Thread? _thread;
        private readonly IntPtr _hwnd;
        private readonly AutoResetEvent _wake = new(false);
        private int _wanted;                           // a new picture of what's behind is wanted
        private int _x, _y;                            // where the copy is taken (screen pixels)
        private readonly bool _desktop;                // copy the desktop itself, not the screen (widgets)
        private volatile int _ox, _oy;                 // desktop mode: the element's top left (screen pixels)
        private int _recapture = 1;                    // desktop mode: a new picture of the desktop is wanted
        private IntPtr _deskDc, _deskBmp, _deskOld;    // desktop mode: the last picture of the whole desktop
        private RECT _deskRect;

        /// <summary>
        /// What's behind turned light (true) or dark (false): the text on the glass
        /// should switch to dark / light to stay readable.
        /// </summary>
        public event Action<bool>? LightBehindChanged;
        private volatile bool _lightBehind;
        private bool _lightReported, _reportedOnce;

        /// <summary>Can the window be left out of a screen copy (Windows 10 2004 and later)? Without it there's no glass.</summary>
        public bool Available { get; }

        /// <param name="tint">A color for the tint instead of the dark gray, and how strong (0–1) it is.</param>
        public LiquidGlass(Window window, FrameworkElement host, FrameworkElement target, Thickness offset, double radius, GlassSettings settings,
                           bool desktop = false, (Color Color, double Alpha)? tint = null)
        {
            _desktop = desktop;
            Bend = Math.Clamp(settings.Bend, 0, 100) / 100.0;
            Dispersion = Math.Clamp(settings.Dispersion, 0, 100) / 100.0;
            Frost = Math.Clamp(settings.Frost, 0, 12);
            TintAlpha = Math.Clamp(settings.Tint, 0, 80) / 100.0;
            if (tint is { } t)
            {
                TintR = t.Color.R; TintG = t.Color.G; TintB = t.Color.B;
                TintAlpha = Math.Clamp(t.Alpha, 0, 1);
                TintLight = (t.Color.R * 3 + t.Color.G * 6 + t.Color.B) / 10.0 / 255;
            }
            RimShine = Math.Clamp(settings.Rim, 0, 100) / 100.0;
            _window = window;
            _host = host;
            _target = target;
            _offset = offset;
            _radius = radius;
            _hwnd = new WindowInteropHelper(window).EnsureHandle();
            // Shown normally in screen copies (older versions kept it out all the time).
            Available = Environment.OSVersion.Version.Build >= 19041 && SetWindowDisplayAffinity(_hwnd, 0 /* WDA_NONE */);
        }

        /// <summary>
        /// Starts the glass (call once the window is in its place). The screen copying
        /// and drawing run on their own thread, so the window never waits for them;
        /// the window only gets the finished picture.
        /// </summary>
        public void Start()
        {
            if (!Available || _running) return;
            if (!_window.IsVisible || _target.ActualWidth < 4 || PresentationSource.FromVisual(_host) == null) return;
            var dpi = VisualTreeHelper.GetDpi(_window);
            int w = (int)Math.Round(_target.ActualWidth * dpi.DpiScaleX);
            int h = (int)Math.Round(_target.ActualHeight * dpi.DpiScaleY);
            // Where the element is on the screen, in pixels (from its untransformed parent,
            // so the open/close animation doesn't move what's copied).
            Point origin = _host.PointToScreen(new Point(_offset.Left, _offset.Top));

            _bitmap = new WriteableBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Bgr32, null);
            _front = new int[w * h];
            _running = true;
            _wanted = 1;
            int ox = (int)Math.Round(origin.X), oy = (int)Math.Round(origin.Y);
            if (_desktop)
            {
                // In real pixels, like the desktop picture.
                RECT r;
                using (new ScreenCapture.DpiScope()) GetWindowRect(_hwnd, out r);
                ox = r.Left + (int)Math.Round(_offset.Left * dpi.DpiScaleX);
                oy = r.Top + (int)Math.Round(_offset.Top * dpi.DpiScaleY);
                _ox = ox; _oy = oy;
            }
            double scale = dpi.DpiScaleX;
            _thread = new Thread(() => Loop(ox, oy, w, h, scale))
            {
                IsBackground = true, Name = "Wibn glass", Priority = ThreadPriority.BelowNormal,
            };
            _thread.Start();
        }

        /// <summary>Take a new picture of what's behind (e.g. the wallpaper changed).</summary>
        public void Refresh()
        {
            Interlocked.Exchange(ref _recapture, 1);
            Interlocked.Exchange(ref _wanted, 1);
            _wake.Set();
        }

        /// <summary>
        /// Desktop mode: the window moved (same size). The glass follows it from the
        /// picture of the desktop it already has, so it's quick enough while dragging.
        /// </summary>
        public void Follow()
        {
            if (!_desktop || !_running) return;
            RECT r;
            using (new ScreenCapture.DpiScope()) if (!GetWindowRect(_hwnd, out r)) return;
            var dpi = VisualTreeHelper.GetDpi(_window);
            _ox = r.Left + (int)Math.Round(_offset.Left * dpi.DpiScaleX);
            _oy = r.Top + (int)Math.Round(_offset.Top * dpi.DpiScaleY);
            Interlocked.Exchange(ref _wanted, 1);
            _wake.Set();
        }

        public void Dispose()   // the thread stops and cleans up by itself
        {
            _running = false;
            _wake.Set();
        }

        private void Loop(int ox, int oy, int w, int h, double scale)
        {
            try
            {
                Prepare(w, h, scale);
                _x = ox - _margin; _y = oy - _margin;
                int frost = (int)Math.Round(Frost * scale);
                while (_running)
                {
                    if (Interlocked.Exchange(ref _wanted, 0) == 1)
                    {
                        // The copy itself on the window's thread (it hides the window from it for a moment).
                        bool copied = false;
                        if (_desktop) copied = CopyDesktop();
                        else
                        {
                            var op = _window.Dispatcher.InvokeAsync(() => copied = _running && CopyBehind());
                            op.Wait(TimeSpan.FromSeconds(2));
                        }
                        if (copied && _running)
                        {
                            DrawFrame(frost);
                            lock (_front) Array.Copy(_out, _front, _out.Length);
                            // One waiting at a time; it shows the latest picture anyway.
                            if (Interlocked.Exchange(ref _pending, 1) == 0)
                                _window.Dispatcher.BeginInvoke(new Action(Present));
                        }
                    }
                    _wake.WaitOne();
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write("liquid glass", ex);   // the plain dark background stays
            }
            finally
            {
                _running = false;
                FreeGdi();
            }
        }

        /// <summary>On the window's thread: show the latest picture.</summary>
        private void Present()
        {
            try
            {
                if (_bitmap == null) return;
                if (_brush == null)
                {
                    _brush = new ImageBrush(_bitmap) { Stretch = Stretch.Fill };
                    if (_target is System.Windows.Controls.Border border) border.Background = _brush;
                    else if (_target is System.Windows.Shapes.Shape shape) shape.Fill = _brush;   // e.g. a round menu's disc
                }
                lock (_front) _bitmap.WritePixels(new Int32Rect(0, 0, _bitmap.PixelWidth, _bitmap.PixelHeight), _front, _bitmap.PixelWidth * 4, 0);
                if (!_reportedOnce || _lightReported != _lightBehind)
                {
                    _reportedOnce = true;
                    _lightReported = _lightBehind;
                    LightBehindChanged?.Invoke(_lightBehind);
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write("liquid glass", ex);
                _running = false;
            }
            finally
            {
                Interlocked.Exchange(ref _pending, 0);
            }
        }

        /// <summary>
        /// On the window's thread: copies what's behind into _src. The window is left out
        /// of screen copies only until the next screen refresh is done (DwmFlush), then
        /// it's shown in them again, so a screen share only misses it for a frame or two.
        /// </summary>
        private bool CopyBehind()
        {
            if (!SetWindowDisplayAffinity(_hwnd, 0x11 /* WDA_EXCLUDEFROMCAPTURE */)) return false;
            try
            {
                DwmFlush();
                if (!BitBlt(_memDc, 0, 0, _cw, _ch, _screenDc, _x, _y, 0x00CC0020 /* SRCCOPY */)) return false;
            }
            finally { SetWindowDisplayAffinity(_hwnd, 0 /* WDA_NONE */); }
            Marshal.Copy(_bits, _src, 0, _src.Length);
            return true;
        }

        /// <summary>
        /// On the glass thread: the desktop (wallpaper) behind the element, drawn by the
        /// desktop window itself, so it works even with windows over the element.
        /// </summary>
        private bool CopyDesktop()
        {
            using var _ = new ScreenCapture.DpiScope();
            if (_deskDc == IntPtr.Zero || Interlocked.Exchange(ref _recapture, 0) == 1)
            {
                FreeDesktop();
                IntPtr desk = FindWindow("Progman", null);
                if (desk == IntPtr.Zero || !GetWindowRect(desk, out var r)) return false;
                int dw = r.Right - r.Left, dh = r.Bottom - r.Top;
                if (dw <= 0 || dh <= 0) return false;
                _deskDc = CreateCompatibleDC(_screenDc);
                _deskBmp = CreateCompatibleBitmap(_screenDc, dw, dh);
                if (_deskDc == IntPtr.Zero || _deskBmp == IntPtr.Zero) { FreeDesktop(); return false; }
                _deskOld = SelectObject(_deskDc, _deskBmp);
                _deskRect = r;
                if (!PrintWindow(desk, _deskDc, 2 /* PW_RENDERFULLCONTENT */)) { FreeDesktop(); return false; }
            }
            int x = _ox - _margin, y = _oy - _margin;
            // Outside the desktop (the edge of the screen): black, the bend barely reaches there.
            PatBlt(_memDc, 0, 0, _cw, _ch, 0x00000042 /* BLACKNESS */);
            if (!BitBlt(_memDc, 0, 0, _cw, _ch, _deskDc, x - _deskRect.Left, y - _deskRect.Top, 0x00CC0020 /* SRCCOPY */)) return false;
            Marshal.Copy(_bits, _src, 0, _src.Length);
            return true;
        }

        private void FreeDesktop()
        {
            if (_deskDc != IntPtr.Zero)
            {
                if (_deskOld != IntPtr.Zero) SelectObject(_deskDc, _deskOld);
                DeleteDC(_deskDc);
            }
            if (_deskBmp != IntPtr.Zero) DeleteObject(_deskBmp);
            _deskDc = _deskBmp = _deskOld = IntPtr.Zero;
        }

        /// <summary>The glass picture into _out (on the glass thread), from _src.</summary>
        private void DrawFrame(int frost)
        {
            // Frost.
            if (frost > 0) BoxBlur(_src, _tmp, _cw, _ch, frost);

            // How light is it behind (after the tint)? Every 7th pixel is plenty.
            long sum = 0; int count = 0;
            for (int i = 0; i < _src.Length; i += 7)
            {
                int c = _src[i];
                sum += ((c >> 16) & 0xFF) * 3 + ((c >> 8) & 0xFF) * 6 + (c & 0xFF);
                count++;
            }
            double light = count > 0 ? (sum / (double)count / 10 / 255) * (1 - TintAlpha) + TintAlpha * TintLight : 0;
            _lightBehind = _lightBehind ? light > 0.50 : light > 0.60;   // a gap so it doesn't flicker

            // 3. Bend, split colors, tint, rim.
            int tintR = (int)(TintR * TintAlpha), tintG = (int)(TintG * TintAlpha), tintB = (int)(TintB * TintAlpha);
            int keep = (int)((1 - TintAlpha) * 256);
            for (int i = 0; i < _out.Length; i++)
            {
                int r = (_src[_mapR[i]] >> 16) & 0xFF;
                int g = (_src[_mapG[i]] >> 8) & 0xFF;
                int b = _src[_mapB[i]] & 0xFF;
                r = ((r * keep) >> 8) + tintR;
                g = ((g * keep) >> 8) + tintG;
                b = ((b * keep) >> 8) + tintB;
                int shine = _rim[i];
                if (shine > 0)
                {
                    r += ((255 - r) * shine) >> 8;
                    g += ((255 - g) * shine) >> 8;
                    b += ((255 - b) * shine) >> 8;
                }
                _out[i] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
            }
        }

        /// <summary>Works out, once per size, where every glass pixel takes its color from.</summary>
        private void Prepare(int w, int h, double scale)
        {
            _w = w; _h = h;
            double bezel = Bezel * scale, radius = Math.Min(_radius * scale, Math.Min(w, h) / 2.0);
            double maxShift = bezel * Bend * (1 + Dispersion);
            _margin = (int)Math.Ceiling(maxShift) + 2;
            _cw = w + 2 * _margin; _ch = h + 2 * _margin;

            _src = new int[_cw * _ch];
            _tmp = new int[_cw * _ch];
            _out = new int[w * h];
            _mapR = new int[w * h]; _mapG = new int[w * h]; _mapB = new int[w * h];
            _rim = new byte[w * h];

            double hx = w / 2.0, hy = h / 2.0;
            // Signed distance to the rounded rectangle (negative inside).
            double Sdf(double px, double py)
            {
                double qx = Math.Abs(px - hx) - (hx - radius), qy = Math.Abs(py - hy) - (hy - radius);
                double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
                return Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - radius;
            }
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double px = x + 0.5, py = y + 0.5;
                    double d = -Sdf(px, py);                     // distance in from the edge
                    double nx = Sdf(px + 1, py) - Sdf(px - 1, py);
                    double ny = Sdf(px, py + 1) - Sdf(px, py - 1);
                    double len = Math.Sqrt(nx * nx + ny * ny);
                    if (len > 1e-6) { nx /= len; ny /= len; } else { nx = ny = 0; }

                    // Near the edge the glass shows what's just outside it, squeezed in
                    // (like the thick rim of a lens); the middle stays sharp.
                    double t = Math.Clamp(d / bezel, 0, 1);
                    double amount = bezel * Bend * (1 - t) * (1 - t);
                    int i = y * w + x;
                    _mapR[i] = Sample(px, py, nx, ny, amount * (1 + Dispersion));
                    _mapG[i] = Sample(px, py, nx, ny, amount);
                    _mapB[i] = Sample(px, py, nx, ny, amount * (1 - Dispersion));

                    // Rim: a thin bright line, stronger where the edge faces up (light from above).
                    double edge = Math.Clamp(1 - d / (1.6 * scale), 0, 1);
                    double facing = 0.35 + 0.65 * Math.Max(0, -ny);
                    double glow = Math.Clamp(1 - d / (bezel * 0.9), 0, 1);   // soft inner light
                    double shine = RimShine * (edge * facing + 0.12 * glow * glow * Math.Max(0, -ny));
                    _rim[i] = (byte)Math.Clamp(shine * 255, 0, 255);
                }
            }

            int Sample(double px, double py, double nx, double ny, double amount)
            {
                int sx = (int)Math.Round(px + nx * amount) + _margin;
                int sy = (int)Math.Round(py + ny * amount) + _margin;
                sx = Math.Clamp(sx, 0, _cw - 1); sy = Math.Clamp(sy, 0, _ch - 1);
                return sy * _cw + sx;
            }

            // Screen copy target.
            _screenDc = GetDC(IntPtr.Zero);
            _memDc = CreateCompatibleDC(_screenDc);
            var bmi = new BITMAPINFO
            {
                biSize = Marshal.SizeOf<BITMAPINFO>(), biWidth = _cw, biHeight = -_ch,  // top-down
                biPlanes = 1, biBitCount = 32, biCompression = 0,
            };
            _dib = CreateDIBSection(_memDc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
            _oldObj = SelectObject(_memDc, _dib);

        }

        /// <summary>Two passes of a sliding box blur (rows, then columns).</summary>
        internal static void BoxBlur(int[] px, int[] tmp, int w, int h, int r)
        {
            if (r < 1) return;
            int div = 2 * r + 1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w, sr = 0, sg = 0, sb = 0;
                for (int k = -r; k <= r; k++)
                {
                    int c = px[row + Math.Clamp(k, 0, w - 1)];
                    sr += (c >> 16) & 0xFF; sg += (c >> 8) & 0xFF; sb += c & 0xFF;
                }
                for (int x = 0; x < w; x++)
                {
                    tmp[row + x] = ((sr / div) << 16) | ((sg / div) << 8) | (sb / div);
                    int a = px[row + Math.Clamp(x - r, 0, w - 1)], b = px[row + Math.Clamp(x + r + 1, 0, w - 1)];
                    sr += ((b >> 16) & 0xFF) - ((a >> 16) & 0xFF);
                    sg += ((b >> 8) & 0xFF) - ((a >> 8) & 0xFF);
                    sb += (b & 0xFF) - (a & 0xFF);
                }
            }
            for (int x = 0; x < w; x++)
            {
                int sr = 0, sg = 0, sb = 0;
                for (int k = -r; k <= r; k++)
                {
                    int c = tmp[Math.Clamp(k, 0, h - 1) * w + x];
                    sr += (c >> 16) & 0xFF; sg += (c >> 8) & 0xFF; sb += c & 0xFF;
                }
                for (int y = 0; y < h; y++)
                {
                    px[y * w + x] = ((sr / div) << 16) | ((sg / div) << 8) | (sb / div);
                    int a = tmp[Math.Clamp(y - r, 0, h - 1) * w + x], b = tmp[Math.Clamp(y + r + 1, 0, h - 1) * w + x];
                    sr += ((b >> 16) & 0xFF) - ((a >> 16) & 0xFF);
                    sg += ((b >> 8) & 0xFF) - ((a >> 8) & 0xFF);
                    sb += (b & 0xFF) - (a & 0xFF);
                }
            }
        }

        private void FreeGdi()
        {
            FreeDesktop();
            if (_memDc != IntPtr.Zero)
            {
                if (_oldObj != IntPtr.Zero) SelectObject(_memDc, _oldObj);
                DeleteDC(_memDc);
            }
            if (_dib != IntPtr.Zero) DeleteObject(_dib);
            if (_screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, _screenDc);
            _memDc = _dib = _screenDc = _oldObj = _bits = IntPtr.Zero;
        }

        // ---- Win32 ---------------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
            public int bmiColors;
        }

        [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? title);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] private static extern bool PatBlt(IntPtr dc, int x, int y, int w, int h, uint rop);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    }
}
