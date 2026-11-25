using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace WinPlus
{
    /// <summary>
    /// When the taskbars' live glass takes a new picture of what's behind them: only a moment after a window
    /// over or next to a bar moved or was resized (when it stops), was minimized or restored, shown or hidden,
    /// or came to the front. Only the bars it reaches take one; nothing changing near them: no pictures at all.
    ///
    /// And while a window shrinks or grows (minimized, restored from the taskbar, made smaller from maximized…)
    /// its animation would pass over the bars and color them: from the moment that starts and for a short
    /// while after, they show the wallpaper.
    /// </summary>
    internal static class GlassCalm
    {
        private const int HoldMs = 700;
        private static Thread? _thread;
        private static WinEventProc? _events;
        private static readonly Dictionary<IntPtr, bool> _zoomed = new();
        // The windows last seen over (or next to) a bar, and where: moving away from it changes the bar too.
        private static readonly Dictionary<IntPtr, RECT> _near = new();
        private static readonly Dictionary<IntPtr, RECT> _rects = new();   // each window's last place (real pixels)

        /// <summary>Starts watching (once; it runs for as long as Wibn does).</summary>
        public static void Start()
        {
            if (_thread != null) return;
            _thread = new Thread(() =>
            {
                SetThreadDpiAwarenessContext(new IntPtr(-4));   // real pixels, like the glasses
                _events = OnEvent;
                // No mouse hook: every mouse move in Windows would pass through Wibn first, and any moment
                // Wibn was busy (cleaning up its memory…) the mouse stuck with it.
                IntPtr h1 = SetWinEventHook(0x0016, 0x0017, IntPtr.Zero, _events, 0, 0, 0 /* OUTOFCONTEXT */);              // minimize start / end
                IntPtr h2 = SetWinEventHook(0x800B, 0x800B, IntPtr.Zero, _events, 0, 0, 0 | 0x0002 /* SKIPOWNPROCESS */);   // a window moved or resized
                IntPtr h4 = SetWinEventHook(0x0003, 0x0003, IntPtr.Zero, _events, 0, 0, 0x0002);                            // a window came to the front
                IntPtr h5 = SetWinEventHook(0x000B, 0x000B, IntPtr.Zero, _events, 0, 0, 0x0002);                            // a move / resize by the mouse ended
                IntPtr h6 = SetWinEventHook(0x8002, 0x8003, IntPtr.Zero, _events, 0, 0, 0x0002);                            // a window shown / hidden
                while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
                if (h1 != IntPtr.Zero) UnhookWinEvent(h1);
                if (h2 != IntPtr.Zero) UnhookWinEvent(h2);
                if (h4 != IntPtr.Zero) UnhookWinEvent(h4);
                if (h5 != IntPtr.Zero) UnhookWinEvent(h5);
                if (h6 != IntPtr.Zero) UnhookWinEvent(h6);
            }) { IsBackground = true, Name = "Wibn glass calm" };
            _thread.Start();
        }

        /// <summary>A window is about to shrink or grow (e.g. Wibn's taskbar minimizes or restores it).</summary>
        public static void Now()
        {
            // Streaming: the glass follows the window as it shrinks or grows; the wallpaper for a moment instead
            // only made it late (it caught up most of a second after the window).
            if (LiquidGlass.LiveStream) return;
            LiquidGlass.WallpaperForAMoment(HoldMs);
        }

        // ---- A new picture, once things are still ----------------------------------------------------------

        private const int QuietMs = 300;
        private static readonly Timer _soon = new(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
        private static readonly object _areaLock = new();
        private static bool _hasArea;
        private static RECT _area;   // where things changed since the last picture

        /// <summary>
        /// A new picture of the bars this area reaches, a moment after the last change. A window still
        /// moving pushes it back (no pictures in the middle of a drag: each one is work for all of Windows).
        /// </summary>
        private static void Later(RECT changed)
        {
            lock (_areaLock)
            {
                if (!_hasArea) { _area = changed; _hasArea = true; }
                else
                {
                    _area.Left = Math.Min(_area.Left, changed.Left); _area.Top = Math.Min(_area.Top, changed.Top);
                    _area.Right = Math.Max(_area.Right, changed.Right); _area.Bottom = Math.Max(_area.Bottom, changed.Bottom);
                }
            }
            try { _soon.Change(QuietMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
        }

        private static void Fire()
        {
            RECT a;
            lock (_areaLock)
            {
                if (!_hasArea) return;
                a = _area; _hasArea = false;
            }
            LiquidGlass.RecaptureLive(a.Left, a.Top, a.Right, a.Bottom);
        }

        private static void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (hwnd == IntPtr.Zero || idObject != 0 /* OBJID_WINDOW */ || idChild != 0) return;
            if (ev is 0x0016 or 0x0017) { CustomTaskbar.Poke(); Now(); return; }   // (it takes the real picture again once it's over)
            if (GetAncestor(hwnd, 2 /* GA_ROOT */) != hwnd) return;
            // The glass helper's own window moves over each bar as it copies: counting it would start a new
            // copy after every copy, without end.
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != 0 && pid == GlassCopy.HelperPid) return;
            if (LiquidGlass.IsOverlay(hwnd)) return;   // NVIDIA's (and others') see-through overlay: nothing shows of it
            if (!Bounds(hwnd, out RECT r)) return;

            // Only a window over or next to a bar (now, or before it changed) changes what's behind it.
            bool near = LiquidGlass.TouchesLive(r.Left, r.Top, r.Right, r.Bottom);
            bool was = _near.TryGetValue(hwnd, out RECT before);
            if (near) { if (!was && _near.Count > 400) _near.Clear(); _near[hwnd] = r; }
            else if (was) _near.Remove(hwnd);
            if (near) Later(r);
            if (was) Later(before);

            // Where each window is and whether it's maximized (kept from any of its events, so the first click on
            // its maximize button is already known).
            bool zoomed = IsZoomed(hwnd);
            bool known = _zoomed.TryGetValue(hwnd, out bool wasZoomed);
            bool hadRect = _rects.TryGetValue(hwnd, out RECT old);
            if (!known && _zoomed.Count > 400) { _zoomed.Clear(); _rects.Clear(); }
            _zoomed[hwnd] = zoomed;
            _rects[hwnd] = r;
            // Maximized or not any more, or another window in front: the taskbar looks at once whether to slide.
            // A window let go after a move (it may now be under a bar): the same.
            if ((known && wasZoomed != zoomed) || ev is 0x0003 or 0x000B) CustomTaskbar.Poke();
            if (ev != 0x800B || !known || wasZoomed == zoomed || !IsWindowVisible(hwnd)) return;
            // From maximized to not (or back) is the "make it smaller / bigger" animation.
            if (!LiquidGlass.LiveStream) { Now(); return; }
            // Streaming: Windows' maximize animation isn't in screen copies, so for its quarter of a second the bars
            // showed the wallpaper where the window was already growing: they show its color instead.
            if (zoomed && hadRect) LiquidGlass.Maximizing(old.Left, old.Top, old.Right, old.Bottom, r.Left, r.Top, r.Right, r.Bottom);
        }

        /// <summary>What's really drawn of the window (not its invisible resize borders), in real pixels.</summary>
        private static bool Bounds(IntPtr h, out RECT r)
        {
            if (DwmGetWindowAttribute(h, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out r, Marshal.SizeOf<RECT>()) != 0 && !GetWindowRect(h, out r)) return false;
            return r.Right > r.Left && r.Bottom > r.Top;
        }

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; public uint lPrivate; }
        private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT value, int size);
    }
}
