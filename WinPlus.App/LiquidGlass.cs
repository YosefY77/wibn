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
    /// Live: about 30 times a second (on its own thread) it copies the screen area behind the element
    /// (the window itself is left out of screen copies with WDA_EXCLUDEFROMCAPTURE)
    /// and redraws the glass, so a video behind it moves in the glass too. The bend
    /// for every pixel is worked out once; a frame is a copy, a blur and a lookup.
    /// It only runs while the window is open.
    /// </summary>
    internal sealed class LiquidGlass : IDisposable
    {
        // Looks (in device-independent pixels where it's a size).
        private const double Bezel = 18;        // how far in from the edge the glass bends
        // The rest comes from the settings (Video download page).
        private readonly double Bend, Dispersion, TintAlpha, RimShine;   // from the settings
        private readonly int Frost;
        private const byte TintR = 0x1C, TintG = 0x1C, TintB = 0x1E;
        private static readonly TimeSpan FrameTime = TimeSpan.FromMilliseconds(33);

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

        /// <summary>
        /// What's behind turned light (true) or dark (false): the text on the glass
        /// should switch to dark / light to stay readable.
        /// </summary>
        public event Action<bool>? LightBehindChanged;
        private volatile bool _lightBehind;
        private bool _lightReported;

        /// <summary>Is the window kept out of screen copies (Windows 10 2004 and later)? Without it there's no glass.</summary>
        public bool Available { get; }

        public LiquidGlass(Window window, FrameworkElement host, FrameworkElement target, Thickness offset, double radius, GlassSettings settings)
        {
            Bend = Math.Clamp(settings.Bend, 0, 100) / 100.0;
            Dispersion = Math.Clamp(settings.Dispersion, 0, 100) / 100.0;
            Frost = Math.Clamp(settings.Frost, 0, 12);
            TintAlpha = Math.Clamp(settings.Tint, 0, 80) / 100.0;
            RimShine = Math.Clamp(settings.Rim, 0, 100) / 100.0;
            _window = window;
            _host = host;
            _target = target;
            _offset = offset;
            _radius = radius;
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            Available = SetWindowDisplayAffinity(hwnd, 0x11 /* WDA_EXCLUDEFROMCAPTURE */);
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
            int ox = (int)Math.Round(origin.X), oy = (int)Math.Round(origin.Y);
            double scale = dpi.DpiScaleX;
            _thread = new Thread(() => Loop(ox, oy, w, h, scale))
            {
                IsBackground = true, Name = "Wibn glass", Priority = ThreadPriority.BelowNormal,
            };
            _thread.Start();
        }

        public void Dispose() => _running = false;   // the thread stops and cleans up by itself

        private void Loop(int ox, int oy, int w, int h, double scale)
        {
            try
            {
                Prepare(w, h, scale);
                int x = ox - _margin, y = oy - _margin;
                int frost = (int)Math.Round(Frost * scale);
                var clock = Stopwatch.StartNew();
                while (_running)
                {
                    var started = clock.Elapsed;
                    if (DrawFrame(x, y, frost) && Interlocked.CompareExchange(ref _pending, 1, 0) == 0)
                    {
                        lock (_front) Array.Copy(_out, _front, _out.Length);
                        _window.Dispatcher.BeginInvoke(new Action(Present));
                    }
                    var rest = FrameTime - (clock.Elapsed - started);
                    if (rest > TimeSpan.Zero) Thread.Sleep(rest);
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
                if (_lightReported != _lightBehind)
                {
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

        /// <summary>One frame into _out (on the glass thread). False if the screen couldn't be copied.</summary>
        private bool DrawFrame(int x, int y, int frost)
        {
            // 1. What's behind, from the screen (this window is left out of the copy).
            if (!BitBlt(_memDc, 0, 0, _cw, _ch, _screenDc, x, y, 0x00CC0020 /* SRCCOPY */)) return false;
            Marshal.Copy(_bits, _src, 0, _src.Length);

            // 2. Frost.
            if (frost > 0) BoxBlur(_src, _tmp, _cw, _ch, frost);

            // How light is it behind (after the tint)? Every 7th pixel is plenty.
            long sum = 0; int count = 0;
            for (int i = 0; i < _src.Length; i += 7)
            {
                int c = _src[i];
                sum += ((c >> 16) & 0xFF) * 3 + ((c >> 8) & 0xFF) * 6 + (c & 0xFF);
                count++;
            }
            double light = count > 0 ? (sum / (double)count / 10 / 255) * (1 - TintAlpha) + TintAlpha * 0.11 : 0;
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
            return true;
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
