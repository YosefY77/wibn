using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace WinPlus
{
    /// <summary>
    /// One of Wibn's taskbars (one per screen): Start, search and task view, the apps,
    /// then the status icons, the clock and the "show desktop" corner. It never takes the
    /// focus from the window you're working in, so clicking an app's button can minimize it.
    /// </summary>
    internal sealed class TaskbarBar : Window
    {
        public const string WindowTitle = "Wibn Taskbar";

        /// <summary>Windows asking for attention (flashing orange on Windows' taskbar).</summary>
        public static readonly HashSet<IntPtr> Flashing = new();

        private readonly bool _primary;
        private IntPtr _hwnd;
        private double _scale = 1;
        private CustomTaskbar.RECT _strip, _monitor, _placed;
        private bool _vertical, _top, _shown, _built;
        private int _shellMsg;

        private readonly Grid _root = new();
        private readonly Grid _host = new();                 // the window's content (the glass's untransformed parent)
        private readonly Border _card = new();               // the bar's background: glass, a color, or see-through
        private LiquidGlass? _glass;
        private readonly DispatcherTimer _glassSoon = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        private readonly DispatcherTimer _wallpaper = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
        private double _radius;
        private StackPanel _apps = new();
        private readonly Dictionary<string, AppButton> _buttons = new();
        private string _signature = "";
        private readonly DispatcherTimer _refresh = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _soon = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(80) };
        private TextBlock? _time, _date, _net, _vol, _bat;
        private FrameworkElement? _clock;

        // Looks (worked out in ApplyLook)
        internal readonly SolidColorBrush Ink = new(Colors.White), Hover = new(Colors.Transparent), Pressed = new(Colors.Transparent), ActiveBack = new(Colors.Transparent);
        internal static readonly SolidColorBrush Accent = new(Color.FromRgb(0x4C, 0xC2, 0xFF));
        internal static readonly SolidColorBrush FlashBack = new(Color.FromArgb(0x80, 0xFF, 0x95, 0x00));
        internal double ButtonSize = 40, IconPx = 24;
        internal bool Vertical => _vertical;
        internal bool AtTop => _top;
        /// <summary>The screen it's on, and where it is now (screen pixels; empty until placed).</summary>
        internal CustomTaskbar.RECT Monitor => _monitor;
        internal CustomTaskbar.RECT Placed => _placed;

        public TaskbarBar(bool primary)
        {
            _primary = primary;
            Title = WindowTitle;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Background = Brushes.Transparent;
            AllowsTransparency = true;   // really see-through, and the glass is drawn by Wibn (like the widgets)
            AllowDrop = true;
            _card.Child = _root;
            _host.Children.Add(_card);
            Content = _host;
            FlowDirection = WindowsIsRtl() ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

            _hwnd = new WindowInteropHelper(this).EnsureHandle();
            TaskbarApps.Ours.Add(_hwnd);
            long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
            int yes = 1;
            DwmSetWindowAttribute(_hwnd, DWMWA_EXCLUDED_FROM_PEEK, ref yes, 4);
            var src = HwndSource.FromHwnd(_hwnd);
            src.AddHook(WndProc);
            _scale = src.CompositionTarget.TransformToDevice.M11;

            if (_primary)
            {
                _shellMsg = RegisterWindowMessage("SHELLHOOK");
                RegisterShellHookWindow(_hwnd);
            }

            _refresh.Tick += (_, _) => RefreshApps();
            _glassSoon.Tick += (_, _) => { _glassSoon.Stop(); StartGlass(); };
            _wallpaper.Tick += (_, _) => _glass?.Refresh();   // wallpaper slideshows
            InputLang.Changed += ShowLang;
            TrayHost.Changed += RefreshTray;
            _soon.Tick += (_, _) => { _soon.Stop(); CustomTaskbar.RefreshAll(); };
            Status.Updated += ShowStatus;
            ContextMenuOpening += (_, e) => e.Handled = true;
            MouseRightButtonUp += (s, e) => { if (e.OriginalSource == _root || e.OriginalSource == _card || e.OriginalSource is Grid or DockPanel or StackPanel) { BarMenu(); e.Handled = true; } };
            Drop += OnDrop;
            HookEndDrag();
        }

        // ---- Place and look ---------------------------------------------------------------------

        /// <summary>Called by the manager four times a second: where it goes and whether it shows.</summary>
        public void Place(CustomTaskbar.RECT strip, CustomTaskbar.RECT monitor, bool visible, CustomTaskbar.RECT windowsTray, bool slide = false, bool overFullscreen = false)
        {
            UpdateAppBar(windowsTray, monitor);
            bool vertical = strip.Height > strip.Width;
            bool top = !vertical && strip.Top <= monitor.Top + 1;
            if (!_built || !Same(strip, _strip) || vertical != _vertical || top != _top)
            {
                _strip = strip; _monitor = monitor; _vertical = vertical; _top = top;
                ApplyLook();
            }

            if (!visible)
            {
                if (!_shown) return;
                TaskbarPreview.HideNow();
                CloseFlyout();
                if (slide) SlideTo(1);   // goes down off the screen, then hides
                else HideNow();
                return;
            }
            bool first = !_shown;
            if (first)
            {
                _slide = slide ? 1 : 0;   // comes up from the screen's edge
                Show(); _shown = true; _refresh.Start();
            }
            SlideTo(0);
            var r = first || _placed.Width == 0 ? Target() : _placed;
            if (first || !Same(r, _placed))
            {
                bool endsMoved = !Same(r, _placed);
                _placed = r;
                MoveWindow();
                if (endsMoved) CustomTaskbar.WidgetsFollowSoon();
                if (_slide == 0) RestartGlass();
            }
            // Full-screen apps (games, videos) often put themselves on top of everything too:
            // the bar keeps coming back above them while it's showing.
            else if (overFullscreen && _slideClock == null)
                SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOACTIVATE | 0x2 /* NOMOVE */ | 0x1 /* NOSIZE */);
            if (first) RefreshApps();
        }

        /// <summary>Up (or on its way up), not hidden or sliding down.</summary>
        internal bool Showing => _shown && !(_slideClock != null && _slideTo >= 1);

        private void HideNow()
        {
            StopSlide();
            _slide = 0;
            Hide(); _shown = false; TaskbarPreview.HideNow(); StopGlass(); CloseFlyout();
        }

        // ---- Sliding off the screen's edge (full screen) ------------------------------------------------

        private double _slide, _slideFrom, _slideTo;   // 0 = in its place, 1 = fully off the screen
        private System.Diagnostics.Stopwatch? _slideClock;
        private const double SlideMs = 170;

        private void SlideTo(double to)
        {
            if (_slideClock != null ? _slideTo == to : _slide == to) return;
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
            if (t >= 1)
            {
                _slide = _slideTo;
                StopSlide();
                if (_slide >= 1) { HideNow(); return; }
                MoveWindow();
                RestartGlass();   // in its place again: the glass sees what's behind it now
                return;
            }
            MoveWindow();
        }

        /// <summary>The window at its place, pushed toward the screen's edge as far as it's slid.</summary>
        private void MoveWindow()
        {
            var r = _placed;
            int dx = 0, dy = 0;
            if (_slide > 0)
            {
                if (!_vertical) dy = _top ? -(int)Math.Round((r.Bottom - _monitor.Top) * _slide) : (int)Math.Round((_monitor.Bottom - r.Top) * _slide);
                else if (r.Left - _monitor.Left < _monitor.Right - r.Right) dx = -(int)Math.Round((r.Right - _monitor.Left) * _slide);
                else dx = (int)Math.Round((_monitor.Right - r.Left) * _slide);
            }
            SetWindowPos(_hwnd, HWND_TOPMOST, r.Left + dx, r.Top + dy, r.Width, r.Height, SWP_NOACTIVATE);
        }

        /// <summary>Where the window goes, in screen pixels, for the chosen shape.</summary>
        private CustomTaskbar.RECT Target()
        {
            var s = _strip;
            int shape = CustomTaskbar.Shape;
            int gap = (int)Math.Round(5 * _scale);
            var r = shape == 0 ? s : new CustomTaskbar.RECT { Left = s.Left + gap, Top = s.Top + gap, Right = s.Right - gap, Bottom = s.Bottom - gap };
            if (shape != 2 && !_vertical)
            {
                // Made shorter by dragging an end: both ends come in by the same amount (it stays centered).
                int inset = InsetPx(r.Width);
                r.Left += inset; r.Right -= inset;
            }
            if (shape == 0) return r;
            if (shape == 2)
            {
                // Dock: only as long as what's on it, in the middle.
                _root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                if (_vertical)
                {
                    int len = Math.Min(r.Height, (int)Math.Ceiling(_root.DesiredSize.Height * _scale) + gap * 2);
                    int mid = (s.Top + s.Bottom) / 2;
                    r.Top = mid - len / 2; r.Bottom = r.Top + len;
                }
                else
                {
                    int len = Math.Min(r.Width, (int)Math.Ceiling(_root.DesiredSize.Width * _scale) + gap * 2);
                    int mid = (s.Left + s.Right) / 2;
                    r.Left = mid - len / 2; r.Right = r.Left + len;
                }
            }
            return r;
        }

        // ---- Shorter or longer by dragging an end ------------------------------------------------------
        //
        // The outer few pixels at either end of a horizontal bar (full or floating) can be dragged in or
        // out; both ends move together. A click there still does what it did (e.g. "show desktop" at the
        // far end): only a press that moves becomes a drag. A double-click there makes it full length again.

        private const double EndZone = 7;        // DIPs at each end that can be dragged
        private const double MinLength = 320;    // never shorter than this (DIPs), nor than what's on it
        private double? _dragInset;              // while dragging: the length being tried (DIPs at each end)
        private double _dragFromX, _dragFromInset;
        private int _dragSide;                    // -1 = left end, 1 = right end
        private bool _dragPending, _dragging;

        /// <summary>Works out its place again (the length changed).</summary>
        internal void Relayout()
        {
            _placed = default;   // the next tick puts it in its new place and redoes the glass
        }

        /// <summary>How far each end comes in (pixels), kept so what's on the bar still fits.</summary>
        private int InsetPx(int fullWidth)
        {
            double want = (_dragInset ?? CustomTaskbar.Inset) * _scale;
            if (want <= 0) return 0;
            _root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double min = Math.Max(MinLength * _scale, _root.DesiredSize.Width * _scale + 24 * _scale);
            double max = Math.Max(0, (fullWidth - min) / 2);
            return (int)Math.Round(Math.Clamp(want, 0, max));
        }

        private bool CanResize => CustomTaskbar.Shape != 2 && !_vertical && _placed.Width > 0;

        /// <summary>Which end the mouse is at (screen pixels): -1 left, 1 right, 0 neither.</summary>
        private int EndAt(double screenX)
        {
            if (!CanResize) return 0;
            double zone = EndZone * _scale;
            if (screenX - _placed.Left <= zone) return -1;
            if (_placed.Right - screenX <= zone) return 1;
            return 0;
        }

        private double ScreenX(MouseEventArgs e) => PointToScreen(e.GetPosition(this)).X;

        private void HookEndDrag()
        {
            PreviewMouseMove += (_, e) =>
            {
                double x = ScreenX(e);
                if (_dragPending && !_dragging && e.LeftButton == MouseButtonState.Pressed && Math.Abs(x - _dragFromX) > 4 * _scale)
                {
                    _dragging = true;
                    _dragInset = CustomTaskbar.Inset;
                    _dragFromInset = InsetPx(_strip.Width) / _scale;
                    _dragInset = _dragFromInset;
                    TaskbarPreview.HideNow();
                    CaptureMouse();
                }
                if (_dragging)
                {
                    // Toward the middle = shorter; both ends move together.
                    double moved = (x - _dragFromX) * -_dragSide / _scale;
                    _dragInset = Math.Max(0, _dragFromInset + moved);
                    var r = Target();
                    if (!Same(r, _placed)) { _placed = r; MoveWindow(); }
                    e.Handled = true;
                    return;
                }
                Cursor = EndAt(x) != 0 ? Cursors.SizeWE : null;
            };
            MouseLeave += (_, _) => { if (!_dragging) Cursor = null; };
            PreviewMouseLeftButtonDown += (_, e) =>
            {
                double x = ScreenX(e);
                int side = EndAt(x);
                if (side == 0) return;
                if (e.ClickCount == 2)
                {
                    _dragPending = false;
                    e.Handled = true;
                    if (CustomTaskbar.Inset != 0) CustomTaskbar.Inset = 0;   // full length again
                    return;
                }
                _dragPending = true;   // a click still goes through; it's a drag only once the mouse moves
                _dragSide = side;
                _dragFromX = x;
            };
            PreviewMouseLeftButtonUp += (_, e) =>
            {
                _dragPending = false;
                if (!_dragging) return;
                e.Handled = true;   // not a click on what's at the end
                EndDrag();
            };
            LostMouseCapture += (_, _) => { if (_dragging) EndDrag(); };
        }

        private void EndDrag()
        {
            _dragging = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            int inset = (int)Math.Round(InsetPx(_strip.Width) / _scale);   // what it really ended at (kept to fit)
            _dragInset = null;
            CustomTaskbar.Inset = inset;   // saved; every bar (all screens) takes it, and the widgets line up again
        }

        private static bool Same(CustomTaskbar.RECT a, CustomTaskbar.RECT b) =>
            a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

        /// <summary>Builds the bar again with the current settings.</summary>
        public void ApplyLook()
        {
            _built = true;
            int shape = CustomTaskbar.Shape, look = CustomTaskbar.Look, opacity = Math.Clamp(CustomTaskbar.Opacity, 0, 100);
            int rgb = CustomTaskbar.BackColor;
            var color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

            // Light background → dark icons and text (the glass says so itself once it sees what's behind).
            SetInk(look != 0 && Widgets.Lightness(color) > 0.6 && (look == 1 || opacity >= 50));

            // Background
            double h = (_vertical ? _strip.Width : _strip.Height) / _scale - (shape == 0 ? 0 : 10);
            _radius = shape == 0 ? 0 : Math.Clamp(h / 2 - 4, 6, 14);
            _card.CornerRadius = new CornerRadius(_radius);
            StopGlass();
            byte alpha = (byte)Math.Max(1, opacity * 255 / 100);   // never 0: fully clear parts would let clicks through
            _card.Background = look switch
            {
                1 => new SolidColorBrush(color),
                2 => new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)),
                _ => new SolidColorBrush(Color.FromArgb(0xE0, 0x1C, 0x1C, 0x1E)),   // until the glass is ready (or with it off)
            };
            _card.BorderThickness = new Thickness(shape == 0 ? 0 : 1);
            _card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            if (look == 0) RestartGlass();

            // Sizes (in device-independent pixels)
            double thick = (_vertical ? _strip.Width : _strip.Height) / _scale;
            if (shape != 0) thick -= 10;
            if (thick < 24) thick = 40;
            IconPx = CustomTaskbar.IconSize switch { 0 => 20, 2 => 28, _ => 24 };
            ButtonSize = Math.Max(24, Math.Min(thick - 6, IconPx + 16));
            IconPx = Math.Min(IconPx, ButtonSize - 10);

            Build();
            _signature = "";
            RefreshApps();
            RefreshTray();
            ShowStatus();
            _placed = default;   // re-placed on the next tick (the dock's length may have changed)
        }

        private void Build()
        {
            _root.Children.Clear();
            _root.ColumnDefinitions.Clear();
            _root.RowDefinitions.Clear();
            _buttons.Clear();
            var orient = _vertical ? Orientation.Vertical : Orientation.Horizontal;

            // Start, search, task view
            var start = new StackPanel { Orientation = orient, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            if (CustomTaskbar.StartButton) start.Children.Add(StartButton());
            if (CustomTaskbar.SearchButton)
                start.Children.Add(SmallButton("", Lang.T("البحث"), () => KeyPress.Send(KeyPress.LWin, KeyPress.S)));
            if (CustomTaskbar.TaskViewButton)
                start.Children.Add(SmallButton("", Lang.T("عرض المهام"), () => KeyPress.Send(KeyPress.LWin, KeyPress.Tab)));

            _apps = new StackPanel { Orientation = orient, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };

            var front = new StackPanel { Orientation = orient, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            front.Children.Add(start);
            front.Children.Add(_apps);

            // Status, clock, desktop corner
            var tray = new StackPanel { Orientation = orient, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            _chevron = SmallButton(_top ? "\uE70D" : "\uE70E", Lang.T("الأيقونات المخفية"), OpenTrayFlyout, 12);
            tray.Children.Add(_chevron);
            _trayIcons = new StackPanel { Orientation = orient, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            tray.Children.Add(_trayIcons);
            tray.Children.Add(LangButton());
            tray.Children.Add(StatusButton());
            tray.Children.Add(ClockButton());
            var end = new StackPanel { Orientation = orient };
            end.Children.Add(tray);
            if (CustomTaskbar.DesktopCorner) end.Children.Add(DesktopCorner());

            bool center = CustomTaskbar.Align == 0 && CustomTaskbar.Shape != 2;
            var pad = _vertical ? new Thickness(0, 4, 0, 0) : new Thickness(4, 0, 0, 0);
            if (center)
            {
                // Three slots: the middle one is centered on the whole bar, like Windows 11.
                if (_vertical)
                {
                    _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    Grid.SetRow(front, 1);
                    Grid.SetRow(end, 2);
                    end.VerticalAlignment = VerticalAlignment.Bottom;
                }
                else
                {
                    _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    Grid.SetColumn(front, 1);
                    Grid.SetColumn(end, 2);
                    end.HorizontalAlignment = HorizontalAlignment.Right;
                }
                _root.Children.Add(front);
                _root.Children.Add(end);
            }
            else
            {
                var dock = new DockPanel { LastChildFill = true };
                DockPanel.SetDock(end, _vertical ? Dock.Bottom : Dock.Right);
                dock.Children.Add(end);
                front.HorizontalAlignment = _vertical ? HorizontalAlignment.Center : HorizontalAlignment.Left;
                front.VerticalAlignment = _vertical ? VerticalAlignment.Top : VerticalAlignment.Center;
                front.Margin = pad;
                if (CustomTaskbar.Shape == 2)
                {
                    // The dock is exactly as long as what's on it.
                    var line = new StackPanel { Orientation = orient, Margin = pad };
                    front.Margin = new Thickness(0);
                    line.Children.Add(front);
                    line.Children.Add(new Border { Width = _vertical ? 0 : 10, Height = _vertical ? 10 : 0 });
                    line.Children.Add(end);
                    line.HorizontalAlignment = HorizontalAlignment.Center;
                    line.VerticalAlignment = VerticalAlignment.Center;
                    _root.Children.Add(line);
                    return;
                }
                dock.Children.Add(front);
                _root.Children.Add(dock);
            }
        }

        // ---- The apps ---------------------------------------------------------------------------

        /// <summary>Puts the buttons in line with the open windows and pins.</summary>
        public void RefreshApps()
        {
            if (!_built) return;
            List<TaskbarApps.Group> groups;
            try { groups = TaskbarApps.Groups(); }
            catch (Exception ex) { CrashLog.Write("taskbar groups", ex); return; }

            string sig = string.Join("|", groups.Select(g => g.Key + ":" + g.Windows.Count));
            if (sig != _signature)
            {
                _signature = sig;
                var keep = new Dictionary<string, AppButton>(_buttons);
                _buttons.Clear();
                _apps.Children.Clear();
                foreach (var g in groups)
                {
                    if (!keep.TryGetValue(g.Key, out var b)) b = new AppButton(this);
                    _buttons[g.Key] = b;
                    _apps.Children.Add(b);
                }
                if (CustomTaskbar.Shape == 2) _placed = default;   // the dock grows / shrinks
            }
            IntPtr fg = TaskbarApps.Foreground();
            foreach (var g in groups)
                if (_buttons.TryGetValue(g.Key, out var b)) b.Show(g, fg);
        }

        /// <summary>The pinned buttons, in order (for dragging them around).</summary>
        internal List<AppButton> PinnedButtons() =>
            _apps.Children.OfType<AppButton>().Where(b => b.Group?.Pin != null).ToList();

        internal void MoveButton(AppButton b, int index)
        {
            _apps.Children.Remove(b);
            _apps.Children.Insert(Math.Clamp(index, 0, _apps.Children.Count), b);
        }

        internal void Changed() { _signature = ""; RefreshApps(); }

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
            foreach (var f in files)
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is ".exe" or ".lnk") TaskbarApps.AddPin(f);
            }
            CustomTaskbar.RefreshAll();
        }

        // ---- Buttons ------------------------------------------------------------------------------

        internal Border ButtonFrame(UIElement child, double w, double h)
        {
            var b = new Border
            {
                Width = w, Height = h, CornerRadius = new CornerRadius(5), Background = Brushes.Transparent,
                Child = child, Margin = new Thickness(_vertical ? 0 : 2, _vertical ? 2 : 0, _vertical ? 0 : 2, _vertical ? 2 : 0),
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            };
            b.MouseEnter += (_, _) => { if (b.Tag == null) b.Background = Hover; };
            b.MouseLeave += (_, _) => { if (b.Tag == null) b.Background = Brushes.Transparent; };
            b.PreviewMouseLeftButtonDown += (_, _) => { if (b.Tag == null) b.Background = Pressed; };
            b.PreviewMouseLeftButtonUp += (_, _) => { if (b.Tag == null) b.Background = b.IsMouseOver ? Hover : Brushes.Transparent; };
            return b;
        }

        private Border SmallButton(string glyph, string tip, Action click, double size = 16)
        {
            var t = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = size,
                Foreground = Ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            double s = ButtonSize;
            var b = ButtonFrame(t, _vertical ? s : Math.Max(28, s - 6), s);
            b.ToolTip = tip;
            b.MouseLeftButtonUp += (_, e) => { click(); e.Handled = true; };
            return b;
        }

        private Border StartButton()
        {
            // The four squares of the Windows logo, in Windows 11's blue.
            double q = IconPx * 0.46, gap = IconPx * 0.06;
            var logo = new Canvas { Width = IconPx * 0.98, Height = IconPx * 0.98, FlowDirection = FlowDirection.LeftToRight };
            var fill = new LinearGradientBrush(Color.FromRgb(0x5E, 0xD0, 0xFF), Color.FromRgb(0x00, 0x78, 0xD4), 45);
            for (int i = 0; i < 4; i++)
            {
                var sq = new Border { Width = q, Height = q, Background = fill, CornerRadius = new CornerRadius(q * 0.12) };
                Canvas.SetLeft(sq, i % 2 * (q + gap));
                Canvas.SetTop(sq, i / 2 * (q + gap));
                logo.Children.Add(sq);
            }
            var b = ButtonFrame(logo, ButtonSize, ButtonSize);
            b.ToolTip = Lang.T("ابدأ");
            b.MouseLeftButtonUp += (_, e) => { KeyPress.Send(KeyPress.LWin); e.Handled = true; };
            b.MouseRightButtonUp += (_, e) => { KeyPress.Send(KeyPress.LWin, KeyPress.X); e.Handled = true; };
            return b;
        }

        private TextBlock? _lang, _region;
        private Border? _langButton;

        /// <summary>The keyboard language (ENG / ARA) like Windows shows it; a click switches it.</summary>
        private Border LangButton()
        {
            _lang = new TextBlock { FontSize = 12, Foreground = Ink, HorizontalAlignment = HorizontalAlignment.Center, FlowDirection = FlowDirection.LeftToRight };
            _region = new TextBlock { FontSize = 10, Foreground = Ink, Opacity = 0.75, HorizontalAlignment = HorizontalAlignment.Center, FlowDirection = FlowDirection.LeftToRight };
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
            stack.Children.Add(_lang);
            if (ButtonSize >= 32) stack.Children.Add(_region);
            var b = ButtonFrame(stack, double.NaN, ButtonSize);
            if (_vertical) { b.Width = ButtonSize; b.Height = double.NaN; stack.Margin = new Thickness(0, 4, 0, 4); }
            b.ToolTip = Lang.T("لغة الكيبورد (كليك عشان تغيّرها)");
            b.MouseLeftButtonUp += (_, e) => { KeyPress.Send(KeyPress.LWin, KeyPress.Space); e.Handled = true; };
            _langButton = b;
            InputLang.Touch();
            ShowLang();
            return b;
        }

        private void ShowLang()
        {
            if (_lang == null || _region == null || _langButton == null) return;
            _langButton.Visibility = InputLang.Many ? Visibility.Visible : Visibility.Collapsed;
            _lang.Text = InputLang.Language;
            _region.Text = InputLang.Region;
        }

        private Border StatusButton()
        {
            var icons = new StackPanel { Orientation = _vertical ? Orientation.Vertical : Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            TextBlock G() => new()
            {
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 15, Foreground = Ink,
                Margin = _vertical ? new Thickness(0, 2, 0, 2) : new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, FlowDirection = FlowDirection.LeftToRight,
            };
            _net = G(); _vol = G(); _bat = G();
            icons.Children.Add(_net);
            icons.Children.Add(_vol);
            icons.Children.Add(_bat);
            var b = ButtonFrame(icons, double.NaN, ButtonSize);
            b.Padding = new Thickness(4, 0, 4, 0);
            if (_vertical) { b.Width = ButtonSize; b.Height = double.NaN; b.Padding = new Thickness(0, 4, 0, 4); }
            b.MouseLeftButtonUp += (_, e) => { KeyPress.Send(KeyPress.LWin, KeyPress.A); e.Handled = true; };
            b.MouseWheel += (_, e) => { Status.ChangeVolume(e.Delta > 0 ? 0.02f : -0.02f); e.Handled = true; };
            return b;
        }

        private Border ClockButton()
        {
            _time = new TextBlock { FontSize = 12, Foreground = Ink, HorizontalAlignment = _vertical ? HorizontalAlignment.Center : HorizontalAlignment.Right, FlowDirection = FlowDirection.LeftToRight };
            _date = new TextBlock { FontSize = 12, Foreground = Ink, HorizontalAlignment = _vertical ? HorizontalAlignment.Center : HorizontalAlignment.Right, FlowDirection = FlowDirection.LeftToRight };
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
            stack.Children.Add(_time);
            if (CustomTaskbar.ShowDate && ButtonSize >= 32) stack.Children.Add(_date);
            if (_vertical) { stack.Margin = new Thickness(0, 4, 0, 4); _time.FontSize = 11; _date.FontSize = 10; }
            var b = ButtonFrame(stack, double.NaN, ButtonSize);
            if (_vertical) { b.Width = ButtonSize + 8; b.Height = double.NaN; }
            b.MouseLeftButtonUp += (_, e) => { KeyPress.Send(KeyPress.LWin, KeyPress.N); e.Handled = true; };
            _clock = b;
            return b;
        }

        private Border DesktopCorner()
        {
            var line = new Border
            {
                Width = _vertical ? double.NaN : 1, Height = _vertical ? 1 : double.NaN,
                Background = new SolidColorBrush(Color.FromArgb(0x50, Ink.Color.R, Ink.Color.G, Ink.Color.B)),
                Margin = _vertical ? new Thickness(6, 0, 6, 0) : new Thickness(0, 8, 0, 8),
            };
            var b = new Border
            {
                Width = _vertical ? double.NaN : 10, Height = _vertical ? 10 : double.NaN, Background = Brushes.Transparent,
                Child = line, Margin = _vertical ? new Thickness(0, 4, 0, 0) : new Thickness(4, 0, 0, 0),
                ToolTip = Lang.T("سطح المكتب"),
            };
            line.Opacity = 0;
            b.MouseEnter += (_, _) => line.Opacity = 1;
            b.MouseLeave += (_, _) => line.Opacity = 0;
            b.MouseLeftButtonUp += (_, e) => { ToggleDesktop(); e.Handled = true; };
            return b;
        }

        private static void ToggleDesktop()
        {
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application");
                if (type == null) return;
                object? shell = Activator.CreateInstance(type);
                type.InvokeMember("ToggleDesktop", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
                if (shell != null) Marshal.ReleaseComObject(shell);
            }
            catch { KeyPress.Send(KeyPress.LWin, KeyPress.D); }
        }

        // ---- Thicker than Windows' strip: the extra room is asked for as an app bar ----------------------

        private bool _appBar;
        private CustomTaskbar.RECT _appBarWant;
        private int _appBarMsg;

        private void UpdateAppBar(CustomTaskbar.RECT tray, CustomTaskbar.RECT monitor)
        {
            bool horizontal = tray.Width >= tray.Height;
            bool bottom = tray.Bottom >= monitor.Bottom - 1, top = tray.Top <= monitor.Top + 1;
            int extra = (int)Math.Round(CustomTaskbar.ThicknessDip * _scale) - tray.Height;
            if (CustomTaskbar.HideInFullscreen) { RemoveAppBar(); return; }   // the whole screen is the windows' then
            // Only beside a normal horizontal taskbar (not Windows' auto-hide, which keeps no room).
            if (!horizontal || tray.Height < 24 || extra <= 0 || !(bottom || top)) { RemoveAppBar(); return; }

            var want = bottom
                ? new CustomTaskbar.RECT { Left = monitor.Left, Right = monitor.Right, Top = tray.Top - extra, Bottom = tray.Top }
                : new CustomTaskbar.RECT { Left = monitor.Left, Right = monitor.Right, Top = tray.Bottom, Bottom = tray.Bottom + extra };
            if (_appBar && Same(want, _appBarWant)) return;   // only when it changes: each change moves maximized windows
            _appBarWant = want;

            if (!_appBar)
            {
                if (_appBarMsg == 0) _appBarMsg = RegisterWindowMessage("WibnAppBar");
                var nw = new CustomTaskbar.APPBARDATA { cbSize = Marshal.SizeOf<CustomTaskbar.APPBARDATA>(), hWnd = _hwnd, uCallbackMessage = (uint)_appBarMsg };
                if (CustomTaskbar.SHAppBarMessage(ABM_NEW, ref nw) == IntPtr.Zero) return;
                _appBar = true;
                CustomTaskbar.RememberAppBar(_hwnd, true);
            }
            var abd = new CustomTaskbar.APPBARDATA { cbSize = Marshal.SizeOf<CustomTaskbar.APPBARDATA>(), hWnd = _hwnd, uEdge = bottom ? ABE_BOTTOM : ABE_TOP, rc = want };
            CustomTaskbar.SHAppBarMessage(ABM_QUERYPOS, ref abd);
            if (bottom) abd.rc.Top = abd.rc.Bottom - extra; else abd.rc.Bottom = abd.rc.Top + extra;   // keep the thickness
            CustomTaskbar.SHAppBarMessage(ABM_SETPOS, ref abd);
        }

        private void RemoveAppBar()
        {
            if (!_appBar) return;
            var abd = new CustomTaskbar.APPBARDATA { cbSize = Marshal.SizeOf<CustomTaskbar.APPBARDATA>(), hWnd = _hwnd };
            CustomTaskbar.SHAppBarMessage(ABM_REMOVE, ref abd);
            _appBar = false;
            _appBarWant = default;
            CustomTaskbar.RememberAppBar(_hwnd, false);
        }

        private const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABE_TOP = 1, ABE_BOTTOM = 3;

        // ---- The icons next to the clock --------------------------------------------------------------

        private Border? _chevron;
        private StackPanel? _trayIcons;
        private TrayFlyout? _flyout;
        private readonly Dictionary<TrayHost.Icon, TrayIconView> _trayViews = new();

        /// <summary>The icons chosen to show on the bar; the rest are under the arrow.</summary>
        private void RefreshTray()
        {
            if (_trayIcons == null) return;
            _trayIcons.Children.Clear();
            _trayViews.Clear();
            if (!TrayHost.Active) return;
            foreach (var i in TrayHost.Icons.Where(i => !i.Hidden && i.Image != null && TrayHost.OnBar(i)))
            {
                var v = new TrayIconView(this, i, ButtonSize, onBar: true);
                _trayViews[i] = v;
                _trayIcons.Children.Add(v);
            }
            if (_flyout?.IsOpen == true) _flyout.Fill();
            if (CustomTaskbar.Shape == 2) _placed = default;   // the dock's length changed
        }

        /// <summary>Where an icon's button is on the screen, in pixels (null if this bar doesn't show it).</summary>
        public CustomTaskbar.RECT? TrayIconRect(TrayHost.Icon icon)
        {
            if (!_shown || !_trayViews.TryGetValue(icon, out var v) || !v.IsVisible) return null;
            var a = v.PointToScreen(new Point(0, 0));
            var z = v.PointToScreen(new Point(v.ActualWidth, v.ActualHeight));
            return new CustomTaskbar.RECT
            {
                Left = (int)Math.Min(a.X, z.X), Top = (int)Math.Min(a.Y, z.Y),
                Right = (int)Math.Max(a.X, z.X), Bottom = (int)Math.Max(a.Y, z.Y),
            };
        }

        /// <summary>The arrow: the hidden icons in a small panel (or, if Wibn couldn't take the icons, Windows' taskbar for a moment).</summary>
        private void OpenTrayFlyout()
        {
            if (!TrayHost.Active) { CustomTaskbar.Peek(); return; }
            if (_flyout?.IsOpen == true) { _flyout.CloseIt(); _flyout = null; return; }
            TaskbarPreview.HideNow();
            if (_chevron != null) _flyout = new TrayFlyout(this, _chevron);
        }

        internal void CloseFlyout() { _flyout?.CloseIt(); _flyout = null; }

        // ---- Status and clock --------------------------------------------------------------------

        private void ShowStatus()
        {
            if (_time == null || _date == null) return;
            var now = DateTime.Now;
            var c = CultureInfo.CurrentCulture;
            _time.Text = now.ToString(CustomTaskbar.Seconds ? c.DateTimeFormat.LongTimePattern : c.DateTimeFormat.ShortTimePattern, c);
            _date.Text = now.ToString(c.DateTimeFormat.ShortDatePattern, c);
            if (_clock != null) _clock.ToolTip = now.ToString(c.DateTimeFormat.LongDatePattern, c);

            if (_net != null)
            {
                _net.Text = Status.Wifi ? "" : "";
                _net.Opacity = Status.Online ? 1 : 0.4;
                _net.ToolTip = Status.Online ? null : Lang.T("ما فيه إنترنت");
            }
            if (_vol != null)
            {
                float v = Status.Volume;
                _vol.Text = Status.Muted ? "" : v <= 0.01 ? "" : v < 0.34 ? "" : v < 0.67 ? "" : "";
                _vol.ToolTip = Status.Muted ? Lang.T("مكتوم") : $"{Math.Round(v * 100)}%";
            }
            if (_bat != null)
            {
                if (Status.Battery is { } p)
                {
                    int step = Math.Clamp((int)Math.Round(p / 10.0), 0, 10);
                    _bat.Text = Status.Charging
                        ? (step == 10 ? "" : ((char)(0xE85A + step)).ToString())
                        : (step == 10 ? "" : ((char)(0xE850 + step)).ToString());
                    _bat.ToolTip = $"{p}%";
                    _bat.Visibility = Visibility.Visible;
                }
                else _bat.Visibility = Visibility.Collapsed;
            }
        }

        // ---- Menus ---------------------------------------------------------------------------------

        internal static ContextMenu NewMenu()
        {
            var app = Application.Current;
            var menu = new ContextMenu { FlowDirection = Lang.Flow };
            menu.Style = (Style)app.FindResource("DarkContextMenu");
            menu.Resources[typeof(MenuItem)] = app.FindResource("DarkMenuItem");
            menu.Resources[MenuItem.SeparatorStyleKey] = app.FindResource("DarkMenuSeparator");
            return menu;
        }

        internal static MenuItem Item(string header, Action click, string? glyph = null)
        {
            var i = new MenuItem { Header = header };
            if (glyph != null) i.Icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13 };
            i.Click += (_, _) => click();
            return i;
        }

        internal void OpenMenu(ContextMenu menu, UIElement target)
        {
            TaskbarPreview.HideNow();
            menu.PlacementTarget = target;
            menu.Placement = _vertical ? PlacementMode.Right : _top ? PlacementMode.Bottom : PlacementMode.Top;
            if (_vertical && FlowDirection == FlowDirection.RightToLeft) menu.Placement = PlacementMode.Left;
            menu.IsOpen = true;
        }

        private void BarMenu()
        {
            var menu = NewMenu();
            menu.Items.Add(Item(Lang.T("إعدادات تاسك بار Wibn"), () => (Application.Current as App)?.ShowTaskbarSettings(), ""));
            menu.Items.Add(Item(Lang.T("مدير المهام"), () => TaskbarApps.Launch("taskmgr.exe"), ""));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(Lang.T("رجّع تاسك بار ويندوز"), () => CustomTaskbar.Enabled = false, ""));
            OpenMenu(menu, _root);
        }

        // ---- Window plumbing -------------------------------------------------------------------------

        public void CloseBar()
        {
            // Every step on its own: one failing must never leave the window up (it would stay
            // on the screen forever, even over full-screen apps, with nothing moving it).
            void Step(string what, Action a) { try { a(); } catch (Exception ex) { CrashLog.Write("taskbar close: " + what, ex); } }
            Step("app bar", RemoveAppBar);
            Step("slide", StopSlide);
            Step("refresh", _refresh.Stop);
            Step("glass", StopGlass);
            Step("events", () => { Status.Updated -= ShowStatus; InputLang.Changed -= ShowLang; TrayHost.Changed -= RefreshTray; });
            Step("flyout", () => _flyout?.CloseIt());
            Step("preview", TaskbarPreview.HideNow);
            Step("shell hook", () => { if (_primary) DeregisterShellHookWindow(_hwnd); });
            TaskbarApps.Ours.Remove(_hwnd);
            Gone = true;
            try { Close(); }
            catch (Exception ex) { CrashLog.Write("taskbar close", ex); try { Hide(); } catch { } }
        }

        /// <summary>Closed for good (not just hidden for a full-screen app).</summary>
        internal bool Gone { get; private set; }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == WM_MOUSEACTIVATE) { handled = true; return (IntPtr)MA_NOACTIVATE; }
            if (_appBarMsg != 0 && msg == _appBarMsg && w.ToInt32() == 1 /* ABN_POSCHANGED */) _appBarWant = default;
            if (msg == 0x001A /* WM_SETTINGCHANGE */ && w.ToInt32() == 0x14 /* SPI_SETDESKWALLPAPER */) _glass?.Refresh();
            if (_shellMsg != 0 && msg == _shellMsg)
            {
                int code = w.ToInt32();
                if (code == HSHELL_FLASH) Flashing.Add(l);
                else if (code is HSHELL_WINDOWACTIVATED or HSHELL_RUDEAPPACTIVATED) Flashing.Remove(l);
                else if (code == HSHELL_WINDOWDESTROYED) Flashing.Remove(l);
                if (!_soon.IsEnabled) _soon.Start();
            }
            return IntPtr.Zero;
        }

        internal void SetInk(bool lightBehind)
        {
            var ink = lightBehind ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xF5, 0xF5, 0xF7);
            Ink.Color = ink;
            Hover.Color = Color.FromArgb(0x24, ink.R, ink.G, ink.B);
            Pressed.Color = Color.FromArgb(0x16, ink.R, ink.G, ink.B);
            ActiveBack.Color = Color.FromArgb(0x18, ink.R, ink.G, ink.B);
            Foreground = Ink;
        }

        // ---- Liquid glass (the same as the widgets': the wallpaper behind, blurred and bent) ----------

        /// <summary>The Liquid Glass page changed something: redo the glass with it.</summary>
        public void RefreshGlass()
        {
            if (CustomTaskbar.Look != 0) return;
            _card.Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x1C, 0x1C, 0x1E));
            RestartGlass();
        }

        private void RestartGlass()
        {
            if (CustomTaskbar.Look != 0) return;
            StopGlass();
            _glassSoon.Stop();
            _glassSoon.Start();
        }

        private void StopGlass()
        {
            _glass?.Dispose();
            _glass = null;
            _wallpaper.Stop();
        }

        private void StartGlass()
        {
            StopGlass();
            if (!_shown || CustomTaskbar.Look != 0) return;
            if (!GlassSettings.Taskbar.On)
            {
                // Turned off on the Liquid Glass page: plain dark gray.
                _card.Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x1C, 0x1C, 0x1E));
                SetInk(false);
                return;
            }
            try
            {
                // The look (bend, colors, frost, tint, rim) is the one set on the Liquid Glass page.
                var glass = new LiquidGlass(this, _host, _card, new Thickness(0), _radius, GlassSettings.Taskbar, desktop: true,
                                            tint: CustomTaskbar.GlassTint());
                if (!glass.Available) return;   // stays the plain color
                _glass = glass;
                glass.LightBehindChanged += light => SetInk(light);
                glass.Start();
                _wallpaper.Start();
            }
            catch (Exception ex) { CrashLog.Write("taskbar glass", ex); }
        }

        /// <summary>Is Windows' own taskbar laid out right to left (Arabic / Hebrew Windows)?</summary>
        private static bool WindowsIsRtl()
        {
            IntPtr tray = TrayHost.ExplorerTray();
            return tray != IntPtr.Zero && (GetWindowLongPtr(tray, GWL_EXSTYLE).ToInt64() & WS_EX_LAYOUTRTL) != 0;
        }

        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_LAYOUTRTL = 0x00400000;
        private const int DWMWA_EXCLUDED_FROM_PEEK = 12, DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_DONOTROUND = 1, DWMWCP_ROUND = 2;
        private const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
        private const int HSHELL_WINDOWDESTROYED = 2, HSHELL_WINDOWACTIVATED = 4, HSHELL_RUDEAPPACTIVATED = 0x8004, HSHELL_FLASH = 0x8006;
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOACTIVATE = 0x10;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")] private static extern bool RegisterShellHookWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool DeregisterShellHookWindow(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? name);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    }

    /// <summary>One app's button on the bar: its icon, a line under it while it runs, and its menu.</summary>
    internal sealed class AppButton : Border
    {
        private readonly TaskbarBar _bar;
        private readonly Image _icon = new();
        private readonly Border _line = new() { CornerRadius = new CornerRadius(1.5) };
        private readonly TextBlock _label = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };
        private string _iconFor = "";
        private Point? _press;
        private bool _dragging;
        private readonly DispatcherTimer _hover = new() { Interval = TimeSpan.FromMilliseconds(450) };

        public TaskbarApps.Group? Group { get; private set; }

        public AppButton(TaskbarBar bar)
        {
            _bar = bar;
            CornerRadius = new CornerRadius(5);
            Background = Brushes.Transparent;
            RenderOptions.SetBitmapScalingMode(_icon, BitmapScalingMode.HighQuality);
            _icon.Width = _icon.Height = bar.IconPx;
            _icon.VerticalAlignment = VerticalAlignment.Center;
            _icon.HorizontalAlignment = HorizontalAlignment.Center;

            var content = new Grid();
            var inner = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            inner.Children.Add(_icon);
            inner.Children.Add(_label);
            content.Children.Add(inner);
            content.Children.Add(_line);
            Child = content;

            bool labels = CustomTaskbar.Labels && !bar.Vertical;
            _label.Visibility = labels ? Visibility.Visible : Visibility.Collapsed;
            _label.Foreground = bar.Ink;
            Height = bar.ButtonSize;
            if (labels) { MaxWidth = 170; MinWidth = bar.ButtonSize; Padding = new Thickness(8, 0, 8, 0); inner.HorizontalAlignment = HorizontalAlignment.Left; }
            else Width = bar.ButtonSize;
            Margin = bar.Vertical ? new Thickness(0, 2, 0, 2) : new Thickness(2, 0, 2, 0);
            if (bar.Vertical)
            {
                _line.Width = 3; _line.HorizontalAlignment = HorizontalAlignment.Left; _line.VerticalAlignment = VerticalAlignment.Center;
                _line.Margin = new Thickness(1, 0, 0, 0);
            }
            else
            {
                _line.Height = 3; _line.VerticalAlignment = bar.AtTop ? VerticalAlignment.Top : VerticalAlignment.Bottom;
                _line.HorizontalAlignment = HorizontalAlignment.Center; _line.Margin = new Thickness(0, 1, 0, 1);
            }

            MouseEnter += (_, _) => { Background = _bar.Hover; if (Group?.Windows.Count > 0) _hover.Start(); else ToolTip = Group?.Name; };
            MouseLeave += (_, _) => { _hover.Stop(); Paint(); TaskbarPreview.HideSoon(); };
            _hover.Tick += (_, _) => { _hover.Stop(); if (IsMouseOver && !_dragging) ShowPreview(); };
            MouseLeftButtonDown += OnDown;
            MouseMove += OnMove;
            MouseLeftButtonUp += OnUp;
            MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle && Group != null) TaskbarApps.Launch(Group.Launch); };
            MouseRightButtonUp += (_, e) => { Menu(); e.Handled = true; };
        }

        private bool _active, _flash;

        /// <summary>Shows a group's state (called on every refresh).</summary>
        public void Show(TaskbarApps.Group g, IntPtr foreground)
        {
            Group = g;
            if (_iconFor != g.IconSource)
            {
                _iconFor = g.IconSource;
                _icon.Source = TaskbarApps.Icon(g.IconSource, g.Windows.FirstOrDefault()?.Hwnd ?? IntPtr.Zero);
            }
            _active = g.Windows.Any(w => w.Hwnd == foreground);
            _flash = !_active && g.Windows.Any(w => TaskbarBar.Flashing.Contains(w.Hwnd));
            _label.Text = g.Windows.Count == 1 ? g.Windows[0].Title : g.Name;
            int n = g.Windows.Count;
            _line.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
            double len = _active ? 16 : 6;
            if (_bar.Vertical) _line.Height = len; else _line.Width = len;
            _line.Background = _active ? TaskbarBar.Accent : new SolidColorBrush(Color.FromArgb(0x99, _bar.Ink.Color.R, _bar.Ink.Color.G, _bar.Ink.Color.B));
            if (n > 0) ToolTip = null;
            Paint();
        }

        private void Paint()
        {
            Background = IsMouseOver ? _bar.Hover : _flash ? TaskbarBar.FlashBack : _active ? _bar.ActiveBack : Brushes.Transparent;
        }

        private void ShowPreview()
        {
            if (Group == null || Group.Windows.Count == 0) return;
            TaskbarPreview.ShowFor(this, _bar, Group);
        }

        // ---- Clicks and dragging -----------------------------------------------------------------

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            _press = e.GetPosition(_bar);
            _dragging = false;
            Background = _bar.Pressed;
            CaptureMouse();
            e.Handled = true;
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (_press == null || e.LeftButton != MouseButtonState.Pressed || Group?.Pin == null) return;
            var p = e.GetPosition(_bar);
            if (!_dragging && (p - _press.Value).Length < 6) return;
            if (!_dragging) { _dragging = true; TaskbarPreview.HideNow(); _hover.Stop(); }
            // Which pinned slot is the mouse over?
            var pinned = _bar.PinnedButtons();
            int index = 0;
            foreach (var other in pinned)
            {
                if (other == this) continue;
                var c = other.TranslatePoint(new Point(other.ActualWidth / 2, other.ActualHeight / 2), _bar);
                bool after = _bar.Vertical ? p.Y > c.Y : p.X > c.X;   // same (mirrored) coordinates for both
                if (after) index++;
            }
            int now = pinned.IndexOf(this);
            if (index != now) _bar.MoveButton(this, index);
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            ReleaseMouseCapture();
            bool wasDrag = _dragging;
            _press = null;
            _dragging = false;
            Paint();
            e.Handled = true;
            if (Group == null) return;
            if (wasDrag)
            {
                if (Group.Pin != null) TaskbarApps.MovePin(Group.Pin, _bar.PinnedButtons().IndexOf(this));
                CustomTaskbar.RefreshAll();
                return;
            }
            if (!IsMouseOver) return;
            Click();
        }

        private void Click()
        {
            var g = Group!;
            TaskbarBar.Flashing.RemoveWhere(h => g.Windows.Any(w => w.Hwnd == h));
            if (g.Windows.Count == 0 || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                TaskbarApps.Launch(g.Launch);
                return;
            }
            IntPtr fg = TaskbarApps.Foreground();
            if (g.Windows.Count == 1)
            {
                var h = g.Windows[0].Hwnd;
                TaskbarPreview.HideNow();
                if (h == fg && !TaskbarApps.IsMinimized(h)) TaskbarApps.Minimize(h);
                else TaskbarApps.Activate(h);
            }
            else if (TaskbarPreview.IsShownFor(this))
            {
                // Several windows: each click goes to the next one.
                int i = g.Windows.FindIndex(w => w.Hwnd == fg);
                TaskbarApps.Activate(g.Windows[(i + 1) % g.Windows.Count].Hwnd);
            }
            else ShowPreview();
            _bar.Dispatcher.BeginInvoke(new Action(() => _bar.RefreshApps()), DispatcherPriority.Background);
        }

        private void Menu()
        {
            if (Group == null) return;
            var g = Group;
            var menu = TaskbarBar.NewMenu();
            menu.Items.Add(TaskbarBar.Item(g.Name, () => TaskbarApps.Launch(g.Launch)));
            if (g.Pin != null)
                menu.Items.Add(TaskbarBar.Item(Lang.T("شيله من التاسك بار"), () => { TaskbarApps.Unpin(g.Pin); CustomTaskbar.RefreshAll(); }, ""));
            else
                menu.Items.Add(TaskbarBar.Item(Lang.T("ثبّته في التاسك بار"), () => { TaskbarApps.PinGroup(g); CustomTaskbar.RefreshAll(); }, ""));
            if (g.Windows.Count > 0)
            {
                menu.Items.Add(new Separator());
                if (g.Windows.Count == 1)
                    menu.Items.Add(TaskbarBar.Item(Lang.T("سكّر النافذة"), () => TaskbarApps.Close(g.Windows[0].Hwnd), ""));
                else
                    menu.Items.Add(TaskbarBar.Item(Lang.F("سكّر كل النوافذ ({0})", g.Windows.Count), () => { foreach (var w in g.Windows) TaskbarApps.Close(w.Hwnd); }, ""));
            }
            _bar.OpenMenu(menu, this);
        }
    }

    /// <summary>One icon next to the clock: clicks and hovering go to its app, like on Windows' taskbar.</summary>
    internal sealed class TrayIconView : Border
    {
        private readonly TrayHost.Icon _icon;
        private readonly TaskbarBar _bar;
        private readonly bool _onBar;
        private bool _double;

        public TrayIconView(TaskbarBar bar, TrayHost.Icon icon, double size, bool onBar)
        {
            _bar = bar;
            _icon = icon;
            _onBar = onBar;
            CornerRadius = new CornerRadius(5);
            Background = Brushes.Transparent;
            Width = onBar ? (bar.Vertical ? size : Math.Max(24, size * 0.72)) : size;
            Height = onBar ? (bar.Vertical ? Math.Max(24, size * 0.72) : size) : size;
            var img = new Image { Source = icon.Image, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            Child = img;
            if (!string.IsNullOrWhiteSpace(icon.Tip) && (icon.ShowTip || icon.Version < 4)) ToolTip = icon.Tip;

            Brush hover = onBar ? bar.Hover : new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            MouseEnter += (_, _) => { Background = hover; TrayHost.Hover(_icon, true); };
            MouseLeave += (_, _) => { Background = Brushes.Transparent; TrayHost.Hover(_icon, false); };
            MouseMove += (_, _) => TrayHost.Mouse(_icon, 0x200 /* WM_MOUSEMOVE */);
            MouseDown += OnDown;
            MouseUp += OnUp;
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (e.ChangedButton == MouseButton.Left) { _double = e.ClickCount == 2; TrayHost.LeftDown(_icon, _double); }
            else if (e.ChangedButton == MouseButton.Right) TrayHost.RightDown(_icon);
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (!_onBar) _bar.CloseFlyout();   // its menu or window opens next
            if (e.ChangedButton == MouseButton.Left) TrayHost.LeftUp(_icon, _double);
            else if (e.ChangedButton == MouseButton.Right) TrayHost.RightUp(_icon);
            else if (e.ChangedButton == MouseButton.Middle) TrayHost.MiddleClick(_icon);
        }
    }

    /// <summary>The network, volume and battery (one copy for all bars, updated every second).</summary>
    internal static class Status
    {
        public static bool Online, Wifi, Muted, Charging;
        public static float Volume;
        public static int? Battery;
        public static event Action? Updated;

        private static readonly DispatcherTimer _timer = Start();
        private static MMDeviceEnumerator? _enumerator;
        private static MMDevice? _device;
        private static int _ticks;

        private static DispatcherTimer Start()
        {
            var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            t.Tick += (_, _) => { try { Update(); } catch { } Updated?.Invoke(); };
            t.Start();
            return t;
        }

        private static void Update()
        {
            if (!CustomTaskbar.Running) return;
            if (_ticks++ % 5 == 0)
            {
                // Every 5 s: the network and which speaker is the default.
                var up = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                                && n.NetworkInterfaceType is not (System.Net.NetworkInformation.NetworkInterfaceType.Loopback or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                                && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                                && !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                Online = up.Count > 0 && System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
                Wifi = up.Any(n => n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211)
                       && !up.Any(n => n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet);
                try
                {
                    _enumerator ??= new MMDeviceEnumerator();
                    var d = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    if (_device == null || _device.ID != d.ID) { _device?.Dispose(); _device = d; } else d.Dispose();
                }
                catch { _device = null; }

                var power = System.Windows.Forms.SystemInformation.PowerStatus;
                Battery = power.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) || power.BatteryLifePercent > 1.0f
                    ? null : (int)Math.Round(power.BatteryLifePercent * 100);
                Charging = power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
            }
            if (_device != null)
            {
                try
                {
                    Volume = _device.AudioEndpointVolume.MasterVolumeLevelScalar;
                    Muted = _device.AudioEndpointVolume.Mute;
                }
                catch { _device = null; }
            }
        }

        public static void ChangeVolume(float by)
        {
            try
            {
                if (_device == null) return;
                var v = _device.AudioEndpointVolume;
                v.MasterVolumeLevelScalar = Math.Clamp(v.MasterVolumeLevelScalar + by, 0f, 1f);
                if (v.Mute && by > 0) v.Mute = false;
                Volume = v.MasterVolumeLevelScalar;
                Muted = v.Mute;
                Updated?.Invoke();
            }
            catch { }
        }
    }

    /// <summary>Presses keys as if typed (Win, Win+S…), for the buttons that open Windows' own panels.</summary>
    internal static class KeyPress
    {
        public const byte LWin = 0x5B, Space = 0x20, Tab = 0x09, S = 0x53, A = 0x41, N = 0x4E, X = 0x58, D = 0x44;

        public static void Send(params byte[] keys)
        {
            foreach (var k in keys) keybd_event(k, 0, KEYEVENTF_EXTENDEDKEY(k), UIntPtr.Zero);
            foreach (var k in keys.Reverse()) keybd_event(k, 0, KEYEVENTF_EXTENDEDKEY(k) | KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private static uint KEYEVENTF_EXTENDEDKEY(byte k) => k == LWin ? 1u : 0u;
        private const uint KEYEVENTF_KEYUP = 2;
        [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    }

    /// <summary>The keyboard language of the window you're typing in (checked 5 times a second, it's cheap).</summary>
    internal static class InputLang
    {
        public static string Language = "", Region = "";
        public static bool Many;
        public static event Action? Changed;

        private static readonly DispatcherTimer _timer = Start();
        private static IntPtr _last = new(-1);
        private static int _lastCount = -1;

        private static DispatcherTimer Start()
        {
            var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
            t.Tick += (_, _) => { if (CustomTaskbar.Running) Update(); };
            t.Start();
            return t;
        }

        /// <summary>Right away (a new bar asks before the first tick).</summary>
        public static void Touch() { if (_lastCount < 0) Update(); }

        private static void Update()
        {
            IntPtr fg = GetForegroundWindow();
            uint thread = GetWindowThreadProcessId(fg, out _);
            // A Store app's typing happens in its inner window.
            var cls = new System.Text.StringBuilder(64);
            GetClassName(fg, cls, cls.Capacity);
            if (cls.ToString() == "ApplicationFrameWindow")
            {
                IntPtr core = FindWindowEx(fg, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
                if (core != IntPtr.Zero) thread = GetWindowThreadProcessId(core, out _);
            }
            IntPtr hkl = GetKeyboardLayout(thread);
            if (hkl == IntPtr.Zero) hkl = GetKeyboardLayout(0);
            int count = GetKeyboardLayoutList(0, null);
            if (hkl == _last && count == _lastCount) return;
            _last = hkl;
            _lastCount = count;
            Many = count > 1;
            int langId = (int)(hkl.ToInt64() & 0xFFFF);
            try
            {
                var culture = new CultureInfo(langId);
                Language = culture.ThreeLetterISOLanguageName.ToUpperInvariant();
                try { Region = new RegionInfo(culture.Name).TwoLetterISORegionName; } catch { Region = ""; }
            }
            catch { Language = "?"; Region = ""; }
            Changed?.Invoke();
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);
        [DllImport("user32.dll")] private static extern int GetKeyboardLayoutList(int count, IntPtr[]? list);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
    }
}
