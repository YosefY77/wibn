using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinPlus
{
    /// <summary>
    /// The mouse kept on a window's picture over the taskbar for a moment: that window shows right
    /// where it is on the screen, see-through over a blur, so you know which one it is before clicking.
    /// It goes as soon as the mouse leaves the picture (or the previews close). It never takes the mouse
    /// or the focus, and nothing is moved: it's only a picture (drawn live by Windows) on a window of its own.
    /// </summary>
    internal static class WindowPeek
    {
        private static readonly DispatcherTimer _wait = new() { Interval = TimeSpan.FromMilliseconds(800) };
        private static IntPtr _pending, _above;
        private static Ghost? _ghost;

        static WindowPeek()
        {
            _wait.Tick += (_, _) =>
            {
                _wait.Stop();
                if (_pending == IntPtr.Zero || !IsWindow(_pending)) return;
                if (Showing(_pending)) return;   // you can already see it: nothing to show
                try { _ghost = new Ghost(_pending, _above); }
                catch (Exception ex) { CrashLog.Write("window peek", ex); _ghost = null; }
            };
        }

        /// <summary>The mouse is on this window's picture (or row); <paramref name="above"/> = the previews' panel (it stays over the ghost).</summary>
        public static void Hover(IntPtr window, IntPtr above)
        {
            if (!CustomTaskbar.PeekWindows) return;
            if (_pending == window && (_wait.IsEnabled || _ghost != null)) return;   // already counting (or showing)
            Hide();
            _pending = window;
            _above = above;
            _wait.Start();
        }

        /// <summary>The mouse left this window's picture.</summary>
        public static void Leave(IntPtr window)
        {
            if (_pending == window) Hide();
        }

        public static void Hide()
        {
            _wait.Stop();
            _pending = IntPtr.Zero;
            var g = _ghost;
            _ghost = null;
            g?.CloseIt();
        }

        /// <summary>The see-through copy of the window, at the window's own place and size.</summary>
        private sealed class Ghost
        {
            private const byte Opacity = 215;   // of 255: a little see-through, but bright and easy to tell apart
            private readonly HwndSource _src;
            private readonly IntPtr _thumb;
            private readonly DispatcherTimer _fade = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            private RECTI _dest;
            private int _step;
            private bool _closed;

            public Ghost(IntPtr window, IntPtr above)
            {
                if (!Where(window, out var place, out _dest)) throw new InvalidOperationException("no place for the window");
                var p = new HwndSourceParameters("Wibn window peek")
                {
                    WindowStyle = unchecked((int)0x80000000) /* WS_POPUP */,
                    ExtendedWindowStyle = 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */ | 0x8 /* TOPMOST */ | 0x20 /* TRANSPARENT */,
                    ParentWindow = IntPtr.Zero,
                    PositionX = place.Left, PositionY = place.Top,
                    Width = place.Right - place.Left, Height = place.Bottom - place.Top,
                };
                _src = new HwndSource(p);
                _src.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
                {
                    if (msg == 0x84 /* WM_NCHITTEST */) { handled = true; return new IntPtr(-1); }   // HTTRANSPARENT: the mouse goes through
                    if (msg == 0x21 /* WM_MOUSEACTIVATE */) { handled = true; return (IntPtr)3; }      // MA_NOACTIVATE
                    return IntPtr.Zero;
                });
                TaskbarApps.Ours.Add(_src.Handle);

                // Clear inside (so the blur shows), rounded like Windows 11's windows, the blur behind it.
                if (_src.CompositionTarget != null) _src.CompositionTarget.BackgroundColor = Colors.Transparent;
                var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                DwmExtendFrameIntoClientArea(_src.Handle, ref m);
                int round = 2; /* DWMWCP_ROUND */
                DwmSetWindowAttribute(_src.Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
                int noPeek = 1;   // (it's ours: Windows' own "peek at desktop" leaves it alone)
                DwmSetWindowAttribute(_src.Handle, 12 /* DWMWA_EXCLUDED_FROM_PEEK */, ref noPeek, sizeof(int));
                Blur(_src.Handle);

                if (DwmRegisterThumbnail(_src.Handle, window, out _thumb) != 0) _thumb = IntPtr.Zero;
                Show(0);

                // Just under the previews' panel (its pictures are owned by it, so they stay over it too).
                var after = above != IntPtr.Zero ? above : new IntPtr(-1) /* TOPMOST */;
                SetWindowPos(_src.Handle, after, place.Left, place.Top, place.Right - place.Left, place.Bottom - place.Top,
                    0x0010 /* NOACTIVATE */ | 0x0040 /* SHOWWINDOW */);
                if (above != IntPtr.Zero)
                    SetWindowPos(above, new IntPtr(-1), 0, 0, 0, 0, 0x0010 | 0x0001 /* NOSIZE */ | 0x0002 /* NOMOVE */);

                // Fades in (the picture; the blur is there right away).
                _fade.Tick += (_, _) =>
                {
                    if (_closed) { _fade.Stop(); return; }
                    _step++;
                    Show((byte)(Opacity * Math.Min(1.0, _step / 9.0)));
                    if (_step >= 9) _fade.Stop();
                };
                _fade.Start();
            }

            private void Show(byte opacity)
            {
                if (_thumb == IntPtr.Zero) return;
                var props = new DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = 0x1 /* RECTDESTINATION */ | 0x4 /* OPACITY */ | 0x8 /* VISIBLE */ | 0x10 /* SOURCECLIENTAREAONLY */,
                    rcDestination = _dest, opacity = opacity, fVisible = true, fSourceClientAreaOnly = false,
                };
                DwmUpdateThumbnailProperties(_thumb, ref props);
            }

            public void CloseIt()
            {
                if (_closed) return;
                _closed = true;
                _fade.Stop();
                if (_thumb != IntPtr.Zero) DwmUnregisterThumbnail(_thumb);
                TaskbarApps.Ours.Remove(_src.Handle);
                try { _src.Dispose(); } catch { }
            }

            /// <summary>
            /// Where the ghost goes (the window's visible edges, on the screen) and where its picture is drawn
            /// inside it (the picture includes Windows' invisible resize borders, so it starts a bit outside).
            /// A minimized window: where it comes back to.
            /// </summary>
            private static bool Where(IntPtr window, out RECTI place, out RECTI dest)
            {
                place = dest = default;
                if (IsIconic(window))
                {
                    var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                    if (!GetWindowPlacement(window, ref wp)) return false;
                    var r = wp.rcNormalPosition;
                    IntPtr mon = MonitorFromRect(ref r, 2 /* NEAREST */);
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (!GetMonitorInfo(mon, ref mi)) return false;
                    if ((wp.flags & 0x2 /* RESTORETOMAXIMIZED */) != 0) place = mi.rcWork;
                    else
                    {
                        // The placement is in work-area coordinates.
                        int dx = mi.rcWork.Left - mi.rcMonitor.Left, dy = mi.rcWork.Top - mi.rcMonitor.Top;
                        place = new RECTI { Left = r.Left + dx, Top = r.Top + dy, Right = r.Right + dx, Bottom = r.Bottom + dy };
                    }
                    dest = new RECTI { Left = 0, Top = 0, Right = place.Right - place.Left, Bottom = place.Bottom - place.Top };
                }
                else
                {
                    if (!GetWindowRect(window, out var wr)) return false;
                    if (DwmGetWindowAttribute(window, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out RECTI fb, Marshal.SizeOf<RECTI>()) != 0) fb = wr;
                    place = fb;
                    dest = new RECTI { Left = wr.Left - fb.Left, Top = wr.Top - fb.Top, Right = wr.Right - fb.Left, Bottom = wr.Bottom - fb.Top };
                }
                return place.Right - place.Left > 20 && place.Bottom - place.Top > 20;
            }

            /// <summary>The blur behind the ghost, a little light so the window looks lit up.</summary>
            private static void Blur(IntPtr hwnd)
            {
                var accent = new ACCENT_POLICY { AccentState = 3 /* ENABLE_BLURBEHIND */, AccentFlags = 2, GradientColor = 0x38FFFFFF };
                int size = Marshal.SizeOf<ACCENT_POLICY>();
                IntPtr ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(accent, ptr, false);
                    var data = new WINCOMPATTRDATA { Attribute = 19 /* WCA_ACCENT_POLICY */, Data = ptr, SizeOfData = size };
                    SetWindowCompositionAttribute(hwnd, ref data);
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
        }

        /// <summary>
        /// The window is already in sight: not minimized, and most of it isn't covered by other windows
        /// (Wibn's own, like the taskbar and the previews, don't count). Checked on a grid of points over it.
        /// </summary>
        private static bool Showing(IntPtr window)
        {
            if (IsIconic(window) || !IsWindowVisible(window)) return false;
            if (DwmGetWindowAttribute(window, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out RECTI r, Marshal.SizeOf<RECTI>()) != 0
                && !GetWindowRect(window, out r)) return false;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w < 20 || h < 20) return false;
            const int n = 6;
            int seen = 0, counted = 0;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    var pt = new POINTI { X = r.Left + w * (2 * i + 1) / (2 * n), Y = r.Top + h * (2 * j + 1) / (2 * n) };
                    if (MonitorFromPoint(pt, 0 /* NULL: off every screen */) == IntPtr.Zero) continue;
                    IntPtr at = WindowFromPoint(pt);
                    IntPtr top = at == IntPtr.Zero ? IntPtr.Zero : GetAncestor(at, 2 /* GA_ROOT */);
                    if (top != IntPtr.Zero && TaskbarApps.Ours.Contains(top)) continue;   // under Wibn's own windows: not counted
                    counted++;
                    if (top == window) seen++;
                }
            return counted > 0 && seen >= counted * 0.75;
        }

        // ---- Native ---------------------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)] private struct RECTI { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MARGINS { public int Left, Right, Top, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct POINTI { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECTI rcMonitor, rcWork; public uint dwFlags; }
        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPLACEMENT { public int length, flags, showCmd; public POINTI ptMin, ptMax; public RECTI rcNormalPosition; }
        [StructLayout(LayoutKind.Sequential)] private struct ACCENT_POLICY { public int AccentState, AccentFlags, GradientColor, AnimationId; }
        [StructLayout(LayoutKind.Sequential)] private struct WINCOMPATTRDATA { public int Attribute; public IntPtr Data; public int SizeOfData; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DWM_THUMBNAIL_PROPERTIES
        {
            public int dwFlags;
            public RECTI rcDestination, rcSource;
            public byte opacity;
            [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
            [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
        }

        [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
        [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECTI value, int size);
        [DllImport("user32.dll")] private static extern bool SetWindowCompositionAttribute(IntPtr h, ref WINCOMPATTRDATA data);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINTI p);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINTI p, uint flags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECTI r);
        [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT wp);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RECTI r, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
