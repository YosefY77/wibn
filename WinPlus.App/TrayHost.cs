using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// The icons next to the clock (Discord, Steam…) for Wibn's taskbar.
    ///
    /// Apps put their icon there by finding the window called "Shell_TrayWnd" (Windows'
    /// taskbar) and sending it the icon. While Wibn's taskbar is on, Wibn has a window
    /// with that name too, kept in front so apps find it first, and gets the icons. Every
    /// message is also passed on to Windows' taskbar, so it always has the same icons:
    /// if Wibn closes or crashes, nothing is lost.
    /// </summary>
    internal static class TrayHost
    {
        /// <summary>One app's icon.</summary>
        public sealed class Icon
        {
            public IntPtr Hwnd;
            public uint Id;
            public Guid? Guid;
            public uint Callback;
            public uint Version;
            public string Tip = "";
            public bool ShowTip;
            public bool Hidden;
            public ImageSource? Image;
            public string Exe = "";
            /// <summary>Who it is across restarts: the program and its icon number (or id).</summary>
            public string Key => Path.GetFileName(Exe).ToLowerInvariant() + "|" + (Guid?.ToString() ?? Id.ToString());
        }

        // The window runs on its own thread: passing every message on to Windows' taskbar can
        // wait (explorer is busy at sign-in), and that must never freeze Wibn's windows.
        private static readonly List<Icon> _icons = new();
        private static readonly object _lock = new();

        /// <summary>A copy of the icons (safe on any thread).</summary>
        public static List<Icon> Icons { get { lock (_lock) return _icons.ToList(); } }

        /// <summary>An icon was added, changed or removed (on the UI thread, a moment later).</summary>
        public static event Action? Changed;

        /// <summary>Where an icon's button is on the screen (for apps that ask where their icon is).</summary>
        public static Func<Icon, CustomTaskbar.RECT?>? Locate;

        public static bool Active => _hwnd != IntPtr.Zero;

        /// <summary>When Wibn itself last told every app "the taskbar is new" (so it isn't taken for an explorer restart).</summary>
        public static DateTime LastBroadcast = DateTime.MinValue;

        private static volatile IntPtr _hwnd;
        private static int _quitMsg;
        private static WndProc? _proc;   // kept alive for as long as Wibn runs
        private static DispatcherTimer? _cleanup;
        private static int _changePending;
        private static readonly int _myPid = Environment.ProcessId;

        // ---- Start / stop -------------------------------------------------------------------------

        public static void Start()
        {
            if (_hwnd != IntPtr.Zero) return;
            try
            {
                LoadPromoted();
                var ready = new ManualResetEventSlim();
                var t = new Thread(() => Run(ready)) { IsBackground = true, Name = "Wibn tray host" };
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                ready.Wait(3000);
                if (_hwnd == IntPtr.Zero) return;
                _cleanup = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
                _cleanup.Tick += (_, _) => RemoveDead();
                _cleanup.Start();
                Broadcast();   // apps send their icons again, to us now
                CrashLog.Note("tray host on");
            }
            catch (Exception ex) { CrashLog.Write("tray host", ex); _hwnd = IntPtr.Zero; }
        }

        /// <summary>The tray thread: makes the window and handles its messages until Stop.</summary>
        private static void Run(ManualResetEventSlim ready)
        {
            IntPtr hwnd = IntPtr.Zero;
            try
            {
                _proc ??= Proc;   // one handler for good: Windows keeps the class (and this handler) after the window is gone
                _quitMsg = RegisterWindowMessage("WibnTrayHostQuit");
                IntPtr inst = GetModuleHandle(null);
                var wc = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                    hInstance = inst, lpszClassName = "Shell_TrayWnd",
                };
                RegisterClassEx(ref wc);   // fails harmlessly if it's still registered from last time
                hwnd = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, "Shell_TrayWnd", "", WS_POPUP, 0, 0, 0, 0,
                                      IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
                if (hwnd == IntPtr.Zero) CrashLog.Note("tray host: no window " + Marshal.GetLastWin32Error());
                _hwnd = hwnd;
                KeepFirst();
            }
            catch (Exception ex) { CrashLog.Write("tray host", ex); }
            finally { ready.Set(); }
            if (hwnd == IntPtr.Zero) return;
            while (GetMessage(out MSG m, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref m);
                DispatchMessage(ref m);
            }
        }

        public static void Stop()
        {
            if (_hwnd == IntPtr.Zero) return;
            _cleanup?.Stop();
            _cleanup = null;
            PostMessage(_hwnd, (uint)_quitMsg, IntPtr.Zero, IntPtr.Zero);   // its own thread closes it
            _hwnd = IntPtr.Zero;
            lock (_lock) _icons.Clear();
            Changed?.Invoke();
            // Windows' taskbar already has every icon (it got a copy of each); this makes sure.
            Broadcast();
            CrashLog.Note("tray host off");
        }

        /// <summary>Keeps our window ahead of Windows' taskbar, so apps find ours first.</summary>
        public static void KeepFirst()
        {
            if (_hwnd == IntPtr.Zero) return;
            if (FindWindow("Shell_TrayWnd", null) != _hwnd)
                SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
        }

        /// <summary>Our window has the taskbar's place, for anything that asks where the taskbar is.</summary>
        public static void SetRect(CustomTaskbar.RECT r)
        {
            if (_hwnd == IntPtr.Zero) return;
            if (GetWindowRect(_hwnd, out var now) && now.Left == r.Left && now.Top == r.Top && now.Right == r.Right && now.Bottom == r.Bottom) return;
            SetWindowPos(_hwnd, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
        }

        /// <summary>Tells every app the taskbar is new, so they send their icons again.</summary>
        public static void Broadcast()
        {
            LastBroadcast = DateTime.Now;
            int msg = RegisterWindowMessage("TaskbarCreated");
            SendNotifyMessage(HWND_BROADCAST, (uint)msg, IntPtr.Zero, IntPtr.Zero);
        }

        // ---- Windows' own taskbar -----------------------------------------------------------------

        /// <summary>Windows' real taskbar: the one explorer owns (not Wibn's window of the same name).</summary>
        public static IntPtr ExplorerTray()
        {
            uint shell = 0;
            IntPtr desk = GetShellWindow();
            if (desk != IntPtr.Zero) GetWindowThreadProcessId(desk, out shell);
            IntPtr h = IntPtr.Zero, other = IntPtr.Zero;
            while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_TrayWnd", null)) != IntPtr.Zero)
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (shell != 0 && pid == shell) return h;
                if (other == IntPtr.Zero && pid != _myPid && !IsWibn(pid)) other = h;
            }
            return other;
        }

        private static bool IsWibn(uint pid)
        {
            try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); return p.ProcessName.Equals("WinPlus", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        // ---- Messages -----------------------------------------------------------------------------

        private static IntPtr Proc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
        {
            try
            {
                if (_quitMsg != 0 && msg == (uint)_quitMsg)
                {
                    DestroyWindow(hwnd);
                    PostQuitMessage(0);
                    return IntPtr.Zero;
                }
                if (msg == WM_COPYDATA && l != IntPtr.Zero)
                {
                    var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(l);
                    IntPtr theirs = Forward(msg, w, l);
                    switch (cds.dwData.ToInt64())
                    {
                        case 1: lock (_lock) return OnNotify(cds) ? (IntPtr)1 : IntPtr.Zero;     // Shell_NotifyIcon
                        case 3: return OnGetRect(cds) ?? theirs;                     // Shell_NotifyIconGetRect
                        default: return theirs;                                      // app bars and the rest: Windows answers
                    }
                }
                // Anything else meant for the taskbar: Windows' taskbar answers it.
                if (msg == WM_COMMAND || (msg >= WM_USER && msg < 0x8000)) return Forward(msg, w, l);
            }
            catch (Exception ex) { CrashLog.Write("tray message", ex); }
            return DefWindowProc(hwnd, msg, w, l);
        }

        private static IntPtr Forward(uint msg, IntPtr w, IntPtr l)
        {
            IntPtr real = ExplorerTray();
            if (real == IntPtr.Zero) return IntPtr.Zero;
            SendMessageTimeout(real, msg, w, l, SMTO_ABORTIFHUNG, 2000, out IntPtr result);
            return result;
        }

        // NOTIFYICONDATA as the shell sends it (handles as 32 bits), after a 4-byte signature and the message.
        private const int OffMsg = 4, OffNid = 8;
        private const int NidWnd = 4, NidId = 8, NidFlags = 12, NidCallback = 16, NidIcon = 20, NidTip = 24,
                          NidState = 280, NidStateMask = 284, NidVersion = 800, NidGuid = 936;

        private static bool OnNotify(COPYDATASTRUCT cds)
        {
            if (cds.cbData < OffNid + NidTip || cds.lpData == IntPtr.Zero) return false;
            IntPtr p = cds.lpData;
            uint message = (uint)Marshal.ReadInt32(p, OffMsg);
            IntPtr nid = p + OffNid;
            var hwnd = new IntPtr((uint)Marshal.ReadInt32(nid, NidWnd));
            uint id = (uint)Marshal.ReadInt32(nid, NidId);
            uint flags = (uint)Marshal.ReadInt32(nid, NidFlags);
            Guid? guid = null;
            if ((flags & NIF_GUID) != 0 && cds.cbData >= OffNid + NidGuid + 16)
            {
                var bytes = new byte[16];
                Marshal.Copy(nid + NidGuid, bytes, 0, 16);
                guid = new Guid(bytes);
            }

            var icon = _icons.FirstOrDefault(i => guid != null ? i.Guid == guid : i.Hwnd == hwnd && i.Id == id);
            switch (message)
            {
                case NIM_ADD:
                case NIM_MODIFY:
                    if (icon == null)
                    {
                        if (message == NIM_MODIFY) return false;   // apps then add it
                        icon = new Icon { Hwnd = hwnd, Id = id, Guid = guid, Exe = ExeOf(hwnd) };
                        _icons.Add(icon);
                    }
                    else if (message == NIM_ADD && icon.Hwnd != hwnd) icon.Hwnd = hwnd;
                    if ((flags & NIF_MESSAGE) != 0) icon.Callback = (uint)Marshal.ReadInt32(nid, NidCallback);
                    if ((flags & NIF_ICON) != 0)
                    {
                        var hIcon = new IntPtr((uint)Marshal.ReadInt32(nid, NidIcon));
                        icon.Image = hIcon == IntPtr.Zero ? null : ToImage(hIcon);
                    }
                    if ((flags & NIF_TIP) != 0)
                        icon.Tip = Marshal.PtrToStringUni(nid + NidTip, 128).Split('\0')[0];
                    if ((flags & NIF_STATE) != 0 && cds.cbData >= OffNid + NidStateMask + 4)
                    {
                        uint state = (uint)Marshal.ReadInt32(nid, NidState), mask = (uint)Marshal.ReadInt32(nid, NidStateMask);
                        if ((mask & NIS_HIDDEN) != 0) icon.Hidden = (state & NIS_HIDDEN) != 0;
                    }
                    icon.ShowTip = (flags & NIF_SHOWTIP) != 0;
                    SoonChanged();
                    return true;

                case NIM_DELETE:
                    if (icon == null) return false;
                    _icons.Remove(icon);
                    SoonChanged();
                    return true;

                case NIM_SETVERSION:
                    if (icon == null) return false;
                    if (cds.cbData >= OffNid + NidVersion + 4) icon.Version = (uint)Marshal.ReadInt32(nid, NidVersion);
                    return true;

                default:   // NIM_SETFOCUS
                    return icon != null;
            }
        }

        /// <summary>An app asks where its icon is (to open its window next to it).</summary>
        private static IntPtr? OnGetRect(COPYDATASTRUCT cds)
        {
            if (cds.cbData < 24 || cds.lpData == IntPtr.Zero || Locate == null) return null;
            IntPtr p = cds.lpData;
            int which = Marshal.ReadInt32(p, 4);   // 1 = top left, 2 = bottom right
            // The window handle is 4 or 8 bytes wide depending on who sent it: try both.
            Icon? icon = null;
            foreach (var (hOff, idOff) in new[] { (16, 20), (16, 24) })
            {
                if (cds.cbData < idOff + 4) continue;
                var h = new IntPtr((uint)Marshal.ReadInt32(p, hOff));
                uint id = (uint)Marshal.ReadInt32(p, idOff);
                lock (_lock) icon = _icons.FirstOrDefault(i => i.Hwnd == h && i.Id == id);
                if (icon != null) break;
            }
            if (icon == null) return null;
            // The buttons live on the UI thread: ask it, but never wait long.
            CustomTaskbar.RECT? found = null;
            try
            {
                var locate = Locate;
                var which0 = icon;
                found = Application.Current?.Dispatcher.Invoke(() => locate?.Invoke(which0), DispatcherPriority.Send,
                                                               CancellationToken.None, TimeSpan.FromMilliseconds(400));
            }
            catch { }
            if (found is not { } r) return null;
            int x = which == 2 ? r.Right : r.Left, y = which == 2 ? r.Bottom : r.Top;
            return (IntPtr)((y << 16) | (x & 0xFFFF));
        }

        private static void SoonChanged()
        {
            if (Interlocked.Exchange(ref _changePending, 1) == 1) return;
            Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                Interlocked.Exchange(ref _changePending, 0);
                Changed?.Invoke();
            }));
        }

        private static void RemoveDead()
        {
            int gone;
            lock (_lock) gone = _icons.RemoveAll(i => !IsWindow(i.Hwnd));
            if (gone > 0) Changed?.Invoke();
        }

        private static ImageSource? ToImage(IntPtr hIcon)
        {
            // A copy: the app may destroy its icon right after.
            IntPtr copy = CopyIcon(hIcon);
            if (copy == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(copy, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
            finally { DestroyIcon(copy); }
        }

        private static string ExeOf(IntPtr hwnd)
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            return TaskbarApps.ProcessPathOf(pid);
        }

        // ---- Clicks (passed to the app the way Windows does) --------------------------------------------

        public static void Mouse(Icon i, uint mouseMsg)
        {
            if (i.Callback == 0 || !IsWindow(i.Hwnd)) return;
            GetWindowThreadProcessId(i.Hwnd, out uint pid);
            AllowSetForegroundWindow(pid);   // so its menu / window can come to the front
            if (i.Version >= 4)
            {
                GetCursorPos(out var pt);
                IntPtr wp = (IntPtr)(((pt.Y & 0xFFFF) << 16) | (pt.X & 0xFFFF));
                IntPtr lp = (IntPtr)(((int)(i.Id & 0xFFFF) << 16) | (int)(mouseMsg & 0xFFFF));
                SendNotifyMessage(i.Hwnd, i.Callback, wp, lp);
            }
            else SendNotifyMessage(i.Hwnd, i.Callback, (IntPtr)i.Id, (IntPtr)mouseMsg);
        }

        public static void LeftDown(Icon i, bool dbl) => Mouse(i, dbl ? WM_LBUTTONDBLCLK : WM_LBUTTONDOWN);

        public static void LeftUp(Icon i, bool dbl)
        {
            Mouse(i, WM_LBUTTONUP);
            if (!dbl && i.Version >= 3) Mouse(i, NIN_SELECT);
        }

        public static void RightDown(Icon i) => Mouse(i, WM_RBUTTONDOWN);

        public static void RightUp(Icon i)
        {
            Mouse(i, WM_RBUTTONUP);
            if (i.Version >= 3) Mouse(i, WM_CONTEXTMENU);
        }

        public static void MiddleClick(Icon i) { Mouse(i, WM_MBUTTONDOWN); Mouse(i, WM_MBUTTONUP); }

        public static void Hover(Icon i, bool enter)
        {
            Mouse(i, WM_MOUSEMOVE);
            if (i.Version >= 4 && !i.ShowTip) Mouse(i, enter ? NIN_POPUPOPEN : NIN_POPUPCLOSE);
        }

        // ---- Which icons show on the bar (the rest are under the arrow) ----------------------------

        private const string Key = @"Software\WinPlus\CustomTaskbar";
        private static HashSet<string> _shown = new(), _hidden = new();
        private static HashSet<string> _windowsPromoted = new();

        /// <summary>Shown on the bar? The user's choice, else what Windows shows on its own taskbar.</summary>
        public static bool OnBar(Icon i)
        {
            string k = i.Key;
            if (_shown.Contains(k)) return true;
            if (_hidden.Contains(k)) return false;
            return _windowsPromoted.Contains(Path.GetFileName(i.Exe).ToLowerInvariant());
        }

        public static void SetOnBar(Icon i, bool on)
        {
            string k = i.Key;
            _shown.Remove(k); _hidden.Remove(k);
            (on ? _shown : _hidden).Add(k);
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(Key);
                key.SetValue("TrayShown", _shown.ToArray(), RegistryValueKind.MultiString);
                key.SetValue("TrayHidden", _hidden.ToArray(), RegistryValueKind.MultiString);
            }
            catch { }
            Changed?.Invoke();
        }

        private static void LoadPromoted()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(Key);
                _shown = new HashSet<string>(key?.GetValue("TrayShown") as string[] ?? Array.Empty<string>());
                _hidden = new HashSet<string>(key?.GetValue("TrayHidden") as string[] ?? Array.Empty<string>());
            }
            catch { }
            // Windows 11 remembers which icons the user chose to show on its taskbar.
            _windowsPromoted = new HashSet<string>();
            try
            {
                using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
                if (root == null) return;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(name);
                    if (k?.GetValue("IsPromoted") is int p && p != 0 && k.GetValue("ExecutablePath") is string exe)
                        _windowsPromoted.Add(Path.GetFileName(exe).ToLowerInvariant());
                }
            }
            catch { }
        }

        // ---- Native ---------------------------------------------------------------------------------

        private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

        private const uint WM_COPYDATA = 0x004A, WM_COMMAND = 0x0111, WM_USER = 0x0400;
        private const uint WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_LBUTTONDBLCLK = 0x203,
                           WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_CONTEXTMENU = 0x7B;
        private const uint NIN_SELECT = 0x400, NIN_POPUPOPEN = 0x406, NIN_POPUPCLOSE = 0x407;
        private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
        private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_STATE = 0x8, NIF_GUID = 0x20, NIF_SHOWTIP = 0x80;
        private const uint NIS_HIDDEN = 0x1;
        private const uint WS_POPUP = 0x80000000, WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80;
        private const uint SMTO_ABORTIFHUNG = 2;
        private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_ASYNCWINDOWPOS = 0x4000;
        private static readonly IntPtr HWND_TOPMOST = new(-1), HWND_BROADCAST = new(0xFFFF);

        [StructLayout(LayoutKind.Sequential)]
        private struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

        [DllImport("user32.dll")] private static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG m);
        [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out CustomTaskbar.RECT r);
        [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] private static extern bool SendNotifyMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern IntPtr CopyIcon(IntPtr h);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint pid);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    }
}
