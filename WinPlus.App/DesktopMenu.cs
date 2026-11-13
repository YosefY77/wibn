using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinPlus
{
    /// <summary>
    /// Right-click on an empty spot of the desktop opens Wibn's own menu instead of Windows'
    /// (which can't be recolored): the same items (view, sort, refresh, paste, new, display
    /// settings, personalize, terminal), with the widgets' look: their glass and color.
    /// "Show more options" opens Windows' own menu. A right-click on an icon (or with Shift)
    /// is left to Windows.
    /// </summary>
    internal static class DesktopMenu
    {
        private const string Key = @"Software\WinPlus\Widgets";

        public static bool Enabled
        {
            get
            {
                try { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key); return !(k?.GetValue("DesktopMenu") is int v && v == 0); }
                catch { return true; }
            }
            set
            {
                try { using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key); k.SetValue("DesktopMenu", value ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord); } catch { }
                if (value && Features.IsOn("widgets")) Start(); else Stop();
            }
        }

        // ---- The mouse hook (its own thread, so it never waits for the UI) -------------------------

        private static Thread? _thread;
        private static uint _threadId;
        private static HookProc? _proc;
        private static volatile bool _swallowUp;

        public static void Start()
        {
            if (_thread != null || !Enabled) return;
            var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                _proc = Proc;
                IntPtr hook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _proc, GetModuleHandle(null), 0);
                ready.Set();
                while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
                if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            }) { IsBackground = true, Name = "Wibn desktop menu" };
            _thread.Start();
            ready.Wait(1000);
        }

        public static void Stop()
        {
            if (_thread == null) return;
            PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
            _thread = null;
            GlassMenu.CloseAll();
        }

        private static IntPtr Proc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                int msg = (int)wParam;
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                bool injected = (info.flags & 0x1) != 0;   // ours (going to Windows' menu) or another app's: let it be
                if (!injected && msg == 0x0204 /* WM_RBUTTONDOWN */ && (GetAsyncKeyState(0x10 /* Shift */) & 0x8000) == 0 && IsDesktop(WindowFromPoint(info.pt)))
                {
                    _swallowUp = true;
                    return (IntPtr)1;
                }
                if (msg == 0x0205 /* WM_RBUTTONUP */ && _swallowUp && !injected)
                {
                    _swallowUp = false;
                    var pt = info.pt;
                    Application.Current?.Dispatcher.BeginInvoke(() => OnRightClick(pt.X, pt.Y));
                    return (IntPtr)1;
                }
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        /// <summary>The desktop itself: its icon list, or the desktop behind it when the icons are hidden (not a window on it, not a widget).</summary>
        private static bool IsDesktop(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            string cls = ClassOf(h);
            if (cls == "SysListView32") { h = GetParent(h); cls = h == IntPtr.Zero ? "" : ClassOf(h); }
            if (cls == "SHELLDLL_DefView") { IntPtr top = GetParent(h); return top != IntPtr.Zero && ClassOf(top) is "Progman" or "WorkerW"; }
            return cls is "Progman" or "WorkerW" && FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero;
        }

        private static void OnRightClick(int x, int y)
        {
            try
            {
                if (OverIcon(x, y)) { WindowsMenu(x, y); return; }   // an icon: its own (Windows') menu
                GlassMenu.CloseAll();
                new GlassMenu(Items(x, y)).ShowAt(x, y);
            }
            catch (Exception ex)
            {
                CrashLog.Write("desktop menu", ex);
                WindowsMenu(x, y);
            }
        }

        /// <summary>Is there a desktop icon under that point? (Asks the desktop's accessibility info.)</summary>
        private static bool OverIcon(int x, int y)
        {
            try
            {
                if (AccessibleObjectFromPoint(new POINT { X = x, Y = y }, out Accessibility.IAccessible acc, out object child) != 0 || acc == null) return false;
                object role = acc.get_accRole(child);
                return role is int r && r == 0x22 /* ROLE_SYSTEM_LISTITEM */;
            }
            catch { return false; }
        }

        /// <summary>Windows' own menu at that spot (a right-click Windows gets this time).</summary>
        private static void WindowsMenu(int x, int y)
        {
            GlassMenu.CloseAll();
            SetCursorPos(x, y);
            var inputs = new[]
            {
                new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = 0x0008 /* RIGHTDOWN */ } },
                new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = 0x0010 /* RIGHTUP */ } },
            };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        // ---- The items ------------------------------------------------------------------------------

        private static List<GlassMenu.Entry> Items(int x, int y)
        {
            var view = DesktopIcons.ViewObject() as IFolderView2;
            int mode = 0, size = 48;
            uint flags = 0;
            try { view?.GetViewModeAndIconSize(out mode, out size); view?.GetCurrentFolderFlags(out flags); } catch { }
            IntPtr list = DesktopIcons.FindDesktopList();
            bool iconsShown = list != IntPtr.Zero && IsWindowVisible(list);
            bool auto = (flags & FWF_AUTOARRANGE) != 0, grid = (flags & FWF_SNAPTOGRID) != 0;
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

            var viewMenu = new List<GlassMenu.Entry>
            {
                new(Lang.T("أيقونات كبيرة"), "", () => SetSize(96)) { Checked = size >= 80 },
                new(Lang.T("أيقونات متوسطة"), "", () => SetSize(48)) { Checked = size is >= 40 and < 80 },
                new(Lang.T("أيقونات صغيرة"), "", () => SetSize(32)) { Checked = size < 40 },
                GlassMenu.Entry.Line,
                new(Lang.T("ترتيب الأيقونات تلقائياً"), "", () => SetFlag(FWF_AUTOARRANGE, !auto)) { Checked = auto },
                new(Lang.T("محاذاة الأيقونات مع الشبكة"), "", () => SetFlag(FWF_SNAPTOGRID, !grid)) { Checked = grid },
                GlassMenu.Entry.Line,
                new(Lang.T("إظهار أيقونات سطح المكتب"), "", ToggleIcons) { Checked = iconsShown },
            };
            var sortMenu = new List<GlassMenu.Entry>
            {
                new(Lang.T("الاسم"), "", () => Sort(10, 1)),
                new(Lang.T("الحجم"), "", () => Sort(12, -1)),
                new(Lang.T("نوع العنصر"), "", () => Sort(4, 1)),
                new(Lang.T("تاريخ التعديل"), "", () => Sort(14, -1)),
            };
            var newMenu = new List<GlassMenu.Entry>
            {
                new(Lang.T("مجلد"), "", () => NewFolder(desktop, x, y)),
                new(Lang.T("اختصار"), "", () => NewShortcut(desktop)),
                new(Lang.T("مستند نصي"), "", () => NewFile(desktop, Lang.T("مستند نصي جديد") + ".txt", x, y)),
            };

            bool canPaste = false;
            try { canPaste = Clipboard.ContainsFileDropList(); } catch { }

            var items = new List<GlassMenu.Entry>
            {
                new(Lang.T("عرض"), "") { Children = viewMenu },
                new(Lang.T("فرز حسب"), "") { Children = sortMenu },
                new(Lang.English ? "Refresh" : "تحديث", "", Refresh),
                GlassMenu.Entry.Line,
            };
            if (canPaste) items.Add(new(Lang.T("لصق"), "", () => Paste(desktop)));
            items.Add(new(Lang.T("جديد"), "") { Children = newMenu });
            items.Add(GlassMenu.Entry.Line);
            items.Add(new(Lang.T("إعدادات الشاشة"), "", () => Open("ms-settings:display")));
            items.Add(new(Lang.T("تخصيص"), "", () => Open("ms-settings:personalization")));
            items.Add(new(Lang.T("فتح في الطرفية"), "", () => Terminal(desktop)));
            items.Add(new(Lang.T("ويدجتات Wibn"), "", () => (Application.Current as App)?.ShowPage("widgets")));
            items.Add(GlassMenu.Entry.Line);
            items.Add(new(Lang.T("عرض المزيد من الخيارات"), "", () => WindowsMenu(x, y)));
            return items;
        }

        // ---- What they do ---------------------------------------------------------------------------

        private const uint FWF_AUTOARRANGE = 0x1, FWF_SNAPTOGRID = 0x4;

        private static void SetSize(int size)
        {
            if (DesktopIcons.ViewObject() is IFolderView2 v) v.SetViewModeAndIconSize(1 /* FVM_ICON */, size);
        }

        private static void SetFlag(uint flag, bool on)
        {
            if (DesktopIcons.ViewObject() is IFolderView2 v) v.SetCurrentFolderFlags(flag, on ? flag : 0);
        }

        private static void ToggleIcons()
        {
            IntPtr list = DesktopIcons.FindDesktopList();
            IntPtr def = list != IntPtr.Zero ? GetParent(list) : IntPtr.Zero;
            if (def != IntPtr.Zero) PostMessage(def, 0x0111 /* WM_COMMAND */, (IntPtr)0x7402, IntPtr.Zero);   // Windows' "Show desktop icons"
        }

        /// <summary>Sort by a property (System.* in the shell's main property set); icons get arranged by it once.</summary>
        private static void Sort(uint pid, int direction)
        {
            if (DesktopIcons.ViewObject() is not IFolderView2 v) return;
            var col = new SORTCOLUMN { key = new PROPERTYKEY { fmtid = new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"), pid = pid }, direction = direction };
            v.SetSortColumns(new[] { col }, 1);
            // Not auto-arranged: arrange once by it (auto-arrange on for a moment, like Windows does).
            v.GetCurrentFolderFlags(out uint flags);
            if ((flags & FWF_AUTOARRANGE) == 0)
            {
                v.SetCurrentFolderFlags(FWF_AUTOARRANGE, FWF_AUTOARRANGE);
                v.SetCurrentFolderFlags(FWF_AUTOARRANGE, 0);
            }
        }

        private static void Refresh()
        {
            IntPtr list = DesktopIcons.FindDesktopList();
            if (list == IntPtr.Zero) return;
            PostMessage(list, 0x0100 /* WM_KEYDOWN */, (IntPtr)0x74 /* F5 */, IntPtr.Zero);
            PostMessage(list, 0x0101 /* WM_KEYUP */, (IntPtr)0x74, IntPtr.Zero);
        }

        private static void Paste(string desktop)
        {
            var files = Clipboard.GetFileDropList().Cast<string>().ToList();
            if (files.Count == 0) return;
            bool move = false;
            try
            {
                if (Clipboard.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4)
                {
                    var b = new byte[4];
                    ms.Read(b, 0, 4);
                    move = (BitConverter.ToInt32(b, 0) & 2) != 0;   // DROPEFFECT_MOVE (cut)
                }
            }
            catch { }
            var op = new SHFILEOPSTRUCT
            {
                wFunc = move ? 0x1u /* FO_MOVE */ : 0x2u /* FO_COPY */,
                pFrom = string.Join("\0", files) + "\0\0",
                pTo = desktop + "\0\0",
                fFlags = 0x40 /* ALLOWUNDO */ | 0x8 /* RENAMEONCOLLISION */,
            };
            SHFileOperation(ref op);
        }

        private static string Unique(string dir, string name)
        {
            string path = Path.Combine(dir, name);
            string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
            for (int i = 2; File.Exists(path) || Directory.Exists(path); i++) path = Path.Combine(dir, $"{stem} ({i}){ext}");
            return path;
        }

        private static void NewFolder(string desktop, int x, int y)
        {
            string path = Unique(desktop, Lang.T("مجلد جديد"));
            Directory.CreateDirectory(path);
            DesktopIcons.PlaceNew(path, x, y);
        }

        private static void NewFile(string desktop, string name, int x, int y)
        {
            string path = Unique(desktop, name);
            File.WriteAllBytes(path, Array.Empty<byte>());
            DesktopIcons.PlaceNew(path, x, y);
        }

        private static void NewShortcut(string desktop)
        {
            // Windows' own "Create shortcut" wizard, into a new empty shortcut file (it fills or removes it).
            string path = Unique(desktop, Lang.T("اختصار جديد") + ".lnk");
            File.WriteAllBytes(path, Array.Empty<byte>());
            Process.Start(new ProcessStartInfo("rundll32.exe", $"appwiz.cpl,NewLinkHere \"{path}\"") { UseShellExecute = false });
        }

        private static void Open(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
        }

        private static void Terminal(string desktop)
        {
            try { Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{desktop}\"") { UseShellExecute = true }); }
            catch { try { Process.Start(new ProcessStartInfo("powershell.exe") { WorkingDirectory = desktop, UseShellExecute = true }); } catch { } }
        }

        // ---- Native ---------------------------------------------------------------------------------

        private static string ClassOf(IntPtr h)
        {
            var sb = new System.Text.StringBuilder(64);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        [ComImport, Guid("1af3a467-214f-4298-908e-06b03e0b39f9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFolderView2
        {
            // IFolderView
            void GetCurrentViewMode(out uint mode);
            void SetCurrentViewMode(uint mode);
            void GetFolder(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object folder);
            void Item(int index, out IntPtr pidl);
            void ItemCount(uint flags, out int count);
            void Items(uint flags, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object items);
            void GetSelectionMarkedItem(out int item);
            void GetFocusedItem(out int item);
            [PreserveSig] int GetItemPosition(IntPtr pidl, out POINT pt);
            void GetSpacing(ref POINT pt);
            void GetDefaultSpacing(out POINT pt);
            [PreserveSig] int GetAutoArrange();
            void SelectItem(int item, uint flags);
            void SelectAndPositionItems(uint count, IntPtr pidls, IntPtr points, uint flags);
            // IFolderView2
            void SetGroupBy(ref PROPERTYKEY key, [MarshalAs(UnmanagedType.Bool)] bool ascending);
            void GetGroupBy(out PROPERTYKEY key, [MarshalAs(UnmanagedType.Bool)] out bool ascending);
            void SetViewProperty(IntPtr pidl, ref PROPERTYKEY key, IntPtr value);
            void GetViewProperty(IntPtr pidl, ref PROPERTYKEY key, IntPtr value);
            void SetTileViewProperties(IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string list);
            void SetExtendedTileViewProperties(IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string list);
            void SetText(int type, [MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetCurrentFolderFlags(uint mask, uint flags);
            void GetCurrentFolderFlags(out uint flags);
            void GetSortColumnCount(out int count);
            void SetSortColumns([MarshalAs(UnmanagedType.LPArray)] SORTCOLUMN[] columns, int count);
            void GetSortColumns(IntPtr columns, int count);
            void GetItem(int item, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
            void GetVisibleItem(int start, [MarshalAs(UnmanagedType.Bool)] bool previous, out int item);
            void GetSelectedItem(int start, out int item);
            void GetSelection([MarshalAs(UnmanagedType.Bool)] bool noneImpliesFolder, [MarshalAs(UnmanagedType.IUnknown)] out object items);
            void GetSelectionState(IntPtr pidl, out uint flags);
            void InvokeVerbOnSelection([MarshalAs(UnmanagedType.LPStr)] string verb);
            void SetViewModeAndIconSize(int mode, int size);
            void GetViewModeAndIconSize(out int mode, out int size);
        }

        [StructLayout(LayoutKind.Sequential)] private struct PROPERTYKEY { public Guid fmtid; public uint pid; }
        [StructLayout(LayoutKind.Sequential)] private struct SORTCOLUMN { public PROPERTYKEY key; public int direction; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extraInfo; }
        [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; public uint lPrivate; }
        [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public MOUSEINPUT mi; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom, pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string? lpszProgressTitle;
        }

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int max);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromPoint(POINT pt, [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible acc, out object child);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
    }

    /// <summary>
    /// A menu in the widgets' look (their glass and color), with submenus that open beside it.
    /// It never takes the focus; a click anywhere else closes it.
    /// </summary>
    internal sealed class GlassMenu : Window
    {
        public sealed class Entry
        {
            public string Text = "", Glyph = "";
            public Action? Run;
            public bool Checked, IsLine;
            public List<Entry>? Children;
            public Entry() { }
            public Entry(string text, string glyph, Action? run = null) { Text = text; Glyph = glyph; Run = run; }
            public static Entry Line => new() { IsLine = true };
        }

        private static readonly List<GlassMenu> Open = new();
        private readonly GlassMenu? _parent;
        private GlassMenu? _child;
        private Border? _childRow;
        private readonly IntPtr _hwnd;
        private readonly Grid _host = new();
        private readonly Border _card = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(4), BorderThickness = new Thickness(1) };
        private readonly SolidColorBrush _ink = new(Colors.White);
        private readonly SolidColorBrush _hover = new(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(60) };
        private readonly DispatcherTimer _openSoon = new() { Interval = TimeSpan.FromMilliseconds(220) };
        private LiquidGlass? _glass;
        private double _scale = 1;
        private bool _closed;

        public GlassMenu(List<Entry> entries, GlassMenu? parent = null)
        {
            _parent = parent;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            FlowDirection = Lang.Flow;
            FontFamily = Widgets.Font();
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            _host.Children.Add(_card);
            Content = _host;

            _hwnd = new WindowInteropHelper(this).EnsureHandle();
            long ex = GetWindowLongPtr(_hwnd, -20).ToInt64();
            SetWindowLongPtr(_hwnd, -20, (IntPtr)(ex | 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */));
            var src = HwndSource.FromHwnd(_hwnd);
            src.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == 0x21 /* WM_MOUSEACTIVATE */) { handled = true; return (IntPtr)3; }
                return IntPtr.Zero;
            });
            _scale = src.CompositionTarget.TransformToDevice.M11;

            ApplyLook();
            Fill(entries);
            _openSoon.Tick += (_, _) => { _openSoon.Stop(); OpenPending(); };
            _watch.Tick += (_, _) => Watch();
            Loaded += (_, _) => StartGlass();
            Open.Add(this);
        }

        public static void CloseAll()
        {
            foreach (var m in Open.ToList()) m.CloseIt();
        }

        // ---- Look: the widgets' ---------------------------------------------------------------------

        private void ApplyLook()
        {
            var back = Widgets.BackColor();
            _card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            _card.Background = back is { } b
                ? new SolidColorBrush(Color.FromArgb(0xF0, b.Color.R, b.Color.G, b.Color.B))
                : new SolidColorBrush(Color.FromArgb(0xE8, 0x1E, 0x1F, 0x22));
            SetInk(back is { } l && Widgets.Lightness(l.Color) > 0.6);
        }

        private void SetInk(bool lightBehind)
        {
            _ink.Color = lightBehind ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xF5, 0xF5, 0xF7);
            _hover.Color = lightBehind ? Color.FromArgb(0x1E, 0, 0, 0) : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF);
        }

        private void StartGlass()
        {
            if (_closed || !GlassSettings.Widgets.On) return;
            try
            {
                // The desktop behind it, like the widgets' glass (it opens over the desktop).
                var glass = new LiquidGlass(this, _host, _card, new Thickness(0), 10, GlassSettings.Widgets, desktop: true, tint: Widgets.BackColor());
                if (!glass.Available) return;
                _glass = glass;
                glass.LightBehindChanged += light => SetInk(light);
                glass.Start();
            }
            catch (Exception ex) { CrashLog.Write("desktop menu glass", ex); }
        }

        // ---- Rows -----------------------------------------------------------------------------------

        private Entry? _pending;
        private Border? _pendingRow;

        private void Fill(List<Entry> entries)
        {
            var stack = new StackPanel { MinWidth = 210 };
            bool anyCheck = entries.Any(e => e.Checked || (e.Children == null && e.Glyph.Length == 0 && !e.IsLine));
            foreach (var e in entries)
            {
                if (e.IsLine)
                {
                    stack.Children.Add(new Border { Height = 1, Margin = new Thickness(8, 4, 8, 4), Background = _ink, Opacity = 0.15 });
                    continue;
                }
                var row = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 6, 10, 6), Background = Brushes.Transparent, Cursor = Cursors.Hand };
                var dock = new DockPanel();
                string mark = e.Checked ? "" : e.Glyph;
                var glyph = new TextBlock { Text = mark, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, Width = 22, Foreground = _ink, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(glyph, Dock.Left);
                if (mark.Length > 0 || anyCheck) dock.Children.Add(glyph);
                if (e.Children != null)
                {
                    var arrow = new TextBlock { Text = FlowDirection == FlowDirection.RightToLeft ? "" : "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10, Foreground = _ink, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
                    DockPanel.SetDock(arrow, Dock.Right);
                    dock.Children.Add(arrow);
                }
                dock.Children.Add(new TextBlock { Text = e.Text, Foreground = _ink, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
                row.Child = dock;

                var entry = e;
                row.MouseEnter += (_, _) =>
                {
                    row.Background = _hover;
                    if (_childRow == row) { _openSoon.Stop(); return; }
                    _pending = entry; _pendingRow = row;
                    _openSoon.Stop(); _openSoon.Start();   // open (or close) the submenu after a short pause
                };
                row.MouseLeave += (_, _) => { if (_childRow != row) row.Background = Brushes.Transparent; };
                row.MouseLeftButtonUp += (_, ev) =>
                {
                    ev.Handled = true;
                    if (entry.Children != null) { _pending = entry; _pendingRow = row; OpenPending(); return; }
                    CloseAll();
                    try { entry.Run?.Invoke(); } catch (Exception ex) { CrashLog.Write("desktop menu item", ex); }
                };
                stack.Children.Add(row);
            }
            _card.Child = stack;
        }

        private void OpenPending()
        {
            if (_closed || _pendingRow == null || _pending == null) return;
            if (_childRow == _pendingRow) return;
            if (_child != null) { _child.CloseIt(); _child = null; }
            if (_childRow != null) _childRow.Background = Brushes.Transparent;
            _childRow = null;
            if (_pending.Children == null) return;
            _childRow = _pendingRow;
            _childRow.Background = _hover;
            _child = new GlassMenu(_pending.Children, this);
            // Beside this row: to the side the text runs toward, or the other one if there's no room.
            var a = _childRow.PointToScreen(new Point(0, 0));
            var z = _childRow.PointToScreen(new Point(_childRow.ActualWidth, _childRow.ActualHeight));
            double left = Math.Min(a.X, z.X), right = Math.Max(a.X, z.X), top = Math.Min(a.Y, z.Y);
            _child.ShowBeside(left - 6 * _scale, right + 6 * _scale, top - 5 * _scale, FlowDirection == FlowDirection.RightToLeft);
        }

        // ---- Place ----------------------------------------------------------------------------------

        /// <summary>The menu's corner at the mouse (screen pixels), kept on the screen.</summary>
        public void ShowAt(int x, int y)
        {
            Show();
            UpdateLayout();
            double w = ActualWidth * _scale, h = ActualHeight * _scale;
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y)).WorkingArea;
            bool rtl = FlowDirection == FlowDirection.RightToLeft;
            double px = rtl ? x - w : x;
            if (px + w > screen.Right) px = x - w;
            if (px < screen.Left) px = x;
            double py = y + h > screen.Bottom ? y - h : y;
            px = Math.Clamp(px, screen.Left, Math.Max(screen.Left, screen.Right - w));
            py = Math.Clamp(py, screen.Top, Math.Max(screen.Top, screen.Bottom - h));
            Left = px / _scale;
            Top = py / _scale;
            _watch.Start();
        }

        private void ShowBeside(double left, double right, double top, bool towardLeft)
        {
            Show();
            UpdateLayout();
            double w = ActualWidth * _scale, h = ActualHeight * _scale;
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)left, (int)top)).WorkingArea;
            double px = towardLeft ? left - w : right;
            if (px < screen.Left) px = right;
            if (px + w > screen.Right) px = left - w;
            double py = Math.Clamp(top, screen.Top, Math.Max(screen.Top, screen.Bottom - h));
            Left = px / _scale;
            Top = py / _scale;
            _watch.Start();
        }

        // ---- Closing --------------------------------------------------------------------------------

        private void Watch()
        {
            if (_closed || _parent != null) return;   // the first menu watches for all of them
            bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0 || (GetAsyncKeyState(0x04) & 0x8000) != 0;
            bool esc = (GetAsyncKeyState(0x1B) & 0x8000) != 0;
            if (esc || (down && !Open.Any(m => m.IsMouseOver))) CloseAll();
        }

        public void CloseIt()
        {
            if (_closed) return;
            _closed = true;
            _watch.Stop();
            _openSoon.Stop();
            _child?.CloseIt();
            _glass?.Dispose();
            _glass = null;
            Open.Remove(this);
            try { Close(); } catch { }
        }

        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    }
}
