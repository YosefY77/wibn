using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// Wibn's own taskbar. Windows' taskbar is hidden (only hidden: nothing in Windows is
    /// changed), and ours sits exactly in the strip Windows keeps free for it, so maximized
    /// windows already leave room for it. One bar per screen that has a taskbar.
    ///
    /// Safety: Windows' taskbar comes back when Wibn closes, when the feature is turned
    /// off, the next time Wibn starts after being killed, and right away from a small guard
    /// process (WinPlus.exe --taskbar-guard) if Wibn dies or freezes.
    /// </summary>
    internal static class CustomTaskbar
    {
        private const string Key = @"Software\WinPlus\CustomTaskbar";

        // ---- Settings ---------------------------------------------------------------------------

        public static int Get(string name, int fallback)
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(Key);
                return k?.GetValue(name) is int v ? v : fallback;
            }
            catch { return fallback; }
        }

        public static void Set(string name, int value)
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(Key);
                k.SetValue(name, value, RegistryValueKind.DWord);
            }
            catch { }
        }

        /// <summary>The user's switch (the "taskbar" feature must be on too).</summary>
        public static bool Enabled
        {
            get => Get("Enabled", 0) != 0;
            set
            {
                Set("Enabled", value ? 1 : 0);
                TaskbarPlacement.ApplyAutoHideMode();   // "hide with full screen" is for Windows' taskbar only
                Apply();
            }
        }

        /// <summary>0 = whole strip, 1 = floating (rounded, a gap around it), 2 = dock (only as wide as what's on it).</summary>
        public static int Shape { get => Get("Shape", 0); set { Set("Shape", value); Restyle(); WidgetsFollowSoon(); } }
        /// <summary>0 = apps in the middle, 1 = at the start.</summary>
        public static int Align { get => Get("Align", 0); set { Set("Align", value); Restyle(); } }
        /// <summary>0 = glass (blurred), 1 = solid color, 2 = see-through.</summary>
        public static int Look { get => Get("Look", 0); set { Set("Look", value); Restyle(); } }
        /// <summary>The background color, 0xRRGGBB.</summary>
        public static int BackColor { get => Get("Color", 0x1C1C1E); set { Set("Color", value); Restyle(); } }
        /// <summary>How strong the color is over the glass / see-through look, 0–100.</summary>
        public static int Opacity { get => Get("Opacity", 35); set { Set("Opacity", value); Restyle(); } }
        /// <summary>0 = small, 1 = medium, 2 = large.</summary>
        public static int IconSize { get => Get("IconSize", 1); set { Set("IconSize", value); Restyle(); } }
        public static bool Labels { get => Get("Labels", 0) != 0; set { Set("Labels", value ? 1 : 0); Restyle(); } }
        /// <summary>Every icon on the bar in white and a little see-through.</summary>
        public static bool WhiteIcons { get => Get("WhiteIcons", 0) != 0; set { Set("WhiteIcons", value ? 1 : 0); Restyle(); } }
        /// <summary>0 = like Windows (48), 1 = thicker (56), 2 = thickest (64).</summary>
        public static int Thickness { get => Get("Thickness", 1); set { Set("Thickness", value); Restyle(); } }
        /// <summary>Where Wibn's bar is: 0 = where Windows' taskbar is (the bottom), 1 = top, 2 = left, 3 = right. Set by dragging the bar.</summary>
        public static int Edge
        {
            get => Math.Clamp(Get("Edge", 0), 0, 3);
            set
            {
                if (value == Edge) return;
                Set("Edge", Math.Clamp(value, 0, 3));
                _workAreaSetAt.Clear();   // the work area changes right away
                Restyle();
                WidgetsFollowSoon();
            }
        }

        /// <summary>The bar's strip on the chosen edge of a screen (as thick as chosen; a side bar a little wider, for the clock).</summary>
        internal static RECT EdgeStrip(RECT m, IntPtr window, int edge)
        {
            uint dpi = GetDpiForWindow(window);
            double scale = (dpi == 0 ? 96 : dpi) / 96.0;
            int thick = (int)Math.Round((edge >= 2 ? ThicknessDip + 16 : ThicknessDip) * scale);
            return edge switch
            {
                1 => new RECT { Left = m.Left, Top = m.Top, Right = m.Right, Bottom = m.Top + thick },
                2 => new RECT { Left = m.Left, Top = m.Top, Right = m.Left + thick, Bottom = m.Bottom },
                3 => new RECT { Left = m.Right - thick, Top = m.Top, Right = m.Right, Bottom = m.Bottom },
                _ => new RECT { Left = m.Left, Top = m.Bottom - thick, Right = m.Right, Bottom = m.Bottom },
            };
        }

        /// <summary>The screen without the bar's strip.</summary>
        private static RECT WithoutStrip(RECT m, RECT strip, int edge)
        {
            var r = m;
            if (edge == 1) r.Top = strip.Bottom;
            else if (edge == 2) r.Left = strip.Right;
            else if (edge == 3) r.Right = strip.Left;
            else r.Bottom = strip.Top;
            return r;
        }

        /// <summary>With the split taskbar: the clock's own edge (0 = bottom, 1 = top), or -1 = beside the apps. Set by dragging the clock's piece.</summary>
        public static int ClockEdge
        {
            get => Math.Clamp(Get("ClockEdge", -1), -1, 1);
            set
            {
                if (value == ClockEdge) return;
                Set("ClockEdge", Math.Clamp(value, -1, 1));
                _workAreaSetAt.Clear();
                Restyle();
                WidgetsFollowSoon();
            }
        }

        /// <summary>
        /// Split taskbar, the clock on its own edge: at which end of it, 0 = right, 1 = left. The info bar on the
        /// same edge then sits in the rest of it (beside the clock when it's on the same side).
        /// </summary>
        public static int ClockSide
        {
            get => Math.Clamp(Get("ClockSide", 0), 0, 1);
            set
            {
                if (value == ClockSide) return;
                Set("ClockSide", Math.Clamp(value, 0, 1));
                foreach (var b in _bars.Values) b.ApplyPieceLengths();
                WidgetsFollowSoon();
                Changed?.Invoke();   // the info bar beside it moves over too
            }
        }

        /// <summary>Split taskbar: how long the apps' piece is at least (DIPs; 0 = as long as what's on it). Set by dragging its ends.</summary>
        public static int AppsLength { get => Math.Max(0, Get("AppsLength", 0)); set { Set("AppsLength", Math.Max(0, value)); foreach (var b in _bars.Values) b.ApplyPieceLengths(); } }
        /// <summary>Split taskbar: where the clock and what's around it sit in their piece when it's longer: 0 = right, 1 = left, 2 = middle.</summary>
        public static int ClockAlign { get => Math.Clamp(Get("ClockAlign", 0), 0, 2); set { Set("ClockAlign", Math.Clamp(value, 0, 2)); foreach (var b in _bars.Values) b.ApplyPieceLengths(); } }
        /// <summary>Split taskbar: how long the clock's piece is at least (DIPs; 0 = as long as what's on it). Set by dragging its end.</summary>
        public static int ClockLength { get => Math.Max(0, Get("ClockLength", 0)); set { Set("ClockLength", Math.Max(0, value)); foreach (var b in _bars.Values) b.ApplyPieceLengths(); } }

        /// <summary>The clock's edge when it's apart from the apps (split, the apps at the bottom or top, the clock on the other), else -1.</summary>
        internal static int ClockApartEdge(int appsEdge)
        {
            int c = ClockEdge;
            return Split && appsEdge is 0 or 1 && c >= 0 && c != appsEdge ? c : -1;
        }

        /// <summary>Where the clock's own bar is on the screen that has this point (null if it isn't apart there).</summary>
        public static RECT? ClockOn(int x, int y)
        {
            if (!_running) return null;
            foreach (var b in _bars.Values)
            {
                var m = b.Monitor;
                var p = b.ClockPlaced;
                if (p.Width == 0 || x < m.Left || x >= m.Right || y < m.Top || y >= m.Bottom) continue;
                return p;
            }
            return null;
        }

        /// <summary>The apps on one bar, and the clock with what's around it on a bar of its own.</summary>
        public static bool Split { get => Get("Split", 0) != 0; set { Set("Split", value ? 1 : 0); Restyle(); } }
        /// <summary>How much shorter the bar is at each end (DIPs; 0 = as long as the screen allows). Set by dragging its ends.</summary>
        public static int Inset { get => Math.Max(0, Get("Inset", 0)); set { Set("Inset", Math.Max(0, value)); Relayout(); } }
        public static double ThicknessDip => Thickness switch { 0 => 48, 2 => 64, _ => 56 };
        public static bool StartButton { get => Get("Start", 1) != 0; set { Set("Start", value ? 1 : 0); Restyle(); } }
        /// <summary>The Start button's icon: 0 = Windows logo, 1 = Wibn's logo, 2+ = StartLogo.Glyphs, -1 = the user's picture.</summary>
        public static int StartIcon { get => Get("StartIcon", 0); set { Set("StartIcon", value); Restyle(); } }
        /// <summary>The Start button's color (0xRRGGBB); -1 = Windows' blue.</summary>
        public static int StartColor { get => Get("StartColor", -1); set { Set("StartColor", value); Restyle(); } }
        /// <summary>A full-screen app (game, video) hides the bar; the mouse at the screen's edge shows it.</summary>
        public static bool HideInFullscreen
        {
            get => Get("Fullscreen", 1) != 0;
            set { Set("Fullscreen", value ? 1 : 0); if (!value) RestoreWorkAreas(); }
        }
        /// <summary>
        /// The window in front is under a bar (a window raised up under the bars along the top…): only that bar
        /// slides away so the window shows; the mouse at the edge over it brings it back. On by default.
        /// </summary>
        public static bool HideOverWindow
        {
            get => Get("HideOverWindow", 1) != 0;
            set { Set("HideOverWindow", value ? 1 : 0); Poke(); InfoBar.CoverSettingChanged(); Changed?.Invoke(); }
        }

        /// <summary>
        /// The glass bars show in streams and recordings (Discord…) all the time; their glass shows the wallpaper behind
        /// them then, not the windows. (With OBS, Streamlabs, XSplit… running it's that way by itself.) Off by default.
        /// </summary>
        public static bool ShowInStreams { get => Get("ShowInStreams", 0) != 0; set { Set("ShowInStreams", value ? 1 : 0); Changed?.Invoke(); } }

        /// <summary>Over a game the mouse at the edge doesn't bring the bar: only the Windows key (Start) does.</summary>
        public static bool GamesKeyOnly { get => Get("GamesKeyOnly", 1) != 0; set => Set("GamesKeyOnly", value ? 1 : 0); }
        public static bool SearchButton { get => Get("Search", 1) != 0; set { Set("Search", value ? 1 : 0); Restyle(); } }
        public static bool TaskViewButton { get => Get("TaskView", 0) != 0; set { Set("TaskView", value ? 1 : 0); Restyle(); } }
        public static bool Seconds { get => Get("Seconds", 0) != 0; set { Set("Seconds", value ? 1 : 0); Restyle(); InfoBar.RebuildAll(); } }   // the info bars' clocks too
        public static bool ShowDate { get => Get("Date", 1) != 0; set { Set("Date", value ? 1 : 0); Restyle(); } }
        public static bool DesktopCorner { get => Get("DesktopCorner", 1) != 0; set { Set("DesktopCorner", value ? 1 : 0); Restyle(); } }

        /// <summary>
        /// The glass's tint: the taskbar's color, as strong as the widgets' glass gets with a color
        /// (the Liquid Glass page's "darken" plus a little), so the same color looks the same on both.
        /// </summary>
        public static (System.Windows.Media.Color Color, double Alpha) GlassTint()
        {
            int rgb = BackColor;
            var c = System.Windows.Media.Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            return (c, Math.Clamp(GlassSettings.SharedTint / 100.0 + 0.10, 0.20, 0.60));
        }

        /// <summary>
        /// The floating bar's left and right ends on the screen that has this point (null if
        /// there's no floating bar there): the widgets at the far columns line up with them.
        /// </summary>
        public static (int Left, int Right)? FloatingEnds(int x, int y)
        {
            if (!_running || Shape != 1) return null;
            foreach (var b in _bars.Values)
            {
                var m = b.Monitor;
                var p = b.FullPlaced;   // its full length: shortening the bar doesn't move the widgets
                if (b.Vertical || p.Width == 0 || x < m.Left || x >= m.Right || y < m.Top || y >= m.Bottom) continue;
                return (p.Left, p.Right);
            }
            return null;
        }

        /// <summary>Where Wibn's side (vertical) bar is on the screen that has this point (null if none there).</summary>
        public static RECT? SideBarOn(int x, int y)
        {
            if (!_running) return null;
            foreach (var b in _bars.Values)
            {
                var m = b.Monitor;
                var p = b.FullPlaced;
                if (!b.Vertical || p.Width == 0 || x < m.Left || x >= m.Right || y < m.Top || y >= m.Bottom) continue;
                return p;
            }
            return null;
        }

        /// <summary>Every piece of Wibn's taskbar on the screen that has this point (the bar, or a split bar's pieces and
        /// the clock's own bar), and whether each is showing.</summary>
        public static List<(RECT Rect, bool Shown)> PiecesOn(int x, int y)
        {
            var list = new List<(RECT, bool)>();
            if (!_running) return list;
            foreach (var b in _bars.Values)
            {
                var m = b.Monitor;
                if (x < m.Left || x >= m.Right || y < m.Top || y >= m.Bottom) continue;
                list.AddRange(b.ScreenPieces());
            }
            return list;
        }

        /// <summary>Where Wibn's horizontal taskbar is on the screen that has this point (null if none there).</summary>
        public static RECT? BarOn(int x, int y)
        {
            if (!_running) return null;
            foreach (var b in _bars.Values)
            {
                var m = b.Monitor;
                var p = b.FullPlaced;
                if (b.Vertical || p.Width == 0 || x < m.Left || x >= m.Right || y < m.Top || y >= m.Bottom) continue;
                return p;
            }
            return null;
        }

        private static DispatcherTimer? _widgetsSoon;

        /// <summary>The bar's ends moved (or it came / went): the widgets line up again, a moment later.</summary>
        public static void WidgetsFollowSoon()
        {
            if (_widgetsSoon == null)
            {
                _widgetsSoon = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
                _widgetsSoon.Tick += (_, _) => { _widgetsSoon.Stop(); try { Widgets.PlaceAll(); } catch (Exception ex) { CrashLog.Write("widgets follow", ex); } };
            }
            _widgetsSoon.Stop();
            _widgetsSoon.Start();
        }

        /// <summary>A setting changed: the bars redraw.</summary>
        public static event Action? Changed;

        /// <summary>Every bar works out its place again (its length changed).</summary>
        private static void Relayout()
        {
            foreach (var b in _bars.Values) b.Relayout();
            WidgetsFollowSoon();
            Changed?.Invoke();
        }

        private static void Restyle()
        {
            foreach (var b in _bars.Values) b.ApplyLook();
            Changed?.Invoke();
        }

        // ---- Running ------------------------------------------------------------------------------

        private static readonly Dictionary<IntPtr, TaskbarBar> _bars = new();   // Windows' taskbar window → our bar

        /// <summary>The color of the taskbar's clock right now (it changes with what's behind); null = Wibn's taskbar isn't up.</summary>
        internal static System.Windows.Media.Color? ClockInk => _bars.Values.FirstOrDefault()?.Ink.Color;
        private static DispatcherTimer? _timer;
        private static Process? _guard;
        private static bool _running;
        private static DateTime _peekUntil = DateTime.MinValue;
        private static bool _peeking;
        private static IntPtr _mainTray;
        private static DateTime? _rebroadcastAt;

        /// <summary>Where an icon next to the clock is on the screen (the first bar that shows it).</summary>
        private static RECT? LocateIcon(TrayHost.Icon icon)
        {
            foreach (var b in _bars.Values)
                if (b.TrayIconRect(icon) is { } r) return r;
            return null;
        }

        public static bool Running => _running;

        /// <summary>Starts or stops to match the settings. Also called once at start-up.</summary>
        public static void Apply()
        {
            bool want = Enabled && Features.IsOn("taskbar");
            if (want && !_running) Start();
            else if (!want && _running) Stop();
            else if (!want && Get("Active", 0) == 1) { ShowWindowsTaskbars(); RemoveLeftAppBars(); RestoreWorkAreas(); Set("Active", 0); }   // killed last time
            Changed?.Invoke();
        }

        private static int _poked;

        /// <summary>
        /// A window was just maximized, restored, minimized or came to the front (any thread): look now, not at the
        /// next tick, so the taskbar slides away (or back) together with the info bars (they hear it from Windows
        /// at once; a tick could be a quarter of a second away).
        /// </summary>
        public static void Poke()
        {
            if (!_running || Interlocked.Exchange(ref _poked, 1) == 1) return;
            try
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                {
                    Interlocked.Exchange(ref _poked, 0);
                    if (!_running) return;
                    try { Tick(); } catch (Exception ex) { CrashLog.Write("taskbar tick", ex); }
                }));
            }
            catch { Interlocked.Exchange(ref _poked, 0); }
        }

        private static void Start()
        {
            _running = true;
            Set("Active", 1);
            StartGuard();
            _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, _) => { try { Tick(); } catch (Exception ex) { CrashLog.Write("taskbar tick", ex); } };
            _timer.Start();
            Tick();
            // The icons next to the clock come to Wibn too (Windows' taskbar keeps a copy of each).
            TrayHost.Locate = LocateIcon;
            TrayHost.Start();
            CrashLog.Note("custom taskbar on");
        }

        /// <summary>Back to Windows' taskbar (turned off, or Wibn is closing).</summary>
        public static void Stop()
        {
            if (!_running) return;
            _running = false;
            _timer?.Stop();
            _timer = null;
            foreach (var b in _bars.Values) { try { b.CloseBar(); } catch { } }
            _bars.Clear();
            TrayHost.Stop();
            ShowWindowsTaskbars();
            RestoreWorkAreas();   // the taskbar's strip is kept free again
            WidgetsFollowSoon();   // back on their usual grid
            Set("Active", 0);
            StopGuard();
            CrashLog.Note("custom taskbar off");
        }

        /// <summary>Every quarter second: keep Windows' taskbars hidden, our bars in place, and out of the way of full-screen apps.</summary>
        private static void Tick()
        {
            if (!_running) return;
            var allTrays = Trays();
            // Our bars: one per screen. Explorer can leave an old second-screen taskbar behind on
            // the main screen (after screens change): it gets hidden too, but no bar of its own
            // (a second bar there would sit on the first one).
            var screens = new HashSet<IntPtr>();
            var trays = allTrays.Where(t => screens.Add(MonitorFromWindow(t, MONITOR_DEFAULTTONEAREST))).ToList();
            foreach (var extra in allTrays.Except(trays))
                if (IsWindowVisible(extra)) ShowWindow(extra, SW_HIDE);

            // Explorer restarted: its new taskbar may now be found before ours, so apps
            // sent their icons there. Get ahead again and ask them to send them again.
            if (trays.Count > 0 && trays[0] != _mainTray)
            {
                bool restarted = _mainTray != IntPtr.Zero;
                _mainTray = trays[0];
                if (restarted) _rebroadcastAt = DateTime.Now.AddSeconds(3);
            }
            if (_rebroadcastAt is { } at && DateTime.Now >= at)
            {
                _rebroadcastAt = null;
                TrayHost.KeepFirst();
                TrayHost.Broadcast();
            }
            if (!_peeking) TrayHost.KeepFirst();

            foreach (var gone in _bars.Keys.Where(h => !trays.Contains(h)).ToList())   // a screen was unplugged, or explorer restarted
            {
                var old = _bars[gone];
                _bars.Remove(gone);
                old.CloseBar();
            }

            IntPtr fg = GetForegroundWindow();
            GetCursorPos(out POINT cursor);

            // "Peek": Windows' taskbar is shown for a moment (for the icons next to its clock).
            if (_peeking)
            {
                bool over = trays.Any(t => GetWindowRect(t, out RECT r) && r.Contains(cursor));
                if (over || IsShellPopup(fg)) _peekUntil = DateTime.Now.AddMilliseconds(1200);
                if (DateTime.Now > _peekUntil) _peeking = false;
            }

            // A bar left behind (its screen's taskbar went away, or Wibn's taskbar was turned
            // off and on): nothing moves it any more, so it goes.
            foreach (var stray in System.Windows.Application.Current.Windows.OfType<TaskbarBar>().Where(b => !b.Gone && !_bars.ContainsValue(b)).ToList())
                stray.CloseBar();

            // Windows' own word for it too: a game in exclusive full screen, or presentation mode
            // (its window can be odd sizes while it switches the screen's resolution).
            bool windowsSaysFullscreen = SHQueryUserNotificationState(out int quns) == 0 && quns is 3 or 4;

            // A window being dragged or resized (e.g. pulled down out of maximized): the bars stay as
            // they are until it's let go, so nothing slides or redraws under the drag.
            bool dragging = fg != IntPtr.Zero && TaskbarPlacement.InMoveSize(GetWindowThreadProcessId(fg, out _));

            bool anyFullscreen = false, anyCovered = false;
            var front = dragging ? (Rect: (RECT?)null, Dragging: true) : FrontWindow();
            foreach (var tray in trays)
            {
                if (!GetWindowRect(tray, out RECT rect) || rect.Width < 8 || rect.Height < 8) continue;
                IntPtr monitor = MonitorFromWindow(tray, MONITOR_DEFAULTTONEAREST);
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(monitor, ref mi)) continue;

                if (_peeking)
                {
                    if (!IsWindowVisible(tray)) { ShowWindow(tray, SW_SHOWNA); SetWindowPos(tray, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS); }
                }
                else if (IsWindowVisible(tray)) ShowWindow(tray, SW_HIDE);

                if (!_bars.TryGetValue(tray, out var bar))
                {
                    bar = new TaskbarBar(tray == trays[0]);
                    _bars[tray] = bar;
                }
                // "Hide in full screen": the whole screen is for the windows, so a maximized window
                // really takes all of it (and is full screen for the bar, which slides down for it).
                int edge = Edge, clockEdge = ClockApartEdge(edge);
                if (edge != 0 || clockEdge >= 0)
                {
                    // Moved to the top or a side (or the clock on a bar of its own): the windows get the
                    // screen without the bars' strips (or the whole screen with "hide in full screen"),
                    // and explorer's strip at the bottom is freed.
                    if (!_peeking)
                    {
                        var target = mi.rcMonitor;
                        if (!HideInFullscreen)
                        {
                            target = WithoutStrip(target, EdgeStrip(mi.rcMonitor, tray, edge), edge);
                            if (clockEdge >= 0) target = WithoutStrip(target, EdgeStrip(mi.rcMonitor, tray, clockEdge), clockEdge);
                        }
                        SetWorkArea(monitor, ref mi, InfoBar.TrimWorkArea(mi.rcMonitor, target));   // the info bar keeps its room
                    }
                }
                else if (!HideInFullscreen) RestoreWorkArea(monitor);
                else if (!_peeking) FullWorkArea(monitor, ref mi);

                // "Full screen" is all the room the windows can have (the info bar keeps its strip).
                var whole = InfoBar.TrimWorkArea(mi.rcMonitor, mi.rcMonitor);
                bool fullscreen = IsFullscreenOn(fg, whole) || CoveredOn(whole) ||
                                  (windowsSaysFullscreen && fg != IntPtr.Zero && !IsOursOrNothing(fg) && MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST) == monitor);
                if (fullscreen) _fullscreenApp[tray] = fg;
                else if (_fullscreenApp.TryGetValue(tray, out var app))
                {
                    // The focus is on Wibn (or nowhere) now: the full-screen app under it still counts.
                    if (IsOursOrNothing(fg) && IsFullscreenOn(app, whole)) fullscreen = true;
                    else _fullscreenApp.Remove(tray);
                }
                anyFullscreen |= fullscreen;
                var strip = edge != 0 ? EdgeStrip(mi.rcMonitor, tray, edge) : Strip(rect, mi.rcMonitor, mi.rcWork, tray);
                // Start (or search) open on this screen: the bar comes up with it, even over a full-screen app.
                bool startOpen = fullscreen && IsStartOrSearch(fg) && MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST) == monitor;
                // A game: the mouse at the edge (aiming, turning the camera) doesn't bring the bar, only the Windows key.
                bool game = fullscreen && HideInFullscreen && GamesKeyOnly && !startOpen &&
                            GameBoost.IsGameWindow(IsOursOrNothing(fg) && _fullscreenApp.TryGetValue(tray, out var fsApp) ? fsApp : fg,
                                                   windowsSaysFullscreen && quns == 3 /* exclusive full screen */);
                // The window in front is under the bar (not full screen): only this bar goes; the mouse at the edge
                // over it (not anywhere along the edge) brings it back.
                bool appsCovered = !fullscreen && !startOpen && front.Rect is { } fr && Covers(fr, bar.AppsSpot);
                bool hidden = (fullscreen && HideInFullscreen && !startOpen && (game || !Revealed(_revealUntil, tray, strip, mi.rcMonitor, cursor)))
                              || (appsCovered && !Revealed(_revealUntil, tray, strip, mi.rcMonitor, cursor, Along(bar.AppsSpot)));
                if ((!fullscreen && !appsCovered) || game) _revealUntil.Remove(tray);
                bool visible = dragging && !_peeking ? bar.Showing : !hidden && !_peeking;
                if (!dragging) bar.CoverHidden = appsCovered;
                // The clock's own bar on the other edge (e.g. at the top): the mouse at that edge brings only it.
                var clockSpot = bar.ClockSpot;
                bool clockCovered = clockEdge >= 0 && !fullscreen && !startOpen && front.Rect is { } fc && Covers(fc, clockSpot);
                bool clockVisible = visible;
                if (clockEdge >= 0 && !_peeking && !dragging)
                {
                    if (fullscreen && HideInFullscreen && !game && !visible)
                        clockVisible = Revealed(_clockRevealUntil, tray, EdgeStrip(mi.rcMonitor, tray, clockEdge), mi.rcMonitor, cursor, ClockSpan(bar));
                    else if (clockCovered)
                        clockVisible = Revealed(_clockRevealUntil, tray, EdgeStrip(mi.rcMonitor, tray, clockEdge), mi.rcMonitor, cursor, Along(clockSpot));
                    else if (!fullscreen) clockVisible = true;   // the apps' piece may be away under a window: the clock isn't
                }
                else if (dragging && !_peeking) clockVisible = bar.ClockShowing;
                if (!fullscreen && !clockCovered) _clockRevealUntil.Remove(tray);
                if (!dragging) bar.ClockCoverHidden = clockCovered;
                anyCovered |= appsCovered || clockCovered;
                bar.Place(strip, mi.rcMonitor, visible, rect, slide: (HideInFullscreen || appsCovered || clockCovered) && !_peeking, overFullscreen: fullscreen && !dragging, clockVisible: clockVisible);
                if (tray == trays[0]) TrayHost.SetRect(strip);   // where the taskbar is, for anything that asks
            }
            // Over a full-screen app it checks the mouse more often, so the bar comes up right away.
            if (_timer != null)
            {
                // (Not while a window is being dragged: the bars stay as they are then anyway.)
                var want = TimeSpan.FromMilliseconds(((anyFullscreen && HideInFullscreen) || anyCovered) && !WindowDrag.Active ? 40 : 250);
                if (_timer.Interval != want) _timer.Interval = want;
            }
        }

        // ---- The whole screen for the windows ---------------------------------------------------------
        //
        // Explorer keeps its taskbar's strip out of the "work area", so a maximized window stops above
        // it even with the bar hidden. With "hide in full screen" on, the work area is the whole screen:
        // maximized windows are really maximized over all of it. Explorer's own work area is written
        // down first, so it comes back when the option or the taskbar is turned off (or after a crash).

        private static readonly Dictionary<IntPtr, DateTime> _workAreaSetAt = new();   // when it may be set again
        private static readonly Dictionary<IntPtr, int> _workAreaFights = new();

        private static void FullWorkArea(IntPtr monitor, ref MONITORINFO mi) => SetWorkArea(monitor, ref mi, InfoBar.TrimWorkArea(mi.rcMonitor, mi.rcMonitor));

        /// <summary>
        /// The work area of this screen becomes target (the whole screen, or the screen without the
        /// strip of a bar moved to the top or a side). Explorer's own is written down first.
        /// </summary>
        private static void SetWorkArea(IntPtr monitor, ref MONITORINFO mi, RECT target)
        {
            var work = mi.rcWork;
            if (Same(work, target)) return;
            // Explorer took it back right away: set it again at most once a second. Only the first
            // time is every window on the system told (WM_SETTINGCHANGE); telling them again and
            // again lags everything (and can make explorer take it back once more).
            var now = DateTime.Now;
            if (_workAreaSetAt.TryGetValue(monitor, out var next) && now < next) return;
            int fights = _workAreaFights.TryGetValue(monitor, out int f) && _workAreaSetAt.ContainsKey(monitor) && (now - next).TotalSeconds < 5 ? f + 1 : 0;
            _workAreaFights[monitor] = fights;
            _workAreaSetAt[monitor] = now.AddSeconds(1);
            uint flags = fights == 0 ? SPIF_SENDCHANGE : 0;

            SaveWorkArea(work);
            // Off the UI thread: the change goes to every window and waits for each one.
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var t = target;
                    SystemParametersInfo(SPI_SETWORKAREA, 0, ref t, flags);
                    FitMaximized(monitor, work, target);   // windows already maximized here follow (new ones do by themselves)
                }
                catch (Exception ex) { CrashLog.Write("work area", ex); }
            });
            mi.rcWork = target;
        }

        private static bool Same(RECT a, RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

        private static void SaveWorkArea(RECT work)
        {
            var list = SavedWorkAreas();
            if (list.Any(w => Overlaps(w, work))) return;   // this screen's is already written down
            list.Add(work);
            Set("WorkAreas", list.Select(w => $"{w.Left},{w.Top},{w.Right},{w.Bottom}").ToArray());
        }

        private static List<RECT> SavedWorkAreas()
        {
            var list = new List<RECT>();
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(Key);
                foreach (var v in k?.GetValue("WorkAreas") as string[] ?? Array.Empty<string>())
                {
                    var p = v.Split(',');
                    if (p.Length == 4 && int.TryParse(p[0], out int l) && int.TryParse(p[1], out int t) && int.TryParse(p[2], out int r) && int.TryParse(p[3], out int b))
                        list.Add(new RECT { Left = l, Top = t, Right = r, Bottom = b });
                }
            }
            catch { }
            return list;
        }

        private static void Set(string name, string[] value)
        {
            try { using var k = Registry.CurrentUser.CreateSubKey(Key); k.SetValue(name, value, RegistryValueKind.MultiString); } catch { }
        }

        private static bool Overlaps(RECT a, RECT b) => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

        /// <summary>One screen's strip is kept for the taskbar again (the option is off there).</summary>
        private static void RestoreWorkArea(IntPtr monitor)
        {
            var list = SavedWorkAreas();
            if (list.Count == 0) return;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref mi)) return;
            int i = list.FindIndex(w => Overlaps(w, mi.rcMonitor));
            if (i < 0) return;
            var w = list[i];
            var was = mi.rcWork;
            SystemParametersInfo(SPI_SETWORKAREA, 0, ref w, SPIF_SENDCHANGE);
            list.RemoveAt(i);
            Set("WorkAreas", list.Select(x => $"{x.Left},{x.Top},{x.Right},{x.Bottom}").ToArray());
            _workAreaSetAt.Remove(monitor);
            _workAreaFights.Remove(monitor);
            FitMaximized(monitor, was, w);
        }

        /// <summary>Every screen gets its taskbar strip back (option off, taskbar off, or after a crash).</summary>
        public static void RestoreWorkAreas()
        {
            var list = SavedWorkAreas();
            foreach (var w0 in list)
            {
                var w = w0;
                IntPtr monitor = MonitorFromRect(ref w, MONITOR_DEFAULTTONEAREST);
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                bool known = GetMonitorInfo(monitor, ref mi);
                SystemParametersInfo(SPI_SETWORKAREA, 0, ref w, SPIF_SENDCHANGE);
                if (known) FitMaximized(monitor, mi.rcWork, w);
            }
            if (list.Count > 0) Set("WorkAreas", Array.Empty<string>());
            _workAreaSetAt.Clear();
            _workAreaFights.Clear();
        }

        /// <summary>Maximized windows on this screen move their edges from the old work area to the new one.</summary>
        private static void FitMaximized(IntPtr monitor, RECT from, RECT to)
        {
            int dl = to.Left - from.Left, dt = to.Top - from.Top, dr = to.Right - from.Right, db = to.Bottom - from.Bottom;
            if (dl == 0 && dt == 0 && dr == 0 && db == 0) return;
            EnumWindows((h, _) =>
            {
                if (IsWindowVisible(h) && IsZoomed(h) && MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) == monitor && GetWindowRect(h, out RECT r))
                    SetWindowPos(h, IntPtr.Zero, r.Left + dl, r.Top + dt, r.Width - dl + dr, r.Height - dt + db,
                                 0x0004 /* NOZORDER */ | SWP_NOACTIVATE | 0x0200 /* NOOWNERZORDER */ | SWP_ASYNCWINDOWPOS);
                return true;
            }, IntPtr.Zero);
        }

        private const uint SPI_SETWORKAREA = 0x2F, SPIF_SENDCHANGE = 0x2;
        [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint param, ref RECT rect, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RECT r, uint flags);

        // ---- Full screen: the mouse at the edge brings the bar up ----------------------------------------

        private static readonly Dictionary<IntPtr, DateTime> _revealUntil = new();
        private static readonly Dictionary<IntPtr, DateTime> _clockRevealUntil = new();   // the clock's own bar
        private static readonly Dictionary<IntPtr, IntPtr> _fullscreenApp = new();

        /// <summary>
        /// The window in front, where it's really drawn (screen pixels), when it can push a bar away (HideOverWindow):
        /// not Wibn's, not the desktop or Windows' own popups, not minimized or maximized (that's the full-screen
        /// rule). Dragging = it's being moved or resized right now (the bars are decided once it's let go).
        /// </summary>
        internal static (RECT? Rect, bool Dragging) FrontWindow()
        {
            if (!HideOverWindow) return (null, false);
            IntPtr fg = GetForegroundWindow();
            if (IsOursOrNothing(fg) || !IsWindowVisible(fg) || IsIconic(fg) || IsZoomed(fg) || LiquidGlass.IsOverlay(fg)) return (null, false);
            var sb = new StringBuilder(64);
            GetClassName(fg, sb, sb.Capacity);
            if (sb.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow") return (null, false);
            if (DwmGetWindowAttribute(fg, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return (null, false);
            if (TaskbarPlacement.InMoveSize(GetWindowThreadProcessId(fg, out _))) return (null, true);
            if (DwmGetWindowAttribute(fg, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out RECT r, Marshal.SizeOf<RECT>()) != 0 && !GetWindowRect(fg, out r)) return (null, false);
            return r.Width > 0 && r.Height > 0 ? (r, false) : (null, false);
        }

        internal static bool Covers(RECT a, RECT b) =>
            a.Width > 0 && b.Width > 0 && a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

        /// <summary>A bar's stretch along its edge (across for a bar along the top or bottom, down for a side one).</summary>
        private static List<(int A, int B)> Along(RECT r) => new() { r.Width >= r.Height ? (r.Left, r.Right) : (r.Top, r.Bottom) };

        private static bool IsOursOrNothing(IntPtr h)
        {
            if (h == IntPtr.Zero || IsShellPopup(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            return pid == (uint)Environment.ProcessId;
        }

        /// <summary>
        /// The clock's own bar along its edge (screen pixels), with the info bar when it sits beside it: the mouse
        /// at the edge brings them down only over there. Null = not placed yet (anywhere along the edge then).
        /// </summary>
        private static List<(int A, int B)>? ClockSpan(TaskbarBar bar)
        {
            var spot = bar.ClockSpot;
            if (spot.Width <= 0) return null;
            // Each bar on its own (not from the first to the last): the gaps between them are empty, the mouse
            // there brings nothing.
            var spans = new List<(int A, int B)> { (spot.Left, spot.Right) };
            foreach (var s in InfoBar.SharedRects)
                if (s.Width > 0 && s.Top < spot.Bottom && spot.Top < s.Bottom)
                    spans.Add((s.Left, s.Right));
            return spans;
        }

        /// <summary>
        /// Over a full-screen app: the bar shows while the mouse is at the screen's edge on the bar's
        /// side (only over <paramref name="span"/> along it, when given), and stays while the mouse is on it
        /// (or on its menus and previews), then goes a moment later.
        /// </summary>
        private static bool Revealed(Dictionary<IntPtr, DateTime> _revealUntil, IntPtr tray, RECT strip, RECT monitor, POINT c, List<(int A, int B)>? span = null)
        {
            bool inside = c.X >= monitor.Left && c.X < monitor.Right && c.Y >= monitor.Top && c.Y < monitor.Bottom;
            bool across = strip.Width >= strip.Height;
            if (span != null) inside &= span.Exists(sp => (across ? c.X : c.Y) >= sp.A - 8 && (across ? c.X : c.Y) <= sp.B + 8);
            bool atEdge = inside && (
                // The mouse pushed right against the edge (its very last pixel), not just near it.
                strip.Bottom >= monitor.Bottom - 1 && strip.Width >= strip.Height ? c.Y >= monitor.Bottom - 1 :
                strip.Top <= monitor.Top + 1 && strip.Width >= strip.Height ? c.Y <= monitor.Top :
                strip.Left <= monitor.Left + 1 ? c.X <= monitor.Left :
                c.X >= monitor.Right - 1);
            var now = DateTime.Now;
            bool shown = _revealUntil.TryGetValue(tray, out var until) && until > now;
            if (atEdge || (shown && (strip.Contains(c) || OverWibn(c, monitor))))
            {
                _revealUntil[tray] = now.AddMilliseconds(250);
                return true;
            }
            return shown;
        }

        /// <summary>The mouse is on one of Wibn's windows (the bar, its previews, menus, the arrow's panel).</summary>
        private static bool OverWibn(POINT c, RECT monitor)
        {
            IntPtr h = WindowFromPoint(c);
            if (h == IntPtr.Zero) return false;
            h = GetAncestor(h, 2 /* GA_ROOT */);
            GetWindowThreadProcessId(h, out uint pid);
            if (pid != (uint)Environment.ProcessId || !GetWindowRect(h, out RECT r)) return false;
            return r.Height < monitor.Height * 0.7 && r.Width < monitor.Width * 0.9;
        }

        /// <summary>The strip Windows keeps for its taskbar on this screen (where ours goes).</summary>
        private static RECT Strip(RECT tray, RECT monitor, RECT work, IntPtr tray_)
        {
            // Normally the taskbar window is exactly that strip. With Windows' own auto-hide on,
            // nothing is kept free, so the bar just covers the taskbar's usual place.
            var r = tray;
            if (tray.Bottom >= monitor.Bottom - 1 && tray.Width >= tray.Height) r = new RECT { Left = monitor.Left, Right = monitor.Right, Top = Math.Min(work.Bottom, tray.Top), Bottom = monitor.Bottom };
            else if (tray.Top <= monitor.Top + 1 && tray.Width >= tray.Height) r = new RECT { Left = monitor.Left, Right = monitor.Right, Top = monitor.Top, Bottom = Math.Max(work.Top, tray.Bottom) };
            if (r.Height < 24) r.Top = r.Bottom - 48;
            // As thick as chosen (with the whole screen for the windows, nothing else makes room for it).
            if (r.Width >= r.Height)
            {
                uint dpi = GetDpiForWindow(tray_);
                int thick = (int)Math.Round(ThicknessDip * (dpi == 0 ? 96 : dpi) / 96.0);
                if (r.Bottom >= monitor.Bottom - 1) r.Top = Math.Min(r.Top, r.Bottom - thick);
                else r.Bottom = Math.Max(r.Bottom, r.Top + thick);
            }
            return r;
        }

        /// <summary>Show Windows' taskbar for a moment, for the icons next to its clock.</summary>
        public static void Peek()
        {
            _peeking = true;
            _peekUntil = DateTime.Now.AddSeconds(4);   // time to reach it with the mouse
            Tick();
        }

        /// <summary>The Liquid Glass settings changed.</summary>
        public static void RefreshGlass()
        {
            foreach (var b in _bars.Values) b.RefreshGlass();
        }

        /// <summary>Something changed (a window opened, closed…): all bars update their buttons.</summary>
        public static void RefreshAll()
        {
            foreach (var b in _bars.Values) b.RefreshApps();
        }

        // ---- Windows' taskbar ---------------------------------------------------------------------

        public static List<IntPtr> Trays()
        {
            var list = new List<IntPtr>();
            IntPtr main = TrayHost.ExplorerTray();   // not Wibn's own window of that name
            if (main != IntPtr.Zero) list.Add(main);
            IntPtr h = IntPtr.Zero;
            while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) list.Add(h);
            return list;
        }

        public static void ShowWindowsTaskbars()
        {
            foreach (var t in Trays()) ShowWindow(t, SW_SHOWNA);
        }

        private static bool IsFullscreenOn(IntPtr fg, RECT monitor)
        {
            if (fg == IntPtr.Zero || TaskbarApps.Ours.Contains(fg) || !IsWindowVisible(fg) || IsIconic(fg)) return false;
            string cls = ClassOf(fg);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
            if (IsShellPopup(fg)) return false;
            if (!GetWindowRect(fg, out RECT r)) return false;
            return r.Left <= monitor.Left && r.Top <= monitor.Top && r.Right >= monitor.Right && r.Bottom >= monitor.Bottom;
        }

        /// <summary>
        /// A full-screen app on this screen even when it isn't the active window (a video full screen
        /// on one screen while you work on the other, or a smaller window opened over it): going down
        /// the windows from the top to the desktop, any real window on this screen covers all of it.
        /// The bar shows only when there's none (just normal windows over the desktop).
        /// </summary>
        private static bool CoveredOn(RECT monitor)
        {
            bool covered = false;
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h) || IsIconic(h) || TaskbarApps.Ours.Contains(h)) return true;
                if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true;
                long ex = GetWindowLongPtr(h, -20 /* GWL_EXSTYLE */).ToInt64();
                if ((ex & 0x20 /* WS_EX_TRANSPARENT */) != 0 || (ex & 0x80 /* WS_EX_TOOLWINDOW */) != 0 || (ex & 0x08000000 /* NOACTIVATE */) != 0) return true;
                if (!GetWindowRect(h, out RECT r)) return true;
                // Where it really shows: maximized windows hang a few invisible pixels onto the next screen.
                var seen = DwmGetWindowAttribute(h, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out RECT frame, Marshal.SizeOf<RECT>()) == 0 ? frame : r;
                int overlapW = Math.Min(seen.Right, monitor.Right) - Math.Max(seen.Left, monitor.Left);
                int overlapH = Math.Min(seen.Bottom, monitor.Bottom) - Math.Max(seen.Top, monitor.Top);
                if (overlapW <= 16 || overlapH <= 16) return true;   // not on this screen
                GetWindowThreadProcessId(h, out uint pid);
                if (pid == (uint)Environment.ProcessId) return true;
                string cls = ClassOf(h);
                if (cls is "Progman" or "WorkerW") return false;   // reached the desktop
                if (IsShellPopup(h)) return true;
                bool full = r.Left <= monitor.Left && r.Top <= monitor.Top && r.Right >= monitor.Right && r.Bottom >= monitor.Bottom;
                if (full) { covered = true; return false; }
                // A smaller window on top (a normal app window, picture-in-picture, a sticky note): a
                // full-screen or maximized app under it still counts, so look under it too.
                return true;
            }, IntPtr.Zero);
            return covered;
        }

        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT value, int size);

        /// <summary>Start menu, search, the tray's flyouts and menus.</summary>
        private static bool IsShellPopup(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            string c = ClassOf(h);
            return c is "Windows.UI.Core.CoreWindow" or "XamlExplorerHostIslandWindow" or "NotifyIconOverflowWindow"
                     or "TopLevelWindowForOverflowXamlIsland" or "Xaml_WindowedPopupClass" or "#32768"
                     or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
        }

        private static IntPtr _startCheckedFor;
        private static bool _startChecked;

        /// <summary>The Start menu or Windows search (by its program; remembered per window, since this runs often).</summary>
        internal static bool IsStartOrSearch(IntPtr h)
        {
            if (h == IntPtr.Zero || ClassOf(h) != "Windows.UI.Core.CoreWindow") return false;
            if (h == _startCheckedFor) return _startChecked;
            bool yes = false;
            try
            {
                GetWindowThreadProcessId(h, out uint pid);
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                yes = p.ProcessName is "StartMenuExperienceHost" or "SearchHost" or "SearchApp";
            }
            catch { }
            _startCheckedFor = h;
            _startChecked = yes;
            return yes;
        }

        private static string ClassOf(IntPtr h)
        {
            var sb = new StringBuilder(64);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        // ---- The extra room of a thicker bar -----------------------------------------------------------
        //
        // A bar thicker than Windows' strip asks Windows for the extra room (as an "app bar"), so
        // maximized windows stop above it. Their handles are written down, so if Wibn dies the
        // guard (or Wibn's next start) gives the room back.

        public static void RememberAppBar(IntPtr hwnd, bool on)
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(Key);
                var list = new HashSet<string>(k.GetValue("AppBars") as string[] ?? Array.Empty<string>());
                string v = hwnd.ToInt64().ToString();
                if (on) list.Add(v); else list.Remove(v);
                k.SetValue("AppBars", list.ToArray(), RegistryValueKind.MultiString);
            }
            catch { }
        }

        public static void RemoveLeftAppBars()
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(Key);
                foreach (var v in k.GetValue("AppBars") as string[] ?? Array.Empty<string>())
                {
                    if (!long.TryParse(v, out long h)) continue;
                    var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = new IntPtr(h) };
                    SHAppBarMessage(1 /* ABM_REMOVE */, ref abd);
                }
                k.SetValue("AppBars", Array.Empty<string>(), RegistryValueKind.MultiString);
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public RECT rc; public IntPtr lParam; }

        [DllImport("shell32.dll")] public static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);

        // ---- The guard ----------------------------------------------------------------------------

        private static void StartGuard()
        {
            try
            {
                string? exe = Environment.ProcessPath;
                if (exe == null) return;
                _guard = Process.Start(new ProcessStartInfo(exe, $"--taskbar-guard {Environment.ProcessId}")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                });
            }
            catch (Exception ex) { CrashLog.Write("taskbar guard", ex); }
        }

        private static void StopGuard()
        {
            try { if (_guard is { HasExited: false }) _guard.Kill(); } catch { }
            _guard = null;
        }

        /// <summary>
        /// The guard process: waits for Wibn. If Wibn closes or dies, Windows' taskbar comes
        /// back; if Wibn stops answering for a few seconds, it comes back too (Wibn hides it
        /// again once it's well).
        /// </summary>
        public static int RunGuard(string[] args)
        {
            if (args.Length < 2 || !int.TryParse(args[1], out int pid)) return 1;
            Process target;
            try { target = Process.GetProcessById(pid); } catch { ShowWindowsTaskbars(); return 0; }
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
            int hung = 0;
            while (true)
            {
                if (target.WaitForExit(2000)) break;
                if (Get("Active", 0) == 0) return 0;   // turned off: nothing to guard
                IntPtr bar = FindWindow(null, TaskbarBar.WindowTitle);
                bool answers = bar == IntPtr.Zero || SendMessageTimeout(bar, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 2500, out _) != IntPtr.Zero;
                hung = answers ? 0 : hung + 1;
                if (hung >= 2) { ShowWindowsTaskbars(); RestoreWorkAreas(); }
            }
            ShowWindowsTaskbars();
            Set("Active", 0);
            RemoveLeftAppBars();    // the extra room the thicker bar kept goes back to the windows
            RestoreWorkAreas();     // and the taskbar's strip is kept free again
            TrayHost.Broadcast();   // apps make sure Windows' taskbar has their icons
            return 0;
        }

        // ---- Native --------------------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
            public bool Contains(POINT p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;
        }

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

        private const int SW_HIDE = 0, SW_SHOWNA = 8;
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const uint SMTO_ABORTIFHUNG = 2;
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10, SWP_ASYNCWINDOWPOS = 0x4000;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string? name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
        // Explorer's taskbars belong to another app: showing or hiding them never waits for explorer.
        [DllImport("user32.dll", EntryPoint = "ShowWindowAsync")] private static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);
    }
}
