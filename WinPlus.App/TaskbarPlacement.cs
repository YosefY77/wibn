using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// Taskbar position (which screen edge) and "hide the taskbar while a
    /// fullscreen app is open".
    /// </summary>
    internal static class TaskbarPlacement
    {
        // ---- Position ---------------------------------------------------------

        public const int Left = 0, Top = 1, Right = 2, Bottom = 3;

        /// <summary>Where the taskbar really is right now (asked from Windows).</summary>
        public static int GetEdge()
        {
            var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
            if (SHAppBarMessage(ABM_GETTASKBARPOS, ref abd) != IntPtr.Zero && abd.uEdge <= 3)
            {
                return (int)abd.uEdge;
            }
            return Bottom;
        }

        /// <summary>
        /// Moving the taskbar is done by Windows' own setting (writing the
        /// registry and restarting explorer didn't work with the new movable
        /// taskbar), so this opens that page.
        /// </summary>
        public static void OpenWindowsTaskbarSettings()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:taskbar")
                {
                    UseShellExecute = true,
                });
            }
            catch { }
        }

        // ---- Hide the taskbar while a fullscreen / maximized window is in front ----
        //
        // Windows' auto-hide switch (ABM_SETSTATE) is ignored by this Windows
        // build, so we hide the taskbar window ourselves:
        //   * hide it and give its space to the window (work area = whole monitor)
        //   * show it while the mouse touches that screen edge, hide it again
        //     when the mouse leaves it
        //   * put everything back when the window isn't fullscreen/maximized

        /// <summary>Hide the taskbar while a fullscreen or maximized window is in front.</summary>
        public static bool AutoHideEnabled
        {
            get => SettingsStoreGet("AutoHideFullscreen", 0) != 0 && Features.IsOn("taskbar") && !CustomTaskbar.Enabled;
            set
            {
                SettingsStoreSet("AutoHideFullscreen", value ? 1 : 0);
                ApplyAutoHideMode();
            }
        }

        private static DispatcherTimer? _timer;
        private static IntPtr _tray;          // taskbar window we hid (0 = none)
        private static RECT _trayRect;        // where it sits
        private static RECT _monitorRect;     // the whole monitor
        private static IntPtr _hiddenMonitor; // monitor of the hidden taskbar
        private static DateTime? _notFullscreenSince;
        private static bool _revealed;        // shown because the mouse is at the edge
        private static string _lastClass = "";

        private static void Log(string text)
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPlus");
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "taskbar.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} [app] autohide: {text}\r\n");
            }
            catch { }
        }

        /// <summary>Starts or stops watching windows according to the setting.</summary>
        public static void ApplyAutoHideMode()
        {
            // If the app was killed while a taskbar was hidden, bring it back first.
            if (_tray == IntPtr.Zero && SettingsStoreGet("AutoHideActive", 0) == 1)
            {
                ShowAllTaskbars();
                SettingsStoreSet("AutoHideActive", 0);
            }

            if (!AutoHideEnabled)
            {
                _timer?.Stop();
                _timer = null;
                if (_moveHook != IntPtr.Zero) { UnhookWinEvent(_moveHook); _moveHook = IntPtr.Zero; }
                RestoreTaskbar();
                return;
            }

            if (_timer == null)
            {
                // A few cheap window queries per tick; faster only while hidden
                // (to catch the mouse at the edge).
                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                // Tells us when the user starts dragging a window by its title bar.
                _moveProc ??= OnMoveSizeStart;
                _moveHook = SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZESTART,
                                            IntPtr.Zero, _moveProc, 0, 0, 0 /* OUTOFCONTEXT */);
                _timer.Tick += (_, _) => Check();
                _timer.Start();
            }
            Check();
        }

        /// <summary>Puts the taskbar back (on exit / when turned off).</summary>
        public static void RestoreTaskbar()
        {
            // Windows we stretched become normal maximized windows again.
            UndoAllPseudo();
            if (_tray == IntPtr.Zero) return;
            ShowWindow(_tray, SW_SHOWNA);
            _tray = IntPtr.Zero;
            _revealed = false;
            SettingsStoreSet("AutoHideActive", 0);
            if (_timer != null) _timer.Interval = TimeSpan.FromMilliseconds(400);
        }

        private static void Check()
        {
            IntPtr fg = GetForegroundWindow();

            // Start menu, search, tray flyouts, the taskbar itself, or no window
            // at all for a moment: leave things as they are.
            if (IsShellWindow(fg) || !IsWindowVisible(fg)) return;

            IntPtr monitor;
            bool fullscreen = IsFullscreenOrMaximized(fg, out monitor);

            if (_tray != IntPtr.Zero)
            {
                if (fullscreen && monitor == _hiddenMonitor)
                {
                    _notFullscreenSince = null;
                    if (IsZoomed(fg))
                    {
                        if (_pseudo.TryGetValue(fg, out var old))
                        {
                            // The user pressed "maximize" on a stretched window:
                            // for them that button means "restore down".
                            _pseudo.Remove(fg);
                            old.Placement.flags = 0;
                            old.Placement.showCmd = SW_SHOWNORMAL;
                            SetWindowPlacement(fg, ref old.Placement);
                            Log("restore down");
                        }
                        else
                        {
                            PseudoMaximize(fg);
                        }
                    }
                    HandleEdge();
                    return;
                }

                // Wait a little before bringing the taskbar back, so quick focus
                // changes (menus, dialogs) don't make it flicker.
                _notFullscreenSince ??= DateTime.Now;
                if ((DateTime.Now - _notFullscreenSince.Value).TotalMilliseconds < 500)
                {
                    HandleEdge();
                    return;
                }
                Log($"show (window: {_lastClass}, fullscreen={fullscreen}, sameMonitor={monitor == _hiddenMonitor})");
                RestoreTaskbar();
                // fall through: another monitor may need hiding now
            }

            if (fullscreen)
            {
                IntPtr tray = TaskbarOnMonitor(monitor);
                if (tray == IntPtr.Zero || !GetWindowRect(tray, out _trayRect)) return;

                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(monitor, ref mi)) return;

                _tray = tray;
                _hiddenMonitor = monitor;
                _monitorRect = mi.rcMonitor;
                _revealed = false;
                _notFullscreenSince = null;
                SettingsStoreSet("AutoHideActive", 1);
                ShowWindow(tray, SW_HIDE);
                // A maximized window can't grow past the work area that explorer
                // keeps for the (hidden) taskbar, which leaves a black strip. So
                // it becomes a normal window covering the whole monitor, and is
                // maximized again when the feature is turned off / on exit.
                // (Doing it whenever the taskbar shows would pull the window to
                // the front over the one the user just switched to.)
                if (IsZoomed(fg)) PseudoMaximize(fg);
                if (_timer != null) _timer.Interval = TimeSpan.FromMilliseconds(80);
                Log($"hide (window: {_lastClass})");
            }
        }

        // Windows we turned from "maximized" into "normal, covering the whole
        // monitor": their old placement and where we put them.
        private sealed class PseudoInfo { public WINDOWPLACEMENT Placement; public RECT Rect; }
        private static readonly System.Collections.Generic.Dictionary<IntPtr, PseudoInfo> _pseudo = new();

        private static void PseudoMaximize(IntPtr hwnd)
        {
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(hwnd, ref wp)) return;

            // Forget windows that were closed.
            foreach (var h in new System.Collections.Generic.List<IntPtr>(_pseudo.Keys))
                if (!IsWindow(h)) _pseudo.Remove(h);

            // Restore without animation, sized near the monitor already.
            var normal = wp;
            normal.showCmd = SW_SHOWNORMAL;
            normal.flags = 0;
            normal.rcNormalPosition = ToWorkspace(_monitorRect);
            SetWindowPlacement(hwnd, ref normal);

            // Now match the visible frame exactly to the monitor (invisible
            // resize borders stay outside, like a real maximized window).
            if (!GetWindowRect(hwnd, out RECT win)) return;
            RECT frame = win;
            DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out frame, Marshal.SizeOf<RECT>());
            var a = _monitorRect;
            int left = a.Left - (frame.Left - win.Left);
            int top = a.Top - (frame.Top - win.Top);
            int right = a.Right + (win.Right - frame.Right);
            int bottom = a.Bottom + (win.Bottom - frame.Bottom);
            SetWindowPos(hwnd, IntPtr.Zero, left, top, right - left, bottom - top,
                         0x0004 /* NOZORDER */ | 0x0010 /* NOACTIVATE */ | 0x0200 /* NOOWNERZORDER */);

            var info = new PseudoInfo { Placement = wp };
            GetWindowRect(hwnd, out info.Rect);
            _pseudo[hwnd] = info;
            Log("stretched window over the taskbar");
        }

        /// <summary>Maximizes the stretched windows again (those the user didn't move/resize).</summary>
        private static void UndoAllPseudo()
        {
            if (_pseudo.Count == 0) return;
            IntPtr fg = GetForegroundWindow();
            foreach (var (hwnd, info) in _pseudo)
            {
                if (!IsWindow(hwnd) || IsIconic(hwnd) || IsZoomed(hwnd)) continue;
                if (!GetWindowRect(hwnd, out RECT r) || !SameRect(r, info.Rect)) continue;
                var wp = info.Placement;
                wp.flags = 0;
                wp.showCmd = SW_SHOWMAXIMIZED;
                SetWindowPlacement(hwnd, ref wp);
            }
            _pseudo.Clear();
            if (fg != IntPtr.Zero) SetForegroundWindow(fg);  // keep the user's window in front
        }

        private static bool SameRect(RECT a, RECT b) =>
            a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

        // WINDOWPLACEMENT uses "workspace" coordinates: relative to the primary
        // monitor's work area instead of the screen.
        // ---- Dragging a stretched window off the top: back to its old size ----
        //
        // A real maximized window shrinks to its old size when you drag its title
        // bar. A stretched one is a normal window, so Windows would drag it at
        // full size. When such a drag starts, we stop it, shrink the window to
        // its size before maximizing (under the mouse), and start the drag again.

        private static IntPtr _moveHook;
        private static WinEventProc? _moveProc;
        private static bool _redragging;

        private static async void OnMoveSizeStart(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
        {
            if (_redragging || obj != 0 || !_pseudo.TryGetValue(hwnd, out var info)) return;
            if (!GetWindowRect(hwnd, out RECT now) || !SameRect(now, info.Rect)) return;
            if (!GetCursorPos(out POINT p)) return;

            RECT frame = now;
            DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out frame, Marshal.SizeOf<RECT>());
            // Grabbing the very top edge is a resize, not a move.
            if (p.Y <= frame.Top + 4) return;

            _pseudo.Remove(hwnd);
            _redragging = true;
            try
            {
                uint tid = GetWindowThreadProcessId(hwnd, out _);
                PostMessage(hwnd, 0x001F /* WM_CANCELMODE */, IntPtr.Zero, IntPtr.Zero);
                for (int i = 0; i < 20 && InMoveSize(tid); i++) await System.Threading.Tasks.Task.Delay(10);
                if (InMoveSize(tid)) return;

                // Old size; keep the mouse at the same spot along the title bar.
                RECT old = FromWorkspace(info.Placement.rcNormalPosition);
                int w = old.Right - old.Left, h = old.Bottom - old.Top;
                int fw = Math.Max(1, frame.Right - frame.Left);
                int left = p.X - (int)((long)(p.X - frame.Left) * w / fw);
                int top = p.Y - Math.Min(p.Y - frame.Top, 16);
                SetWindowPos(hwnd, IntPtr.Zero, left, top, w, h,
                             0x0004 /* NOZORDER */ | 0x0200 /* NOOWNERZORDER */);

                // Its normal size is the old one again (for Windows' own restore).
                var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                if (GetWindowPlacement(hwnd, ref wp))
                {
                    wp.flags = 0;
                    wp.showCmd = SW_SHOWNORMAL;
                    SetWindowPlacement(hwnd, ref wp);
                }

                // Still holding the mouse button: carry on dragging.
                if ((GetAsyncKeyState(0x01 /* VK_LBUTTON */) & 0x8000) != 0)
                {
                    PostMessage(hwnd, 0x0112 /* WM_SYSCOMMAND */, (IntPtr)0xF012 /* SC_MOVE | HTCAPTION */, IntPtr.Zero);
                }
                Log("drag: back to size before maximize");
            }
            finally
            {
                await System.Threading.Tasks.Task.Delay(100);
                _redragging = false;
            }
        }

        internal static bool InMoveSize(uint tid)
        {
            var gi = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            return GetGUIThreadInfo(tid, ref gi) && (gi.flags & 0x2 /* GUI_INMOVESIZE */) != 0;
        }

        private static RECT FromWorkspace(RECT r)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            IntPtr primary = MonitorFromWindow(IntPtr.Zero, 1 /* PRIMARY */);
            if (!GetMonitorInfo(primary, ref mi)) return r;
            int dx = mi.rcWork.Left - mi.rcMonitor.Left, dy = mi.rcWork.Top - mi.rcMonitor.Top;
            return new RECT { Left = r.Left + dx, Top = r.Top + dy, Right = r.Right + dx, Bottom = r.Bottom + dy };
        }

        private static RECT ToWorkspace(RECT r)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            IntPtr primary = MonitorFromWindow(IntPtr.Zero, 1 /* PRIMARY */);
            if (!GetMonitorInfo(primary, ref mi)) return r;
            int dx = mi.rcWork.Left - mi.rcMonitor.Left, dy = mi.rcWork.Top - mi.rcMonitor.Top;
            return new RECT { Left = r.Left - dx, Top = r.Top - dy, Right = r.Right - dx, Bottom = r.Bottom - dy };
        }

        /// <summary>While hidden: show the taskbar when the mouse touches its edge,
        /// hide it again when the mouse leaves it.</summary>
        private static void HandleEdge()
        {
            if (!GetCursorPos(out POINT p)) return;

            if (!_revealed)
            {
                if (AtTaskbarEdge(p))
                {
                    ShowWindow(_tray, SW_SHOWNA);
                    _revealed = true;
                }
                else
                {
                    if (IsWindowVisible(_tray))
                    {
                        ShowWindow(_tray, SW_HIDE);  // Windows showed it again by itself
                    }
                }
            }
            else
            {
                bool inside = p.X >= _trayRect.Left && p.X < _trayRect.Right &&
                              p.Y >= _trayRect.Top && p.Y < _trayRect.Bottom;
                if (!inside)
                {
                    ShowWindow(_tray, SW_HIDE);
                    _revealed = false;
                }
            }
        }

        private static bool AtTaskbarEdge(POINT p)
        {
            var r = _trayRect;
            var m = _monitorRect;
            const int edge = 2;
            bool vertical = (r.Right - r.Left) < (r.Bottom - r.Top);
            if (vertical)
            {
                if (p.Y < r.Top || p.Y >= r.Bottom) return false;
                bool onLeft = r.Left <= m.Left + 1;
                return onLeft ? p.X <= m.Left + edge : p.X >= m.Right - 1 - edge;
            }
            if (p.X < r.Left || p.X >= r.Right) return false;
            bool onTop = r.Top <= m.Top + 1;
            return onTop ? p.Y <= m.Top + edge : p.Y >= m.Bottom - 1 - edge;
        }

        private static bool IsShellWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return true;
            var cls = new System.Text.StringBuilder(64);
            GetClassName(hwnd, cls, cls.Capacity);
            string c = cls.ToString();
            return c is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow"
                     or "XamlExplorerHostIslandWindow" or "NotifyIconOverflowWindow"
                     or "TopLevelWindowForOverflowXamlIsland" or "Xaml_WindowedPopupClass"
                     or "#32768";
        }

        private static bool IsFullscreenOrMaximized(IntPtr hwnd, out IntPtr monitor)
        {
            monitor = IntPtr.Zero;
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;

            var cls = new System.Text.StringBuilder(64);
            GetClassName(hwnd, cls, cls.Capacity);
            _lastClass = cls.ToString();
            if (_lastClass is "Progman" or "WorkerW") return false;  // desktop

            monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (IsZoomed(hwnd)) return true;

            if (!GetWindowRect(hwnd, out RECT r)) return false;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref mi)) return false;
            return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
                   r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
        }

        private static IntPtr TaskbarOnMonitor(IntPtr monitor)
        {
            IntPtr primary = TrayHost.ExplorerTray();
            if (primary != IntPtr.Zero && MonitorFromWindow(primary, 2) == monitor) return primary;
            IntPtr hwnd = IntPtr.Zero;
            while ((hwnd = FindWindowEx(IntPtr.Zero, hwnd, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            {
                if (MonitorFromWindow(hwnd, 2) == monitor) return hwnd;
            }
            return IntPtr.Zero;
        }

        private static void ShowAllTaskbars()
        {
            IntPtr primary = TrayHost.ExplorerTray();
            if (primary != IntPtr.Zero) ShowWindow(primary, SW_SHOWNA);
            IntPtr hwnd = IntPtr.Zero;
            while ((hwnd = FindWindowEx(IntPtr.Zero, hwnd, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            {
                ShowWindow(hwnd, SW_SHOWNA);
            }
            // Explorer recalculates the work area when the taskbar shows again.
            SendMessageTimeout((IntPtr)0xFFFF, 0x001A, IntPtr.Zero, "TraySettings", 0x0002, 1000, out _);
        }

        // ---- Settings helpers (same key as the rest of the app) -------------------

        private static int SettingsStoreGet(string name, int fallback)
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\WinPlus\TaskbarScroll");
            return key?.GetValue(name) is int v ? v : fallback;
        }

        private static void SettingsStoreSet(string name, int value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\WinPlus\TaskbarScroll");
            key.SetValue(name, value, RegistryValueKind.DWord);
        }

        // ---- Win32 ------------------------------------------------------------

        private const uint ABM_GETTASKBARPOS = 0x5;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("shell32.dll")] private static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string cls, string? name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT value, int size);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint param, ref RECT rect, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);

        private const int SW_HIDE = 0, SW_SHOWNORMAL = 1, SW_SHOWMAXIMIZED = 3, SW_SHOWNA = 8;

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPLACEMENT
        {
            public int length;
            public int flags;
            public int showCmd;
            public POINT ptMinPosition;
            public POINT ptMaxPosition;
            public RECT rcNormalPosition;
        }

        [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT wp);
        [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT wp);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
        private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO info);

        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public int cbSize;
            public uint flags;
            public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
            public RECT rcCaret;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }
    }
}
