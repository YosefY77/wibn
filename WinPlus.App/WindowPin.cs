using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// "Pin on top" (with a pin icon) in the menu that opens with a right-click on a window's title bar (its system
    /// menu, with Restore, Minimize, Maximize, Close): picked, the window stays above all the others ("always on
    /// top") and the item gets a check mark; picked again, it goes back among them.
    ///
    /// How: the item is put into the window's own menu when it comes in front (the menu belongs to that window, so
    /// Wibn can't get its command); while that menu is open Wibn watches for a click (or Enter) on the item, and
    /// does it itself. Nothing runs while no such menu is open. Turned off (or Wibn closing), the item is taken out.
    /// </summary>
    internal static class WindowPin
    {
        private const string Key = @"Software\WinPlus\WindowPin";
        private const uint ItemId = 0x5A70;   // a command id no window uses (the low 4 bits are Windows' own)

        public static bool Enabled
        {
            get { try { using var k = Registry.CurrentUser.OpenSubKey(Key); return k?.GetValue("On") is int v && v != 0; } catch { return false; } }
            set
            {
                try { using var k = Registry.CurrentUser.CreateSubKey(Key); k.SetValue("On", value ? 1 : 0, RegistryValueKind.DWord); } catch { }
                Refresh();
            }
        }

        private static readonly HashSet<IntPtr> _pinned = new();                // windows pinned from the menu (Wibn's own keeps on top only then)
        private static readonly Dictionary<IntPtr, bool> _withItem = new();     // windows whose menu has the item (true: we added a line too)
        private static IntPtr _sysHook, _focusHook;
        private static WinEventProc? _proc;
        private static uint _focusPid;
        // The menu open now, if it's one of ours.
        private static IntPtr _menu, _menuOwner, _menuPopup;
        private static int _focused = -1;
        private static IntPtr _mouse, _keys;
        private static HookProc? _mouseProc, _keysProc;

        /// <summary>Was this window pinned from the menu?</summary>
        public static bool IsPinned(IntPtr h) => _pinned.Contains(h);

        /// <summary>Starts or stops to match the setting (at start-up, and whenever it changes).</summary>
        public static void Refresh()
        {
            if (Enabled) Start(); else Stop();
        }

        private static void Start()
        {
            if (_sysHook != IntPtr.Zero) return;
            _proc = OnEvent;
            // The front window changing, and menus opening and closing.
            _sysHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MENUPOPUPEND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
            AddItem(GetForegroundWindow());
        }

        public static void Stop()
        {
            if (_sysHook != IntPtr.Zero) { UnhookWinEvent(_sysHook); _sysHook = IntPtr.Zero; }
            MenuClosed();
            foreach (var (h, line) in _withItem) RemoveItem(h, line);
            _withItem.Clear();
        }

        private static void OnEvent(IntPtr hook, uint ev, IntPtr h, int obj, int child, uint thread, uint time)
        {
            try
            {
                switch (ev)
                {
                    case EVENT_SYSTEM_FOREGROUND when obj == 0:
                        AddItem(h);
                        break;
                    case EVENT_SYSTEM_MENUPOPUPSTART:
                        MenuOpened(h);
                        break;
                    case EVENT_SYSTEM_MENUPOPUPEND or EVENT_SYSTEM_MENUEND:
                        if (ev == EVENT_SYSTEM_MENUEND || h == _menuPopup) MenuClosed();
                        break;
                    case EVENT_OBJECT_FOCUS when h == _menuPopup && h != IntPtr.Zero:
                        _focused = child - 1;   // the item the keyboard is on (counted from 1)
                        break;
                }
            }
            catch (Exception ex) { CrashLog.Write("window pin", ex); }
        }

        // ---- The item in a window's menu -----------------------------------------------------------------

        /// <summary>Puts the item in the window's menu (once), just before Close, with its check mark right.</summary>
        private static void AddItem(IntPtr h)
        {
            if (!Fits(h)) return;
            IntPtr m = GetSystemMenu(h, false);
            if (m == IntPtr.Zero) return;
            if (GetMenuState(m, ItemId, MF_BYCOMMAND) == 0xFFFFFFFF)
            {
                int count = GetMenuItemCount(m), close = -1;
                for (int i = 0; i < count; i++) if (GetMenuItemID(m, i) == SC_CLOSE) { close = i; break; }
                if (close < 0) close = count;
                bool line;
                // ... Maximize, [pin], ——, Close: into the line before Close if there's one, else with a line of its own.
                if (close > 0 && (GetMenuState(m, (uint)(close - 1), MF_BYPOSITION) & MF_SEPARATOR) != 0)
                {
                    if (!InsertMenu(m, (uint)(close - 1), MF_BYPOSITION | MF_STRING, ItemId, "Pin on top")) return;
                    line = false;
                }
                else
                {
                    if (!InsertMenu(m, (uint)close, MF_BYPOSITION | MF_STRING, ItemId, "Pin on top")) return;
                    InsertMenu(m, (uint)close + 1, MF_BYPOSITION | MF_SEPARATOR, 0, null);
                    line = true;
                }
                if (_withItem.Count > 300) Prune();
                _withItem[h] = line;
                SetIcon(h, m);
            }
            Check(h, m);
        }

        private static void Check(IntPtr h, IntPtr m) =>
            CheckMenuItem(m, ItemId, MF_BYCOMMAND | (Topmost(h) ? MF_CHECKED : MF_UNCHECKED));

        private static void RemoveItem(IntPtr h, bool line)
        {
            if (!IsWindow(h)) return;
            IntPtr m = GetSystemMenu(h, false);
            if (m == IntPtr.Zero) return;
            int count = GetMenuItemCount(m), at = -1;
            for (int i = 0; i < count; i++) if (GetMenuItemID(m, i) == ItemId) { at = i; break; }
            if (at < 0) return;
            DeleteMenu(m, (uint)at, MF_BYPOSITION);
            if (line && at < GetMenuItemCount(m) && (GetMenuState(m, (uint)at, MF_BYPOSITION) & MF_SEPARATOR) != 0)
                DeleteMenu(m, (uint)at, MF_BYPOSITION);
        }

        private static void Prune()
        {
            var gone = new List<IntPtr>();
            foreach (var h in _withItem.Keys) if (!IsWindow(h)) gone.Add(h);
            foreach (var h in gone) { _withItem.Remove(h); _pinned.Remove(h); }
        }

        /// <summary>A normal window with a minimize button (not the desktop, the taskbar, a tool window, a menu…).</summary>
        private static bool Fits(IntPtr h)
        {
            if (h == IntPtr.Zero || !IsWindowVisible(h)) return false;
            long style = GetWindowLongPtr(h, GWL_STYLE).ToInt64();
            long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            if ((style & WS_MINIMIZEBOX) == 0 || (style & WS_SYSMENU) == 0 || (style & WS_CHILD) != 0) return false;
            if ((ex & WS_EX_TOOLWINDOW) != 0 || (ex & WS_EX_NOACTIVATE) != 0) return false;
            var cls = new StringBuilder(64);
            GetClassName(h, cls, cls.Capacity);
            return cls.ToString() is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
        }

        // ---- The menu open -------------------------------------------------------------------------------

        /// <summary>A menu opened: if it's a window's menu with the item in it, watch for the item being picked.</summary>
        private static void MenuOpened(IntPtr popup)
        {
            if (popup == IntPtr.Zero) return;
            if (SendMessageTimeout(popup, MN_GETHMENU, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 200, out var res) == IntPtr.Zero) return;
            IntPtr m = res;
            if (m == IntPtr.Zero || GetMenuState(m, ItemId, MF_BYCOMMAND) == 0xFFFFFFFF) return;
            IntPtr owner = IntPtr.Zero;
            foreach (var h in _withItem.Keys) if (IsWindow(h) && GetSystemMenu(h, false) == m) { owner = h; break; }
            if (owner == IntPtr.Zero) return;
            _menu = m; _menuOwner = owner; _menuPopup = popup; _focused = -1;
            Check(owner, m);
            // The keyboard's place in the menu: told by that app's menu (only while it's open).
            GetWindowThreadProcessId(popup, out uint pid);
            if (_focusHook == IntPtr.Zero || _focusPid != pid)
            {
                if (_focusHook != IntPtr.Zero) UnhookWinEvent(_focusHook);
                _focusPid = pid;
                _focusHook = SetWinEventHook(EVENT_OBJECT_FOCUS, EVENT_OBJECT_FOCUS, IntPtr.Zero, _proc!, pid, 0, WINEVENT_OUTOFCONTEXT);
            }
            if (_mouse == IntPtr.Zero) { _mouseProc = OnMouse; _mouse = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0); }
            if (_keys == IntPtr.Zero) { _keysProc = OnKey; _keys = SetWindowsHookEx(WH_KEYBOARD_LL, _keysProc, GetModuleHandle(null), 0); }
        }

        private static void MenuClosed()
        {
            _menu = _menuOwner = _menuPopup = IntPtr.Zero;
            _focused = -1;
            if (_mouse != IntPtr.Zero) { UnhookWindowsHookEx(_mouse); _mouse = IntPtr.Zero; }
            if (_keys != IntPtr.Zero) { UnhookWindowsHookEx(_keys); _keys = IntPtr.Zero; }
            if (_focusHook != IntPtr.Zero) { UnhookWinEvent(_focusHook); _focusHook = IntPtr.Zero; _focusPid = 0; }
        }

        /// <summary>A button let go over the item (left, or right: a menu from a right-click takes that too).</summary>
        private static IntPtr OnMouse(int code, IntPtr msg, IntPtr data)
        {
            if (code >= 0 && (msg == (IntPtr)WM_LBUTTONUP || msg == (IntPtr)WM_RBUTTONUP) && _menu != IntPtr.Zero)
            {
                var pt = Marshal.PtrToStructure<POINT>(data);
                int at = MenuItemFromPoint(IntPtr.Zero, _menu, pt);
                if (at >= 0 && GetMenuItemID(_menu, at) == ItemId) Picked();
            }
            return CallNextHookEx(IntPtr.Zero, code, msg, data);
        }

        /// <summary>Enter on the item.</summary>
        private static IntPtr OnKey(int code, IntPtr msg, IntPtr data)
        {
            if (code >= 0 && msg == (IntPtr)WM_KEYDOWN && _menu != IntPtr.Zero && Marshal.ReadInt32(data) == VK_RETURN
                && _focused >= 0 && GetMenuItemID(_menu, _focused) == ItemId)
                Picked();
            return CallNextHookEx(IntPtr.Zero, code, msg, data);
        }

        /// <summary>The item picked: done once the menu has closed (never inside the hook, so the mouse never waits).</summary>
        private static void Picked()
        {
            IntPtr h = _menuOwner;
            if (h == IntPtr.Zero) return;
            _menuOwner = IntPtr.Zero;   // once
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                try { Toggle(h); } catch (Exception ex) { CrashLog.Write("window pin", ex); }
            }), DispatcherPriority.Background);
        }

        /// <summary>On top, or back among the others.</summary>
        private static void Toggle(IntPtr h)
        {
            if (!IsWindow(h)) return;
            bool top = Topmost(h);
            // (A window of an app run as admin can't be changed from here: it just stays as it was.)
            SetWindowPos(h, top ? HWND_NOTOPMOST : HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            if (Topmost(h)) _pinned.Add(h); else _pinned.Remove(h);
            IntPtr m = GetSystemMenu(h, false);
            if (m != IntPtr.Zero) Check(h, m);
        }

        // ---- The item's icon --------------------------------------------------------------------------

        private static readonly Dictionary<int, IntPtr> _icons = new();   // one pin picture per size (kept: menus show it)

        /// <summary>A pin beside the item, as big as the menu's other icons at that window's scale.</summary>
        private static void SetIcon(IntPtr h, IntPtr m)
        {
            int size = Math.Max(16, (int)Math.Round(16 * Math.Max(96u, GetDpiForWindow(h)) / 96.0));
            if (!_icons.TryGetValue(size, out var bmp)) { bmp = PinBitmap(size); _icons[size] = bmp; }
            if (bmp == IntPtr.Zero) return;
            var info = new MENUITEMINFO { cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(), fMask = MIIM_BITMAP, hbmpItem = bmp };
            SetMenuItemInfo(m, ItemId, false, ref info);
        }

        /// <summary>The pin glyph drawn into a see-through picture (blue: it shows on light and dark menus alike).</summary>
        private static IntPtr PinBitmap(int size)
        {
            try
            {
                var visual = new System.Windows.Media.DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    var text = new System.Windows.Media.FormattedText("", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new System.Windows.Media.Typeface("Segoe Fluent Icons, Segoe MDL2 Assets"), size * 0.9,
                        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x96, 0xDD)), 1.0);
                    dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
                }
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(size, size, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(visual);
                var pixels = new byte[size * size * 4];
                rtb.CopyPixels(pixels, size * 4, 0);

                var bi = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32 };
                IntPtr hbmp = CreateDIBSection(IntPtr.Zero, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
                if (hbmp == IntPtr.Zero) return IntPtr.Zero;
                Marshal.Copy(pixels, 0, bits, pixels.Length);   // already premultiplied, as menus want it
                return hbmp;
            }
            catch (Exception ex) { CrashLog.Write("window pin icon", ex); return IntPtr.Zero; }
        }

        private static bool Topmost(IntPtr h) => (GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;

        /// <summary>
        /// Wibn's own window is a normal window: it never stays above the others by itself, only when pinned from
        /// its menu. (Called when it's shown and every time it loses the front.)
        /// </summary>
        public static void KeepNormal(Window w)
        {
            void Check()
            {
                var h = new WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero || _pinned.Contains(h)) return;
                if (w.Topmost) w.Topmost = false;
                if (Topmost(h)) SetWindowPos(h, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            w.SourceInitialized += (_, _) => Check();
            w.Activated += (_, _) => Check();
            w.Deactivated += (_, _) => Check();
        }

        // ---- Win32 -------------------------------------------------------------------------------------

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MENUEND = 0x0005,
            EVENT_SYSTEM_MENUPOPUPSTART = 0x0006, EVENT_SYSTEM_MENUPOPUPEND = 0x0007,
            EVENT_OBJECT_FOCUS = 0x8005, WINEVENT_OUTOFCONTEXT = 0;
        private const int GWL_STYLE = -16, GWL_EXSTYLE = -20, WH_MOUSE_LL = 14, WH_KEYBOARD_LL = 13, VK_RETURN = 0x0D;
        private const int WM_KEYDOWN = 0x0100, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;
        private const uint MN_GETHMENU = 0x01E1, SMTO_ABORTIFHUNG = 0x2, SC_CLOSE = 0xF060;
        private const uint MF_BYCOMMAND = 0x0, MF_BYPOSITION = 0x400, MF_STRING = 0x0, MF_SEPARATOR = 0x800,
            MF_CHECKED = 0x8, MF_UNCHECKED = 0x0;
        private const long WS_MINIMIZEBOX = 0x20000, WS_SYSMENU = 0x80000, WS_CHILD = 0x40000000,
            WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
        private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        private static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2);

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

        private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr h, int obj, int child, uint thread, uint time);
        private delegate IntPtr HookProc(int code, IntPtr msg, IntPtr data);

        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint tid);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern IntPtr GetSystemMenu(IntPtr h, bool revert);
        [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr m);
        [DllImport("user32.dll")] private static extern uint GetMenuItemID(IntPtr m, int pos);
        [DllImport("user32.dll")] private static extern uint GetMenuState(IntPtr m, uint id, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool InsertMenu(IntPtr m, uint pos, uint flags, uint id, string? text);
        [DllImport("user32.dll")] private static extern bool DeleteMenu(IntPtr m, uint pos, uint flags);
        [DllImport("user32.dll")] private static extern uint CheckMenuItem(IntPtr m, uint id, uint flags);
        [DllImport("user32.dll")] private static extern bool SetMenuItemInfo(IntPtr m, uint item, bool byPosition, ref MENUITEMINFO info);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr section, uint offset);

        private const uint MIIM_BITMAP = 0x80;

        [StructLayout(LayoutKind.Sequential)]
        private struct MENUITEMINFO
        {
            public uint cbSize, fMask, fType, fState, wID;
            public IntPtr hSubMenu, hbmpChecked, hbmpUnchecked, dwItemData, dwTypeData;
            public uint cch;
            public IntPtr hbmpItem;
        }

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
        [DllImport("user32.dll")] private static extern int MenuItemFromPoint(IntPtr h, IntPtr m, POINT pt);
        [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    }
}
