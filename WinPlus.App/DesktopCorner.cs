using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// The desktop's files from the bottom-left corner: when the mouse reaches the corner of a
    /// screen, a small card slides out of it; a click on it opens the panel with everything on
    /// the desktop (<see cref="DesktopPanel"/>). The card slides back once the mouse leaves it.
    /// Runs with the widgets, and can be turned off on the Widgets page.
    /// </summary>
    internal static class DesktopCorner
    {
        private const string Key = @"Software\WinPlus\Widgets";
        private static readonly Color Accent = Color.FromRgb(0x4C, 0x8D, 0xFF);

        private static readonly DispatcherTimer Poll = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
        private static readonly DispatcherTimer Reload = new() { Interval = TimeSpan.FromMilliseconds(300) };
        private static readonly List<FileSystemWatcher> Watchers = new();
        private static CornerCard? _card;
        private static DesktopPanel? _panel;
        private static bool _running, _hooked;
        private static DateTime _lastNear;

        public static bool Enabled
        {
            get => Read("DesktopCorner", 1) != 0;
            set
            {
                Write("DesktopCorner", value ? 1 : 0);
                if (value && Features.IsOn("widgets")) Start(); else Stop();
            }
        }

        /// <summary>The panel stays when you click outside it.</summary>
        public static bool Pinned
        {
            get => Read("DesktopPanelPinned", 0) != 0;
            set => Write("DesktopPanelPinned", value ? 1 : 0);
        }

        public static void Start()
        {
            if (_running || !Enabled) return;
            _running = true;
            if (!_hooked)
            {
                _hooked = true;
                Poll.Tick += (_, _) => OnPoll();
                Reload.Tick += (_, _) => { Reload.Stop(); Refresh(); };
            }
            Poll.Start();
            Watch();
            // The panel and its tiles (with their icons) ahead of time, so the first click opens it at once.
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => { if (_running) Panel().Load(Items()); });
        }

        public static void Stop()
        {
            _running = false;
            Poll.Stop();
            Reload.Stop();
            foreach (var w in Watchers) w.Dispose();
            Watchers.Clear();
            _card?.Close();
            _card = null;
            _panel?.Close();
            _panel = null;
        }

        // ---- The corner ---------------------------------------------------------------------------

        /// <summary>A few times a second: the mouse on the left edge just above the taskbar shows the
        /// tab; once it has been away from there and from the tab for a moment, the tab goes.</summary>
        private static void OnPoll()
        {
            POINT p;
            System.Windows.Forms.Screen screen;
            System.Drawing.Rectangle b, wa;
            bool fullscreen;
            double band;
            var card = _card;
            RECT r = default;
            using (new ScreenCapture.DpiScope())   // real pixels, like the card's spot
            {
                if (!GetCursorPos(out p)) return;
                screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y));
                b = screen.Bounds;
                wa = screen.WorkingArea;
                band = CornerCard.TabHeight * CornerCard.ScaleOf(wa.Left + 1, wa.Bottom - 1);
                fullscreen = FullscreenOn(screen) || CoveredAt(p);
                if (card != null) r = card.ScreenRect;
            }
            // The strip of the left edge where the tab comes out: right above the taskbar.
            bool inCorner = p.X <= b.Left + 1 && p.Y >= wa.Bottom - band && p.Y <= wa.Bottom + 2;
            bool shown = card?.Shown == true;

            if (inCorner && !shown && !fullscreen)
            {
                card = _card ??= NewCard();
                card.SlideIn(screen);
                _lastNear = DateTime.UtcNow;
                return;
            }
            if (!shown || card == null) return;

            // Near = on that strip, or on the tab (with a little room around it).
            bool near = inCorner || (p.X >= r.Left - 2 && p.X <= r.Right + 24 && p.Y >= r.Top - 24 && p.Y <= r.Bottom + 4);
            if (near) _lastNear = DateTime.UtcNow;
            else if ((DateTime.UtcNow - _lastNear).TotalMilliseconds > 600) card.SlideOut();
        }

        private static CornerCard NewCard()
        {
            var card = new CornerCard(Accent);
            card.Clicked += () =>
            {
                card.SlideOut();
                Toggle(card);
            };
            return card;
        }

        /// <summary>A program filling that screen (a game, a video) in front: no card over it.</summary>
        private static bool FullscreenOn(System.Windows.Forms.Screen screen)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == GetShellWindow()) return false;
            var cls = new System.Text.StringBuilder(64);
            GetClassName(fg, cls, cls.Capacity);
            if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
            if (!GetWindowRect(fg, out var r)) return false;
            var b = screen.Bounds;
            return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
        }

        /// <summary>A program's window is over that spot (the desktop isn't what's showing there):
        /// no tab then, it shows only on the bare desktop (or over Wibn's own widgets).</summary>
        private static bool CoveredAt(POINT p)
        {
            IntPtr hwnd = WindowFromPoint(p);
            if (hwnd == IntPtr.Zero) return false;
            IntPtr root = GetAncestor(hwnd, 2 /* GA_ROOT */);
            if (root == IntPtr.Zero) root = hwnd;
            var cls = new System.Text.StringBuilder(64);
            GetClassName(root, cls, cls.Capacity);
            if (cls.ToString() is "Progman" or "WorkerW") return false;
            GetWindowThreadProcessId(root, out uint pid);
            return pid != (uint)Environment.ProcessId;   // our own windows (the widgets, the tab) don't count
        }

        // ---- The panel ----------------------------------------------------------------------------

        private static void Toggle(Window opener)
        {
            if (_panel?.IsVisible == true) { _panel.HideIt(); return; }
            var panel = Panel(opener);
            panel.Load(Items());
            panel.ShowCentered();
        }

        private static DesktopPanel Panel(Window? opener = null)
        {
            if (_panel == null)
            {
                _panel = new DesktopPanel(Accent, opener ?? (_card ??= NewCard()));
                _panel.PinChanged += pinned => Pinned = pinned;
                var made = _panel;
                made.Closed += (_, _) => { if (_panel == made) _panel = null; };
            }
            _panel.ShowPinned(Pinned);
            return _panel;
        }

        // ---- The desktop's files ----------------------------------------------------------------

        private static IEnumerable<string> Folders() =>
            new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory }
                .Select(Environment.GetFolderPath).Where(d => d.Length > 0 && Directory.Exists(d)).Distinct(StringComparer.OrdinalIgnoreCase);

        /// <summary>What the desktop shows: folders first, then files, by name; no hidden or system ones.</summary>
        internal static List<string> Items()
        {
            var list = new List<string>();
            foreach (var dir in Folders())
            {
                try
                {
                    foreach (var path in Directory.EnumerateFileSystemEntries(dir))
                    {
                        try
                        {
                            var a = File.GetAttributes(path);
                            if ((a & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                            list.Add(path);
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return list
                .OrderBy(p => Directory.Exists(p) ? 0 : 1)
                .ThenBy(DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>Like the desktop: shortcuts without ".lnk" / ".url".</summary>
        internal static string DisplayName(string path)
        {
            string ext = Path.GetExtension(path);
            return ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path);
        }

        private static void Watch()
        {
            if (Watchers.Count > 0) return;
            foreach (var dir in Folders())
            {
                try
                {
                    var w = new FileSystemWatcher(dir) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes };
                    FileSystemEventHandler changed = (_, _) => Application.Current?.Dispatcher.BeginInvoke(() => { Reload.Stop(); Reload.Start(); });
                    w.Created += changed;
                    w.Deleted += changed;
                    w.Changed += changed;
                    w.Renamed += (s, e) => changed(s, e);
                    w.EnableRaisingEvents = true;
                    Watchers.Add(w);
                }
                catch { }
            }
        }

        private static void Refresh()
        {
            if (!_running) return;
            var items = Items();
            _panel?.Load(items);   // even while hidden: it's ready (and quick) the next time
        }

        // ---- Settings -----------------------------------------------------------------------------

        private static int Read(string name, int fallback)
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(Key); return k?.GetValue(name) is int v ? v : fallback; }
            catch { return fallback; }
        }

        private static void Write(string name, int value)
        {
            try { using var k = Registry.CurrentUser.CreateSubKey(Key); k.SetValue(name, value, RegistryValueKind.DWord); }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    }

    /// <summary>
    /// The tab that comes out of the left edge of the screen, right above the taskbar: narrow,
    /// straight, with "Desktop" written up its length. It slides out from behind the edge and
    /// slides back. Always on top, never takes the keyboard.
    /// </summary>
    internal sealed class CornerCard : Window
    {
        public const double TabWidth = 40, TabHeight = 150;
        private const double Hidden = -(TabWidth + 4);   // all of it behind the screen's edge
        private static readonly Brush PlainBack = new SolidColorBrush(Color.FromArgb(0xF0, 0x1E, 0x1F, 0x22));
        private static readonly Brush Edge = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));

        private readonly SolidColorBrush _ink = new(Color.FromRgb(0xF2, 0xF3, 0xF5));
        private readonly Border _tab;
        private readonly TranslateTransform _slide = new(Hidden, 0);
        private IntPtr _hwnd;

        public event Action? Clicked;
        public bool Shown { get; private set; }

        /// <summary>Where it is on the screen (real pixels).</summary>
        public DesktopCorner.RECT ScreenRect
        {
            get
            {
                DesktopCorner.RECT r = default;
                if (_hwnd != IntPtr.Zero) GetWindowRect(_hwnd, out r);
                return r;
            }
        }

        public CornerCard(Color accent)
        {
            Title = "Wibn desktop tab";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            Width = TabWidth + 8;
            Height = TabHeight + 4;
            FontFamily = Widgets.Font();

            // Just "Desktop", up the tab (read from the bottom).
            var line = new TextBlock
            {
                Text = Lang.T("سطح المكتب"), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = _ink,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                FlowDirection = Lang.Flow, LayoutTransform = new RotateTransform(-90),
            };

            _tab = new Border
            {
                Width = TabWidth, Height = TabHeight,
                CornerRadius = new CornerRadius(0, 10, 10, 0), Background = Widgets.BackColor() is { } b
                    ? new SolidColorBrush(Color.FromArgb(0xF0, b.Color.R, b.Color.G, b.Color.B)) : PlainBack,
                BorderBrush = Edge, BorderThickness = new Thickness(0, 1, 1, 1), Child = line, Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
                RenderTransform = _slide,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.35, Color = Colors.Black },
            };
            if (Widgets.BackColor() is { } back && Widgets.Lightness(back.Color) > 0.6) _ink.Color = Color.FromRgb(0x1C, 0x1C, 0x1E);
            Content = new Grid { Background = Brushes.Transparent, Children = { _tab } };

            _tab.MouseLeftButtonUp += (_, e) => { e.Handled = true; Clicked?.Invoke(); };
            SourceInitialized += (_, _) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                long ex = GetWindowLongPtr(_hwnd, -20).ToInt64();
                SetWindowLongPtr(_hwnd, -20, new IntPtr((ex | 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */) & ~0x40000L /* APPWINDOW */));
            };
        }

        /// <summary>Swings out of the left edge of that screen, its foot right on the taskbar.</summary>
        public void SlideIn(System.Windows.Forms.Screen screen)
        {
            Shown = true;
            if (!IsVisible)
            {
                _slide.BeginAnimation(TranslateTransform.XProperty, null);
                _slide.X = Hidden;
                Show();
            }
            using (new ScreenCapture.DpiScope())
            {
                var area = screen.WorkingArea;
                double scale = ScaleOf(area.Left + 1, area.Bottom - 1);
                int w = (int)Math.Round(Width * scale), h = (int)Math.Round(Height * scale);
                SetWindowPos(_hwnd, new IntPtr(-1) /* TOPMOST */, screen.Bounds.Left, area.Bottom - h, w, h, 0x0010 /* NOACTIVATE */ | 0x0040 /* SHOW */);
            }
            Move(0, 240, EasingMode.EaseOut);
        }

        public void SlideOut()
        {
            if (!Shown) return;
            Shown = false;
            var move = Move(Hidden, 180, EasingMode.EaseIn);
            move.Completed += (_, _) => { if (!Shown) Hide(); };
        }

        private DoubleAnimation Move(double x, int ms, EasingMode mode)
        {
            var a = new DoubleAnimation(x, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = mode } };
            _slide.BeginAnimation(TranslateTransform.XProperty, a);
            return a;
        }

        /// <summary>The screen scale (1 = 100%) of the monitor at that point (real pixels).</summary>
        internal static double ScaleOf(int x, int y)
        {
            try
            {
                IntPtr mon = MonitorFromPoint(new DesktopCorner.POINT { X = x, Y = y }, 2 /* NEAREST */);
                if (GetDpiForMonitor(mon, 0, out uint dpiX, out _) == 0 && dpiX > 0) return dpiX / 96.0;
            }
            catch { }
            return 1;
        }

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out DesktopCorner.RECT r);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(DesktopCorner.POINT p, uint flags);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    }
}
