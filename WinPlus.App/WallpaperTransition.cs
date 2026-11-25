using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinPlus
{
    /// <summary>
    /// Draws the change from one wallpaper to the next into our windows under the desktop icons.
    /// Both pictures are made screen-sized once; every frame is then only plain copies (GDI) into a
    /// back buffer and one copy to the window, timed with the screen's refresh (DwmFlush). That keeps
    /// it smooth on two screens without the graphics card or WPF.
    /// </summary>
    internal static unsafe class WallpaperTransition
    {
        /// <summary>How long each one takes (ms).</summary>
        private static int Duration(Wallpapers.Transition fx) => fx switch
        {
            Wallpapers.Transition.Grow => 1100,
            Wallpapers.Transition.Fade => 850,
            Wallpapers.Transition.Wipe => 1000,
            Wallpapers.Transition.Wave => 1250,
            Wallpapers.Transition.Blinds => 950,
            Wallpapers.Transition.Tiles => 1200,
            Wallpapers.Transition.Slide => 900,
            Wallpapers.Transition.Diamond => 1050,
            _ => 0,
        };

        /// <summary>The ones "Random" picks from.</summary>
        private static readonly Wallpapers.Transition[] Picks =
        {
            Wallpapers.Transition.Grow, Wallpapers.Transition.Fade, Wallpapers.Transition.Wipe, Wallpapers.Transition.Wave,
            Wallpapers.Transition.Blinds, Wallpapers.Transition.Tiles, Wallpapers.Transition.Slide, Wallpapers.Transition.Diamond,
        };

        /// <summary>
        /// Plays the transition on every screen (blocks until done; call it off the UI thread).
        /// <paramref name="shown"/> runs once the old picture is in the windows (they can be made visible).
        /// </summary>
        public static void Run(List<(IntPtr Hwnd, System.Drawing.Rectangle Bounds)> screens, BitmapSource? from, BitmapSource to,
                               Wallpapers.Transition fx, System.Drawing.Point? origin, Action shown)
        {
            if (fx == Wallpapers.Transition.Random) fx = Picks[Random.Shared.Next(Picks.Length)];
            var list = new List<Screen>();
            try
            {
                foreach (var (hwnd, b) in screens)
                {
                    var s = new Screen(hwnd, b.Width, b.Height);
                    s.Old.Draw(from);
                    s.New.Draw(to);
                    var o = origin is { } p && b.Contains(p) ? new System.Drawing.Point(p.X - b.X, p.Y - b.Y) : new System.Drawing.Point(b.Width / 2, b.Height / 2);
                    s.Cx = o.X; s.Cy = o.Y;
                    s.Seed(fx);
                    list.Add(s);
                }

                // The old picture first, then the windows show.
                foreach (var s in list) s.Show(s.Old);
                GdiFlush();
                shown();

                int ms = Duration(fx);
                if (ms > 0)
                {
                    var clock = Stopwatch.StartNew();
                    while (true)
                    {
                        double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / ms);
                        foreach (var s in list) s.Frame(fx, t);
                        GdiFlush();
                        if (t >= 1) break;
                        if (DwmFlush() < 0) System.Threading.Thread.Sleep(8);   // next screen refresh
                    }
                }
                foreach (var s in list) s.Show(s.New);
                GdiFlush();
            }
            catch (Exception ex)
            {
                CrashLog.Write("wallpaper transition", ex);
                try { shown(); } catch { }
            }
            finally
            {
                foreach (var s in list) s.Dispose();
            }
        }

        private static double EaseInOut(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
        private static double EaseOut(double t) => 1 - Math.Pow(1 - t, 4);

        // ---- One screen -------------------------------------------------------------------------------

        private sealed class Screen : IDisposable
        {
            public readonly IntPtr Hwnd;
            public readonly int W, H;
            public readonly Surface Old, New, Back;
            public int Cx, Cy;
            private double[] _tileStart = Array.Empty<double>();
            private int _cols, _rows;
            private readonly bool _rtl = !Lang.English;

            public Screen(IntPtr hwnd, int w, int h)
            {
                Hwnd = hwnd; W = w; H = h;
                Old = new Surface(w, h);
                New = new Surface(w, h);
                Back = new Surface(w, h);
            }

            public void Seed(Wallpapers.Transition fx)
            {
                if (fx != Wallpapers.Transition.Tiles) return;
                // Tiles about 120 px, each starting at its own moment, a sweep across with some chance in it.
                _cols = Math.Max(4, W / 120);
                _rows = Math.Max(3, H / 120);
                _tileStart = new double[_cols * _rows];
                for (int r = 0; r < _rows; r++)
                    for (int c = 0; c < _cols; c++)
                    {
                        double along = (_rtl ? _cols - 1 - c : c) / (double)Math.Max(1, _cols - 1);
                        _tileStart[r * _cols + c] = Math.Clamp(along * 0.45 + Random.Shared.NextDouble() * 0.25, 0, 0.65);
                    }
            }

            public void Show(Surface s) => ToWindow(s.Dc);

            private void ToWindow(IntPtr src)
            {
                IntPtr dc = GetDC(Hwnd);
                if (dc == IntPtr.Zero) return;
                BitBlt(dc, 0, 0, W, H, src, 0, 0, SRCCOPY);
                ReleaseDC(Hwnd, dc);
            }

            /// <summary>Builds the frame at time t (0–1) in the back buffer and puts it on the window.</summary>
            public void Frame(Wallpapers.Transition fx, double t)
            {
                IntPtr back = Back.Dc;
                BitBlt(back, 0, 0, W, H, Old.Dc, 0, 0, SRCCOPY);
                switch (fx)
                {
                    case Wallpapers.Transition.Grow:
                    {
                        double e = EaseInOut(t);
                        double maxR = Math.Sqrt(Math.Pow(Math.Max(Cx, W - Cx), 2) + Math.Pow(Math.Max(Cy, H - Cy), 2)) + 2;
                        int r = (int)(maxR * e);
                        ClipCopy(CreateEllipticRgn(Cx - r, Cy - r, Cx + r, Cy + r));
                        break;
                    }
                    case Wallpapers.Transition.Diamond:
                    {
                        double e = EaseInOut(t);
                        int r = (int)((Math.Max(Cx, W - Cx) + Math.Max(Cy, H - Cy) + 2) * e);
                        var pts = new[] { new POINT(Cx, Cy - r), new POINT(Cx + r, Cy), new POINT(Cx, Cy + r), new POINT(Cx - r, Cy) };
                        ClipCopy(CreatePolygonRgn(pts, pts.Length, 1));
                        break;
                    }
                    case Wallpapers.Transition.Fade:
                    {
                        byte a = (byte)Math.Round(255 * Math.Sin(t * Math.PI / 2) * Math.Sin(t * Math.PI / 2));
                        var bf = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = a };
                        AlphaBlend(back, 0, 0, W, H, New.Dc, 0, 0, W, H, bf);
                        break;
                    }
                    case Wallpapers.Transition.Wipe:
                    {
                        // A soft edge: the new picture up to x, then a band that fades out.
                        int band = Math.Max(60, W / 10);
                        int x = (int)(EaseInOut(t) * (W + band)) - band;
                        CopyRect(0, 0, x, H);
                        const int steps = 12;
                        for (int i = 0; i < steps; i++)
                        {
                            int x0 = x + band * i / steps, x1 = x + band * (i + 1) / steps;
                            byte a = (byte)(255 * (steps - i) / (steps + 1));
                            BlendRect(x0, 0, x1 - x0, H, a);
                        }
                        break;
                    }
                    case Wallpapers.Transition.Wave:
                    {
                        // An edge like a wave that rolls across.
                        double amp = Math.Max(30, W / 24.0), len = Math.Max(200, H / 2.2);
                        double e = EaseInOut(t);
                        double baseX = e * (W + 2 * amp) - amp;
                        double phase = t * Math.PI * 3;
                        int n = 48;
                        var pts = new POINT[n + 3];
                        for (int i = 0; i <= n; i++)
                        {
                            int y = H * i / n;
                            int x = (int)(baseX + amp * Math.Sin(y / len * Math.PI * 2 + phase));
                            pts[i] = new POINT(Flip(x), y);
                        }
                        pts[n + 1] = new POINT(Flip(-10), H);
                        pts[n + 2] = new POINT(Flip(-10), 0);
                        ClipCopy(CreatePolygonRgn(pts, pts.Length, 1));
                        break;
                    }
                    case Wallpapers.Transition.Blinds:
                    {
                        // Slats that open one a little after the other.
                        int n = 12;
                        double slat = W / (double)n;
                        for (int i = 0; i < n; i++)
                        {
                            int k = _rtl ? n - 1 - i : i;
                            double p = EaseOut(Math.Clamp((t - k * 0.04) / (1 - (n - 1) * 0.04), 0, 1));
                            int x0 = (int)(i * slat), w = (int)Math.Ceiling(slat * p);
                            if (w > 0) CopyRect(_rtl ? (int)((i + 1) * slat) - w : x0, 0, w, H);
                        }
                        break;
                    }
                    case Wallpapers.Transition.Tiles:
                    {
                        // Squares that grow out of their middles.
                        double tw = W / (double)_cols, th = H / (double)_rows;
                        for (int r = 0; r < _rows; r++)
                            for (int c = 0; c < _cols; c++)
                            {
                                double p = EaseOut(Math.Clamp((t - _tileStart[r * _cols + c]) / 0.35, 0, 1));
                                if (p <= 0) continue;
                                int x0 = (int)(c * tw), y0 = (int)(r * th), x1 = (int)((c + 1) * tw), y1 = (int)((r + 1) * th);
                                int w = x1 - x0, h = y1 - y0;
                                int sw = (int)Math.Ceiling(w * p), sh = (int)Math.Ceiling(h * p);
                                CopyRect(x0 + (w - sw) / 2, y0 + (h - sh) / 2, sw, sh);
                            }
                        break;
                    }
                    case Wallpapers.Transition.Slide:
                    {
                        // The new one slides in and pushes the old one out.
                        int off = (int)(EaseInOut(t) * W);
                        if (_rtl)
                        {
                            BitBlt(back, off, 0, W - off, H, Old.Dc, 0, 0, SRCCOPY);
                            BitBlt(back, 0, 0, off, H, New.Dc, W - off, 0, SRCCOPY);
                        }
                        else
                        {
                            BitBlt(back, 0, 0, W - off, H, Old.Dc, off, 0, SRCCOPY);
                            BitBlt(back, W - off, 0, off, H, New.Dc, 0, 0, SRCCOPY);
                        }
                        break;
                    }
                }
                ToWindow(back);
            }

            private int Flip(int x) => _rtl ? W - x : x;

            /// <summary>The new picture inside the region (the region is deleted).</summary>
            private void ClipCopy(IntPtr rgn)
            {
                SelectClipRgn(Back.Dc, rgn);
                BitBlt(Back.Dc, 0, 0, W, H, New.Dc, 0, 0, SRCCOPY);
                SelectClipRgn(Back.Dc, IntPtr.Zero);
                DeleteObject(rgn);
            }

            /// <summary>The new picture in a rectangle (x measured from the start side).</summary>
            private void CopyRect(int x, int y, int w, int h)
            {
                if (w <= 0 || h <= 0) return;
                if (_rtl && w < W) x = W - x - w;
                BitBlt(Back.Dc, x, y, w, h, New.Dc, x, y, SRCCOPY);
            }

            private void BlendRect(int x, int y, int w, int h, byte alpha)
            {
                if (w <= 0 || h <= 0 || x >= W) return;
                if (x < 0) { w += x; x = 0; }
                if (x + w > W) w = W - x;
                if (w <= 0) return;
                if (_rtl) x = W - x - w;
                AlphaBlend(Back.Dc, x, y, w, h, New.Dc, x, y, w, h, new BLENDFUNCTION { SourceConstantAlpha = alpha });
            }

            public void Dispose()
            {
                Old.Dispose();
                New.Dispose();
                Back.Dispose();
            }
        }

        // ---- A screen-sized picture in memory ---------------------------------------------------------------

        private sealed class Surface : IDisposable
        {
            public readonly IntPtr Dc;
            private readonly IntPtr _bmp, _old;
            private readonly int _w, _h;

            public Surface(int w, int h)
            {
                _w = w; _h = h;
                Dc = CreateCompatibleDC(IntPtr.Zero);
                var bmi = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                _bmp = CreateDIBSection(Dc, ref bmi, 0, out _, IntPtr.Zero, 0);
                _old = SelectObject(Dc, _bmp);
            }

            /// <summary>The picture, filling the screen (cut at the sides that don't fit); black without one.</summary>
            public void Draw(BitmapSource? pic)
            {
                PatBlt(Dc, 0, 0, _w, _h, 0x00000042 /* BLACKNESS */);
                if (pic == null) return;
                var src = pic.Format == PixelFormats.Bgr32 || pic.Format == PixelFormats.Bgra32 ? pic : new FormatConvertedBitmap(pic, PixelFormats.Bgr32, null, 0);
                int pw = src.PixelWidth, ph = src.PixelHeight;
                var px = new byte[pw * ph * 4];
                src.CopyPixels(px, pw * 4, 0);
                double scale = Math.Max((double)_w / pw, (double)_h / ph);
                int sw = Math.Min(pw, (int)Math.Round(_w / scale)), sh = Math.Min(ph, (int)Math.Round(_h / scale));
                int sx = (pw - sw) / 2, sy = (ph - sh) / 2;
                var bmi = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = pw, biHeight = -ph, biPlanes = 1, biBitCount = 32 };
                SetStretchBltMode(Dc, 4 /* HALFTONE */);
                SetBrushOrgEx(Dc, 0, 0, IntPtr.Zero);
                fixed (byte* p = px)
                    StretchDIBits(Dc, 0, 0, _w, _h, sx, sy, sw, sh, (IntPtr)p, ref bmi, 0, SRCCOPY);
            }

            public void Dispose()
            {
                SelectObject(Dc, _old);
                DeleteObject(_bmp);
                DeleteDC(Dc);
            }
        }

        // ---- Win32 ----------------------------------------------------------------------------------------

        private const uint SRCCOPY = 0x00CC0020;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth, biHeight;
            public ushort biPlanes, biBitCount;
            public uint biCompression, biSizeImage;
            public int biXPelsPerMeter, biYPelsPerMeter;
            public uint biClrUsed, biClrImportant;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] private static extern bool PatBlt(IntPtr dc, int x, int y, int w, int h, uint rop);
        [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr old);
        [DllImport("gdi32.dll")]
        private static extern int StretchDIBits(IntPtr dc, int x, int y, int w, int h, int sx, int sy, int sw, int sh,
            IntPtr bits, ref BITMAPINFOHEADER bmi, uint usage, uint rop);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateEllipticRgn(int l, int t, int r, int b);
        [DllImport("gdi32.dll")] private static extern IntPtr CreatePolygonRgn(POINT[] pts, int count, int mode);
        [DllImport("gdi32.dll")] private static extern int SelectClipRgn(IntPtr dc, IntPtr rgn);
        [DllImport("gdi32.dll")] private static extern bool GdiFlush();
        [DllImport("msimg32.dll")]
        private static extern bool AlphaBlend(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, BLENDFUNCTION bf);
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    }
}
