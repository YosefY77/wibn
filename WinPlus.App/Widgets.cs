using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

using System.Windows.Threading;

namespace WinPlus
{
    /// <summary>What a widget shows. It's told its size (in desktop icon cells) and
    /// when it's on screen, so it can update only then.</summary>
    internal abstract class WidgetContent : Border
    {
        public virtual void SetCells(int w, int h) { }
        public virtual void Start() { }
        public virtual void Stop() { }
        /// <summary>A maximized or full-screen window is over it (true), or it shows again (false):
        /// something heavy (sound, animation) can pause meanwhile.</summary>
        public virtual void Covered(bool covered) { }
        /// <summary>Glass with something light behind it: dark text (true), or light text (false).</summary>
        public virtual void SetLightBehind(bool light) { }
        /// <summary>The color its sliders and such use.</summary>
        public virtual void SetAccent(Color color) { }
        /// <summary>Its saved spot, size and options (set by its window before it starts).</summary>
        public Widgets.Info? Info { get; set; }
        /// <summary>Its own items at the top of its right-click menu.</summary>
        public virtual IEnumerable<Control> MenuItems() => Array.Empty<Control>();
        /// <summary>No card at all (no glass, no background, no edge): just what it draws, on the wallpaper.</summary>
        public virtual bool Bare => false;
        /// <summary>How small and big it can be, in cells (most widgets: 2 to 6 each way).</summary>
        public virtual (int MinW, int MinH, int MaxW, int MaxH) Limits => (Widgets.MinCells, Widgets.MinCells, Widgets.MaxCells, Widgets.MaxCells);
        /// <summary>The sizes in its "Size" menu; null = the squares 2 × 2 to 6 × 6.</summary>
        public virtual (int W, int H)[]? SizeChoices => null;
        /// <summary>The room between the card's edge and what it shows; null = the usual.</summary>
        public virtual Thickness? CardPadding => null;
        /// <summary>Set by its window: asks for a new size in cells (e.g. a bar turned from across to down).</summary>
        public Action<int, int>? RequestCells { get; set; }
    }

    /// <summary>
    /// Widgets: small windows on the desktop. They sit on a grid of square cells as tall
    /// as a desktop icon cell (so 2 × 2 is a square), starting where the icons start, so
    /// moving one lines it up with the other widgets and the icons' rows,
    /// and their size goes from 2×2 to 6×6 cells. They stay under every window and
    /// survive "show desktop" (owned by the desktop window). Saved in
    /// %LocalAppData%\WinPlus\widgets.json.
    /// </summary>
    internal static class Widgets
    {
        public const int MinCells = 2, MaxCells = 6;
        /// <summary>The widest any widget may be (a bar can be longer and thinner than the rest).</summary>
        public const int AnyMin = 1, AnyMax = 10;

        public sealed class Info
        {
            public string Id { get; set; } = Guid.NewGuid().ToString("N");
            public string Type { get; set; } = "";
            public int X { get; set; }
            public int Y { get; set; }
            public bool Placed { get; set; }
            /// <summary>The spot the user put it on (screen pixels). Only a drag or resize by the user
            /// changes it; where it is right now (X, Y) can differ for a while, e.g. while the work
            /// area is smaller at sign-in, and it goes back here once things settle.</summary>
            public int? WantX { get; set; }
            public int? WantY { get; set; }
            public int W { get; set; } = 3;
            public int H { get; set; } = 3;
            /// <summary>The widget's color (sliders and such) as #RRGGBB; empty = the app's blue.</summary>
            public string Color { get; set; } = "";
            /// <summary>The widget's own options (e.g. the prayer times' city).</summary>
            public Dictionary<string, string> Options { get; set; } = new();
        }

        public sealed record Kind(string Type, string Icon, string Name, string Description, int W, int H, Func<WidgetContent> Make);

        public static readonly Kind[] Kinds =
        {
            new("volume", "", Lang.T("أصوات البرامج"), Lang.T("صوت السماعات وكل برنامج يشغّل صوت، مع زر كتم. اضغط على أيقونة البرنامج عشان تكتمه"),
                3, 4, () => new VolumeWidget()),
            new("system", "", Lang.T("استهلاك الجهاز"), Lang.T("كم ياخذ المعالج والرام وكرت الشاشة من طاقتهم، وكم باقي في القرص. يتحدث كل ثانية"),
                3, 3, () => new SystemWidget()),
            new("prayer", "", Lang.T("مواقيت الصلاة"), Lang.T("أوقات الصلاة لمدينتك بتقويم أم القرى، والصلاة الجاية وكم باقي عليها. كليك يمين عليه عشان تختار مدينتك"),
                2, 2, () => new PrayerWidget()),
            new("media", "", Lang.T("يشتغل الحين"), Lang.T("الأغنية أو المقطع اللي يشتغل في أي برنامج (سبوتيفاي، يوتيوب في المتصفح…) مع صورته وأزرار التشغيل والتالي والسابق"),
                4, 2, () => new MediaWidget()),
            new("battery", "", Lang.T("البطاريات"), Lang.T("بطارية الماوس والكيبورد والسماعة (بلوتوث أو قطعة يو اس بي) وبطارية الجهاز إذا لابتوب. يتحدث كل نص دقيقة"),
                3, 3, () => new BatteryWidget()),
            new("weather", "", Lang.T("الطقس"), Lang.T("درجة الحرارة الحين والجو، والعظمى والصغرى، والرطوبة والرياح، والأيام الجاية. كليك يمين عليه عشان تختار مدينتك"),
                3, 4, () => new WeatherWidget()),
            new("picture", "", Lang.T("صورة"), Lang.T("صورة أو GIF من جهازك على سطح المكتب، والـ GIF يتحرك. كليك يمين عليه عشان تغيّرها أو تختار تملأ الويدجت أو تبان كاملة"),
                3, 3, () => new PictureWidget()),
            new("text", "", Lang.T("كتابة"), Lang.T("كلامك على سطح المكتب بدون إطار ولا خلفية. دبل كليك عليه عشان تكتب، وكليك يمين عشان تغيّر الخط والحجم واللون"),
                4, 2, () => new TextWidget()),
            new("shortcuts", "", Lang.T("اختصارات"), Lang.T("برامجك ومجلداتك المفضلة، كليك وحدة وتفتح. اسحبها عليه من أي مكان، أو كليك يمين عليه ← أضف"),
                3, 3, () => new ShortcutsWidget()),
            new("adhkar", "", Lang.T("أذكار"), Lang.T("ذكر مع مصدره، وكل ما ضغطت عليه يطلع لك ذكر ثاني عشوائي"),
                4, 2, () => new AdhkarWidget()),
            new("speed", "", Lang.T("سرعة الإنترنت"), Lang.T("سرعة التحميل والرفع والبنق. اضغط عليه عشان يقيس، ويقدر يقيس لحاله كل ساعة (من كليك يمين)"),
                3, 3, () => new SpeedWidget()),
            new("quickbar", "", Lang.T("شريط الإجراءات السريعة"), Lang.T("شريط أفقي أو عمودي فيه أزرار بكليك وحدة: برامج وملفات، لقطة شاشة، كتم المايك أو كتم نفسك في ديسكورد، قفل الجهاز وغيرها. كليك يمين عليه ← أضف"),
                5, 1, () => new QuickBarWidget()),
            new("waves", "", Lang.T("موجات الصوت"), Lang.T("موجات تتحرك مع الصوت اللي يطلع من السماعة أو لما تتكلم في المايك. كليك يمين عليه عشان تختار الجهاز والشكل"),
                4, 2, () => new AudioWavesWidget()),
            new("calendar", "", Lang.T("التقويم"), Lang.T("تاريخ اليوم بالميلادي أو الهجري ومواعيدك الجاية بألوانها. اضغط عليه يفتح التقويم في نص الشاشة وتضيف مواعيد مع تنبيه"),
                3, 3, () => new CalendarWidget()),
        };

        public static Kind? KindOf(string type) => Kinds.FirstOrDefault(k => k.Type == type);

        private static readonly List<Info> _list = new();
        private static readonly Dictionary<string, WidgetWindow> _windows = new();
        private static bool _running, _loaded;

        /// <summary>The list or a widget changed (for the page).</summary>
        public static event Action? Changed;

        public static IReadOnlyList<Info> All { get { Load(); return _list; } }

        // ---- On / off ---------------------------------------------------------------------------

        public static void Start()
        {
            Load();
            _running = true;
            // They come in one after another, from the top of the screen down.
            // Their intros wait until Wibn's start-up work is done (see IntroReady), so they're smooth.
            _introWaiting.Clear();
            _introGate = true;
            foreach (var info in _list) Show(info);
            var settle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(900) };
            settle.Tick += (_, _) =>
            {
                settle.Stop();
                // Then once nothing else is waiting on the UI thread.
                Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(OpenIntroGate));
            };
            settle.Start();
            if (_coverTimer == null)
            {
                _coverTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };   // a fallback: the events below do it right away
                _coverTimer.Tick += (_, _) => { try { CheckCovered(); } catch (Exception ex) { CrashLog.Write("widgets covered", ex); } };
            }
            _coverTimer.Start();
            HookWindowEvents();
            // At sign-in the desktop, its icons and the taskbars come up over several seconds, and
            // the grid and work area change meanwhile: put every widget on its spot again a few times.
            foreach (int s in new[] { 3, 10, 25 })
            {
                var again = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(s) };
                again.Tick += (_, _) => { again.Stop(); if (_running) { try { PlaceAll(); } catch (Exception ex) { CrashLog.Write("widgets place", ex); } } };
                again.Start();
            }
            DesktopCorner.Start();
            DesktopMenu.Start();
        }

        public static void Stop()
        {
            _running = false;
            _coverTimer?.Stop();
            UnhookWindowEvents();
            foreach (var w in _windows.Values.ToList()) w.CloseByUs();
            _windows.Clear();
            DesktopCorner.Stop();
            DesktopMenu.Stop();
        }

        // ---- Intros at start-up -------------------------------------------------------------------

        private static bool _introGate;
        private static readonly List<WidgetWindow> _introWaiting = new();

        /// <summary>A widget's window is loaded and ready for its intro: during start-up it waits for
        /// the others (they then come together, top of the screen first); otherwise it plays now.</summary>
        public static void IntroReady(WidgetWindow w)
        {
            if (_introGate) { _introWaiting.Add(w); return; }
            w.PlayIntro(TimeSpan.Zero, prepare: false);
        }

        private static void OpenIntroGate()
        {
            _introGate = false;
            int order = 0;
            foreach (var w in _introWaiting.Where(w => w.IsLoaded).OrderBy(w => w.Info.Y).ThenBy(w => w.Info.X))
                w.PlayIntro(TimeSpan.FromMilliseconds(AnimSettings.GapMs * order++), prepare: false);
            _introWaiting.Clear();
        }

        /// <summary>Every widget on screen plays its intro again (the Animations page's "try it").</summary>
        public static void ReplayIntro()
        {
            int order = 0;
            foreach (var w in _windows.Values.Where(w => w.IsVisible).OrderBy(w => w.Info.Y).ThenBy(w => w.Info.X))
                w.PlayIntro(TimeSpan.FromMilliseconds(AnimSettings.GapMs * order++));
        }

        // ---- Hidden under maximized / full-screen windows ----------------------------------------
        //
        // A widget under a maximized or full-screen window can't be seen anyway: it's hidden and its
        // glass stops (its pictures are freed), and it comes back when that window is gone.

        private static DispatcherTimer? _coverTimer;
        private static WinEventProc? _eventProc;   // kept alive while hooked
        private static readonly List<IntPtr> _hooks = new();
        private static bool _checkPending;

        /// <summary>
        /// Windows tells Wibn the moment a window is maximized, restored, minimized, closed, made
        /// active or moved: the widgets are checked right then, so they're back before the window
        /// has finished shrinking (the timer is only a fallback).
        /// </summary>
        private static void HookWindowEvents()
        {
            if (_hooks.Count > 0) return;
            _eventProc ??= OnWindowEvent;
            void Hook(uint min, uint max)
            {
                IntPtr h = SetWinEventHook(min, max, IntPtr.Zero, _eventProc, 0, 0, 0x0002 /* OUTOFCONTEXT | SKIPOWNPROCESS */);
                if (h != IntPtr.Zero) _hooks.Add(h);
            }
            Hook(0x0003, 0x0003);   // EVENT_SYSTEM_FOREGROUND
            Hook(0x0016, 0x0017);   // EVENT_SYSTEM_MINIMIZESTART / END
            Hook(0x8001, 0x8003);   // EVENT_OBJECT_DESTROY / SHOW / HIDE
            Hook(0x800B, 0x800B);   // EVENT_OBJECT_LOCATIONCHANGE (maximize, restore, full screen)
            Hook(0x8017, 0x8018);   // EVENT_OBJECT_CLOAKED / UNCLOAKED (virtual desktops)
        }

        private static void UnhookWindowEvents()
        {
            foreach (var h in _hooks) UnhookWinEvent(h);
            _hooks.Clear();
        }

        private static void OnWindowEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (hwnd == IntPtr.Zero || idObject != 0 /* OBJID_WINDOW */ || idChild != 0) return;
            if (GetAncestor(hwnd, 2 /* GA_ROOT */) != hwnd) return;   // only top-level windows
            if (_checkPending) return;   // many events at once: one check
            _checkPending = true;
            Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                _checkPending = false;
                try { if (_running) CheckCovered(); } catch (Exception ex) { CrashLog.Write("widgets covered", ex); }
            }));
        }

        private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);

        private static void CheckCovered()
        {
            if (_windows.Count == 0) return;
            var covered = CoveredScreens();
            foreach (var w in _windows.Values) w.SetCovered(covered.Contains(w.Screen()));
        }

        /// <summary>The screens that have a maximized window, or the active window in full screen.</summary>
        private static HashSet<IntPtr> CoveredScreens()
        {
            var set = new HashSet<IntPtr>();
            IntPtr fg = GetForegroundWindow();
            uint me = (uint)Environment.ProcessId;
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h) || IsIconic(h)) return true;
                GetWindowThreadProcessId(h, out uint pid);
                if (pid == me) return true;
                long ex = GetWindowLongPtr(h, -20).ToInt64();
                if ((ex & (0x80 /* TOOLWINDOW */ | 0x20 /* TRANSPARENT */ | 0x08000000 /* NOACTIVATE */)) != 0) return true;   // overlays
                if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true;   // other desktops, suspended apps
                var cls = new System.Text.StringBuilder(64);
                GetClassName(h, cls, cls.Capacity);
                if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
                IntPtr mon = MonitorFromWindow(h, 2 /* NEAREST */);
                if (IsZoomed(h)) { set.Add(mon); return true; }
                if (h == fg && GetWindowRect(h, out var r))
                {
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(mon, ref mi) && r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
                        r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom) set.Add(mon);
                }
                return true;
            }, IntPtr.Zero);
            return set;
        }

        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr l);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int max);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);

        /// <summary>Every widget on its spot again (e.g. Wibn's taskbar moved its ends).</summary>
        public static void PlaceAll()
        {
            foreach (var w in _windows.Values) w.Place();
        }

        /// <summary>The glass settings changed: redo the glass of every widget.</summary>
        public static void RefreshGlass()
        {
            foreach (var w in _windows.Values) w.RestartGlass();
            CustomTaskbar.RefreshGlass();   // Wibn's taskbar uses the same glass
        }

        // ---- The widgets' background color (all of them) -------------------------------------------

        public enum BackMode { Default = 0, Taskbar = 1, Custom = 2 }

        private const string BackKey = @"Software\WinPlus\Widgets";

        /// <summary>Default (the glass's dark tint, or dark gray), the taskbar's color, or one picked.</summary>
        public static BackMode Back
        {
            get
            {
                try { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(BackKey); return k?.GetValue("BackMode") is int v && v is >= 0 and <= 2 ? (BackMode)v : BackMode.Default; }
                catch { return BackMode.Default; }
            }
            set
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(BackKey);
                k.SetValue("BackMode", (int)value, Microsoft.Win32.RegistryValueKind.DWord);
            }
        }

        /// <summary>The picked color, 0xFFRRGGBB.</summary>
        public static uint BackCustom
        {
            get
            {
                try { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(BackKey); return k?.GetValue("BackColor") is int v ? unchecked((uint)v) : 0xFF2B3A55; }
                catch { return 0xFF2B3A55; }
            }
            set
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(BackKey);
                k.SetValue("BackColor", unchecked((int)value), Microsoft.Win32.RegistryValueKind.DWord);
            }
        }

        /// <summary>The widgets' color right now (null = the default look), and how strongly it covers the glass.</summary>
        public static (Color Color, double Alpha)? BackColor()
        {
            (Color Color, double Alpha)? c = Back switch
            {
                BackMode.Taskbar => TaskbarColor(),
                BackMode.Custom => (FromArgb(BackCustom), 1.0),
                _ => null,
            };
            if (c is not { } v) return null;
            // Light enough that the glass still shows through it: as strong as the glass's own
            // "darken" setting plus a little (34% → 44%), never more than the color's own opacity.
            double glass = Math.Clamp(GlassSettings.SharedTint / 100.0 + 0.10, 0.20, 0.60);
            return (v.Color, Math.Min(v.Alpha, glass));
        }

        /// <summary>The taskbar's color: Wibn's own when its taskbar color is on (its see-through
        /// amount too), otherwise Windows' (the accent color if "show it on the taskbar" is on,
        /// else dark or light gray with the theme).</summary>
        public static (Color Color, double Alpha) TaskbarColor()
        {
            if (SettingsStore.ColorEnabled)
            {
                uint c = SettingsStore.Color;
                return (FromArgb(c), Math.Clamp((c >> 24) / 255.0, 0.35, 0.92));
            }
            try
            {
                using var p = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                bool accent = p?.GetValue("ColorPrevalence") is int a && a == 1;
                bool light = p?.GetValue("SystemUsesLightTheme") is int l && l == 1;
                if (accent)
                {
                    using var e = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
                    if (e?.GetValue("AccentColorMenu") is int abgr)   // what Start and the taskbar use (ABGR)
                        return (Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16)), 0.85);
                }
                return (light ? Color.FromRgb(0xEE, 0xEE, 0xEE) : Color.FromRgb(0x20, 0x20, 0x20), 0.85);
            }
            catch { return (Color.FromRgb(0x20, 0x20, 0x20), 0.85); }
        }

        // ---- The widgets' font (all of them) -------------------------------------------------------

        public const string DefaultFont = "Segoe UI";

        /// <summary>The font family's name, as Windows knows it.</summary>
        public static string FontName
        {
            get
            {
                try { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(BackKey); return k?.GetValue("Font") is string s && s.Length > 0 ? s : DefaultFont; }
                catch { return DefaultFont; }
            }
            set
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(BackKey);
                k.SetValue("Font", value, Microsoft.Win32.RegistryValueKind.String);
                foreach (var w in _windows.Values) w.FontFamily = Font();
            }
        }

        /// <summary>The chosen font; if it was uninstalled, the default. Letters it doesn't have
        /// (e.g. Arabic in an English-only font) come from Windows' fallback fonts.</summary>
        public static FontFamily Font()
        {
            string name = FontName;
            bool there = Fonts.SystemFontFamilies.Any(f => string.Equals(f.Source, name, StringComparison.OrdinalIgnoreCase));
            return new FontFamily(there ? name : DefaultFont);
        }

        private static Color FromArgb(uint c) => Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c);

        /// <summary>How light a color is (0–1), to pick dark or light text on it.</summary>
        public static double Lightness(Color c) => (c.R * 3 + c.G * 6 + c.B) / 10.0 / 255;

        public static void Add(string type)
        {
            var kind = KindOf(type);
            if (kind == null) return;
            Load();
            var info = new Info { Type = type, W = kind.W, H = kind.H };
            _list.Add(info);
            Save();
            if (_running) Show(info);
            Changed?.Invoke();
        }

        public static void Remove(string id)
        {
            Load();
            _list.RemoveAll(i => i.Id == id);
            if (_windows.Remove(id, out var w)) w.CloseByUs();
            PictureWidget.DeleteFiles(id);
            Save();
            Changed?.Invoke();
        }

        private static void Show(Info info)
        {
            if (_windows.ContainsKey(info.Id)) return;
            var kind = KindOf(info.Type);
            if (kind == null) return;
            WidgetWindow window;
            try { window = new WidgetWindow(info, kind.Make()); }
            catch (Exception ex) { CrashLog.Write("widget " + info.Type, ex); return; }
            _windows[info.Id] = window;
            window.Closed += (_, _) =>
            {
                if (_windows.TryGetValue(info.Id, out var w) && w == window) _windows.Remove(info.Id);
                // Closed by Windows (e.g. Explorer restarted and took the desktop with it): bring it back.
                if (!window.ClosedByUs && _running && _list.Contains(info))
                {
                    var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    t.Tick += (_, _) => { t.Stop(); if (_running && _list.Contains(info)) Show(info); };
                    t.Start();
                }
            };
            window.Show();
        }

        // ---- Saving -----------------------------------------------------------------------------

        private static string FilePath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPlus", "widgets.json");

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                var list = JsonSerializer.Deserialize<List<Info>>(File.ReadAllText(FilePath));
                if (list == null) return;
                foreach (var i in list.Where(i => KindOf(i.Type) != null))
                {
                    i.W = Math.Clamp(i.W, AnyMin, AnyMax);   // each widget then keeps to its own limits
                    i.H = Math.Clamp(i.H, AnyMin, AnyMax);
                    _list.Add(i);
                }
            }
            catch (Exception ex) { CrashLog.Write("widgets load", ex); }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_list));
            }
            catch (Exception ex) { CrashLog.Write("widgets save", ex); }
            Changed?.Invoke();
        }

        // ---- The desktop's icon grid (real pixels) ----------------------------------------------

        /// <summary>One cell is Sx × Sy; the cells start at Ox, Oy (screen coordinates).</summary>
        /// <remarks>
        /// With Wibn's floating taskbar the columns are stretched a hair (StepX) so they fill exactly
        /// from the bar's left end to its right end: every gap stays the same, and the far widgets
        /// line up with the bar's ends.
        /// </remarks>
        public readonly record struct Grid(int Sx, int Sy, int Ox, int Oy, double Fx = 0, double Fy = 0, int RowsTop = 0, int RowsBottom = 0, int MainBottom = int.MinValue)
        {
            public double StepX => Fx > 0 ? Fx : Sx;
            public double StepY => Fy > 0 ? Fy : Sy;
            /// <summary>Where row k starts.</summary>
            public int RowY(int k) => Oy + (int)Math.Round(k * StepY);
            /// <summary>How tall r rows are.</summary>
            public int High(int r) => (int)Math.Round(r * StepY);
            /// <summary>Where column k starts.</summary>
            public int ColX(int k) => Ox + (int)Math.Round(k * StepX);
            /// <summary>How wide c columns are.</summary>
            public int Wide(int c) => (int)Math.Round(c * StepX);
        }

        public static Grid GetGrid()
        {
            using var _ = new ScreenCapture.DpiScope();
            IntPtr list = DesktopList();
            int lx = 0, ly = 0, sx = 0, sy = 0;
            if (list != IntPtr.Zero)
            {
                if (GetWindowRect(list, out var r)) { lx = r.Left; ly = r.Top; }
                long spacing = SendMessage(list, 0x1033 /* LVM_GETITEMSPACING */, IntPtr.Zero, IntPtr.Zero).ToInt64();
                sx = (int)(spacing & 0xFFFF);
                sy = (int)((spacing >> 16) & 0xFFFF);
            }
            if (sx < 24 || sy < 24)
            {
                var sp = DesktopIcons.Spacing();
                if (sp is { } p && p.X >= 24 && p.Y >= 24) { sx = p.X; sy = p.Y; }
                else
                {
                    double scale = GetDpiForSystem() / 96.0;
                    sx = (int)(76 * scale);
                    sy = (int)(100 * scale);
                }
            }
            int ox = lx, oy = ly;
            if (DesktopIcons.AnyItemPosition() is { } at)
            {
                ox = lx + Mod(at.X, sx);
                oy = ly + Mod(at.Y, sy);
            }
            // Square cells, so a 2 × 2 widget (any widget) is a square: as tall as an icon
            // cell, the rows line up with the icons' rows.
            int cell = Math.Max(sx, sy);

            var wa = WorkArea(0, 0);
            int cx = (wa.Left + wa.Right) / 2, cy = (wa.Top + wa.Bottom) / 2;
            int inset = (int)Math.Round(4 * GetDpiForSystem() / 96.0);   // the cards sit 4 px inside their windows
            double fx = 0, fy = 0;
            int rowsTop = 0, rowsBottom = 0;

            // Wibn's floating taskbar on the main screen: the columns span exactly its width.
            if (CustomTaskbar.FloatingEnds(cx, cy) is { } ends)
            {
                int span = ends.Right - ends.Left + 2 * inset;
                int n = Math.Max(1, (int)Math.Round(span / (double)cell));
                ox = ends.Left - inset;
                fx = span / (double)n;
            }

            // Wibn's taskbar on the main screen: the rows stop as far from it as the widgets are from
            // each other (two card insets), stretched a hair so they still fill the screen evenly.
            if (CustomTaskbar.BarOn(cx, cy) is { } bar)
            {
                bool barAtTop = bar.Top + bar.Bottom < wa.Top + wa.Bottom;
                int first = oy + (int)Math.Ceiling((wa.Top - oy) / (double)cell) * cell;   // the first icon row
                int top = barAtTop ? bar.Bottom + inset : first;
                int bottom = barAtTop ? wa.Bottom : bar.Top - inset;
                int span = bottom - top;
                if (span > cell)
                {
                    int n = Math.Max(1, (int)Math.Round(span / (double)cell));
                    oy = top;
                    fy = span / (double)n;
                    rowsTop = top; rowsBottom = bottom;
                }
            }
            return new Grid(cell, cell, ox, oy, fx, fy, rowsTop, rowsBottom, rowsBottom != 0 ? wa.Bottom : int.MinValue);
        }

        private static int Mod(int a, int m) => ((a % m) + m) % m;

        /// <summary>The nearest grid spot for a w × h widget at (x, y), kept inside its screen's work area.</summary>
        public static (int X, int Y) Snap(Grid g, int x, int y, int w, int h)
        {
            var wa = WorkArea(x + w / 2, y + h / 2);
            int kx = (int)Math.Round((x - g.Ox) / g.StepX);
            int ky = (int)Math.Round((y - g.Oy) / g.StepY);
            int minX = (int)Math.Ceiling((wa.Left - g.Ox) / g.StepX - 0.01), maxX = (int)Math.Floor((wa.Right - w - g.Ox) / g.StepX + 0.01);
            int waTop = wa.Top, waBottom = wa.Bottom;
            if (g.RowsBottom != 0 && wa.Bottom == g.MainBottom) { waTop = g.RowsTop; waBottom = g.RowsBottom; }   // the screen with Wibn's taskbar
            int minY = (int)Math.Ceiling((waTop - g.Oy) / g.StepY - 0.01), maxY = (int)Math.Floor((waBottom - h - g.Oy) / g.StepY + 0.01);
            kx = maxX < minX ? minX : Math.Clamp(kx, minX, maxX);
            ky = maxY < minY ? minY : Math.Clamp(ky, minY, maxY);
            return (g.ColX(kx), g.RowY(ky));
        }

        /// <summary>A free spot for a new widget: from the top right of the main screen, down then left.</summary>
        public static (int X, int Y) FreeSpot(Grid g, int w, int h, string selfId)
        {
            var taken = _windows.Values.Where(v => v.Info.Id != selfId && v.Info.Placed)
                .Select(v => new System.Drawing.Rectangle(v.Info.X, v.Info.Y, g.Wide(v.Info.W), g.High(v.Info.H))).ToList();
            var wa = WorkArea(0, 0);
            var (x0, y0) = Snap(g, wa.Right - w, wa.Top, w, h);
            for (int col = 0; col < 40; col++)
            {
                int x = x0 - (int)Math.Round(col * g.StepX);
                if (x < wa.Left) break;
                for (int row = 0; ; row++)
                {
                    int y = y0 + (int)Math.Round(row * g.StepY);
                    if (y + h > wa.Bottom) break;
                    var r = new System.Drawing.Rectangle(x, y, w, h);
                    if (!taken.Any(t => t.IntersectsWith(r))) return (x, y);
                }
            }
            return (x0, y0);
        }

        private static RECT WorkArea(int x, int y)
        {
            using var _ = new ScreenCapture.DpiScope();
            IntPtr mon = MonitorFromPoint(new POINT { X = x, Y = y }, 2 /* NEAREST */);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            return GetMonitorInfo(mon, ref mi) ? mi.rcWork : new RECT { Right = 1920, Bottom = 1040 };
        }

        /// <summary>The window that holds the desktop icons (SysListView32).</summary>
        private static IntPtr DesktopList()
        {
            IntPtr view = FindWindowEx(DesktopOwner(), IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view == IntPtr.Zero)
            {
                IntPtr worker = IntPtr.Zero;
                while ((worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
                {
                    view = FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (view != IntPtr.Zero) break;
                }
            }
            return view == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
        }

        /// <summary>The desktop window (Progman). Widgets owned by it stay with the desktop.</summary>
        public static IntPtr DesktopOwner() => FindWindow("Progman", null);

        // ---- Win32 ------------------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
        [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
    }

    /// <summary>The window around one widget: a rounded card, moved by dragging it
    /// anywhere empty, resized by its corner, always on the icon grid.</summary>
    internal sealed class WidgetWindow : Window
    {
        public readonly Widgets.Info Info;
        private readonly WidgetContent _content;
        private readonly Border _card;
        private readonly FrameworkElement _grip;
        private IntPtr _hwnd;
        private Widgets.Grid _grid;
        private bool _moving, _sizing;
        private Widgets.POINT _startCursor;
        private Widgets.RECT _startRect;
        public bool ClosedByUs { get; private set; }
        private LiquidGlass? _glass;
        private readonly DispatcherTimer _glassTimer, _wallpaperTimer;
        private static readonly Brush PlainBack = new SolidColorBrush(Color.FromArgb(0xE8, 0x1E, 0x1F, 0x22));
        private static readonly Brush PlainEdge = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
        private static readonly Brush GlassEdge = new LinearGradientBrush(
            Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 90);

        public WidgetWindow(Widgets.Info info, WidgetContent content)
        {
            Info = info;
            _content = content;
            content.Info = info;
            var lim = content.Limits;
            info.W = Math.Clamp(info.W, lim.MinW, lim.MaxW);
            info.H = Math.Clamp(info.H, lim.MinH, lim.MaxH);
            content.RequestCells = (cw, ch) => { if (_hwnd == IntPtr.Zero) return; _grid = Widgets.GetGrid(); Resize(cw, ch, save: true); };
            Title = "Wibn widget";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Width = Height = 200;
            FontFamily = Widgets.Font();   // the text inherits it; icons keep their own icon font
            Foreground = (Brush)Application.Current.FindResource("Fg");

            _card = new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = PlainBack,
                BorderBrush = PlainEdge,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(4),
                Padding = content.CardPadding ?? new Thickness(10, 8, 10, 8),
                Child = content,
                FlowDirection = Lang.Flow,
                ClipToBounds = false,
            };
            _card.ContextMenu = BuildMenu();
            _content.SetAccent(AccentOf(info));

            var gripLines = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 11,1 L 1,11 M 11,5 L 5,11 M 11,9 L 9,11"),
                Stroke = (Brush)Application.Current.FindResource("Muted"),
                StrokeThickness = 1.3,
                Margin = new Thickness(2),
            };
            _grip = new Border
            {
                Width = 18, Height = 18, Background = Brushes.Transparent, Cursor = Cursors.SizeNWSE,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 6, 6), Opacity = 0, Child = gripLines,
                ToolTip = Lang.T("اسحب عشان تغيّر الحجم"),
            };
            _grip.MouseLeftButtonDown += (_, e) => { Begin(sizing: true); e.Handled = true; };

            // The card and its grip fill the widget's cells, like every widget.
            var frame = new Grid();
            frame.Children.Add(_card);
            frame.Children.Add(_grip);
            var root = new Grid { FlowDirection = FlowDirection.LeftToRight, Background = Brushes.Transparent };
            WidgetIntro.Prepare(root);   // hidden until its intro plays
            root.Children.Add(frame);
            Content = root;

            MouseEnter += (_, _) => { _grip.Opacity = 1; if (_content.Bare) _card.BorderBrush = BareEdge; };
            MouseLeave += (_, _) =>
            {
                if (_sizing) return;
                _grip.Opacity = 0;
                if (_content.Bare && !Typing) _card.BorderBrush = Brushes.Transparent;
            };
            if (content.Bare)
            {
                // Nothing to see, but still all there for the mouse (fully clear pixels can't be clicked);
                // its edge shows only while the mouse is over it, so you can find it to move or resize.
                _card.Background = BareBack;
                _card.BorderBrush = Brushes.Transparent;
            }
            MouseLeftButtonDown += (_, e) => { if (!e.Handled) { Begin(sizing: false); e.Handled = true; } };
            MouseMove += (_, _) => Track();
            MouseLeftButtonUp += (_, _) => End();
            LostMouseCapture += (_, _) => End();

            // The glass copies what's behind from a fixed spot: redo it a moment after a move or a resize.
            _glassTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _glassTimer.Tick += (_, _) => { _glassTimer.Stop(); StartGlass(); };
            _wallpaperTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
            _wallpaperTimer.Tick += (_, _) => OnWallpaperTick();
            _wallpaperTimer.Start();

            SourceInitialized += (_, _) => OnSourceInitialized();
            Loaded += (_, _) => { _content.Start(); RestartGlass(); Widgets.IntroReady(this); };
            Closed += (_, _) => { _content.Stop(); StopGlass(); _glassTimer.Stop(); _wallpaperTimer.Stop(); };
        }

        public void CloseByUs()
        {
            ClosedByUs = true;
            Close();
        }

        private static readonly Brush BareBack = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0));
        private static readonly Brush BareEdge = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF));

        /// <summary>Taking the keyboard (a widget you type in). Widgets normally never take it, so
        /// clicking one doesn't pull focus from the window you're in.</summary>
        public bool Typing { get; private set; }

        public void BeginTyping()
        {
            if (_hwnd == IntPtr.Zero || Typing) return;
            Typing = true;
            long ex = GetWindowLongPtr(_hwnd, -20).ToInt64();
            SetWindowLongPtr(_hwnd, -20, new IntPtr(ex & ~0x08000000L /* NOACTIVATE */));
            if (_content.Bare) _card.BorderBrush = BareEdge;
            Activate();
        }

        public void EndTyping()
        {
            if (_hwnd == IntPtr.Zero || !Typing) return;
            Typing = false;
            long ex = GetWindowLongPtr(_hwnd, -20).ToInt64();
            SetWindowLongPtr(_hwnd, -20, new IntPtr(ex | 0x08000000L));
            if (_content.Bare && !IsMouseOver) _card.BorderBrush = Brushes.Transparent;
        }

        private void OnSourceInitialized()
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            // Off Alt+Tab, and owned by the desktop so "show desktop" leaves it there.
            long ex = GetWindowLongPtr(_hwnd, -20).ToInt64();
            SetWindowLongPtr(_hwnd, -20, new IntPtr((ex | 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */) & ~0x40000L /* APPWINDOW */));
            IntPtr owner = Widgets.DesktopOwner();
            if (owner != IntPtr.Zero) SetWindowLongPtr(_hwnd, -8 /* GWLP_HWNDPARENT */, owner);
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            Place();
        }

        /// <summary>Puts it on the grid at its saved spot and size (or a free spot if it's new).</summary>
        public void Place()
        {
            if (_hwnd == IntPtr.Zero) return;
            _grid = Widgets.GetGrid();
            int w = _grid.Wide(Info.W), h = _grid.High(Info.H);
            if (Info.Placed) { Info.WantX ??= Info.X; Info.WantY ??= Info.Y; }   // saved before the wanted spot existed
            var (x, y) = Info.Placed ? Widgets.Snap(_grid, Info.WantX!.Value, Info.WantY!.Value, w, h) : Widgets.FreeSpot(_grid, w, h, Info.Id);
            bool first = !Info.Placed;
            if (first) { Info.WantX = x; Info.WantY = y; }
            Widgets.RECT now;
            using (new ScreenCapture.DpiScope()) Widgets.GetWindowRect(_hwnd, out now);
            bool same = now.Left == x && now.Top == y && now.Right - now.Left == w && now.Bottom - now.Top == h;
            Info.X = x; Info.Y = y; Info.Placed = true;
            if (same && !first) return;   // already there: nothing moves, nothing redrawn
            Move(x, y, w, h);
            RestartGlass();
            _content.SetCells(Info.W, Info.H);
            Widgets.Save();
        }

        // ---- Liquid glass ------------------------------------------------------------------------

        /// <summary>Plays its intro again (from hidden, top down).</summary>
        public void PlayIntro(TimeSpan delay, bool prepare = true)
        {
            if (Content is not System.Windows.Controls.Grid root) return;
            if (prepare) WidgetIntro.Prepare(root);
            WidgetIntro.Play(root, delay);
        }

        // ---- Hidden under a maximized / full-screen window --------------------------------------

        private bool _covered;

        /// <summary>The screen it's on.</summary>
        public IntPtr Screen() => _hwnd == IntPtr.Zero ? IntPtr.Zero : Widgets.MonitorFromWindow(_hwnd, 2 /* NEAREST */);

        /// <summary>Hides it (and stops its glass) while a window covers its screen; shows it again after.</summary>
        public void SetCovered(bool covered)
        {
            if (covered == _covered || _hwnd == IntPtr.Zero) return;
            if (covered && (_moving || _sizing || Typing)) return;
            _covered = covered;
            if (covered)
            {
                _glassTimer.Stop();
                StopGlass();
                _content.Covered(true);
                Hide();
            }
            else
            {
                Show();
                SetWindowPos(_hwnd, new IntPtr(1) /* BOTTOM */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /* NOSIZE | NOMOVE | NOACTIVATE */);
                _content.Covered(false);
                RestartGlass();
            }
        }

        /// <summary>Redo the glass soon (after a move, a resize, or new settings).</summary>
        public void RestartGlass()
        {
            StopGlass();
            _glassTimer.Stop();
            _glassTimer.Start();
        }

        /// <summary>Stops the glass; its last picture stays on the card until a new one comes (no flash).</summary>
        private void StopGlass()
        {
            _glass?.Dispose();
            _glass = null;
        }

        /// <summary>No glass: dark gray, or the widgets' color (dark text on a light one).</summary>
        private void ShowPlain()
        {
            if (_content.Bare) return;
            var back = Widgets.BackColor();
            _card.Background = back is { } b ? new SolidColorBrush(Color.FromArgb(0xF0, b.Color.R, b.Color.G, b.Color.B)) : PlainBack;
            _card.BorderBrush = PlainEdge;
            _content.SetLightBehind(back is { } l && Widgets.Lightness(l.Color) > 0.6);
        }

        private void StartGlass()
        {
            StopGlass();
            if (_moving || _sizing) return;
            if (_content.Bare) return;
            if (!IsVisible || !GlassSettings.Widgets.On) { ShowPlain(); return; }
            try
            {
                // The desktop itself is copied (not the screen), so windows over it don't matter.
                // Where the card really is in the window (a square one sits in the middle).
                var host = (FrameworkElement)Content;
                Point at = _card.TranslatePoint(new Point(0, 0), host);
                var glass = new LiquidGlass(this, host, _card, new Thickness(at.X, at.Y, 0, 0), 14, GlassSettings.Widgets, desktop: true,
                                            tint: Widgets.BackColor());
                if (!glass.Available) { ShowPlain(); return; }
                _glass = glass;
                _card.BorderBrush = GlassEdge;
                glass.LightBehindChanged += light => _content.SetLightBehind(light);
                glass.Start();
            }
            catch (Exception ex) { ShowPlain(); CrashLog.Write("widget glass", ex); }
        }

        /// <summary>Every minute: a new picture of the wallpaper (slideshows).</summary>
        private void OnWallpaperTick()
        {
            if (_glass != null && !_moving && !_sizing && IsVisible) _glass.Refresh();
        }

        private void Move(int x, int y, int w, int h)
        {
            using var _ = new ScreenCapture.DpiScope();
            SetWindowPos(_hwnd, new IntPtr(1) /* BOTTOM */, x, y, w, h, 0x0010 /* NOACTIVATE */);
        }

        // ---- Moving and resizing ----------------------------------------------------------------

        private void Begin(bool sizing)
        {
            if (_hwnd == IntPtr.Zero) return;
            _grid = Widgets.GetGrid();
            using (new ScreenCapture.DpiScope())
            {
                Widgets.GetCursorPos(out _startCursor);
                Widgets.GetWindowRect(_hwnd, out _startRect);
            }
            _sizing = sizing;
            _moving = !sizing;
            // The glass stays: it follows a move, and is redone for the new size when it lands.
            CaptureMouse();
        }

        private void Track()
        {
            if (!_moving && !_sizing) return;
            Widgets.POINT p;
            using (new ScreenCapture.DpiScope()) Widgets.GetCursorPos(out p);
            int dx = p.X - _startCursor.X, dy = p.Y - _startCursor.Y;
            if (_moving)
            {
                int w = _grid.Wide(Info.W), h = _grid.High(Info.H);
                var (x, y) = Widgets.Snap(_grid, _startRect.Left + dx, _startRect.Top + dy, w, h);
                if (x != Info.X || y != Info.Y)
                {
                    Info.X = x; Info.Y = y;
                    Move(x, y, w, h);
                    _glass?.Follow();
                }
            }
            else
            {
                var lim = _content.Limits;
                int cw = Math.Clamp((int)Math.Round((_startRect.Right - _startRect.Left + dx) / _grid.StepX), lim.MinW, lim.MaxW);
                int ch = Math.Clamp((int)Math.Round((_startRect.Bottom - _startRect.Top + dy) / _grid.StepY), lim.MinH, lim.MaxH);
                if (cw != Info.W || ch != Info.H) Resize(cw, ch, save: false);
            }
        }

        private void End()
        {
            if (!_moving && !_sizing) return;
            bool sized = _sizing;
            _moving = _sizing = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            if (!IsMouseOver) _grip.Opacity = 0;
            if (sized || _glass == null) RestartGlass();   // a move: the glass already followed
            Info.WantX = Info.X; Info.WantY = Info.Y;   // the user put it here
            Widgets.Save();
        }

        private void Resize(int cw, int ch, bool save)
        {
            Info.W = cw; Info.H = ch;
            int w = _grid.Wide(cw), h = _grid.High(ch);
            var (x, y) = Widgets.Snap(_grid, Info.WantX ?? Info.X, Info.WantY ?? Info.Y, w, h);
            Info.X = x; Info.Y = y;
            if (save) { Info.WantX = x; Info.WantY = y; }   // a new size picked: where it landed is its spot
            Move(x, y, w, h);
            _content.SetCells(cw, ch);
            if (save) { RestartGlass(); Widgets.Save(); }
        }

        private ContextMenu BuildMenu()
        {
            var menu = new ContextMenu { FlowDirection = Lang.Flow };
            // Dark, with white text (the items and their submenus pick these up).
            var app = Application.Current;
            menu.Style = (Style)app.FindResource("DarkContextMenu");
            menu.Resources[typeof(MenuItem)] = app.FindResource("DarkMenuItem");
            menu.Resources[MenuItem.SeparatorStyleKey] = app.FindResource("DarkMenuSeparator");
            var own = _content.MenuItems().ToList();
            foreach (var c in own) menu.Items.Add(c);
            if (own.Count > 0) menu.Items.Add(new Separator());
            var size = new MenuItem { Header = Lang.T("الحجم") };
            menu.Opened += (_, _) =>
            {
                // Built each time: a widget's sizes can change (a bar turned from across to down).
                size.Items.Clear();
                var choices = _content.SizeChoices
                    ?? Enumerable.Range(Widgets.MinCells, Widgets.MaxCells - Widgets.MinCells + 1).Select(n => (n, n)).ToArray();
                foreach (var (cw, ch) in choices)
                {
                    int w = cw, h = ch;
                    var item = new MenuItem { Header = $"{w} × {h}", FlowDirection = FlowDirection.LeftToRight, IsChecked = w == Info.W && h == Info.H };
                    item.Click += (_, _) => { _grid = Widgets.GetGrid(); Resize(w, h, save: true); };
                    size.Items.Add(item);
                }
            };
            menu.Items.Add(size);
            menu.Items.Add(BuildColorMenu());
            menu.Items.Add(new Separator());
            var remove = new MenuItem { Header = Lang.T("شيل الويدجت") };
            remove.Click += (_, _) => Widgets.Remove(Info.Id);
            menu.Items.Add(remove);
            return menu;
        }

        // ---- Color -------------------------------------------------------------------------------

        internal static readonly (string Name, string Hex)[] Colors =
        {
            ("أزرق", "#4C8DFF"), ("سماوي", "#40C8E0"), ("أخضر", "#34C759"), ("أصفر", "#FFCC00"),
            ("برتقالي", "#FF9500"), ("أحمر", "#FF453A"), ("وردي", "#FF5FA2"), ("بنفسجي", "#AF52DE"),
            ("أبيض", "#F2F3F5"),
        };
        private const string DefaultColor = "#4C8DFF";

        private static Color AccentOf(Widgets.Info info)
        {
            try { return (Color)ColorConverter.ConvertFromString(string.IsNullOrEmpty(info.Color) ? DefaultColor : info.Color); }
            catch { return (Color)ColorConverter.ConvertFromString(DefaultColor); }
        }

        private void SetColor(Color c)
        {
            Info.Color = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            _content.SetAccent(c);
            Widgets.Save();
        }

        private MenuItem BuildColorMenu()
        {
            var item = new MenuItem { Header = Lang.T("اللون") };
            foreach (var (name, hex) in Colors)
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                var choice = new MenuItem
                {
                    Header = Lang.T(name), Tag = hex,
                    Icon = new System.Windows.Shapes.Ellipse
                    {
                        Width = 12, Height = 12, Fill = new SolidColorBrush(color),
                        Stroke = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1,
                    },
                };
                choice.Click += (_, _) => SetColor(color);
                item.Items.Add(choice);
            }
            item.Items.Add(new Separator());
            var custom = new MenuItem { Header = Lang.T("لون ثاني…") };
            custom.Click += (_, _) => PickColor();
            item.Items.Add(custom);
            item.SubmenuOpened += (_, _) =>
            {
                string now = AccentHex();
                foreach (var i in item.Items.OfType<MenuItem>())
                    i.IsChecked = i.Tag is string h ? string.Equals(h, now, StringComparison.OrdinalIgnoreCase)
                                                    : !Colors.Any(c => string.Equals(c.Hex, now, StringComparison.OrdinalIgnoreCase));
            };
            return item;
        }

        private string AccentHex()
        {
            var c = AccentOf(Info);
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }

        /// <summary>Any color, from Windows' color picker.</summary>
        private void PickColor()
        {
            var now = AccentOf(Info);
            using var dialog = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true, AnyColor = true,
                Color = System.Drawing.Color.FromArgb(now.R, now.G, now.B),
            };
            var owner = new System.Windows.Forms.NativeWindow();
            owner.AssignHandle(_hwnd);
            try
            {
                if (dialog.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK)
                    SetColor(Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B));
            }
            finally { owner.ReleaseHandle(); }
        }

        // ---- Stay under every window ------------------------------------------------------------

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_WINDOWPOSCHANGING = 0x0046, WM_DISPLAYCHANGE = 0x007E, WM_SETTINGCHANGE = 0x001A;
            if (msg == WM_WINDOWPOSCHANGING && lParam != IntPtr.Zero)
            {
                var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                if ((pos.flags & 0x0004 /* NOZORDER */) == 0)
                {
                    pos.hwndInsertAfter = new IntPtr(1); // HWND_BOTTOM
                    Marshal.StructureToPtr(pos, lParam, false);
                }
            }
            else if (msg == WM_SETTINGCHANGE && wParam.ToInt32() == 0x14 /* SPI_SETDESKWALLPAPER */)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, RestartGlass);   // a new wallpaper
            }
            else if (msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero && Widgets.Back == Widgets.BackMode.Taskbar
                     && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, RestartGlass);   // Windows' colors changed
            }
            else if (msg == WM_DISPLAYCHANGE || (msg == WM_SETTINGCHANGE && wParam.ToInt32() == 0x2F /* SPI_SETWORKAREA */))
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, Place);
            }
            return IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

        [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
