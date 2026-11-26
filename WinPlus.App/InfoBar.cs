using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// The info bar: an extra bar on any edge of any screen (bottom, top, left or right) that shows
    /// the time, the date, the next prayer's countdown, the processor's and graphics card's use and
    /// heat, the memory and more. Each one can be shown or hidden.
    ///
    /// It's Wibn's taskbar in every way but what's on it: the same shape (full, floating, dock), look
    /// (glass, color, see-through), color and thickness, taken from the taskbar's settings; its ends
    /// are dragged to make it shorter or longer (a double-click there: full length again); pressing on
    /// it and dragging moves it to another edge or screen; it slides away for full-screen apps and
    /// comes back with the mouse at its edge. It keeps its room: maximized windows stop at it.
    /// Works with or without Wibn's own taskbar.
    /// </summary>
    internal static class InfoBar
    {
        internal const string Key = @"Software\WinPlus\InfoBar";

        // ---- Settings ---------------------------------------------------------------------------

        internal static int GetIn(string key, string name, int fallback)
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(key); return k?.GetValue(name) is int v ? v : fallback; }
            catch { return fallback; }
        }

        internal static void SetIn(string key, string name, int value)
        {
            try { using var k = Registry.CurrentUser.CreateSubKey(key); k.SetValue(name, value, RegistryValueKind.DWord); } catch { }
        }

        internal static string GetSIn(string key, string name, string fallback)
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(key); return k?.GetValue(name) as string ?? fallback; }
            catch { return fallback; }
        }

        internal static void SetSIn(string key, string name, string value)
        {
            try { using var k = Registry.CurrentUser.CreateSubKey(key); k.SetValue(name, value, RegistryValueKind.String); } catch { }
        }

        private static int Get(string name, int fallback) => GetIn(Key, name, fallback);
        private static void Set(string name, int value) => SetIn(Key, name, value);
        private static string GetS(string name, string fallback) => GetSIn(Key, name, fallback);
        private static void SetS(string name, string value) => SetSIn(Key, name, value);

        /// <summary>The extra bars are on (all of them).</summary>
        public static bool Enabled { get => Get("Enabled", 0) != 0; set { Set("Enabled", value ? 1 : 0); Apply(); } }

        /// <summary>Seconds in its clock: with Wibn's taskbar on, the taskbar's own setting (one switch for both clocks).</summary>
        public static bool Seconds { get => CustomTaskbar.Enabled ? CustomTaskbar.Seconds : Get("Seconds", 0) != 0; set { Set("Seconds", value ? 1 : 0); RebuildAll(); } }

        /// <summary>The prayer times' city (by default the prayer widget's, if there's one).</summary>
        public static PrayerWidget.City City
        {
            get
            {
                string name = GetS("City", "");
                if (name == "")
                {
                    try { name = Widgets.All.FirstOrDefault(i => i.Options.ContainsKey("city"))?.Options["city"] ?? ""; } catch { }
                    if (name == "الكويت") name = "مدينة الكويت";
                }
                return PrayerWidget.Cities.FirstOrDefault(c => c.Name == name) ?? PrayerWidget.Cities[0];
            }
            set { SetS("City", value.Name); RebuildAll(); }
        }

        public sealed record Item(string Id, string Name, string Description, bool On);

        /// <summary>Everything a bar can show, in its order on the bar.</summary>
        public static readonly Item[] Items =
        {
            new("clock", "الساعة", "الوقت الحين", true),
            new("date", "التاريخ", "اليوم والتاريخ الميلادي", true),
            new("hijri", "التاريخ الهجري", "بتقويم أم القرى", false),
            new("prayer", "مؤقت الصلاة", "كم باقي على الصلاة الجاية ووقتها", true),
            new("cpu", "استهلاك المعالج", "كم بالمية المعالج شغّال", true),
            new("cputemp", "حرارة المعالج", "تحتاج برنامج LibreHardwareMonitor شغّال في أغلب الأجهزة المكتبية", true),
            new("gpu", "استهلاك كرت الشاشة", "كم بالمية كرت الشاشة شغّال", true),
            new("gputemp", "حرارة كرت الشاشة", "من ويندوز نفسه، مثل إدارة المهام", true),
            new("ram", "استهلاك الرام", "كم مستخدم من الرام ومن كم", true),
            new("net", "سرعة النت", "التنزيل والرفع الحين", false),
            new("disk", "مساحة القرص", "كم مستخدم من قرص الويندوز", false),
        };

        /// <summary>The widgets a bar can have (a click on one opens it above the bar). Not the desktop-only ones.</summary>
        public static IEnumerable<Widgets.Kind> WidgetKinds => Widgets.Kinds.Where(k => k.Type is not ("text" or "picture"));

        /// <summary>A setting changed (the settings page follows).</summary>
        public static event Action? Changed;

        // ---- One bar's settings ------------------------------------------------------------------

        /// <summary>
        /// One extra bar's own settings. The first one (0) keeps the old place in the registry
        /// (Software\WinPlus\InfoBar), the others have a key of their own under it (Bar2, Bar3…).
        /// </summary>
        public sealed class Bar
        {
            public readonly int Id;
            public readonly string Key;

            public Bar(int id)
            {
                Id = id;
                Key = id == 0 ? InfoBar.Key : InfoBar.Key + @"\Bar" + id;
            }

            private int Get(string name, int fallback) => GetIn(Key, name, fallback);
            private void Set(string name, int value) => SetIn(Key, name, value);
            private string GetS(string name, string fallback) => GetSIn(Key, name, fallback);
            private void SetS(string name, string value) => SetSIn(Key, name, value);

            /// <summary>Its number for people (1, 2, 3…): its place in the list.</summary>
            public int Number => Math.Max(1, BarIds.IndexOf(Id) + 1);

            /// <summary>0 = bottom, 1 = top, 2 = left, 3 = right (like <see cref="CustomTaskbar.Edge"/>).</summary>
            public int Edge { get => Math.Clamp(Get("Edge", 3), 0, 3); set { Set("Edge", Math.Clamp(value, 0, 3)); Rebuild(); } }

            /// <summary>A point on the chosen screen (screen pixels); none = the main screen.</summary>
            public POINT? ScreenPoint
            {
                get
                {
                    int x = Get("ScreenX", int.MinValue), y = Get("ScreenY", int.MinValue);
                    return x == int.MinValue || y == int.MinValue ? null : new POINT { X = x, Y = y };
                }
            }

            internal void SetScreen(POINT onScreen) { Set("ScreenX", onScreen.X); Set("ScreenY", onScreen.Y); }

            /// <summary>Puts it on this edge of the screen that has this point, at this end of it (after a drag).
            /// False when that edge has no room left for it.</summary>
            public bool MoveTo(int edge, POINT onScreen, int side)
            {
                if (!HasRoom(edge, onScreen, Id)) return false;
                SetScreen(onScreen);
                Set("Side", side);
                Edge = edge;
                return true;
            }

            /// <summary>Beside a piece of Wibn's taskbar: 0 = the start (left / top), 1 = the end (right / bottom).</summary>
            public int Side { get => Math.Clamp(Get("Side", 0), 0, 1); set { Set("Side", Math.Clamp(value, 0, 1)); Rebuild(); } }

            /// <summary>Beside a taskbar piece: how long it is at least (DIPs; 0 = as long as what's on it).</summary>
            public int ShareLength { get => Math.Max(0, Get("ShareLength", 0)); set { Set("ShareLength", Math.Max(0, value)); Rebuild(); } }

            /// <summary>How much shorter it is at each end (DIPs; 0 = as long as the screen allows).</summary>
            public int Inset { get => Math.Max(0, Get("Inset", 0)); set { Set("Inset", Math.Max(0, value)); Rebuild(); } }

            public bool Shows(string id)
            {
                var item = Items.First(i => i.Id == id);
                // A new bar starts with the clock only; the first one with the usual set.
                return Get("Show_" + id, (Id == 0 ? item.On : item.Id == "clock") ? 1 : 0) != 0;
            }

            public void SetShows(string id, bool on)
            {
                Set("Show_" + id, on ? 1 : 0);
                Rebuild();
            }

            /// <summary>The widgets on it (their types), in order.</summary>
            public List<string> WidgetTypes =>
                GetS("Widgets", "").Split(',', StringSplitOptions.RemoveEmptyEntries).Where(t => Widgets.KindOf(t) != null).ToList();

            public bool HasWidget(string type) => WidgetTypes.Contains(type);

            public void SetWidget(string type, bool on)
            {
                var list = WidgetTypes;
                list.Remove(type);
                if (on) list.Add(type);
                SetS("Widgets", string.Join(",", list));
                Rebuild();
            }

            /// <summary>A widget's saved options on this bar (its city, its shortcuts…), its own, apart from the desktop's.</summary>
            public Widgets.Info WidgetInfo(string type)
            {
                try
                {
                    string json = GetS("Widget_" + type, "");
                    if (json.Length > 0 && System.Text.Json.JsonSerializer.Deserialize<Widgets.Info>(json) is { } info) return info;
                }
                catch { }
                var kind = Widgets.KindOf(type);
                return new Widgets.Info { Type = type, W = kind?.W ?? 3, H = kind?.H ?? 3, Placed = true };
            }

            public void SaveWidgetInfo(Widgets.Info info)
            {
                try { string json = System.Text.Json.JsonSerializer.Serialize(info); if (GetS("Widget_" + info.Type, "") != json) SetS("Widget_" + info.Type, json); } catch { }
            }

            /// <summary>Was it beside a piece of Wibn's taskbar last time? (At start-up it waits for that piece.)</summary>
            internal bool WasShared { get => Get("WasShared", 0) != 0; set => Set("WasShared", value ? 1 : 0); }

            // The app bar (the room kept for it) is written down, so it's taken back after a crash.
            internal void RememberAppBar(IntPtr hwnd) => Set("AppBar", (int)hwnd.ToInt64());

            internal void RemoveLeftAppBar()
            {
                int h = Get("AppBar", 0);
                if (h == 0) return;
                var abd = new CustomTaskbar.APPBARDATA { cbSize = Marshal.SizeOf<CustomTaskbar.APPBARDATA>(), hWnd = new IntPtr(h) };
                CustomTaskbar.SHAppBarMessage(1 /* ABM_REMOVE */, ref abd);
                Set("AppBar", 0);
            }

            private void Rebuild()
            {
                if (_bars.TryGetValue(Id, out var w)) w.Rebuild();
                Changed?.Invoke();
            }
        }

        /// <summary>The bars, in order (their ids). The first one is 0.</summary>
        public static List<int> BarIds
        {
            get
            {
                var ids = new List<int>();
                foreach (var s in GetS("Bars", "0").Split(',', StringSplitOptions.RemoveEmptyEntries))
                    if (int.TryParse(s, out int id) && id >= 0 && !ids.Contains(id)) ids.Add(id);
                if (ids.Count == 0) ids.Add(0);
                return ids;
            }
            private set => SetS("Bars", string.Join(",", value));
        }

        public static List<Bar> Bars => BarIds.Select(id => new Bar(id)).ToList();

        // ---- Room ---------------------------------------------------------------------------------

        /// <summary>
        /// Is there room for one more bar (or this one, moved) on this edge of the screen that has this point?
        /// The windows must keep at least half the screen each way. Bars sitting beside a piece of Wibn's taskbar
        /// (on its line, side by side) take none of the windows' room, so they don't count.
        /// </summary>
        public static bool HasRoom(int edge, POINT onScreen, int? except = null)
        {
            if (!MonitorOf(onScreen, out var m, out double scale)) return true;
            var others = Bars.Where(b => b.Id != except && Same(MonitorPointOf(b), m)
                                         && !(_bars.TryGetValue(b.Id, out var w) && w.SharedPlaced != null)).ToList();
            bool vertical = edge >= 2;
            double used = others.Where(b => (b.Edge >= 2) == vertical).Sum(b => ThickPx(b.Edge, scale)) + ThickPx(edge, scale);
            // Wibn's taskbar on that axis takes some too.
            if (CustomTaskbar.Running && (CustomTaskbar.Edge >= 2) == vertical) used += CustomTaskbar.ThicknessDip * scale;
            double size = vertical ? m.Right - m.Left : m.Bottom - m.Top;
            return size - used >= size / 2;
        }

        private static double ThickPx(int edge, double scale) =>
            (edge >= 2 ? CustomTaskbar.ThicknessDip + 40 : CustomTaskbar.ThicknessDip) * scale;

        private static CustomTaskbar.RECT MonitorPointOf(Bar b)
        {
            MonitorOf(b.ScreenPoint, out var m, out _);
            return m;
        }

        private static bool Same(CustomTaskbar.RECT a, CustomTaskbar.RECT b) =>
            a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

        internal static bool MonitorOf(POINT? at, out CustomTaskbar.RECT monitor, out double scale)
        {
            IntPtr mon = at is { } p ? MonitorFromPoint(p, 2 /* NEAREST */) : MonitorFromPoint(new POINT(), 1 /* PRIMARY */);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            monitor = default;
            scale = 1;
            if (!GetMonitorInfo(mon, ref mi)) return false;
            monitor = mi.rcMonitor;
            scale = GetDpiForMonitor(mon, 0, out uint dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;
            return true;
        }

        /// <summary>
        /// One more bar, on the screen the mouse is on, on the edge with the fewest bars that still has room.
        /// False (nothing added) when no edge has room left.
        /// </summary>
        public static bool Add()
        {
            GetCursorPos(out var c);
            if (!MonitorOf(c, out var m, out _)) return false;
            var center = new POINT { X = (m.Left + m.Right) / 2, Y = (m.Top + m.Bottom) / 2 };
            var bars = Bars.Where(b => Same(MonitorPointOf(b), m)).ToList();
            int[] order = { 3, 2, 1, 0 };   // right, left, top, bottom
            int edge = -1;
            foreach (int e in order.OrderBy(e => bars.Count(b => b.Edge == e)))
                if (HasRoom(e, center)) { edge = e; break; }
            if (edge < 0) return false;

            var ids = BarIds;
            int id = Enumerable.Range(2, 1000).First(i => !ids.Contains(i));
            var bar = new Bar(id);
            try { Registry.CurrentUser.DeleteSubKeyTree(bar.Key, false); } catch { }   // nothing left from an old one
            bar.SetScreen(center);
            SetIn(bar.Key, "Edge", edge);
            ids.Add(id);
            BarIds = ids;
            if (Enabled) Apply(); else Enabled = true;
            return true;
        }

        /// <summary>
        /// Moves this bar earlier (-1) or later (+1) in the order. On the same edge the earlier one sits nearer the
        /// screen's edge (the later ones further in), and on a taskbar piece's line nearer the line's end.
        /// The bars are put up again in the new order.
        /// </summary>
        public static bool MoveInOrder(int id, int by)
        {
            var ids = BarIds;
            int i = ids.IndexOf(id), j = i + by;
            if (i < 0 || j < 0 || j >= ids.Count) return false;
            (ids[i], ids[j]) = (ids[j], ids[i]);
            BarIds = ids;
            // Their room is taken again in the new order (Windows stacks app bars in the order they're made).
            foreach (int b in _bars.Keys.ToList()) StopBar(b);
            Apply();
            return true;
        }

        /// <summary>Takes this bar away (the last one: the extra bars go off instead, keeping its settings).</summary>
        public static void Remove(int id)
        {
            var ids = BarIds;
            if (ids.Count <= 1) { Enabled = false; return; }
            ids.Remove(id);
            BarIds = ids;
            StopBar(id);
            if (id != 0) try { Registry.CurrentUser.DeleteSubKeyTree(new Bar(id).Key, false); } catch { }
            Apply();
        }

        // ---- Running ----------------------------------------------------------------------------

        private static readonly Dictionary<int, InfoBarWindow> _bars = new();
        private static bool _following;

        public static bool Running => _bars.Count > 0;

        /// <summary>The running bars, in their order.</summary>
        internal static List<InfoBarWindow> Windows => BarIds.Where(_bars.ContainsKey).Select(id => _bars[id]).ToList();

        /// <summary>The bars before this one (they're placed first; this one goes beside or further in from them).</summary>
        internal static List<InfoBarWindow> Before(InfoBarWindow w)
        {
            var all = Windows;
            int i = all.IndexOf(w);
            return i <= 0 ? new List<InfoBarWindow>() : all.GetRange(0, i);
        }

        /// <summary>Where the bars beside a piece of Wibn's taskbar are (screen pixels).</summary>
        /// <summary>"Hide when a window is under it" was turned on or off: every bar looks again.</summary>
        internal static void CoverSettingChanged()
        {
            foreach (var w in Windows) try { w.CheckCover(); } catch (Exception ex) { CrashLog.Write("info bar cover", ex); }
        }

        internal static IEnumerable<CustomTaskbar.RECT> SharedRects => Windows.Select(w => w.SharedPlaced).Where(r => r != null).Select(r => r!.Value);

        /// <summary>Starts or stops the bars to match the settings. Also called once at start-up.</summary>
        public static void Apply()
        {
            if (!_following)
            {
                // The taskbar's look changed (shape, color, thickness…): the bars take it too.
                _following = true;
                CustomTaskbar.Changed += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { foreach (var w in Windows) w.Rebuild(); });
            }
            bool want = Enabled && Features.IsOn("taskbar");
            var ids = BarIds;
            foreach (int id in _bars.Keys.ToList())
                if (!want || !ids.Contains(id)) StopBar(id);
            foreach (int id in ids)
            {
                var bar = new Bar(id);
                if (!want) { bar.RemoveLeftAppBar(); continue; }
                if (_bars.ContainsKey(id)) continue;
                bar.RemoveLeftAppBar();   // from a crash last time
                try
                {
                    var w = new InfoBarWindow(bar);
                    _bars[id] = w;
                    w.Start();
                    CrashLog.Note("info bar on: " + id);
                }
                catch (Exception ex) { CrashLog.Write("info bar", ex); _bars.Remove(id); }
            }
            Changed?.Invoke();
        }

        private static void StopBar(int id)
        {
            if (!_bars.TryGetValue(id, out var w)) return;
            _bars.Remove(id);
            try { w.CloseBar(); } catch (Exception ex) { CrashLog.Write("info bar close", ex); }
            CustomTaskbar.WidgetsFollowSoon();
        }

        /// <summary>Gone (Wibn is closing): the windows get their room back.</summary>
        public static void Stop()
        {
            foreach (int id in _bars.Keys.ToList()) StopBar(id);
        }

        internal static void RebuildAll()
        {
            foreach (var w in Windows) w.Rebuild();
            Changed?.Invoke();
        }

        /// <summary>The Liquid Glass settings changed.</summary>
        public static void RefreshGlass() { foreach (var w in Windows) w.Rebuild(); }

        /// <summary>
        /// The windows' room on this screen without the bars' strips (Wibn's taskbar sets the work area
        /// itself in some modes, so it asks here first).
        /// </summary>
        internal static CustomTaskbar.RECT TrimWorkArea(CustomTaskbar.RECT monitor, CustomTaskbar.RECT target)
        {
            foreach (var bar in Windows)
            {
                if (!bar.Reserving) continue;
                var p = bar.Strip;
                if (p.Width <= 0 || p.Left < monitor.Left - 1 || p.Right > monitor.Right + 1 || p.Top < monitor.Top - 1 || p.Bottom > monitor.Bottom + 1) continue;
                switch (bar.PlacedEdge)
                {
                    case 1: target.Top = Math.Max(target.Top, p.Bottom); break;
                    case 2: target.Left = Math.Max(target.Left, p.Right); break;
                    case 3: target.Right = Math.Min(target.Right, p.Left); break;
                    default: target.Bottom = Math.Min(target.Bottom, p.Top); break;
                }
            }
            return target;
        }

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public CustomTaskbar.RECT rcMonitor, rcWork; public uint dwFlags; }
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT p, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    }

    /// <summary>The info bar's window (built like <see cref="TaskbarBar"/>).</summary>
    internal sealed class InfoBarWindow : Window
    {
        private sealed class Chip
        {
            public string Id = "";
            public Border Root = null!;
            public TextBlock Label = null!, Value = null!, Sub = null!;
        }

        private readonly IntPtr _hwnd;
        private readonly Grid _host = new();
        private readonly Border _card = new();
        private readonly StackPanel _list = new();
        private readonly List<Chip> _chips = new();
        private readonly DispatcherTimer _tick, _follow;
        // The same ink as the taskbar's: white, or dark on a light background.
        private readonly SolidColorBrush _ink = new(Colors.White), _muted = new(Colors.White), _line = new(Colors.White),
                                         _hover = new(Colors.Transparent), _pressed = new(Colors.Transparent);
        private static readonly SolidColorBrush Hot = new(Color.FromRgb(0xFF, 0x6B, 0x5F));
        private static readonly SolidColorBrush Warm = new(Color.FromRgb(0xFF, 0xC1, 0x4D));
        private LiquidGlass? _glass;
        private readonly Sensors _sensors = new();
        private int _sampling;
        private Sensors.Reading? _last;
        private bool _closed;
        private int _edge;
        private double _scale = 1, _radius;
        private CustomTaskbar.RECT _monitor, _strip, _full, _placed;

        /// <summary>The strip kept for it (its room), in screen pixels.</summary>
        public CustomTaskbar.RECT Strip => _strip;
        public int PlacedEdge => _edge;
        /// <summary>Its room is kept (also while it's slid away for a full-screen app, so the windows don't jump).</summary>
        public bool Reserving => _appBar;

        /// <summary>This bar's own settings.</summary>
        public readonly InfoBar.Bar S;

        public InfoBarWindow(InfoBar.Bar bar)
        {
            S = bar;
            Title = "Wibn info bar";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            UseLayoutRounding = true;
            FlowDirection = Lang.Flow;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            Foreground = _ink;
            // Off screen until it's placed (no flash in the corner).
            Left = -32000; Top = -32000; Width = 10; Height = 10;

            _card.Child = _list;
            _host.Children.Add(_hidden);
            _host.Children.Add(_card);
            Content = _host;

            _hwnd = new WindowInteropHelper(this).EnsureHandle();
            TaskbarApps.Ours.Add(_hwnd);
            long ex = GetWindowLongPtr(_hwnd, -20).ToInt64();
            SetWindowLongPtr(_hwnd, -20, (IntPtr)(ex | 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */));
            int yes = 1;
            DwmSetWindowAttribute(_hwnd, 12 /* DWMWA_EXCLUDED_FROM_PEEK */, ref yes, 4);
            HwndSource.FromHwnd(_hwnd)?.AddHook(Hook);

            _tick = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
            _tick.Tick += (_, _) => { try { Tick(); } catch (Exception ex2) { CrashLog.Write("info bar tick", ex2); } };
            _reveal = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(40) };
            _reveal.Tick += (_, _) => RevealTick();
            // Beside a taskbar piece: it follows it closely (the piece gets longer, goes away for full screen…).
            _follow = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(120) };
            _follow.Tick += (_, _) => { try { if ((_share != null || Waiting) && !_moving && !_resizing) Place(); } catch (Exception ex3) { CrashLog.Write("info bar follow", ex3); } };
            _cover = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(150) };
            _cover.Tick += (_, _) => { try { CheckCover(); } catch (Exception ex4) { CrashLog.Write("info bar cover", ex4); } };

            HookEndDrag();
            HookMoveDrag();
            ContextMenuOpening += (_, e) => e.Handled = true;
            MouseRightButtonUp += (_, e) => { e.Handled = true; ShowMenu(); };
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            SetInk(false);
        }

        private void OnDisplayChanged(object? s, EventArgs e) => Dispatcher.BeginInvoke(() => { if (!_closed) Rebuild(); });

        /// <summary>Its place beside a taskbar piece (null = on a line of its own).</summary>
        internal CustomTaskbar.RECT? SharedPlaced => _share != null ? _placed : null;

        /// <summary>At start-up, until this time, it stays hidden while the taskbar piece it sat beside isn't up yet.</summary>
        private DateTime _waitShareUntil = DateTime.MinValue;
        private bool Waiting => DateTime.UtcNow < _waitShareUntil;

        public void Start()
        {
            if (S.WasShared && CustomTaskbar.Running) _waitShareUntil = DateTime.UtcNow.AddSeconds(20);
            Show();
            Rebuild();
            _tick.Start();
            _follow.Start();
            _cover.Start();
        }

        public void CloseBar()
        {
            _closed = true;
            _tick.Stop();
            _follow.Stop();
            _cover.Stop();
            _reveal.Stop();
            StopSlide();
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            _glass?.Dispose();
            _glass = null;
            _edgePreview?.Close();
            RemoveAppBar();
            TaskbarApps.Ours.Remove(_hwnd);
            _sensors.Dispose();
            foreach (var type in _widgets.Keys.ToList()) StopWidget(type);
            Close();
        }

        // ---- Building (the taskbar's look) --------------------------------------------------------

        private bool Vertical => _edge >= 2;
        /// <summary>Beside a taskbar piece it's a floating piece too (like a split taskbar's).</summary>
        private int Shape => _share != null ? 1 : CustomTaskbar.Shape;
        // Text like the taskbar's clock, a little bigger or smaller with the taskbar's icon size.
        private double ValueSize => (Vertical ? 11.5 : 12.5) + (CustomTaskbar.IconSize - 1);
        private double SmallSize => (Vertical ? 10 : 11) + (CustomTaskbar.IconSize - 1) * 0.5;
        /// <summary>How thick the strip is (DIPs): the taskbar's thickness (a side bar wider, for the words), or the line it shares.</summary>
        private double ThickDip => _share is { } s ? s.ThickDip : Vertical ? CustomTaskbar.ThicknessDip + 40 : CustomTaskbar.ThicknessDip;

        // ---- Beside a piece of Wibn's taskbar, on the same line ----------------------------------------

        /// <summary>The line it shares (screen pixels), the free part it goes in (along the edge), and whether the taskbar shows.</summary>
        private sealed record Share(CustomTaskbar.RECT Band, int SegStart, int SegEnd, int Side, double ThickDip, bool Shown,
                                    bool Own = false, bool Lead = false);   // Own: a line of info bars only (see OwnLine)
        private Share? _share;
        private string _shareKey = "";
        private DateTime? _shareLostAt;

        private static string KeyOf(Share? s) => s == null ? "-" : Math.Round(s.ThickDip).ToString(CultureInfo.InvariantCulture);

        /// <summary>The screen it's on (and its scale).</summary>
        private bool ScreenOf(out MONITORINFO mi, out IntPtr mon)
        {
            var at = S.ScreenPoint;
            mon = at is { } p ? MonitorFromPoint(p, 2 /* NEAREST */) : MonitorFromPoint(new InfoBar.POINT(), 1 /* PRIMARY */);
            mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return false;
            _monitor = mi.rcMonitor;
            _scale = GetDpiForMonitor(mon, 0, out uint dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;
            return true;
        }

        /// <summary>
        /// A piece of Wibn's taskbar on its edge with room beside it (e.g. the clock's own bar at the top right):
        /// it goes on the same line, in the free part at its chosen end. Null = no piece there, or no room.
        /// </summary>
        private Share? FindShare()
        {
            if (!ScreenOf(out _, out _)) return null;
            if (!CustomTaskbar.Running) return OwnLine();
            var m = _monitor;
            int gap = (int)Math.Round(5 * _scale), near = gap * 3;
            var on = new List<(CustomTaskbar.RECT Rect, bool Shown)>();
            foreach (var (r, shown) in CustomTaskbar.PiecesOn((m.Left + m.Right) / 2, (m.Top + m.Bottom) / 2))
            {
                if (r.Width <= 0 || r.Height <= 0) continue;
                bool fits = _edge switch
                {
                    0 => r.Width > r.Height && m.Bottom - r.Bottom <= near,
                    1 => r.Width > r.Height && r.Top - m.Top <= near,
                    2 => r.Height > r.Width && r.Left - m.Left <= near,
                    _ => r.Height > r.Width && m.Right - r.Right <= near,
                };
                if (fits) on.Add((r, shown));
            }
            if (on.Count == 0) return null;
            // Bars before this one already on that line: their places are taken too.
            var taken = InfoBar.Before(this).Select(b => b.SharedPlaced).Where(r => r != null).Select(r => r!.Value).ToList();

            // The line: as thick as the pieces, with the floating gap around them.
            var band = Vertical
                ? new CustomTaskbar.RECT { Left = Math.Max(m.Left, on.Min(p => p.Rect.Left) - gap), Right = Math.Min(m.Right, on.Max(p => p.Rect.Right) + gap), Top = m.Top, Bottom = m.Bottom }
                : new CustomTaskbar.RECT { Top = Math.Max(m.Top, on.Min(p => p.Rect.Top) - gap), Bottom = Math.Min(m.Bottom, on.Max(p => p.Rect.Bottom) + gap), Left = m.Left, Right = m.Right };

            // The free parts along the edge, between the pieces.
            var free = new List<(int A, int B)>();
            int cur = (Vertical ? m.Top : m.Left) + gap, end = (Vertical ? m.Bottom : m.Right) - gap;
            foreach (var p in on.Select(p => p.Rect).Concat(taken).OrderBy(r => Vertical ? r.Top : r.Left))
            {
                int a = (Vertical ? p.Top : p.Left) - gap, b = (Vertical ? p.Bottom : p.Right) + gap;
                if (a > cur) free.Add((cur, a));
                cur = Math.Max(cur, b);
            }
            if (end > cur) free.Add((cur, end));
            int min = (int)Math.Round(100 * _scale);
            free.RemoveAll(f => f.B - f.A < min);
            if (free.Count == 0) return null;   // no room on that line: it gets its own, next to it

            int side = S.Side;
            var seg = side == 0 ? free[0] : free[^1];
            double thick = (Vertical ? band.Width : band.Height) / _scale;
            return new Share(band, seg.A, seg.B, side, thick, on.Any(p => p.Shown));
        }

        // The line each edge's info bars share without Wibn's taskbar (screen, edge → the line, as the first one keeps it).
        private static readonly Dictionary<(CustomTaskbar.RECT Monitor, int Edge), CustomTaskbar.RECT> OwnLines = new();

        /// <summary>
        /// Without Wibn's taskbar (Windows' own is up): the info bars on the same edge of a screen go side by side on
        /// one line, like beside Wibn's taskbar, instead of each keeping a line of its own from Windows (they ended up
        /// stacked one under the other). The first one keeps the line's room; the others sit beside it. Alone on its
        /// edge: a line of its own as before (null).
        /// </summary>
        private Share? OwnLine()
        {
            var m = _monitor;
            var mates = InfoBar.Windows.Where(w => w != this && !w._closed && w._edge == _edge && Same(w._monitor, m)).ToList();
            if (mates.Count == 0) return null;
            var earlier = InfoBar.Before(this).Where(w => mates.Contains(w)).ToList();
            bool lead = earlier.Count == 0;
            double thickDip = CustomTaskbar.ThicknessDip + (Shape == 0 ? 0 : 10);
            int thick = (int)Math.Round(thickDip * _scale);
            var want = _edge switch
            {
                1 => new CustomTaskbar.RECT { Left = m.Left, Right = m.Right, Top = m.Top, Bottom = m.Top + thick },
                2 => new CustomTaskbar.RECT { Left = m.Left, Right = m.Left + thick, Top = m.Top, Bottom = m.Bottom },
                3 => new CustomTaskbar.RECT { Left = m.Right - thick, Right = m.Right, Top = m.Top, Bottom = m.Bottom },
                _ => new CustomTaskbar.RECT { Left = m.Left, Right = m.Right, Top = m.Bottom - thick, Bottom = m.Bottom },
            };
            var key = (m, _edge);
            CustomTaskbar.RECT band;
            if (lead)
            {
                // Windows says where there's room (next to its own taskbar), and keeps it for the line.
                band = ReserveAppBar(_edge, want, query: true, thick);
                if (band.Width <= 0 || band.Height <= 0) band = want;
                OwnLines[key] = band;
            }
            else band = OwnLines.TryGetValue(key, out var kept) ? kept : want;

            int gap = (int)Math.Round(5 * _scale);
            var taken = earlier.Select(b => b.SharedPlaced).Where(r => r != null).Select(r => r!.Value).ToList();
            var free = new List<(int A, int B)>();
            int cur = (Vertical ? band.Top : band.Left) + gap, end = (Vertical ? band.Bottom : band.Right) - gap;
            foreach (var p in taken.OrderBy(r => Vertical ? r.Top : r.Left))
            {
                int a = (Vertical ? p.Top : p.Left) - gap, b = (Vertical ? p.Bottom : p.Right) + gap;
                if (a > cur) free.Add((cur, a));
                cur = Math.Max(cur, b);
            }
            if (end > cur) free.Add((cur, end));
            free.RemoveAll(f => f.B - f.A < (int)Math.Round(100 * _scale));
            if (free.Count == 0) return null;   // no room left on the line: a line of its own
            var seg = S.Side == 0 ? free[0] : free[^1];
            // Over a full-screen app the first one slides away (Windows tells it); the others go and come with it.
            var leader = lead ? this : earlier[0];
            bool shown = !leader._fullscreen || leader._slideTo == 0;
            return new Share(band, seg.A, seg.B, S.Side, (Vertical ? band.Width : band.Height) / _scale, shown, Own: true, Lead: lead);
        }

        /// <summary>Its place on the shared line: as long as what's on it (or longer, by its inner end), at its end of the free part.</summary>
        private CustomTaskbar.RECT ShareTarget(Share s)
        {
            int gap = (int)Math.Round(5 * _scale);
            double content = ContentLength() + 2;   // + the rim
            int len = (int)Math.Ceiling(Math.Max(content, _dragLen ?? S.ShareLength) * _scale);
            len = Math.Min(len, s.SegEnd - s.SegStart);
            int a = s.Side == 0 ? s.SegStart : s.SegEnd - len;
            _full = Vertical
                ? new CustomTaskbar.RECT { Left = s.Band.Left + gap, Right = s.Band.Right - gap, Top = s.SegStart, Bottom = s.SegEnd }
                : new CustomTaskbar.RECT { Top = s.Band.Top + gap, Bottom = s.Band.Bottom - gap, Left = s.SegStart, Right = s.SegEnd };
            return Vertical
                ? new CustomTaskbar.RECT { Left = _full.Left, Right = _full.Right, Top = a, Bottom = a + len }
                : new CustomTaskbar.RECT { Top = _full.Top, Bottom = _full.Bottom, Left = a, Right = a + len };
        }

        /// <summary>Everything again with the current settings (and its place).</summary>
        public void Rebuild()
        {
            if (_closed) return;
            bool moved = _edge != S.Edge;
            _edge = S.Edge;
            // The taskbar is being rebuilt too (its look changed): keep the line until it's back in its place.
            _share = FindShare() ?? (moved ? null : _share);
            _shareKey = KeyOf(_share);
            BuildChips();
            Place(force: true);
            ApplyLook();
            if (_last is { } r) ShowReading(r);
            UpdateTimes();
            Place(force: true);   // the dock's length follows what's on it
            RestartGlass();
        }

        /// <summary>
        /// How long what's on it is (DIPs): the longest it has been since the chips were built. The numbers on it
        /// change all the time (a countdown, the heat, the speed…) and the bar grew and shrank with every one of
        /// them; now it only grows when something really needs more room, and keeps that length.
        /// </summary>
        private double ContentLength()
        {
            _list.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double now = Vertical ? _list.DesiredSize.Height : _list.DesiredSize.Width;
            if (now > _widest) _widest = now;
            return _widest;
        }

        private double _widest;   // see ContentLength (cleared when the chips are built again)

        private void BuildChips()
        {
            _widest = 0;
            _list.Children.Clear();
            _chips.Clear();
            _list.Orientation = Vertical ? Orientation.Vertical : Orientation.Horizontal;
            _list.HorizontalAlignment = HorizontalAlignment.Center;
            _list.VerticalAlignment = VerticalAlignment.Center;
            _list.Margin = Vertical ? new Thickness(0, 6, 0, 6) : new Thickness(6, 0, 6, 0);

            // CPU use and heat share one chip ("CPU 23% 54°"), the graphics card too.
            var ids = new List<string>();
            foreach (var item in InfoBar.Items)
            {
                if (!S.Shows(item.Id)) continue;
                string id = item.Id == "cputemp" ? "cpu" : item.Id == "gputemp" ? "gpu" : item.Id;
                if (!ids.Contains(id)) ids.Add(id);
            }
            var types = S.WidgetTypes;
            DropWidgets(types);
            foreach (string type in types) ids.Add("w:" + type);
            if (ids.Count == 0) ids.Add("clock");   // never an empty bar

            bool first = true;
            foreach (string id in ids)
            {
                if (!first) _list.Children.Add(Divider());
                first = false;
                var chip = NewChip(id);
                _chips.Add(chip);
                _list.Children.Add(chip.Root);
            }
        }

        private FrameworkElement Divider() => Vertical
            ? new Border { Height = 1, Width = 24, Background = _line, Margin = new Thickness(0, 3, 0, 3), HorizontalAlignment = HorizontalAlignment.Center }
            : new Border { Width = 1, Height = 16, Background = _line, Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };

        private Chip NewChip(string id)
        {
            if (id.StartsWith("w:")) return NewWidgetChip(id[2..]);
            var c = new Chip { Id = id };
            c.Label = new TextBlock { FontSize = SmallSize, Foreground = _muted, VerticalAlignment = VerticalAlignment.Center };
            c.Value = new TextBlock { FontSize = ValueSize, Foreground = _ink, VerticalAlignment = VerticalAlignment.Center };
            c.Sub = new TextBlock { FontSize = SmallSize, Foreground = _muted, VerticalAlignment = VerticalAlignment.Center };
            c.Value.Typography.NumeralAlignment = FontNumeralAlignment.Tabular;   // the digits don't jump each second
            c.Label.Text = id switch
            {
                "cpu" => "CPU", "gpu" => "GPU", "ram" => "RAM",
                "net" => Lang.T("النت"), "disk" => SystemDrive(),
                _ => "",
            };
            if (id is "cpu" or "gpu" or "ram" or "net" or "disk") c.Label.FlowDirection = FlowDirection.LeftToRight;
            if (id is "clock" or "cpu" or "gpu" or "ram" or "net" or "disk" or "prayer") c.Value.FlowDirection = FlowDirection.LeftToRight;

            var panel = new StackPanel { Orientation = Vertical ? Orientation.Vertical : Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var t in new[] { c.Label, c.Value, c.Sub })
            {
                if (Vertical) { t.HorizontalAlignment = HorizontalAlignment.Center; t.TextAlignment = TextAlignment.Center; t.TextWrapping = TextWrapping.Wrap; }
                else t.Margin = new Thickness(0, 0, 5, 0);
                panel.Children.Add(t);
            }
            if (!Vertical) c.Sub.Margin = new Thickness(0);

            // A button like the taskbar's: lights up under the mouse, a click opens what fits.
            double thick = ThickDip - (Shape == 0 ? 0 : 10);
            var b = new Border
            {
                CornerRadius = new CornerRadius(5), Background = Brushes.Transparent, Child = panel,
                Padding = Vertical ? new Thickness(2, 6, 2, 6) : new Thickness(8, 0, 8, 0),
                Height = Vertical ? double.NaN : Math.Max(24, Math.Min(thick - 6, 40)),
                Width = Vertical ? Math.Max(40, thick - 12) : double.NaN,
                Margin = Vertical ? new Thickness(0, 2, 0, 2) : new Thickness(2, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                ToolTip = Tip(id),
            };
            b.MouseEnter += (_, _) => b.Background = _hover;
            b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
            b.PreviewMouseLeftButtonDown += (_, _) => b.Background = _pressed;
            b.PreviewMouseLeftButtonUp += (_, _) => b.Background = b.IsMouseOver ? _hover : Brushes.Transparent;
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; Click(id); };
            c.Root = b;
            return c;
        }

        // ---- Widgets on the bar --------------------------------------------------------------------------
        //
        // Each widget keeps running out of sight (in a hidden corner of this window, so its timers, sound and
        // network work as on the desktop), and the bar shows a small form of it that fits its thickness and
        // follows it. A few (the quick actions bar) go on the bar themselves.

        private sealed class BarWidget
        {
            public WidgetContent Content = null!;
            public Widgets.Info Info = null!;
            public ContextMenu? Menu;
            public Color? Accent;
        }

        private readonly Dictionary<string, BarWidget> _widgets = new();
        private int _saveTicks, _wallTicks;
        private readonly Canvas _hidden = new() { Width = 0, Height = 0, ClipToBounds = true, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };

        private BarWidget? WidgetOf(string type)
        {
            if (_widgets.TryGetValue(type, out var w)) return w;
            var kind = Widgets.KindOf(type);
            if (kind == null) return null;
            try
            {
                var info = S.WidgetInfo(type);
                var content = kind.Make();
                content.Info = info;
                content.SetCells(info.W, info.H);
                // Room to lay itself out as on the desktop, out of sight.
                content.Width = Math.Max(160, info.W * 80);
                content.Height = Math.Max(80, info.H * 80);
                w = new BarWidget { Content = content, Info = info };
                _widgets[type] = w;
                if (!_hidden.Children.Contains(content) && !content.BarSelf) _hidden.Children.Add(content);
                content.BarChanged = () => Dispatcher.BeginInvoke(() => { if (!_closed) Rebuild(); });
                ApplyColor(w);
                content.Start();
                return w;
            }
            catch (Exception ex) { CrashLog.Write("bar widget " + type, ex); return null; }
        }

        /// <summary>The widgets no longer on the bar stop (and keep their options).</summary>
        private void DropWidgets(IEnumerable<string> keep)
        {
            foreach (var type in _widgets.Keys.Except(keep).ToList()) StopWidget(type);
        }

        private void StopWidget(string type)
        {
            if (!_widgets.Remove(type, out var w)) return;
            try { w.Content.Stop(); } catch (Exception ex) { CrashLog.Write("bar widget stop", ex); }
            S.SaveWidgetInfo(w.Info);
            if (w.Content.Parent is Panel p) p.Children.Remove(w.Content);
            else if (w.Content.Parent is Decorator d) d.Child = null;
        }

        /// <summary>A widget on the bar: its small form (or itself), as thick as the bar. Right-click: its own options.</summary>
        private Chip NewWidgetChip(string type)
        {
            var c = new Chip { Id = "w:" + type, Label = new TextBlock(), Value = new TextBlock(), Sub = new TextBlock() };
            var w = WidgetOf(type);
            double thick = ThickDip - (Shape == 0 ? 0 : 10);
            var look = new WidgetContent.BarLook(Vertical, Vertical ? Math.Max(40, thick - 12) : Math.Max(24, Math.Min(thick - 6, 40)),
                                                 _ink, _hover, ValueSize, SmallSize);
            FrameworkElement view;
            if (w == null) view = new TextBlock { Text = "—", Foreground = _ink, VerticalAlignment = VerticalAlignment.Center };
            else if (w.Content.BarSelf)
            {
                // The widget itself, along the bar: one square per button.
                var content = w.Content;
                if (content.Parent is Panel p) p.Children.Remove(content);
                else if (content.Parent is Decorator d) d.Child = null;
                content.Info!.Options["vertical"] = Vertical ? "1" : "0";
                content.SetCells(Vertical ? 1 : 6, Vertical ? 6 : 1);
                int n = Math.Max(1, content.Info.Options.TryGetValue("items", out var items) ? items.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length : 1);
                double side = look.Thick;
                content.Width = Vertical ? side : side * n;
                content.Height = Vertical ? side * n : side;
                content.Margin = new Thickness(0);
                content.Padding = new Thickness(0);
                view = content;
            }
            else view = w.Content.BarView(look) ?? new TextBlock { Text = Widgets.KindOf(type)?.Name ?? type, Foreground = _ink, FontSize = ValueSize, VerticalAlignment = VerticalAlignment.Center };

            var b = new Border
            {
                Child = view, Background = Brushes.Transparent,
                Height = Vertical ? double.NaN : look.Thick, MinWidth = Vertical ? look.Thick : 0,
                Margin = Vertical ? new Thickness(0, 2, 0, 2) : new Thickness(2, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            };
            b.MouseRightButtonUp += (_, e) => { e.Handled = true; WidgetMenu(type); };
            c.Root = b;
            return c;
        }

        /// <summary>
        /// The widget's color (e.g. the sound waves'): one of the widgets' colors, the bar's text color (it follows
        /// the clock's), or any color from the picker.
        /// </summary>
        private MenuItem ColorMenu(BarWidget w)
        {
            var item = new MenuItem { Header = Lang.T("اللون"), Tag = "colors" };
            MenuItem Choice(string header, string tag, Color dot, Action pick)
            {
                var c = new MenuItem
                {
                    Header = header, Tag = tag,
                    Icon = new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(dot) },
                };
                c.Click += (_, _) => pick();
                return c;
            }
            item.Items.Add(Choice(Lang.T("نفس لون كتابة التاسك بار"), "ink", _ink.Color, () => SetWidgetColor(w, "ink")));
            item.Items.Add(new Separator());
            foreach (var (name, hex) in WidgetWindow.Colors)
            {
                string h = hex;
                item.Items.Add(Choice(Lang.T(name), h, (Color)ColorConverter.ConvertFromString(h), () => SetWidgetColor(w, h)));
            }
            item.Items.Add(new Separator());
            var custom = new MenuItem { Header = Lang.T("لون ثاني…") };
            custom.Click += (_, _) =>
            {
                Color now = AccentOf(w);
                if (ColorPickerWindow.Ask(now, _hwnd) is { } picked) SetWidgetColor(w, $"#{picked.R:X2}{picked.G:X2}{picked.B:X2}");
            };
            item.Items.Add(custom);
            return item;
        }

        private Color AccentOf(BarWidget w)
        {
            if (w.Info.Color == "ink") return _ink.Color;
            try { return (Color)ColorConverter.ConvertFromString(string.IsNullOrEmpty(w.Info.Color) ? "#4C8DFF" : w.Info.Color); }
            catch { return Color.FromRgb(0x4C, 0x8D, 0xFF); }
        }

        private void SetWidgetColor(BarWidget w, string color)
        {
            w.Info.Color = color;
            ApplyColor(w);
            S.SaveWidgetInfo(w.Info);
        }

        private void ApplyColor(BarWidget w)
        {
            var c = AccentOf(w);
            if (w.Accent == c) return;
            w.Accent = c;
            try { w.Content.SetAccent(c); } catch (Exception ex) { CrashLog.Write("bar widget color", ex); }
        }

        /// <summary>A widget's own right-click menu (as on the desktop), and taking it off the bar.</summary>
        private void WidgetMenu(string type)
        {
            if (!_widgets.TryGetValue(type, out var w)) { ShowMenu(); return; }
            if (w.Menu == null)
            {
                var menu = new ContextMenu { FlowDirection = Lang.Flow };
                var own = w.Content.MenuItems().ToList();
                foreach (var item in own) menu.Items.Add(item);
                menu.Items.Add(ColorMenu(w));
                menu.Items.Add(new Separator());
                var remove = new MenuItem { Header = Lang.T("شيله من التاسك بار") };
                remove.Click += (_, _) => S.SetWidget(type, false);
                menu.Items.Add(remove);
                var bar = new MenuItem { Header = Lang.T("خيارات التاسك بار…") };
                bar.Click += (_, _) => Dispatcher.BeginInvoke(ShowMenu);
                menu.Items.Add(bar);
                w.Menu = menu;
            }
            // The color in use gets its check mark.
            if (w.Menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Tag as string == "colors") is { } colors)
                foreach (var i in colors.Items.OfType<MenuItem>())
                    i.IsChecked = i.Tag is string h ? string.Equals(h, w.Info.Color, StringComparison.OrdinalIgnoreCase)
                                                    : w.Info.Color.StartsWith("#") && !WidgetWindow.Colors.Any(c => string.Equals(c.Hex, w.Info.Color, StringComparison.OrdinalIgnoreCase));
            w.Menu.PlacementTarget = this;
            w.Menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            w.Menu.IsOpen = true;
        }

        /// <summary>A click on a piece: the clock and dates open the calendar (like the taskbar's clock), the numbers Task Manager.</summary>
        private static void Click(string id)
        {
            try
            {
                if (id is "clock" or "date" or "hijri" or "prayer") KeyPress.Send(KeyPress.LWin, KeyPress.N);
                else if (id == "net") System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:network-status") { UseShellExecute = true });
                else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr.exe") { UseShellExecute = true });
            }
            catch (Exception ex) { CrashLog.Write("info bar click", ex); }
        }

        private static string Tip(string id) => id switch
        {
            "cpu" => Lang.T("المعالج: الاستهلاك والحرارة"),
            "gpu" => Lang.T("كرت الشاشة: الاستهلاك والحرارة"),
            "ram" => Lang.T("الرام المستخدمة"),
            "net" => Lang.T("سرعة النت الحين: التنزيل والرفع"),
            "disk" => Lang.T("المساحة المستخدمة من قرص الويندوز"),
            "prayer" => Lang.T("كم باقي على الصلاة الجاية. كليك يمين عشان تغيّر المدينة"),
            _ => "",
        };

        /// <summary>The background, corners and edge: exactly the taskbar's (<see cref="TaskbarBar.ApplyLook"/>).</summary>
        private void ApplyLook()
        {
            int shape = Shape, look = CustomTaskbar.Look, opacity = Math.Clamp(CustomTaskbar.Opacity, 0, 100);
            int rgb = CustomTaskbar.BackColor;
            var color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            SetInk(look != 0 && Widgets.Lightness(color) > 0.6 && (look == 1 || opacity >= 50));

            double h = ThickDip - (shape == 0 ? 0 : 10);
            _radius = shape == 0 ? 0 : Math.Clamp((Vertical ? h : h) / 2 - 4, 6, 14);
            byte alpha = (byte)Math.Max(1, opacity * 255 / 100);   // never 0: fully clear parts would let clicks through
            // With the glass: its last picture stays until the new glass is ready (no dark gray blink).
            bool keepGlass = look == 0 && GlassSettings.Taskbar.On && _card.Background is ImageBrush;
            if (!keepGlass) _card.Background = look switch
            {
                1 => new SolidColorBrush(color),
                2 => new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)),
                _ => new SolidColorBrush(Color.FromArgb(0xE0, 0x1C, 0x1C, 0x1E)),   // until the glass is ready (or with it off)
            };
            _card.CornerRadius = new CornerRadius(_radius);
            _card.BorderThickness = new Thickness(shape == 0 ? 0 : 1);
            _card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        }

        private bool _lightBehind;

        private void SetInk(bool lightBehind)
        {
            _lightBehind = lightBehind;
            // All its text in the taskbar clock's color (when Wibn's taskbar is up), else white or dark for what's behind.
            var ink = CustomTaskbar.ClockInk ?? (lightBehind ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xF5, 0xF5, 0xF7));
            _ink.Color = ink;
            _muted.Color = ink;
            _line.Color = Color.FromArgb(0x40, ink.R, ink.G, ink.B);
            _hover.Color = Color.FromArgb(0x24, ink.R, ink.G, ink.B);
            _pressed.Color = Color.FromArgb(0x16, ink.R, ink.G, ink.B);
        }

        // ---- Glass ------------------------------------------------------------------------------

        private DispatcherTimer? _glassSoon;

        private void RestartGlass()
        {
            _glass?.Dispose();
            _glass = null;
            if (CustomTaskbar.Look != 0 || !GlassSettings.Taskbar.On) return;
            if (_glassSoon == null)
            {
                _glassSoon = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
                _glassSoon.Tick += (_, _) => { _glassSoon.Stop(); StartGlass(); };
            }
            _glassSoon.Stop();
            _glassSoon.Start();
        }

        private void StartGlass()
        {
            if (_closed || !IsVisible || _slide > 0 || _slideClock != null || CustomTaskbar.Look != 0 || !GlassSettings.Taskbar.On) return;
            _glass?.Dispose();
            _glass = null;
            try
            {
                UpdateLayout();
                if (_card.ActualWidth < 4) return;
                // Live, like Wibn's taskbar: what's behind it (the wallpaper while a window shrinks or grows over it).
                var glass = new LiquidGlass(this, _host, _card, new Thickness(0), _radius, GlassSettings.Taskbar,
                                            tint: CustomTaskbar.GlassTint(), live: true);
                if (!glass.Available) return;   // stays the plain color
                glass.LightBehindChanged += SetInk;
                _glass = glass;
                glass.Start();
            }
            catch (Exception ex) { CrashLog.Write("info bar glass", ex); }
        }

        // ---- Where it goes ----------------------------------------------------------------------

        private bool _appBar;
        private int _appBarMsg;
        private CustomTaskbar.RECT _reserved;

        /// <summary>Works out its strip (its room) and its window's place in it, and puts it there.</summary>
        private void Place(bool force = false)
        {
            if (_closed) return;
            // Beside a taskbar piece, or on its own line: when that changes, everything is built again for it.
            var share = FindShare();
            // Start-up: the taskbar piece it sat beside last time isn't placed yet. Stay hidden (off screen) for it,
            // not on a line of its own in the middle and then jumping over.
            if (share == null && _share == null && Waiting && CustomTaskbar.Running) return;
            _waitShareUntil = DateTime.MinValue;
            // The taskbar has no place for a moment while it's rebuilt: stay beside it unless it's really gone.
            if (_share != null && share == null)
            {
                _shareLostAt ??= DateTime.UtcNow;
                if ((DateTime.UtcNow - _shareLostAt.Value).TotalSeconds < 1.5) return;
            }
            _shareLostAt = null;
            if (S.WasShared != (share != null)) S.WasShared = share != null;
            if (KeyOf(share) != _shareKey) { _share = share; Rebuild(); return; }
            _share = share;
            if (share != null) { PlaceShared(share, force); return; }
            if (!_fullscreen && !_covered) SlideTo(0);   // was beside a piece that went away for a full-screen app

            if (!ScreenOf(out var mi, out _)) return;
            var m = mi.rcMonitor;
            int thick = (int)Math.Round(ThickDip * _scale);
            int edge = _edge;

            var w = mi.rcWork;
            var r = edge switch
            {
                1 => new CustomTaskbar.RECT { Left = m.Left, Right = m.Right, Top = m.Top, Bottom = m.Top + thick },
                2 => new CustomTaskbar.RECT { Left = m.Left, Right = m.Left + thick, Top = m.Top, Bottom = m.Bottom },
                3 => new CustomTaskbar.RECT { Left = m.Right - thick, Right = m.Right, Top = m.Top, Bottom = m.Bottom },
                _ => new CustomTaskbar.RECT { Left = m.Left, Right = m.Right, Top = m.Bottom - thick, Bottom = m.Bottom },
            };

            if (CustomTaskbar.Running)
            {
                // Wibn's taskbar keeps Windows' hidden and sets the windows' room itself: step past its bars here.
                int cx = (m.Left + m.Right) / 2, cy = (m.Top + m.Bottom) / 2;
                foreach (var bar in new[] { CustomTaskbar.BarOn(cx, cy), CustomTaskbar.SideBarOn(cx, cy), CustomTaskbar.ClockOn(cx, cy) })
                {
                    if (bar is not { } b || b.Width <= 0) continue;
                    // The taskbar's whole strip (a floating one has a gap around it).
                    int gap = CustomTaskbar.Shape == 0 ? 0 : (int)Math.Round(5 * _scale);
                    b.Left -= gap; b.Top -= gap; b.Right += gap; b.Bottom += gap;
                    bool horizontal = b.Width >= b.Height;
                    bool same = edge switch
                    {
                        0 => horizontal && b.Bottom >= m.Bottom - 2, 1 => horizontal && b.Top <= m.Top + 2,
                        2 => !horizontal && b.Left <= m.Left + 2, _ => !horizontal && b.Right >= m.Right - 2,
                    };
                    if (same)
                    {
                        // On the same edge: next to it, further in.
                        if (edge == 0) { r.Bottom = Math.Min(r.Bottom, b.Top); r.Top = r.Bottom - thick; }
                        else if (edge == 1) { r.Top = Math.Max(r.Top, b.Bottom); r.Bottom = r.Top + thick; }
                        else if (edge == 2) { r.Left = Math.Max(r.Left, b.Right); r.Right = r.Left + thick; }
                        else { r.Right = Math.Min(r.Right, b.Left); r.Left = r.Right - thick; }
                    }
                    else if (edge >= 2 && horizontal)
                    {
                        // A side bar stops at a taskbar at the top or bottom.
                        if (b.Bottom >= m.Bottom - 2) r.Bottom = Math.Min(r.Bottom, b.Top);
                        else if (b.Top <= m.Top + 2) r.Top = Math.Max(r.Top, b.Bottom);
                    }
                    else if (edge <= 1 && !horizontal)
                    {
                        if (b.Left <= m.Left + 2) r.Left = Math.Max(r.Left, b.Right);
                        else if (b.Right >= m.Right - 2) r.Right = Math.Min(r.Right, b.Left);
                    }
                }
                foreach (var other in InfoBar.Before(this))
                {
                    if (other.SharedPlaced != null || !other.Reserving) continue;
                    var b = other.Strip;
                    if (b.Width <= 0 || b.Left < m.Left - 1 || b.Right > m.Right + 1 || b.Top < m.Top - 1 || b.Bottom > m.Bottom + 1) continue;
                    int oe = other.PlacedEdge;
                    if (oe == edge)
                    {
                        if (edge == 0) { r.Bottom = Math.Min(r.Bottom, b.Top); r.Top = r.Bottom - thick; }
                        else if (edge == 1) { r.Top = Math.Max(r.Top, b.Bottom); r.Bottom = r.Top + thick; }
                        else if (edge == 2) { r.Left = Math.Max(r.Left, b.Right); r.Right = r.Left + thick; }
                        else { r.Right = Math.Min(r.Right, b.Left); r.Left = r.Right - thick; }
                    }
                    else if (edge >= 2 && oe <= 1)
                    {
                        if (oe == 0) r.Bottom = Math.Min(r.Bottom, b.Top); else r.Top = Math.Max(r.Top, b.Bottom);
                    }
                    else if (edge <= 1 && oe >= 2)
                    {
                        if (oe == 2) r.Left = Math.Max(r.Left, b.Right); else r.Right = Math.Min(r.Right, b.Left);
                    }
                }
                ReserveAppBar(edge, r, query: false, thick);
            }
            else
            {
                // Windows' own taskbar: it says where there's room (next to it, or next to other bars).
                r = ReserveAppBar(edge, r, query: true, thick);
                if (edge >= 2) { r.Top = Math.Max(r.Top, w.Top); r.Bottom = Math.Min(r.Bottom, w.Bottom); }
                else { r.Left = Math.Max(r.Left, w.Left); r.Right = Math.Min(r.Right, w.Right); }
            }
            if (r.Width <= 0 || r.Height <= 0) return;

            bool stripChanged = !Same(r, _strip);
            _strip = r;
            var target = Target();
            if (!force && !stripChanged && Same(target, _placed)) return;
            bool resized = target.Width != _placed.Width || target.Height != _placed.Height;
            _placed = target;
            if (_slideClock == null) MoveWindow();
            if (resized && !force) RestartGlass();
            if (stripChanged) CustomTaskbar.WidgetsFollowSoon();
        }

        /// <summary>
        /// On the line of a taskbar piece: the taskbar keeps that room (or not, with "hide in full screen"), so it
        /// keeps none of its own; and it goes and comes back with the taskbar (full screen).
        /// </summary>
        private void PlaceShared(Share s, bool force)
        {
            if (!s.Lead) RemoveAppBar();   // (the first one on its own line keeps that line's room)
            bool stripChanged = !Same(s.Band, _strip);
            _strip = s.Band;
            var target = ShareTarget(s);
            if (force || stripChanged || !Same(target, _placed))
            {
                bool resized = target.Width != _placed.Width || target.Height != _placed.Height;
                _placed = target;
                if (_slideClock == null) MoveWindow();
                if (resized && !force) RestartGlass();
                if (stripChanged) CustomTaskbar.WidgetsFollowSoon();
            }
            // (Over a full-screen app or a window: down while the mouse at the edge brought it, see RevealTick.)
            bool revealed = DateTime.Now < _revealUntil;
            SlideTo(s.Shown && (!_fullscreen || revealed) && (!_covered || revealed) ? 0 : 1);
        }

        /// <summary>The window's place in its strip for the taskbar's shape (like <see cref="TaskbarBar"/>'s Target).</summary>
        private CustomTaskbar.RECT Target()
        {
            if (_share is { } shared) return ShareTarget(shared);
            var s = _strip;
            int shape = Shape;
            int gap = (int)Math.Round(5 * _scale);
            var r = shape == 0 ? s : new CustomTaskbar.RECT { Left = s.Left + gap, Top = s.Top + gap, Right = s.Right - gap, Bottom = s.Bottom - gap };
            _full = r;
            if (shape == 2)
            {
                // Dock: only as long as what's on it, in the middle.
                double content = ContentLength();
                if (Vertical)
                {
                    int len = Math.Min(r.Height, (int)Math.Ceiling(content * _scale) + gap * 2);
                    int mid = (s.Top + s.Bottom) / 2;
                    r.Top = mid - len / 2; r.Bottom = r.Top + len;
                }
                else
                {
                    int len = Math.Min(r.Width, (int)Math.Ceiling(content * _scale) + gap * 2);
                    int mid = (s.Left + s.Right) / 2;
                    r.Left = mid - len / 2; r.Right = r.Left + len;
                }
                return r;
            }
            // Made shorter by dragging an end: both ends come in by the same amount (it stays centered).
            int inset = InsetPx(Vertical ? r.Height : r.Width);
            if (Vertical) { r.Top += inset; r.Bottom -= inset; }
            else { r.Left += inset; r.Right -= inset; }
            return r;
        }

        /// <summary>The window at its place, pushed toward the screen's edge as far as it's slid.</summary>
        private void MoveWindow()
        {
            var r = _placed;
            int dx = 0, dy = 0;
            if (_slide > 0)
            {
                if (_edge == 0) dy = (int)Math.Round((_monitor.Bottom - r.Top) * _slide);
                else if (_edge == 1) dy = -(int)Math.Round((r.Bottom - _monitor.Top) * _slide);
                else if (_edge == 2) dx = -(int)Math.Round((r.Right - _monitor.Left) * _slide);
                else dx = (int)Math.Round((_monitor.Right - r.Left) * _slide);
            }
            SetWindowPos(_hwnd, new IntPtr(-1) /* TOPMOST */, r.Left + dx, r.Top + dy, r.Width, r.Height, 0x0010 /* NOACTIVATE */);
        }

        /// <summary>Keeps the strip's room for it (an app bar), and gives back where it really goes.</summary>
        private CustomTaskbar.RECT ReserveAppBar(int edge, CustomTaskbar.RECT want, bool query, int thick)
        {
            uint abEdge = edge switch { 1 => 1u /* TOP */, 2 => 0u /* LEFT */, 3 => 2u /* RIGHT */, _ => 3u /* BOTTOM */ };
            if (!_appBar)
            {
                if (_appBarMsg == 0) _appBarMsg = RegisterWindowMessage("WibnInfoBar");
                var nw = new CustomTaskbar.APPBARDATA { cbSize = Marshal.SizeOf<CustomTaskbar.APPBARDATA>(), hWnd = _hwnd, uCallbackMessage = (uint)_appBarMsg };
                if (CustomTaskbar.SHAppBarMessage(0 /* ABM_NEW */, ref nw) == IntPtr.Zero) return want;
                _appBar = true;
                S.RememberAppBar(_hwnd);
            }
            var abd = new CustomTaskbar.APPBARDATA { cbSize = Marshal.SizeOf<CustomTaskbar.APPBARDATA>(), hWnd = _hwnd, uEdge = abEdge, rc = want };
            if (query)
            {
                CustomTaskbar.SHAppBarMessage(2 /* ABM_QUERYPOS */, ref abd);
                // Keep its thickness from the edge it was pushed to.
                if (edge == 0) abd.rc.Top = abd.rc.Bottom - thick;
                else if (edge == 1) abd.rc.Bottom = abd.rc.Top + thick;
                else if (edge == 2) abd.rc.Right = abd.rc.Left + thick;
                else abd.rc.Left = abd.rc.Right - thick;
            }
            var result = abd.rc;
            if (Same(result, _reserved)) return result;   // only when it changes: each change moves maximized windows
            _reserved = result;
            CustomTaskbar.SHAppBarMessage(3 /* ABM_SETPOS */, ref abd);
            return result;
        }

        private void RemoveAppBar()
        {
            if (!_appBar) return;
            var abd = new CustomTaskbar.APPBARDATA { cbSize = Marshal.SizeOf<CustomTaskbar.APPBARDATA>(), hWnd = _hwnd };
            CustomTaskbar.SHAppBarMessage(1 /* ABM_REMOVE */, ref abd);
            _appBar = false;
            _reserved = default;
            S.RememberAppBar(IntPtr.Zero);
        }

        private IntPtr Hook(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x21 /* WM_MOUSEACTIVATE */) { handled = true; return (IntPtr)3 /* MA_NOACTIVATE */; }
            if (_appBarMsg != 0 && msg == _appBarMsg)
            {
                int code = wParam.ToInt32();
                if (code == 1 /* ABN_POSCHANGED */) Dispatcher.BeginInvoke(() => Place());
                else if (code == 2 /* ABN_FULLSCREENAPP */) OnFullscreen(lParam != IntPtr.Zero);
            }
            return IntPtr.Zero;
        }

        // ---- Full screen: slides away like the taskbar, the mouse at its edge brings it back --------------

        private bool _fullscreen;
        private DateTime _revealUntil;
        private readonly DispatcherTimer _reveal;
        private double _slide, _slideFrom, _slideTo;   // 0 = in its place, 1 = fully off the screen
        private System.Diagnostics.Stopwatch? _slideClock;
        private const double SlideMs = 170;

        private void OnFullscreen(bool on)
        {
            if (_share is { Own: false }) return;   // beside a taskbar piece it goes and comes with the taskbar
            if (on)
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero || TaskbarApps.Ours.Contains(fg)) return;
                if (MonitorFromWindow(fg, 2) != MonitorFromWindow(_hwnd, 2)) return;
                _fullscreen = true;
                _reveal.Start();
                SlideTo(1);
            }
            else if (_fullscreen)
            {
                _fullscreen = false;
                if (!_covered) { _reveal.Stop(); SlideTo(0); }
            }
        }

        // ---- A window under it: it slides away so the window shows (the mouse at its edge brings it) ---------

        private readonly DispatcherTimer _cover;
        private bool _covered;

        /// <summary>
        /// The window in front is under this bar (see CustomTaskbar.HideOverWindow): only this bar slides away, like
        /// over a full-screen app. While that window is being moved it stays as it is, and it's decided once it's let go.
        /// </summary>
        internal void CheckCover()
        {
            if (_closed || _moving || _resizing || _fullscreen) return;
            var (front, dragging) = CustomTaskbar.FrontWindow();
            if (dragging) return;
            bool want = front is { } f && CustomTaskbar.Covers(f, _placed);
            if (want == _covered) return;
            _covered = want;
            if (want)
            {
                _revealUntil = DateTime.MinValue;
                _reveal.Start();
                SlideTo(1);
            }
            else
            {
                _reveal.Stop();
                if (_share != null) Place(); else SlideTo(0);
            }
        }

        /// <summary>Over a full-screen app: in while the mouse is at its edge or on it, out a moment after.</summary>
        private void RevealTick()
        {
            if (_moving || !((_fullscreen && (_share == null || _share.Lead)) || _covered)) return;
            GetCursorPos(out var c);
            var m = _monitor;
            bool inside = c.X >= m.Left && c.X < m.Right && c.Y >= m.Top && c.Y < m.Bottom;
            var p = _placed;
            // Only over the bar itself along the edge (it may be shorter than the screen), not anywhere on it.
            bool along = Vertical ? c.Y >= p.Top - 8 && c.Y <= p.Bottom + 8 : c.X >= p.Left - 8 && c.X <= p.Right + 8;
            bool atEdge = inside && along && _edge switch
            {
                // The mouse pushed right against the edge (its very last pixel), not just near it.
                0 => c.Y >= m.Bottom - 1, 1 => c.Y <= m.Top, 2 => c.X <= m.Left, _ => c.X >= m.Right - 1,
            };
            bool onIt = c.X >= p.Left && c.X < p.Right && c.Y >= p.Top && c.Y < p.Bottom;
            var now = DateTime.Now;
            if (atEdge || (_slideTo == 0 && onIt)) _revealUntil = now.AddMilliseconds(250);
            SlideTo(now < _revealUntil ? 0 : 1);
        }

        private bool _widgetsCovered;

        /// <summary>Slid away (a full-screen app): the widgets on it stop updating (sound, timers…) until it's back.</summary>
        private void CoverWidgets(bool covered)
        {
            if (_widgetsCovered == covered) return;
            _widgetsCovered = covered;
            foreach (var w in _widgets.Values)
                try { w.Content.Covered(covered); } catch (Exception ex) { CrashLog.Write("bar widget cover", ex); }
        }

        private void SlideTo(double to)
        {
            if (to == 0) CoverWidgets(false);
            if (_slideClock != null ? _slideTo == to : _slide == to) return;
            // A live glass stays on as it slides (it follows the bar); thrown away and built again, the bars along
            // the top came back without their glass for a moment.
            if (to > 0 && !LiquidGlass.LiveStream) { _glass?.Dispose(); _glass = null; }
            if (!IsVisible) Show();
            _slideFrom = _slide;
            _slideTo = to;
            if (_slideClock == null)
            {
                _slideClock = System.Diagnostics.Stopwatch.StartNew();
                CompositionTarget.Rendering += SlideFrame;
            }
            else _slideClock.Restart();
        }

        private void StopSlide()
        {
            if (_slideClock == null) return;
            CompositionTarget.Rendering -= SlideFrame;
            _slideClock = null;
        }

        private void SlideFrame(object? sender, EventArgs e)
        {
            if (_slideClock == null) return;
            double t = Math.Min(1, _slideClock.Elapsed.TotalMilliseconds / SlideMs);
            double ease = 1 - Math.Pow(1 - t, 3);
            _slide = _slideFrom + (_slideTo - _slideFrom) * ease;
            MoveWindow();
            if (t < 1) return;
            _slide = _slideTo;
            StopSlide();
            if (_slide >= 1) { Hide(); CoverWidgets(true); }
            else if (!LiquidGlass.LiveStream || _glass == null) RestartGlass();   // in its place again: the glass sees what's behind it now
        }

        // ---- Every second ---------------------------------------------------------------------------

        private void Tick()
        {
            if (_closed) return;
            if (!_moving && !_resizing) Place();
            SetInk(_lightBehind);   // the clock's color may have changed
            foreach (var w in _widgets.Values) if (w.Info.Color == "ink") ApplyColor(w);   // follows the clock's color
            if (++_wallTicks >= 60) { _wallTicks = 0; _glass?.Refresh(); }   // wallpaper slideshows
            if (++_saveTicks >= 10) { _saveTicks = 0; foreach (var w in _widgets.Values) S.SaveWidgetInfo(w.Info); }
            UpdateTimes();
            // The numbers are read on another thread (the graphics card's can take a moment).
            if (Interlocked.Exchange(ref _sampling, 1) == 0)
            {
                bool cpuTemp = S.Shows("cputemp"), gpuTemp = S.Shows("gputemp"), net = S.Shows("net");
                Task.Run(() =>
                {
                    try
                    {
                        var r = _sensors.Read(cpuTemp, gpuTemp, net);
                        Dispatcher.BeginInvoke(() => { if (!_closed) { _last = r; ShowReading(r); } });
                    }
                    catch (Exception ex) { CrashLog.Write("info bar sensors", ex); }
                    finally { Interlocked.Exchange(ref _sampling, 0); }
                });
            }
        }

        private Chip? ChipOf(string id) => _chips.FirstOrDefault(c => c.Id == id);

        private void UpdateTimes()
        {
            var now = DateTime.Now;
            var culture = Lang.English ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("ar-SA");
            if (ChipOf("clock") is { } clock)
            {
                int h = now.Hour % 12 == 0 ? 12 : now.Hour % 12;
                clock.Value.Text = InfoBar.Seconds ? $"{h}:{now.Minute:00}:{now.Second:00}" : $"{h}:{now.Minute:00}";
                clock.Sub.Text = now.Hour < 12 ? Lang.T("ص") : Lang.T("م");
            }
            if (ChipOf("date") is { } date)
            {
                var gregorian = (CultureInfo)culture.Clone();
                gregorian.DateTimeFormat.Calendar = new GregorianCalendar();
                date.Label.Text = now.ToString("dddd", gregorian);
                date.Value.Text = now.ToString("d MMMM", gregorian);
                date.Root.ToolTip = now.ToString("dddd d MMMM yyyy", gregorian);
            }
            if (ChipOf("hijri") is { } hijri)
            {
                string text = PrayerWidget.Hijri(now.Date);
                int cut = Lang.English ? -1 : text.LastIndexOf(' ');   // the year apart (Arabic: "1448هـ")
                hijri.Value.Text = cut > 0 ? text[..cut] : text;
                hijri.Sub.Text = cut > 0 ? text[(cut + 1)..] : "";
                hijri.Root.ToolTip = text;
            }
            if (ChipOf("prayer") is { } prayer) UpdatePrayer(prayer);
        }

        private DateTime[] _today = Array.Empty<DateTime>(), _tomorrow = Array.Empty<DateTime>();
        private DateTime _prayerDate;
        private string _prayerCity = "";

        private void UpdatePrayer(Chip chip)
        {
            var city = InfoBar.City;
            DateTime now = DateTime.UtcNow.AddHours(city.Zone);   // the city's own clock
            if (_prayerDate != now.Date || _prayerCity != city.Name)
            {
                _prayerDate = now.Date;
                _prayerCity = city.Name;
                _today = PrayerTimes.For(now.Date, city);
                _tomorrow = PrayerTimes.For(now.Date.AddDays(1), city);
            }
            int next = -1;
            for (int i = 0; i < 6; i++)
                if (i != 1 && _today[i] > now) { next = i; break; }   // sunrise isn't a prayer
            DateTime at = next >= 0 ? _today[next] : _tomorrow[0];
            int shown = next >= 0 ? next : 0;
            var left = at - now;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            chip.Label.Text = Lang.T(PrayerWidget.Names[shown]);
            chip.Value.Text = $"{(int)left.TotalHours}:{left.Minutes:00}:{left.Seconds:00}";
            chip.Sub.Text = PrayerWidget.Clock(at);
            chip.Value.Foreground = left.TotalMinutes <= 10 ? Warm : _ink;
            chip.Root.ToolTip = Lang.F("صلاة {0} الساعة {1} في {2}", Lang.T(PrayerWidget.Names[shown]), PrayerWidget.Clock(at), city.Shown);
        }

        private void ShowReading(Sensors.Reading r)
        {
            if (ChipOf("cpu") is { } cpu)
            {
                bool use = S.Shows("cpu"), heat = S.Shows("cputemp");
                cpu.Value.Text = use ? $"{Math.Round(r.Cpu)}%" : Temp(r.CpuTemp);
                cpu.Sub.Text = use && heat ? Temp(r.CpuTemp) : "";
                cpu.Value.Foreground = use ? (r.Cpu >= 90 ? Hot : _ink) : TempBrush(r.CpuTemp, 85, 95);
                if (use && heat) cpu.Sub.Foreground = TempBrush(r.CpuTemp, 85, 95, muted: true);
                cpu.Root.ToolTip = r.CpuTemp == null && heat
                    ? Lang.T("ويندوز ما يعطي حرارة المعالج الحقيقية في أغلب الأجهزة المكتبية. شغّل برنامج LibreHardwareMonitor (مجاني) وتطلع هنا لحالها.")
                    : Lang.T("المعالج: الاستهلاك والحرارة");
            }
            if (ChipOf("gpu") is { } gpu)
            {
                bool use = S.Shows("gpu"), heat = S.Shows("gputemp");
                gpu.Value.Text = use ? (r.Gpu is double g ? $"{Math.Round(g)}%" : "—") : Temp(r.GpuTemp);
                gpu.Sub.Text = use && heat ? Temp(r.GpuTemp) : "";
                gpu.Value.Foreground = use ? (r.Gpu >= 90 ? Hot : _ink) : TempBrush(r.GpuTemp, 80, 90);
                if (use && heat) gpu.Sub.Foreground = TempBrush(r.GpuTemp, 80, 90, muted: true);
            }
            if (ChipOf("ram") is { } ram)
            {
                double pct = r.RamTotal > 0 ? r.RamUsed * 100.0 / r.RamTotal : 0;
                ram.Value.Text = $"{Math.Round(pct)}%";
                ram.Sub.Text = $"{Gb(r.RamUsed)}/{Gb(r.RamTotal, 0)} GB";
                ram.Sub.FlowDirection = FlowDirection.LeftToRight;
                ram.Value.Foreground = pct >= 90 ? Hot : _ink;
            }
            if (ChipOf("disk") is { } disk)
            {
                double pct = r.DiskTotal > 0 ? r.DiskUsed * 100.0 / r.DiskTotal : 0;
                disk.Value.Text = $"{Math.Round(pct)}%";
                disk.Sub.Text = Lang.F("{0} GB فاضي", Gb(r.DiskTotal - r.DiskUsed, 0));
                disk.Value.Foreground = pct >= 95 ? Hot : _ink;
            }
            if (ChipOf("net") is { } net)
            {
                net.Value.Text = "↓ " + Speed(r.NetDown);
                net.Sub.Text = "↑ " + Speed(r.NetUp);
                net.Sub.FlowDirection = FlowDirection.LeftToRight;
            }
        }

        private static string Temp(double? c) => c is double v ? $"{Math.Round(v)}°" : "—";

        private Brush TempBrush(double? c, double warm, double hot, bool muted = false) =>
            c is double v && v >= hot ? Hot : c is double w && w >= warm ? Warm : muted ? _muted : _ink;

        private static string Gb(long bytes, int digits = 1) =>
            Math.Round(bytes / 1073741824.0, digits).ToString(digits == 0 ? "0" : "0.0", CultureInfo.InvariantCulture);

        private static string Speed(double bytesPerSecond)
        {
            if (bytesPerSecond >= 1024 * 1024) return (bytesPerSecond / 1048576).ToString("0.0", CultureInfo.InvariantCulture) + " MB/s";
            return Math.Round(bytesPerSecond / 1024) + " KB/s";
        }

        private static string SystemDrive() =>
            (System.IO.Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\").TrimEnd('\\');

        // ---- Right-click menu -------------------------------------------------------------------------

        private void ShowMenu()
        {
            var menu = new ContextMenu { FlowDirection = Lang.Flow };
            var shows = new MenuItem { Header = Lang.T("اللي يبان فيه") };
            foreach (var item in InfoBar.Items)
            {
                var it = item;
                var mi = new MenuItem { Header = Lang.T(it.Name), IsCheckable = true, IsChecked = S.Shows(it.Id), StaysOpenOnClick = true };
                mi.Click += (_, _) => S.SetShows(it.Id, mi.IsChecked);
                shows.Items.Add(mi);
            }
            menu.Items.Add(shows);
            // Widgets on the bar: a click on one opens it beside the bar.
            var widgets = new MenuItem { Header = Lang.T("الويدجتس فيه") };
            foreach (var kind in InfoBar.WidgetKinds)
            {
                var k = kind;
                var mi = new MenuItem { Header = k.Name, IsCheckable = true, IsChecked = S.HasWidget(k.Type), StaysOpenOnClick = true };
                mi.Click += (_, _) => S.SetWidget(k.Type, mi.IsChecked);
                widgets.Items.Add(mi);
            }
            menu.Items.Add(widgets);
            // The prayer times' city: only when this bar shows them.
            if (S.Shows("prayer") || S.HasWidget("prayer")) menu.Items.Add(PrayerWidget.CityMenu(() => InfoBar.City, c => InfoBar.City = c));

            // Where it is, all in one place: the edge, its end beside the taskbar, its order, its length.
            var where = new MenuItem { Header = Lang.T("مكانه") };
            string[] names = { "تحت", "فوق", "يسار", "يمين" };
            for (int i = 0; i < 4; i++)
            {
                int edge = i;
                var mi = new MenuItem { Header = Lang.T(names[i]), IsCheckable = true, IsChecked = _edge == i };
                mi.Click += (_, _) => S.Edge = edge;
                where.Items.Add(mi);
            }
            bool more = false;
            void Gap() { if (!more) { where.Items.Add(new Separator()); more = true; } }
            if (_share != null)
            {
                // Beside a taskbar piece: which end of the line it's at.
                string[] sides = Vertical ? new[] { "فوق", "تحت" } : new[] { "يسار", "يمين" };
                Gap();
                for (int i = 0; i < 2; i++)
                {
                    int v = i;
                    var mi = new MenuItem { Header = Lang.F("جنب التاسك بار: {0}", Lang.T(sides[i])), IsCheckable = true, IsChecked = S.Side == i };
                    mi.Click += (_, _) => S.Side = v;
                    where.Items.Add(mi);
                }
            }
            var ids = InfoBar.BarIds;
            if (ids.Count > 1)
            {
                int at = ids.IndexOf(S.Id);
                Gap();
                var earlier = new MenuItem { Header = Lang.T("قدّمه (أقرب لطرف الشاشة)"), IsEnabled = at > 0 };
                earlier.Click += (_, _) => InfoBar.MoveInOrder(S.Id, -1);
                var later = new MenuItem { Header = Lang.T("أخّره (أبعد عن طرف الشاشة)"), IsEnabled = at < ids.Count - 1 };
                later.Click += (_, _) => InfoBar.MoveInOrder(S.Id, 1);
                where.Items.Add(earlier);
                where.Items.Add(later);
            }
            if (_share != null ? S.ShareLength > 0 : S.Inset > 0)
            {
                Gap();
                var full = new MenuItem { Header = Lang.T(_share != null ? "رجّعه على قد اللي فيه" : "رجّعه بطوله الكامل") };
                full.Click += (_, _) => { if (_share != null) S.ShareLength = 0; else S.Inset = 0; };
                where.Items.Add(full);
            }
            menu.Items.Add(where);
            var cover = new MenuItem { Header = Lang.T("يختفي لما تكون نافذة تحته"), IsCheckable = true, IsChecked = CustomTaskbar.HideOverWindow };
            cover.Click += (_, _) => CustomTaskbar.HideOverWindow = cover.IsChecked;
            menu.Items.Add(cover);
            var streams = new MenuItem { Header = Lang.T("يظهر في البث"), IsCheckable = true, IsChecked = CustomTaskbar.ShowInStreams };
            streams.Click += (_, _) => CustomTaskbar.ShowInStreams = streams.IsChecked;
            menu.Items.Add(streams);
            menu.Items.Add(new Separator());
            var add = new MenuItem { Header = Lang.T("أضف تاسك بار إضافي ثاني") };
            add.Click += (_, _) => AddAnother();
            menu.Items.Add(add);
            var settings = new MenuItem { Header = Lang.T("الإعدادات…") };
            settings.Click += (_, _) => (Application.Current as App)?.ShowTaskbarSettings();
            menu.Items.Add(settings);
            var off = new MenuItem { Header = Lang.T(InfoBar.BarIds.Count > 1 ? "شيل هالتاسك بار" : "سكّر التاسك بار الإضافي") };
            off.Click += (_, _) => InfoBar.Remove(S.Id);
            menu.Items.Add(off);
            menu.PlacementTarget = this;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
        }

        /// <summary>One more bar; if no edge has room, says so.</summary>
        internal static void AddAnother()
        {
            if (InfoBar.Add()) return;
            MessageBox.Show(Lang.T("ما في مساحة كافية لتاسك بار ثاني على هالشاشة. شيل واحد أول."), "Wibn",
                            MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ---- Shorter or longer by dragging an end (like the taskbar) ---------------------------------
        //
        // The outer few pixels at either end can be dragged in or out; both ends move together. A
        // double-click there makes it full length again. Not for the dock (it's as long as what's on it).

        private const double EndZone = 7;        // DIPs at each end that can be dragged
        private const double MinLength = 120;    // never shorter than this (DIPs), nor than what's on it
        private double? _dragInset;              // while dragging: the length being tried (DIPs at each end)
        private double? _dragLen;                // beside a taskbar piece, while dragging its inner end: the length being tried (DIPs)
        private double _dragFrom, _dragFromInset;
        private int _dragSide;                    // -1 = start (left / top), 1 = end (right / bottom)
        private bool _resizePending, _resizing;

        /// <summary>How far each end comes in (pixels), kept so what's on the bar still fits.</summary>
        private int InsetPx(int fullLength)
        {
            double want = (_dragInset ?? S.Inset) * _scale;
            if (want <= 0) return 0;
            _list.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double content = Vertical ? _list.DesiredSize.Height : _list.DesiredSize.Width;
            double min = Math.Max(MinLength * _scale, content * _scale + 24 * _scale);
            double max = Math.Max(0, (fullLength - min) / 2);
            return (int)Math.Round(Math.Clamp(want, 0, max));
        }

        private bool CanResize => (_share != null || Shape != 2) && _placed.Width > 0;
        private double Along(Point screen) => Vertical ? screen.Y : screen.X;

        /// <summary>Which end the mouse is at: -1 start, 1 end, 0 neither.</summary>
        private int EndAt(Point screen)
        {
            if (!CanResize) return 0;
            double zone = EndZone * _scale, at = Along(screen);
            double start = Vertical ? _placed.Top : _placed.Left, end = Vertical ? _placed.Bottom : _placed.Right;
            // Beside a taskbar piece only its inner end moves (the outer one stays at the screen's end).
            if (_share is { } s) return s.Side == 0 ? (end - at <= zone ? 1 : 0) : (at - start <= zone ? -1 : 0);
            if (at - start <= zone) return -1;
            if (end - at <= zone) return 1;
            return 0;
        }

        private Point ScreenAt(MouseEventArgs e) => PointToScreen(e.GetPosition(this));

        private void HookEndDrag()
        {
            PreviewMouseMove += (_, e) =>
            {
                var p = ScreenAt(e);
                if (_resizePending && !_resizing && e.LeftButton == MouseButtonState.Pressed && Math.Abs(Along(p) - _dragFrom) > 4 * _scale)
                {
                    _resizing = true;
                    if (_share != null) { _dragFromInset = (Vertical ? _placed.Height : _placed.Width) / _scale; _dragLen = _dragFromInset; }
                    else { _dragFromInset = InsetPx(Vertical ? _full.Height : _full.Width) / _scale; _dragInset = _dragFromInset; }
                    CaptureMouse();
                }
                if (_resizing)
                {
                    double moved = (Along(p) - _dragFrom) * _dragSide / _scale;   // out = longer
                    if (_share is { } sh) _dragLen = Math.Clamp(_dragFromInset + moved, 0, (sh.SegEnd - sh.SegStart) / _scale);
                    else _dragInset = Math.Max(0, _dragFromInset - moved);   // toward the middle = shorter; both ends move together
                    var r = Target();
                    if (!Same(r, _placed)) { _placed = r; MoveWindow(); }
                    e.Handled = true;
                    return;
                }
                if (!_moving) Cursor = EndAt(p) != 0 ? (Vertical ? Cursors.SizeNS : Cursors.SizeWE) : null;
            };
            MouseLeave += (_, _) => { if (!_resizing && !_moving) Cursor = null; };
            PreviewMouseLeftButtonDown += (_, e) =>
            {
                var p = ScreenAt(e);
                int side = EndAt(p);
                if (side == 0) return;
                e.Handled = true;
                if (e.ClickCount == 2)
                {
                    _resizePending = false;
                    if (_share != null) { if (S.ShareLength != 0) S.ShareLength = 0; }   // just as long as what's on it again
                    else if (S.Inset != 0) S.Inset = 0;   // full length again
                    return;
                }
                _resizePending = true;
                _dragSide = side;
                _dragFrom = Along(p);
            };
            PreviewMouseLeftButtonUp += (_, e) =>
            {
                _resizePending = false;
                if (!_resizing) return;
                e.Handled = true;
                EndResize();
            };
            LostMouseCapture += (_, _) => { if (_resizing) EndResize(); };
        }

        private void EndResize()
        {
            _resizing = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            if (_share != null)
            {
                int len = (int)Math.Round((Vertical ? _placed.Height : _placed.Width) / _scale);
                _dragLen = null;
                S.ShareLength = len;
                return;
            }
            int inset = (int)Math.Round(InsetPx(Vertical ? _full.Height : _full.Width) / _scale);   // what it really ended at
            _dragInset = null;
            S.Inset = inset;   // saved, and the glass is redone for the new length
        }

        // ---- Moving it to another edge by dragging it (like the taskbar) ------------------------------
        //
        // Pressing on the bar and dragging shows where it would go (the screen edge nearest the mouse,
        // on any screen); letting go puts it there. The windows make room for it.

        private bool _movePending, _moving;
        private Point _moveFrom;
        private int _moveEdge, _moveSide;
        private InfoBar.POINT _moveAt;
        private EdgePreview? _edgePreview;
        private static readonly Color Accent = Color.FromRgb(0x4C, 0x8D, 0xFF);

        private void HookMoveDrag()
        {
            PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (e.Handled) return;   // an end: that makes it shorter instead
                _movePending = true;
                _moveFrom = ScreenAt(e);
            };
            PreviewMouseMove += (_, e) =>
            {
                if (!_movePending || e.LeftButton != MouseButtonState.Pressed) { _movePending = false; return; }
                var now = ScreenAt(e);
                if (!_moving)
                {
                    if (Math.Abs(now.X - _moveFrom.X) < 14 * _scale && Math.Abs(now.Y - _moveFrom.Y) < 14 * _scale) return;
                    _moving = true;
                    CaptureMouse();
                    _edgePreview ??= new EdgePreview();
                }
                PreviewEdge(now);
                e.Handled = true;
            };
            PreviewMouseLeftButtonUp += (_, e) =>
            {
                _movePending = false;
                if (!_moving) return;
                e.Handled = true;   // not a click on what's under it
                EndMove(apply: true);
            };
            LostMouseCapture += (_, _) => { if (_moving) EndMove(apply: true); };
            KeyDown += (_, e) => { if (_moving && e.Key == Key.Escape) EndMove(apply: false); };
        }

        private void PreviewEdge(Point screen)
        {
            var pt = new InfoBar.POINT { X = (int)screen.X, Y = (int)screen.Y };
            IntPtr mon = MonitorFromPoint(pt, 2);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return;
            var m = mi.rcMonitor;
            // How far the mouse is from each edge, as a part of the screen's size.
            double w = Math.Max(1, m.Right - m.Left), h = Math.Max(1, m.Bottom - m.Top);
            var d = new[] { (m.Bottom - pt.Y) / h, (pt.Y - m.Top) / h, (pt.X - m.Left) / w, (m.Right - pt.X) / w };
            int edge = 0;
            for (int i = 1; i < 4; i++) if (d[i] < d[edge]) edge = i;
            _moveEdge = edge;
            _moveAt = new InfoBar.POINT { X = (m.Left + m.Right) / 2, Y = (m.Top + m.Bottom) / 2 };
            // Its end of the edge (beside a taskbar piece there): the half the mouse is on.
            _moveSide = edge >= 2 ? (pt.Y < _moveAt.Y ? 0 : 1) : (pt.X < _moveAt.X ? 0 : 1);
            double scale = GetDpiForMonitor(mon, 0, out uint dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;
            int thick = (int)Math.Round((edge >= 2 ? CustomTaskbar.ThicknessDip + 40 : CustomTaskbar.ThicknessDip) * scale);
            var strip = edge switch
            {
                1 => new CustomTaskbar.RECT { Left = m.Left, Right = m.Right, Top = m.Top, Bottom = m.Top + thick },
                2 => new CustomTaskbar.RECT { Left = m.Left, Right = m.Left + thick, Top = m.Top, Bottom = m.Bottom },
                3 => new CustomTaskbar.RECT { Left = m.Right - thick, Right = m.Right, Top = m.Top, Bottom = m.Bottom },
                _ => new CustomTaskbar.RECT { Left = m.Left, Right = m.Right, Top = m.Bottom - thick, Bottom = m.Bottom },
            };
            _edgePreview?.ShowAt(strip, Accent);
        }

        private void EndMove(bool apply)
        {
            _moving = false;
            _movePending = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            _edgePreview?.Close();
            _edgePreview = null;
            if (apply && !S.MoveTo(_moveEdge, _moveAt, _moveSide))
                MessageBox.Show(Lang.T("ما في مساحة كافية على هالطرف. خلّيته مكانه."), "Wibn", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static bool Same(CustomTaskbar.RECT a, CustomTaskbar.RECT b) =>
            a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

        // ---- Native -----------------------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public CustomTaskbar.RECT rcMonitor, rcWork; public uint dwFlags; }
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(InfoBar.POINT p, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);
        [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
        [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out InfoBar.POINT p);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    }
}
