using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace WinPlus
{
    /// <summary>
    /// The calendar widget: today's date (Gregorian or Hijri, the other one under it if wanted)
    /// and the coming appointments with their colors. A click opens the calendar in the middle
    /// of the screen, where appointments are added. Right-click: which calendar, and what it shows.
    /// </summary>
    internal sealed class CalendarWidget : WidgetContent
    {
        private readonly SolidColorBrush _ink = new(Color.FromRgb(0xF2, 0xF3, 0xF5));
        private readonly SolidColorBrush _muted = new(Color.FromRgb(0xA0, 0xA3, 0xAA));
        private readonly SolidColorBrush _accent = new(Color.FromRgb(0x4C, 0x8D, 0xFF));
        private readonly TextBlock _weekday, _day, _month, _second, _empty;
        private readonly StackPanel _date, _list;
        private readonly Border _listHost;
        private readonly Grid _root;
        private readonly DispatcherTimer _tick = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        private CalendarPanel? _panel;
        private Point? _pressAt;
        private Window? _window;
        private DateTime _shownDay;
        private int _w = 3, _h = 3;

        public CalendarWidget()
        {
            _weekday = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = _accent, HorizontalAlignment = HorizontalAlignment.Center };
            _day = new TextBlock { FontSize = 54, FontWeight = FontWeights.Bold, Foreground = _ink, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -4, 0, -4) };
            _month = new TextBlock { FontSize = 15, Foreground = _ink, HorizontalAlignment = HorizontalAlignment.Center };
            _second = new TextBlock { FontSize = 12, Foreground = _muted, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
            _date = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MinWidth = 120 };
            _date.Children.Add(_weekday);
            _date.Children.Add(_day);
            _date.Children.Add(_month);
            _date.Children.Add(_second);
            var dateBox = new Viewbox { Child = _date, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both };

            _list = new StackPanel();
            _empty = new TextBlock { Text = Lang.T("ما فيه مواعيد جاية"), FontSize = 12, Foreground = _muted, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
            _listHost = new Border { Child = _list, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Top };

            _root = new Grid { FlowDirection = Lang.Flow };
            _root.Children.Add(dateBox);
            _root.Children.Add(_listHost);
            Child = _root;
            Background = Brushes.Transparent;   // a click anywhere in it counts
            Cursor = Cursors.Hand;
            ToolTip = Lang.T("اضغط عشان يفتح التقويم وتضيف مواعيد");

            PreviewMouseLeftButtonDown += (_, e) => _pressAt = PointToScreen(e.GetPosition(this));
            _tick.Tick += (_, _) => { if (DateTime.Today != _shownDay) Fill(); };
            Appointments.Changed += OnAppointments;
        }

        private void OnAppointments() => Fill();

        public override void Start()
        {
            Fill();
            Layout();
            _tick.Start();
            if (_window == null && Window.GetWindow(this) is { } w)
            {
                _window = w;
                // The widget's window takes the mouse while it might be dragged, so the let-go is heard there.
                w.PreviewMouseLeftButtonUp += (_, e) =>
                {
                    if (_pressAt is not { } at) return;
                    _pressAt = null;
                    var now = PointToScreen(e.GetPosition(this));
                    if (Math.Abs(now.X - at.X) < 5 && Math.Abs(now.Y - at.Y) < 5) TogglePanel();
                };
                w.Closed += (_, _) =>
                {
                    Appointments.Changed -= OnAppointments;
                    _panel?.Close();
                    _panel = null;
                };
            }
        }

        public override void Stop() => _tick.Stop();

        // ---- Its options ---------------------------------------------------------------------------

        private string Opt(string key, string def) => Info?.Options.TryGetValue(key, out var v) == true ? v : def;

        private void SetOpt(string key, string value)
        {
            if (Info == null) return;
            Info.Options[key] = value;
            Widgets.Save();
            Fill();
            Layout();
            _panel?.Refresh();
        }

        /// <summary>Hijri (true) or Gregorian as the main calendar.</summary>
        public bool Hijri { get => Opt("cal", "g") == "h"; set => SetOpt("cal", value ? "h" : "g"); }
        /// <summary>Arabic-Indic digits (٠١٢…) instead of 0 1 2.</summary>
        public bool Indic { get => Opt("digits", "w") == "a"; set => SetOpt("digits", value ? "a" : "w"); }
        private bool ShowSecond { get => Opt("second", "1") == "1"; set => SetOpt("second", value ? "1" : "0"); }
        private bool ShowWeekday { get => Opt("weekday", "1") == "1"; set => SetOpt("weekday", value ? "1" : "0"); }
        private bool ShowYear { get => Opt("year", "1") == "1"; set => SetOpt("year", value ? "1" : "0"); }
        private bool ShowList { get => Opt("list", "1") == "1"; set => SetOpt("list", value ? "1" : "0"); }

        public Color Accent => _accent.Color;

        // ---- What it shows -------------------------------------------------------------------------

        private void Fill()
        {
            var today = DateTime.Today;
            _shownDay = today;
            bool hijri = Hijri, indic = Indic;
            var cal = CalText.Of(hijri, today);
            bool isHijri = cal is UmAlQuraCalendar;
            _weekday.Text = CalText.Weekday(today.DayOfWeek);
            _weekday.Visibility = ShowWeekday ? Visibility.Visible : Visibility.Collapsed;
            _day.Text = CalText.Num(cal.GetDayOfMonth(today), indic);
            string month = CalText.Month(cal.GetMonth(today), isHijri);
            _month.Text = ShowYear ? $"{month} {CalText.Num(cal.GetYear(today), indic)}{CalText.Era(isHijri)}" : month;
            _second.Text = CalText.Date(today, !hijri, indic);
            _second.Visibility = ShowSecond ? Visibility.Visible : Visibility.Collapsed;

            _list.Children.Clear();
            var coming = Appointments.Upcoming(8);
            if (coming.Count == 0) _list.Children.Add(_empty);
            foreach (var a in coming) _list.Children.Add(Row(a, hijri, indic));
        }

        private FrameworkElement Row(Appointment a, bool hijri, bool indic)
        {
            var bar = new Border { Width = 4, CornerRadius = new CornerRadius(2), Background = CalText.Brush(a.Color), Margin = new Thickness(0, 1, 8, 1) };
            var title = new TextBlock { Text = a.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = _ink, TextTrimming = TextTrimming.CharacterEllipsis };
            var when = new TextBlock { Text = CalText.When(a, hijri, indic), FontSize = 11, Foreground = _muted, TextTrimming = TextTrimming.CharacterEllipsis };
            var text = new StackPanel();
            text.Children.Add(title);
            text.Children.Add(when);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(bar, Dock.Left);
            row.Children.Add(bar);
            row.Children.Add(text);
            return row;
        }

        public override void SetCells(int w, int h)
        {
            _w = w;
            _h = h;
            Layout();
        }

        /// <summary>The date alone when small; with the coming appointments beside it (wide) or under it (tall).</summary>
        private void Layout()
        {
            _root.RowDefinitions.Clear();
            _root.ColumnDefinitions.Clear();
            var dateBox = _root.Children[0];
            bool list = ShowList && (_w >= 4 || _h >= 4);
            _listHost.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetRow(dateBox, 0); Grid.SetColumn(dateBox, 0);
            Grid.SetRow(_listHost, 0); Grid.SetColumn(_listHost, 0);
            if (!list) return;
            if (_w > _h)
            {
                _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_h <= 2 ? 0.8 : 1, GridUnitType.Star) });
                _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
                Grid.SetColumn(_listHost, 1);
                _listHost.Margin = new Thickness(14, 2, 0, 0);
                _listHost.VerticalAlignment = VerticalAlignment.Center;
            }
            else
            {
                _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(_h >= 5 ? 0.8 : 1, GridUnitType.Star) });
                _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.2, GridUnitType.Star) });
                Grid.SetRow(_listHost, 1);
                _listHost.Margin = new Thickness(4, 12, 4, 0);
                _listHost.VerticalAlignment = VerticalAlignment.Top;
            }
        }

        public override void SetAccent(Color color)
        {
            _accent.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, new Duration(TimeSpan.FromMilliseconds(200))));
            _panel?.SetAccent(color);
        }

        public override void SetLightBehind(bool light)
        {
            var d = new Duration(TimeSpan.FromMilliseconds(200));
            _ink.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(light ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xF2, 0xF3, 0xF5), d));
            _muted.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(light ? Color.FromArgb(0xB0, 0x3C, 0x3C, 0x43) : Color.FromRgb(0xA0, 0xA3, 0xAA), d));
        }

        // ---- The calendar in the middle of the screen -----------------------------------------------

        private void TogglePanel()
        {
            if (_panel?.IsVisible == true) { _panel.HideIt(); return; }
            if (_panel == null)
            {
                _panel = new CalendarPanel(this, _window);
                var made = _panel;
                made.Closed += (_, _) => { if (_panel == made) _panel = null; };
            }
            _panel.ShowCentered();
        }

        // ---- Right-click menu ----------------------------------------------------------------------

        public override IEnumerable<Control> MenuItems()
        {
            var open = new MenuItem { Header = Lang.T("افتح التقويم") };
            open.Click += (_, _) => TogglePanel();
            yield return open;

            var which = new MenuItem { Header = Lang.T("التقويم") };
            var greg = new MenuItem { Header = Lang.T("ميلادي") };
            var hij = new MenuItem { Header = Lang.T("هجري") };
            greg.Click += (_, _) => Hijri = false;
            hij.Click += (_, _) => Hijri = true;
            which.Items.Add(greg);
            which.Items.Add(hij);
            which.SubmenuOpened += (_, _) => { greg.IsChecked = !Hijri; hij.IsChecked = Hijri; };
            yield return which;

            var show = new MenuItem { Header = Lang.T("يبين") };
            Check(show, "اسم اليوم", () => ShowWeekday, v => ShowWeekday = v);
            Check(show, "السنة", () => ShowYear, v => ShowYear = v);
            Check(show, "التاريخ الثاني (هجري أو ميلادي)", () => ShowSecond, v => ShowSecond = v);
            Check(show, "المواعيد الجاية (من حجم 4)", () => ShowList, v => ShowList = v);
            show.Items.Add(new Separator());
            Check(show, "أرقام عربية ١٢٣", () => Indic, v => Indic = v);
            yield return show;
        }

        private static void Check(MenuItem parent, string text, Func<bool> get, Action<bool> set)
        {
            var item = new MenuItem { Header = Lang.T(text) };
            item.Click += (_, _) => set(!get());
            parent.SubmenuOpened += (_, _) => item.IsChecked = get();
            parent.Items.Add(item);
        }
    }

    /// <summary>Dates and times in words, in either calendar, for the calendar widget and its window.</summary>
    internal static class CalText
    {
        private static readonly UmAlQuraCalendar Um = new();
        private static readonly GregorianCalendar Greg = new();

        private static readonly string[] Months =
            { "يناير", "فبراير", "مارس", "أبريل", "مايو", "يونيو", "يوليو", "أغسطس", "سبتمبر", "أكتوبر", "نوفمبر", "ديسمبر" };
        private static readonly string[] MonthsEn =
            { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };
        private static readonly string[] Days = { "الأحد", "الاثنين", "الثلاثاء", "الأربعاء", "الخميس", "الجمعة", "السبت" };
        private static readonly string[] DaysEn = { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };
        private static readonly string[] DaysShort = { "أحد", "اثنين", "ثلاثاء", "أربعاء", "خميس", "جمعة", "سبت" };
        private static readonly string[] DaysShortEn = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };

        /// <summary>Hijri (Umm al-Qura) or Gregorian; Gregorian outside the years Umm al-Qura knows (1900–2077).</summary>
        public static System.Globalization.Calendar Of(bool hijri, DateTime day) => hijri && day >= Um.MinSupportedDateTime && day <= Um.MaxSupportedDateTime ? Um : Greg;

        public static string Month(int m, bool hijri) =>
            hijri ? (Lang.English ? PrayerWidget.HijriMonthsEn : PrayerWidget.HijriMonths)[m - 1] : (Lang.English ? MonthsEn : Months)[m - 1];

        public static string Weekday(DayOfWeek d) => (Lang.English ? DaysEn : Days)[(int)d];
        public static string WeekdayShort(DayOfWeek d) => (Lang.English ? DaysShortEn : DaysShort)[(int)d];
        public static string Era(bool hijri) => hijri ? (Lang.English ? " AH" : "هـ") : "";

        /// <summary>A number, in Arabic-Indic digits if wanted.</summary>
        public static string Num(int n, bool indic) => Digits(n.ToString(CultureInfo.InvariantCulture), indic);

        public static string Digits(string s, bool indic) =>
            indic ? new string(s.Select(c => c is >= '0' and <= '9' ? (char)('٠' + (c - '0')) : c).ToArray()) : s;

        /// <summary>"13 نوفمبر 2026" or "22 جمادى الأولى 1448هـ" (the weekday first if wanted).</summary>
        public static string Date(DateTime d, bool hijri, bool indic, bool weekday = false)
        {
            var cal = Of(hijri, d);
            bool h = cal is UmAlQuraCalendar;
            string s = $"{Num(cal.GetDayOfMonth(d), indic)} {Month(cal.GetMonth(d), h)} {Num(cal.GetYear(d), indic)}{Era(h)}";
            return weekday ? $"{Weekday(d.DayOfWeek)}{(Lang.English ? ", " : "، ")}{s}" : s;
        }

        /// <summary>"5:30 م" / "5:30 PM".</summary>
        public static string Time(int minutes, bool indic)
        {
            int h = minutes / 60 % 24, m = minutes % 60;
            int h12 = h % 12 == 0 ? 12 : h % 12;
            string ampm = Lang.English ? (h < 12 ? "AM" : "PM") : (h < 12 ? "ص" : "م");
            return Digits($"{h12}:{m:00}", indic) + " " + ampm;
        }

        /// <summary>When an appointment is, said simply: "اليوم 5:30 م", "بكرة", "الأحد 16 نوفمبر".</summary>
        public static string When(Appointment a, bool hijri, bool indic)
        {
            var today = DateTime.Today;
            int days = (a.Date.Date - today).Days;
            string day = days switch
            {
                0 => Lang.T("اليوم"),
                1 => Lang.T("بكرة"),
                > 1 and < 7 => Weekday(a.Date.DayOfWeek),
                _ => ShortDate(a.Date, hijri, indic),
            };
            return a.Minutes is int m ? $"{day} {Time(m, indic)}" : $"{day} · {Lang.T("طول اليوم")}";
        }

        /// <summary>"16 نوفمبر" (the year too if it isn't this year's).</summary>
        public static string ShortDate(DateTime d, bool hijri, bool indic)
        {
            var cal = Of(hijri, d);
            bool h = cal is UmAlQuraCalendar;
            string s = $"{Num(cal.GetDayOfMonth(d), indic)} {Month(cal.GetMonth(d), h)}";
            return cal.GetYear(d) == cal.GetYear(DateTime.Today) ? s : $"{s} {Num(cal.GetYear(d), indic)}";
        }

        /// <summary>A notification's text: its time, and how long until it.</summary>
        public static string Alert(Appointment a)
        {
            if (a.Minutes is not int m) return Lang.T("اليوم، طول اليوم");
            int left = (int)Math.Round((a.When - DateTime.Now).TotalMinutes);
            string at = Lang.F("الساعة {0}", Time(m, false));
            if (a.Date.Date != DateTime.Today) at = $"{ShortDate(a.Date, false, false)}، {at}";
            if (left <= 0) return at;
            string inText = left < 60 ? Lang.F("بعد {0} دقيقة", left)
                          : left < 24 * 60 ? Lang.F("بعد {0} ساعة", Math.Round(left / 60.0, 1))
                          : Lang.F("بعد {0} يوم", Math.Round(left / 1440.0));
            return $"{at} ({inText})";
        }

        public static SolidColorBrush Brush(string hex)
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
            catch { return new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)); }
        }
    }
}
