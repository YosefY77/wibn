using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinPlus
{
    /// <summary>
    /// The calendar, in the middle of the screen in the widgets' glass (like the desktop's
    /// window): the month on one side, the picked day and its appointments on the other, and
    /// a form to add one (its time or all day, its color, and a notification). Gregorian or
    /// Hijri, switched at the top. A click outside it, or Escape, hides it.
    /// </summary>
    internal sealed class CalendarPanel : Window
    {
        private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
        private static readonly Brush PlainBack = new SolidColorBrush(Color.FromArgb(0xE8, 0x1E, 0x1F, 0x22));
        private static readonly Brush PlainEdge = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
        private static readonly Brush GlassEdge = new LinearGradientBrush(
            Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 90);
        private static readonly int[] BeforeChoices = { 0, 10, 30, 60, 1440 };

        private readonly SolidColorBrush _fg = new(Color.FromRgb(0xF2, 0xF3, 0xF5));
        private readonly SolidColorBrush _muted = new(Color.FromRgb(0xA0, 0xA3, 0xAA));
        private readonly SolidColorBrush _faint = new(Color.FromArgb(0x60, 0xA0, 0xA3, 0xAA));
        private readonly SolidColorBrush _hover = new(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
        private readonly SolidColorBrush _field = new(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        private readonly SolidColorBrush _accent;
        private readonly SolidColorBrush _accentSoft;

        private readonly CalendarWidget _owner;
        private readonly Window? _opener;
        private readonly Grid _host;
        private readonly Border _card;
        private readonly OutsideClickWatcher _outside = new();
        private readonly ScaleTransform _zoom = new(1, 1);
        private readonly System.Windows.Threading.DispatcherTimer _revealFallback = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private LiquidGlass? _glass;
        private bool _waitingForGlass;

        // The month
        private readonly TextBlock _monthTitle, _todayText;
        private readonly UniformGrid _daysHead = new() { Columns = 7, Rows = 1 };
        private readonly UniformGrid _days = new() { Columns = 7, Rows = 6 };
        private readonly Border _gregButton, _hijriButton;
        private DateTime _viewMonth;   // any day in the month on screen
        private DateTime _selected = DateTime.Today;

        // The picked day
        private readonly TextBlock _dayTitle, _daySecond, _dayEmpty;
        private readonly StackPanel _dayList = new();

        // The form
        private readonly TextBox _title, _hour, _minute;
        private readonly TextBlock _titleHint, _formTitle;
        private readonly Border _allDay, _ampm, _notify, _addButton, _cancelButton;
        private readonly TextBlock _ampmText, _addText;
        private readonly StackPanel _timeRow, _beforeRow, _colorRow;
        private readonly List<Border> _beforePills = new(), _swatches = new();
        private bool _isAllDay, _isPm, _isNotify = true;
        private int _before = 10;
        private string _color = "#4C8DFF";
        private Appointment? _editing;

        public CalendarPanel(CalendarWidget owner, Window? opener)
        {
            _owner = owner;
            _opener = opener;
            var a = owner.Accent;
            _accent = new SolidColorBrush(a);
            _accentSoft = new SolidColorBrush(Color.FromArgb(0x40, a.R, a.G, a.B));
            _color = $"#{a.R:X2}{a.G:X2}{a.B:X2}";
            Title = Lang.T("التقويم");
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            Width = 860;
            Height = 560;
            WindowStartupLocation = WindowStartupLocation.Manual;
            FontFamily = Widgets.Font();
            Foreground = _fg;

            // ---- Header: title, today … Gregorian | Hijri, close
            var header = new DockPanel { Margin = new Thickness(18, 14, 12, 8), Background = Brushes.Transparent };
            var close = IconButton(Glyph(""), Lang.T("سكّر"));
            close.MouseLeftButtonUp += (_, e) => { e.Handled = true; HideIt(); };
            DockPanel.SetDock(close, Dock.Right);
            header.Children.Add(close);
            _gregButton = Pill(Lang.T("ميلادي"), () => SetHijri(false));
            _hijriButton = Pill(Lang.T("هجري"), () => SetHijri(true));
            var switcher = new Border
            {
                Background = _field, CornerRadius = new CornerRadius(9), Padding = new Thickness(3), Margin = new Thickness(0, 0, 8, 0),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _gregButton, _hijriButton } },
                VerticalAlignment = VerticalAlignment.Center,
            };
            _gregButton.Margin = _hijriButton.Margin = new Thickness(0);
            DockPanel.SetDock(switcher, Dock.Right);
            header.Children.Add(switcher);
            var titles = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(new TextBlock { Text = Lang.T("التقويم"), FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = _fg, VerticalAlignment = VerticalAlignment.Center });
            _todayText = new TextBlock { FontSize = 12, Foreground = _muted, Margin = new Thickness(10, 3, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(_todayText);
            header.Children.Add(titles);

            // ---- The month (start side)
            var prev = IconButton(Glyph(Lang.English ? "" : ""), Lang.T("الشهر اللي قبل"));
            var next = IconButton(Glyph(Lang.English ? "" : ""), Lang.T("الشهر الجاي"));
            prev.MouseLeftButtonUp += (_, e) => { e.Handled = true; MoveMonth(-1); };
            next.MouseLeftButtonUp += (_, e) => { e.Handled = true; MoveMonth(1); };
            var todayButton = Pill(Lang.T("اليوم"), () => { _selected = DateTime.Today; _viewMonth = DateTime.Today; Refresh(); });
            todayButton.Background = _field;
            _monthTitle = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = _fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
            var monthBar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(prev, Dock.Left);
            DockPanel.SetDock(todayButton, Dock.Right);
            DockPanel.SetDock(next, Dock.Right);
            monthBar.Children.Add(prev);
            monthBar.Children.Add(todayButton);
            monthBar.Children.Add(next);
            monthBar.Children.Add(_monthTitle);
            prev.Margin = new Thickness(0);
            foreach (DayOfWeek d in Enum.GetValues(typeof(DayOfWeek)))
                _daysHead.Children.Add(new TextBlock { Text = CalText.WeekdayShort(d), FontSize = 11, Foreground = _muted, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) });
            var month = new DockPanel();
            DockPanel.SetDock(monthBar, Dock.Top);
            DockPanel.SetDock(_daysHead, Dock.Top);
            month.Children.Add(monthBar);
            month.Children.Add(_daysHead);
            month.Children.Add(_days);

            // ---- The picked day (end side): its date, its appointments, the form
            _dayTitle = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = _fg, TextWrapping = TextWrapping.Wrap };
            _daySecond = new TextBlock { FontSize = 12, Foreground = _muted, Margin = new Thickness(0, 2, 0, 8) };
            _dayEmpty = new TextBlock { Text = Lang.T("ما فيه مواعيد بهاليوم"), FontSize = 12, Foreground = _muted, Margin = new Thickness(0, 6, 0, 6) };
            var listScroll = new ScrollViewer { Content = _dayList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
            SmoothScroll.Attach(listScroll);

            _formTitle = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = _muted, Margin = new Thickness(0, 0, 0, 6) };
            _title = new TextBox
            {
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = _fg, CaretBrush = _fg,
                FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0), MaxLength = 120,
            };
            _titleHint = new TextBlock { Text = Lang.T("اسم الموعد…"), Foreground = _muted, FontSize = 13, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
            _title.TextChanged += (_, _) => _titleHint.Visibility = _title.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _title.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Submit(); } };
            var titleBox = new Border
            {
                Height = 34, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 0, 10, 0), Background = _field,
                Child = new Grid { Children = { _titleHint, _title } },
            };

            // Time: hour : minute AM/PM, or all day
            _hour = NumberBox(1, 12);
            _minute = NumberBox(0, 59);
            _ampmText = new TextBlock { FontSize = 12, Foreground = _fg, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _ampm = new Border { Height = 30, MinWidth = 40, CornerRadius = new CornerRadius(8), Background = _field, Child = _ampmText, Cursor = Cursors.Hand, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 0, 8, 0) };
            _ampm.MouseLeftButtonDown += (_, e) => { e.Handled = true; _isPm = !_isPm; ShowAmPm(); };
            _timeRow = new StackPanel { Orientation = Orientation.Horizontal, FlowDirection = FlowDirection.LeftToRight };
            _timeRow.Children.Add(Boxed(_hour));
            _timeRow.Children.Add(new TextBlock { Text = ":", Foreground = _fg, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 2) });
            _timeRow.Children.Add(Boxed(_minute));
            _timeRow.Children.Add(_ampm);
            _allDay = Pill(Lang.T("طول اليوم"), () => { _isAllDay = !_isAllDay; ShowForm(); });
            var timeLine = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(_allDay, Dock.Right);
            timeLine.Children.Add(_allDay);
            timeLine.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { Label("الوقت"), _timeRow } });

            // Color
            _colorRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            _colorRow.Children.Add(Label("اللون"));
            foreach (var (name, hex) in WidgetWindow.Colors)
            {
                string h = hex;
                var dot = new Border
                {
                    Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = CalText.Brush(h), Margin = new Thickness(0, 0, 6, 0),
                    BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = Lang.T(name), Tag = h,
                };
                dot.MouseLeftButtonDown += (_, e) => { e.Handled = true; _color = h; ShowForm(); };
                _swatches.Add(dot);
                _colorRow.Children.Add(dot);
            }

            // Notification: on/off, and how long before
            _notify = Pill("", () => { _isNotify = !_isNotify; ShowForm(); });
            _beforeRow = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (int m in BeforeChoices)
            {
                int mins = m;
                var p = Pill(BeforeText(m), () => { _before = mins; ShowForm(); });
                p.Tag = mins;
                p.Padding = new Thickness(8, 0, 8, 0);
                _beforePills.Add(p);
                _beforeRow.Children.Add(p);
            }
            var notifyLine = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            notifyLine.Children.Add(_notify);
            notifyLine.Children.Add(_beforeRow);

            // Add / save, cancel
            _addText = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _addButton = new Border { Height = 34, MinWidth = 96, CornerRadius = new CornerRadius(8), Background = _accent, Child = _addText, Cursor = Cursors.Hand, Padding = new Thickness(14, 0, 14, 0) };
            _addButton.MouseLeftButtonDown += (_, e) => { e.Handled = true; Submit(); };
            _cancelButton = Pill(Lang.T("إلغاء"), () => StartNew());
            _cancelButton.Height = 34;
            _cancelButton.Background = _field;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            buttons.Children.Add(_addButton);
            buttons.Children.Add(_cancelButton);

            var form = new StackPanel();
            form.Children.Add(_formTitle);
            form.Children.Add(titleBox);
            form.Children.Add(timeLine);
            form.Children.Add(_colorRow);
            form.Children.Add(notifyLine);
            form.Children.Add(buttons);
            var formCard = new Border { Background = _field, CornerRadius = new CornerRadius(12), Padding = new Thickness(12), Margin = new Thickness(0, 10, 0, 0), Child = form };

            var day = new DockPanel();
            DockPanel.SetDock(_dayTitle, Dock.Top);
            DockPanel.SetDock(_daySecond, Dock.Top);
            DockPanel.SetDock(formCard, Dock.Bottom);
            day.Children.Add(_dayTitle);
            day.Children.Add(_daySecond);
            day.Children.Add(formCard);
            day.Children.Add(listScroll);

            var body = new Grid { Margin = new Thickness(18, 4, 18, 16) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.Children.Add(month);
            Grid.SetColumn(day, 2);
            body.Children.Add(day);

            var root = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            root.Children.Add(body);

            _card = new Border
            {
                CornerRadius = new CornerRadius(16), Background = PlainBack, BorderBrush = PlainEdge, BorderThickness = new Thickness(1),
                Margin = new Thickness(4), Child = root, FlowDirection = Lang.Flow,
                RenderTransform = _zoom, RenderTransformOrigin = new Point(0.5, 0.5),
            };
            _host = new Grid { FlowDirection = FlowDirection.LeftToRight, Background = Brushes.Transparent };
            _host.Children.Add(_card);
            Content = _host;

            // Drag it by any empty spot.
            _card.MouseLeftButtonDown += (_, e) =>
            {
                if (e.Handled || e.OriginalSource is DependencyObject d && IsInside<TextBox>(d)) return;
                try { DragMove(); } catch (InvalidOperationException) { }
            };
            LocationChanged += (_, _) => _glass?.Follow();
            KeyDown += (_, e) => { if (e.Key == Key.Escape) { if (_editing != null) StartNew(); else HideIt(); } };
            _outside.Clicked += OnClickSomewhere;
            _revealFallback.Tick += (_, _) => Reveal();
            Appointments.Changed += Refresh;
            Closed += (_, _) => { Appointments.Changed -= Refresh; _outside.Stop(); StopGlass(); _revealFallback.Stop(); };

            _viewMonth = DateTime.Today;
            StartNew();
        }

        private bool Hijri => _owner.Hijri;
        private bool Indic => _owner.Indic;

        public void SetAccent(Color c)
        {
            _accent.Color = c;
            _accentSoft.Color = Color.FromArgb(0x40, c.R, c.G, c.B);
            Refresh();
        }

        private void SetHijri(bool hijri)
        {
            if (_owner.Hijri != hijri) _owner.Hijri = hijri;   // the widget refreshes this too
            else Refresh();
        }

        // ---- Showing and hiding (like the desktop's window) ---------------------------------------

        public void ShowCentered()
        {
            _card.Opacity = 0;
            _selected = DateTime.Today;
            _viewMonth = DateTime.Today;
            StartNew();
            Refresh();
            Show();
            var dpi = VisualTreeHelper.GetDpi(this);
            var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
            double x = area.X / dpi.DpiScaleX, y = area.Y / dpi.DpiScaleY, w = area.Width / dpi.DpiScaleX, h = area.Height / dpi.DpiScaleY;
            Width = Math.Min(860, w - 40);
            Height = Math.Min(560, h - 40);
            Left = x + (w - Width) / 2;
            Top = y + (h - Height) / 2;
            Activate();
            _title.Focus();
            _outside.Start();

            if (_glass != null) { _glass.Follow(); Reveal(); return; }
            UpdateLayout();
            _waitingForGlass = true;
            _revealFallback.Start();
            StartGlass();
            if (_glass == null) Reveal();
        }

        private void Reveal()
        {
            _waitingForGlass = false;
            _revealFallback.Stop();
            var d = AnimSettings.Open;   // the Animations page
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = ease });
            _zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, d) { EasingFunction = ease });
            _zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, d) { EasingFunction = ease });
        }

        public void HideIt()
        {
            if (!IsVisible) return;
            _outside.Stop();
            _revealFallback.Stop();
            _waitingForGlass = false;
            var d = AnimSettings.Close;
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var fade = new DoubleAnimation(0, d) { EasingFunction = ease };
            fade.Completed += (_, _) => { if (_card.Opacity == 0) Hide(); };
            _card.BeginAnimation(OpacityProperty, fade);
            _zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, d) { EasingFunction = ease });
            _zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, d) { EasingFunction = ease });
        }

        private void OnClickSomewhere(IntPtr hwnd)
        {
            if (!IsVisible) return;
            IntPtr top = GetAncestor(hwnd, 2 /* GA_ROOT */);
            var source = top == IntPtr.Zero ? null : System.Windows.Interop.HwndSource.FromHwnd(top);
            if (source != null)
            {
                if (source.RootVisual is Window w) { if (w == this || w == _opener) return; }
                else return;   // its own menus and tooltips
            }
            HideIt();
        }

        // ---- The month -----------------------------------------------------------------------------

        private void MoveMonth(int delta)
        {
            var cal = CalText.Of(Hijri, _viewMonth);
            try
            {
                var first = cal.AddDays(_viewMonth, 1 - cal.GetDayOfMonth(_viewMonth));
                var moved = cal.AddMonths(first, delta);
                if (CalText.Of(Hijri, moved).GetType() != cal.GetType()) return;   // past the years the Hijri calendar knows
                _viewMonth = moved;
            }
            catch (ArgumentOutOfRangeException) { return; }
            Refresh();
        }

        /// <summary>Everything again: the switch, the month, the picked day and its appointments.</summary>
        public void Refresh()
        {
            bool hijri = Hijri, indic = Indic;
            Highlight(_gregButton, !hijri);
            Highlight(_hijriButton, hijri);
            _todayText.Text = CalText.Date(DateTime.Today, hijri, indic, weekday: true);

            var cal = CalText.Of(hijri, _viewMonth);
            bool isHijri = cal is UmAlQuraCalendar;
            int y = cal.GetYear(_viewMonth), m = cal.GetMonth(_viewMonth);
            _monthTitle.Text = $"{CalText.Month(m, isHijri)} {CalText.Num(y, indic)}{CalText.Era(isHijri)}";
            var first = cal.ToDateTime(y, m, 1, 0, 0, 0, 0);
            int count = cal.GetDaysInMonth(y, m);
            var start = first.AddDays(-(int)first.DayOfWeek);   // the grid starts on Sunday
            var marked = Appointments.All.GroupBy(a => a.Date.Date).ToDictionary(g => g.Key, g => g.Select(a => a.Color).Distinct().Take(3).ToList());

            _days.Children.Clear();
            for (int i = 0; i < 42; i++)
            {
                var d = start.AddDays(i);
                bool inMonth = d >= first && d < first.AddDays(count);
                _days.Children.Add(DayCell(d, cal, inMonth, marked.TryGetValue(d, out var colors) ? colors : null, hijri, indic));
            }
            ShowDay();
            ShowForm();
        }

        private FrameworkElement DayCell(DateTime d, System.Globalization.Calendar cal, bool inMonth, List<string>? colors, bool hijri, bool indic)
        {
            bool today = d == DateTime.Today, picked = d == _selected.Date;
            var number = new TextBlock
            {
                Text = CalText.Num(cal.GetDayOfMonth(d), indic), FontSize = 14, FontWeight = today ? FontWeights.Bold : FontWeights.Normal,
                Foreground = today && !picked ? _accent : inMonth ? _fg : _faint, HorizontalAlignment = HorizontalAlignment.Center,
            };
            var other = CalText.Of(!hijri, d);
            var small = new TextBlock
            {
                Text = CalText.Num(other.GetDayOfMonth(d), indic), FontSize = 9, Foreground = inMonth ? _muted : _faint,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -1, 0, 0),
            };
            var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Height = 6, Margin = new Thickness(0, 2, 0, 0) };
            foreach (var c in colors ?? new List<string>())
                dots.Children.Add(new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(2.5), Background = CalText.Brush(c), Margin = new Thickness(1, 0, 1, 0) });
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(number);
            stack.Children.Add(small);
            stack.Children.Add(dots);
            var cell = new Border
            {
                Margin = new Thickness(2), CornerRadius = new CornerRadius(9), Child = stack, Cursor = Cursors.Hand,
                Background = picked ? _accentSoft : Brushes.Transparent,
                BorderBrush = today ? _accent : Brushes.Transparent, BorderThickness = new Thickness(today ? 1.5 : 0),
            };
            cell.MouseEnter += (_, _) => { if (!picked) cell.Background = _hover; };
            cell.MouseLeave += (_, _) => { if (!picked) cell.Background = Brushes.Transparent; };
            cell.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                _selected = d;
                if (!inMonth) _viewMonth = d;
                Refresh();
                if (e.ClickCount == 2) _title.Focus();
            };
            return cell;
        }

        // ---- The picked day ------------------------------------------------------------------------

        private void ShowDay()
        {
            bool hijri = Hijri, indic = Indic;
            _dayTitle.Text = CalText.Date(_selected, hijri, indic, weekday: true);
            _daySecond.Text = CalText.Date(_selected, !hijri, indic);
            _dayList.Children.Clear();
            var list = Appointments.OnDay(_selected);
            if (list.Count == 0) _dayList.Children.Add(_dayEmpty);
            foreach (var a in list) _dayList.Children.Add(AppointmentRow(a, indic));
        }

        private FrameworkElement AppointmentRow(Appointment a, bool indic)
        {
            var bar = new Border { Width = 4, CornerRadius = new CornerRadius(2), Background = CalText.Brush(a.Color), Margin = new Thickness(0, 0, 10, 0) };
            var title = new TextBlock { Text = a.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = _fg, TextTrimming = TextTrimming.CharacterEllipsis };
            string when = a.Minutes is int m ? CalText.Time(m, indic) : Lang.T("طول اليوم");
            if (a.Notify) when += "   ·   " + (a.Minutes == null ? Lang.T("تنبيه الصبح") : Lang.F("تنبيه: {0}", BeforeText(a.Before)));
            var sub = new TextBlock { Text = when, FontSize = 11, Foreground = _muted };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(title);
            text.Children.Add(sub);
            var bell = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 12, Foreground = _muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0), Visibility = a.Notify ? Visibility.Visible : Visibility.Collapsed };
            var delete = IconButton(Glyph("", 12), Lang.T("احذف الموعد"));
            delete.Visibility = Visibility.Hidden;
            delete.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (_editing == a) StartNew(); Appointments.Remove(a); };
            var row = new DockPanel();
            DockPanel.SetDock(bar, Dock.Left);
            DockPanel.SetDock(delete, Dock.Right);
            DockPanel.SetDock(bell, Dock.Right);
            row.Children.Add(bar);
            row.Children.Add(delete);
            row.Children.Add(bell);
            row.Children.Add(text);
            var card = new Border
            {
                Child = row, CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 6, 4, 6), Margin = new Thickness(0, 0, 0, 4),
                Background = _editing == a ? _accentSoft : Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = Lang.T("اضغط عشان تعدّله"),
            };
            card.MouseEnter += (_, _) => { delete.Visibility = Visibility.Visible; if (_editing != a) card.Background = _hover; };
            card.MouseLeave += (_, _) => { delete.Visibility = Visibility.Hidden; if (_editing != a) card.Background = Brushes.Transparent; };
            card.MouseLeftButtonDown += (_, e) => { e.Handled = true; Edit(a); };
            return card;
        }

        // ---- The form ------------------------------------------------------------------------------

        /// <summary>An empty form for a new appointment on the picked day, at the next full hour.</summary>
        private void StartNew()
        {
            _editing = null;
            _title.Text = "";
            int next = (DateTime.Now.Hour + 1) % 24;
            SetTime(next * 60);
            _isAllDay = false;
            _isNotify = true;
            _before = 10;
            var a = _accent.Color;
            _color = $"#{a.R:X2}{a.G:X2}{a.B:X2}";
            if (IsLoaded) { ShowDay(); ShowForm(); }
        }

        private void Edit(Appointment a)
        {
            _editing = a;
            _title.Text = a.Title;
            _isAllDay = a.Minutes == null;
            SetTime(a.Minutes ?? 9 * 60);
            _color = a.Color;
            _isNotify = a.Notify;
            _before = a.Before;
            ShowDay();
            ShowForm();
            _title.Focus();
            _title.CaretIndex = _title.Text.Length;
        }

        private void SetTime(int minutes)
        {
            int h = minutes / 60 % 24;
            _isPm = h >= 12;
            _hour.Text = (h % 12 == 0 ? 12 : h % 12).ToString(CultureInfo.InvariantCulture);
            _minute.Text = (minutes % 60).ToString("00", CultureInfo.InvariantCulture);
            ShowAmPm();
        }

        private int ReadTime()
        {
            int h = int.TryParse(_hour.Text, out var hv) ? Math.Clamp(hv, 1, 12) : 12;
            int m = int.TryParse(_minute.Text, out var mv) ? Math.Clamp(mv, 0, 59) : 0;
            return (h % 12 + (_isPm ? 12 : 0)) * 60 + m;
        }

        private void ShowAmPm() => _ampmText.Text = Lang.English ? (_isPm ? "PM" : "AM") : (_isPm ? "م" : "ص");

        /// <summary>The form's buttons and pills as they're set now.</summary>
        private void ShowForm()
        {
            _formTitle.Text = _editing == null
                ? Lang.F("موعد جديد · {0}", CalText.ShortDate(_selected, Hijri, Indic))
                : Lang.T("تعديل الموعد");
            _addText.Text = _editing == null ? Lang.T("أضف") : Lang.T("احفظ");
            _cancelButton.Visibility = _editing == null ? Visibility.Collapsed : Visibility.Visible;
            Highlight(_allDay, _isAllDay);
            _timeRow.Opacity = _isAllDay ? 0.35 : 1;
            _timeRow.IsEnabled = !_isAllDay;
            foreach (var s in _swatches)
                s.BorderBrush = string.Equals((string)s.Tag, _color, StringComparison.OrdinalIgnoreCase) ? _fg : Brushes.Transparent;
            ((TextBlock)_notify.Child).Text = _isNotify ? Lang.T("نبّهني") : Lang.T("بدون تنبيه");
            Highlight(_notify, _isNotify);
            _beforeRow.Visibility = _isNotify && !_isAllDay ? Visibility.Visible : Visibility.Collapsed;
            foreach (var p in _beforePills) Highlight(p, (int)p.Tag == _before);
        }

        private void Submit()
        {
            string title = _title.Text.Trim();
            if (title.Length == 0) { _title.Focus(); FlashHint(); return; }
            var a = _editing ?? new Appointment();
            a.Title = title;
            a.Date = _selected.Date;
            a.Minutes = _isAllDay ? null : ReadTime();
            a.Color = _color;
            a.Notify = _isNotify;
            a.Before = _before;
            bool isNew = _editing == null;
            _editing = null;
            _title.Text = "";
            if (isNew) Appointments.Add(a); else Appointments.Update(a);   // both refresh the window
            StartNew();
            _title.Focus();
        }

        private void FlashHint()
        {
            var anim = new ColorAnimation(Color.FromRgb(0xFF, 0x45, 0x3A), _muted.Color, TimeSpan.FromMilliseconds(900));
            var b = new SolidColorBrush(_muted.Color);
            _titleHint.Foreground = b;
            b.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }

        private static string BeforeText(int minutes) => minutes switch
        {
            0 => Lang.T("وقته"),
            1440 => Lang.T("قبل بيوم"),
            60 => Lang.T("قبل بساعة"),
            _ => Lang.F("قبل {0} دقيقة", minutes),
        };

        // ---- Glass (the widgets' settings and color) ------------------------------------------------

        private void StartGlass()
        {
            StopGlass();
            if (!GlassSettings.Widgets.On) { ShowPlain(); return; }
            try
            {
                Point at = _card.TranslatePoint(new Point(0, 0), _host);
                var glass = new LiquidGlass(this, _host, _card, new Thickness(at.X, at.Y, 0, 0), 16, GlassSettings.Widgets, desktop: true,
                                            tint: Widgets.BackColor());
                if (!glass.Available) { ShowPlain(); return; }
                _glass = glass;
                _card.BorderBrush = GlassEdge;
                glass.LightBehindChanged += light =>
                {
                    SetLightBehind(light);
                    if (_waitingForGlass) Reveal();
                };
                glass.Start();
            }
            catch (Exception ex) { ShowPlain(); CrashLog.Write("calendar glass", ex); }
        }

        private void StopGlass()
        {
            _glass?.Dispose();
            _glass = null;
        }

        private void ShowPlain()
        {
            var back = Widgets.BackColor();
            _card.Background = back is { } b ? new SolidColorBrush(Color.FromArgb(0xF0, b.Color.R, b.Color.G, b.Color.B)) : PlainBack;
            _card.BorderBrush = PlainEdge;
            SetLightBehind(back is { } l && Widgets.Lightness(l.Color) > 0.6);
        }

        private void SetLightBehind(bool light)
        {
            var d = new Duration(TimeSpan.FromMilliseconds(200));
            void To(SolidColorBrush b, Color c) => b.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(c, d));
            To(_fg, light ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xF2, 0xF3, 0xF5));
            To(_muted, light ? Color.FromArgb(0xB0, 0x3C, 0x3C, 0x43) : Color.FromRgb(0xA0, 0xA3, 0xAA));
            To(_faint, light ? Color.FromArgb(0x50, 0x3C, 0x3C, 0x43) : Color.FromArgb(0x60, 0xA0, 0xA3, 0xAA));
            To(_hover, light ? Color.FromArgb(0x16, 0x00, 0x00, 0x00) : Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
            To(_field, light ? Color.FromArgb(0x18, 0x00, 0x00, 0x00) : Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        }

        // ---- Pieces ---------------------------------------------------------------------------------

        private TextBlock Glyph(string glyph, double size = 12) => new()
        {
            Text = glyph, FontFamily = IconFont, FontSize = size, Foreground = _fg,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };

        private TextBlock Label(string text) => new()
        {
            Text = Lang.T(text), FontSize = 12, Foreground = _muted, VerticalAlignment = VerticalAlignment.Center, MinWidth = 44, Margin = new Thickness(0, 0, 8, 0),
        };

        private Border IconButton(UIElement content, string tip)
        {
            var b = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent,
                Child = content, Cursor = Cursors.Hand, Margin = new Thickness(4, 0, 0, 0),
            };
            if (tip.Length > 0) b.ToolTip = tip;
            b.MouseEnter += (_, _) => { if (b.Background == Brushes.Transparent) b.Background = _hover; };
            b.MouseLeave += (_, _) => { if (b.Background == _hover) b.Background = Brushes.Transparent; };
            b.MouseLeftButtonDown += (_, e) => e.Handled = true;   // not a drag of the window
            return b;
        }

        /// <summary>A small rounded button; its look (on or off) is set with <see cref="Highlight"/>.</summary>
        private Border Pill(string text, Action click)
        {
            var b = new Border
            {
                Height = 28, CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 6, 0),
                Background = Brushes.Transparent, Cursor = Cursors.Hand,
                Child = new TextBlock { Text = text, FontSize = 12, Foreground = _fg, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center },
            };
            b.MouseLeftButtonDown += (_, e) => { e.Handled = true; click(); };
            return b;
        }

        private void Highlight(Border pill, bool on)
        {
            pill.Background = on ? _accent : _field;
            if (pill.Child is TextBlock t) t.Foreground = on ? Brushes.White : _fg;
        }

        private Border Boxed(TextBox box) => new()
        {
            Width = 40, Height = 30, CornerRadius = new CornerRadius(8), Background = _field, Child = box,
        };

        /// <summary>A number box: digits only, the mouse wheel and arrow keys move it, it wraps around.</summary>
        private TextBox NumberBox(int min, int max)
        {
            var box = new TextBox
            {
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = _fg, CaretBrush = _fg, FontSize = 14,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, MaxLength = 2,
                FlowDirection = FlowDirection.LeftToRight,
            };
            box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
            void Step(int by)
            {
                int v = int.TryParse(box.Text, out var n) ? n : min;
                v += by;
                if (v > max) v = min; else if (v < min) v = max;
                box.Text = max == 59 ? v.ToString("00", CultureInfo.InvariantCulture) : v.ToString(CultureInfo.InvariantCulture);
            }
            box.PreviewMouseWheel += (_, e) => { e.Handled = true; Step(e.Delta > 0 ? (max == 59 ? 5 : 1) : (max == 59 ? -5 : -1)); };
            box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Up) { e.Handled = true; Step(1); }
                else if (e.Key == Key.Down) { e.Handled = true; Step(-1); }
                else if (e.Key == Key.Enter) { e.Handled = true; Submit(); }
            };
            box.GotKeyboardFocus += (_, _) => box.SelectAll();
            box.LostKeyboardFocus += (_, _) => { if (int.TryParse(box.Text, out var n)) box.Text = max == 59 ? Math.Clamp(n, min, max).ToString("00") : Math.Clamp(n, min, max).ToString(); else Step(0); };
            return box;
        }

        private static bool IsInside<T>(DependencyObject? d) where T : DependencyObject
        {
            for (; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d is T) return true;
            return false;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    }
}
