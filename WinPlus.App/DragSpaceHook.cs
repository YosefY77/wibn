using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// Opens the convert menu when you press Space while dragging a file from
    /// File Explorer or the desktop.
    ///
    /// Two low-level hooks (the mouse and the keyboard) only watch for: the left
    /// button held down on an Explorer window + the mouse moved (a drag) + Space.
    /// Only that Space is swallowed; everything else passes straight through.
    /// The callbacks are tiny, so they cost nothing noticeable.
    /// </summary>
    internal static class DragSpaceHook
    {
        private const string OurKey = @"Software\WinPlus\TaskbarScroll";

        /// <summary>Space → convert menu (kept for the convert page).</summary>
        public static bool Enabled
        {
            get => IsKeyOn(MediaTools.Mode.Convert, ignoreFeature: true);
            set => SetKeyOn(MediaTools.Mode.Convert, value);
        }

        private static string FeatureOf(MediaTools.Mode m) => m switch
        {
            MediaTools.Mode.Resize => "resize",
            MediaTools.Mode.Compress => "compress",
            MediaTools.Mode.Archive => "archive",
            _ => "convert",
        };

        private static string ValueOf(MediaTools.Mode m) => m switch
        {
            MediaTools.Mode.Resize => "ResizeKey",
            MediaTools.Mode.Compress => "CompressKey",
            MediaTools.Mode.Archive => "ArchiveKey",
            _ => "SpaceConvert",
        };

        /// <summary>Is this menu's key on (its own switch, and its feature chosen)?</summary>
        public static bool IsKeyOn(MediaTools.Mode m, bool ignoreFeature = false)
        {
            if (!ignoreFeature && !Features.IsOn(FeatureOf(m))) return false;
            using var key = Registry.CurrentUser.OpenSubKey(OurKey);
            return key?.GetValue(ValueOf(m)) is not int v || v != 0;  // on by default
        }

        public static void SetKeyOn(MediaTools.Mode m, bool on)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(OurKey))
                key.SetValue(ValueOf(m), on ? 1 : 0, RegistryValueKind.DWord);
            Refresh();
        }

        // Read by the keyboard hook (no registry access in there).
        private static volatile bool _onConvert, _onResize, _onCompress, _onArchive;

        /// <summary>Hooks run only while at least one of the keys is on.</summary>
        public static void Refresh()
        {
            _onConvert = IsKeyOn(MediaTools.Mode.Convert);
            _onResize = IsKeyOn(MediaTools.Mode.Resize);
            _onCompress = IsKeyOn(MediaTools.Mode.Compress);
            _onArchive = IsKeyOn(MediaTools.Mode.Archive);
            if (_onConvert || _onResize || _onCompress || _onArchive) Start(); else Stop();
        }

        // The hooks live on their own thread with its own message loop: whatever the
        // app's window is doing (converting, downloading…), the mouse and keyboard
        // never wait for it.
        private static Thread? _thread;
        private static uint _threadId;
        private static HookProc? _mouseProc, _keyProc;   // kept alive (the hooks call them)
        private static volatile bool _down, _dragging, _fromExplorer, _menuOpen;
        private static volatile int _heldKey;   // key being held (its repeats are ignored)
        private static POINT _downAt;
        private static RadialMenu? _menu;                // UI thread only

        public static void Start()
        {
            if (_thread != null || !(_onConvert || _onResize || _onCompress || _onArchive)) return;
            _thread = new Thread(HookThread) { IsBackground = true, Name = "WinPlus drag hook" };
            _thread.Start();
        }

        public static void Stop()
        {
            if (_thread == null) return;
            PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
            _thread = null;
        }

        private static void HookThread()
        {
            _threadId = GetCurrentThreadId();
            _mouseProc = MouseProc;
            _keyProc = KeyProc;
            IntPtr module = GetModuleHandle(null);
            IntPtr mouseHook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _mouseProc, module, 0);
            IntPtr keyHook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _keyProc, module, 0);
            while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
            if (keyHook != IntPtr.Zero) UnhookWindowsHookEx(keyHook);
        }

        private static void OnUi(Action action) =>
            Application.Current?.Dispatcher.BeginInvoke(action);

        private static IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                int msg = (int)wParam;
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                switch (msg)
                {
                    case 0x0201: // WM_LBUTTONDOWN
                        _down = true;
                        _dragging = false;
                        _downAt = info.pt;
                        _fromExplorer = IsExplorerAt(info.pt);
                        break;
                    case 0x0200: // WM_MOUSEMOVE
                        if (_down && !_dragging &&
                            (Math.Abs(info.pt.X - _downAt.X) > 6 || Math.Abs(info.pt.Y - _downAt.Y) > 6))
                            _dragging = true;
                        break;
                    case 0x0202: // WM_LBUTTONUP
                        _down = _dragging = false;
                        if (_menuOpen) OnUi(() => _menu?.OnMouseReleased());
                        break;
                }
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        private static IntPtr KeyProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                int msg = (int)wParam;
                int vk = Marshal.ReadInt32(lParam);
                bool keyDown = msg == 0x0100 || msg == 0x0104;   // WM_KEYDOWN / WM_SYSKEYDOWN
                bool keyUp = msg == 0x0101 || msg == 0x0105;

                // Space = convert, R = resize, C = compress, Z = archive (while dragging from Explorer).
                MediaTools.Mode? mode = vk switch
                {
                    0x20 => MediaTools.Mode.Convert,
                    0x52 => MediaTools.Mode.Resize,
                    0x43 => MediaTools.Mode.Compress,
                    0x5A => MediaTools.Mode.Archive,
                    _ => null,
                };
                bool keyOn = mode switch
                {
                    MediaTools.Mode.Convert => _onConvert,
                    MediaTools.Mode.Resize => _onResize,
                    MediaTools.Mode.Compress => _onCompress,
                    MediaTools.Mode.Archive => _onArchive,
                    _ => false,
                };
                if (mode != null && (_menuOpen || (keyOn && _dragging && _fromExplorer)))
                {
                    if (keyDown && _heldKey != vk)   // a new press, not the key repeating
                    {
                        _heldKey = vk;
                        if (_menuOpen)
                        {
                            OnUi(() => _menu?.Dismiss());          // the key again: close
                        }
                        else
                        {
                            _menuOpen = true;
                            GetCursorPos(out POINT p);
                            var m = mode.Value;
                            OnUi(() => ShowMenu(p, m));
                        }
                    }
                    if (keyUp && _heldKey == vk) _heldKey = 0;
                    return (IntPtr)1;  // Explorer never sees this key
                }
                if (keyUp && _heldKey == vk) _heldKey = 0;
                if (vk == 0x1B && keyDown && _menuOpen) OnUi(() => _menu?.Cancel());  // Esc
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        private static void ShowMenu(POINT p, MediaTools.Mode mode)
        {
            if (_menu != null) return;
            try
            {
                _menu = new RadialMenu(new Point(p.X, p.Y), mode);
                _menu.Closed += (_, _) => { _menu = null; _menuOpen = false; };
                _menu.Show();
            }
            catch (Exception ex)
            {
                CrashLog.Write("RadialMenu", ex);
                _menu = null;
                _menuOpen = false;
            }
        }

        /// <summary>Is the window under this point File Explorer or the desktop?</summary>
        private static bool IsExplorerAt(POINT p)
        {
            IntPtr hwnd = WindowFromPoint(p);
            if (hwnd == IntPtr.Zero) return false;
            IntPtr root = GetAncestor(hwnd, 2 /* GA_ROOT */);
            var cls = new StringBuilder(64);
            GetClassName(root, cls, cls.Capacity);
            return cls.ToString() is "CabinetWClass" or "ExploreWClass" or "Progman" or "WorkerW";
        }

        // ---- Win32 ---------------------------------------------------------------------

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData, flags, time;
            public IntPtr extraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam, lParam;
            public uint time;
            public POINT pt;
            public uint lPrivate;
        }

        [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    }
}
