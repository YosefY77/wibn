using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WinPlus
{
    /// <summary>
    /// The weather widget: now (icon, temperature, what it's like, today's high and low,
    /// humidity and wind) for the chosen city, and the next days on tall sizes. From
    /// Open-Meteo (free, no account), every 15 minutes while it's on the desktop; the last
    /// reading stays up if the internet drops. The city is picked from its right-click menu,
    /// the same list as the prayer times (and it starts on the prayer widget's city).
    /// </summary>
    internal sealed class WeatherWidget : WidgetContent
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

        private readonly SolidColorBrush _accent = new(Color.FromRgb(0x4C, 0x8D, 0xFF));
        private readonly SolidColorBrush _ink = new(Color.FromRgb(0xF2, 0xF3, 0xF5));
        private readonly SolidColorBrush _inkMuted = new(Color.FromRgb(0xA0, 0xA3, 0xAA));
        private readonly SolidColorBrush _pill = new(Color.FromArgb(0x59, 0x00, 0x00, 0x00));

        private readonly StackPanel _middle;
        private readonly Grid _icon = new() { Width = 24, Height = 24 };
        private readonly ScaleTransform _iconScale = new(1.7, 1.7);
        private readonly TextBlock _temp, _desc, _city;
        private readonly UniformGrid _days = new() { Rows = 1, Margin = new Thickness(0, 8, 0, 0) };
        private readonly Viewbox _daysBox;
        private readonly DispatcherTimer _timer;
        private Forecast? _data;
        private string _forCity = "";
        private int _dayCount = 4;
        private bool _small, _loading;

        public WeatherWidget()
        {
            _temp = new TextBlock
            {
                FontSize = 40, FontWeight = FontWeights.SemiBold, Foreground = _ink,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), Text = "--°",
            };
            var iconBox = new Grid { LayoutTransform = _iconScale, VerticalAlignment = VerticalAlignment.Center };
            iconBox.Children.Add(_icon);
            var now = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
                FlowDirection = FlowDirection.LeftToRight,
            };
            now.Children.Add(iconBox);
            now.Children.Add(_temp);

            _desc = new TextBlock { FontSize = 17, Foreground = _ink, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
            _city = new TextBlock { FontSize = 14, Foreground = _ink };
            var cityPill = new Border
            {
                Background = _pill, CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 1, 9, 2),
                HorizontalAlignment = HorizontalAlignment.Center, Child = _city, Margin = new Thickness(0, 5, 0, 0),
            };

            _middle = new StackPanel { Width = 170, Margin = new Thickness(0, 2, 0, 2) };
            _middle.Children.Add(now);
            _middle.Children.Add(_desc);
            _middle.Children.Add(cityPill);
            var main = new Viewbox { Child = _middle, Stretch = Stretch.Uniform, MaxHeight = 190 };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            // The days shrink (never grow) when the widget is narrow, so they all still fit.
            _daysBox = new Viewbox { Child = _days, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
            Grid.SetRow(_daysBox, 1);
            grid.Children.Add(main);
            grid.Children.Add(_daysBox);
            Child = grid;

            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(15) };
            _timer.Tick += (_, _) => _ = Refresh();
            ShowStatus(Lang.T("أجيب الطقس…"));
        }

        public override void Start()
        {
            _ = Refresh();
            _timer.Start();
        }

        public override void Stop() => _timer.Stop();

        public override void SetCells(int w, int h)
        {
            // Only 2 × 2 is the short version; every other size shows everything (shrunk to fit).
            _small = w <= 2 && h <= 2;
            _daysBox.Visibility = _small ? Visibility.Collapsed : Visibility.Visible;
            _dayCount = w >= 4 ? 5 : 4;
            _middle.Width = _small ? 140 : 170;
            _temp.FontSize = _small ? 44 : 42;
            _desc.FontSize = _small ? 16 : 17;
            _city.FontSize = _small ? 15 : 14;
            if (_data != null) ShowDays(_data);
        }

        public override void SetAccent(Color color) =>
            _accent.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, new Duration(TimeSpan.FromMilliseconds(200))));

        public override void SetLightBehind(bool light)
        {
            var d = new Duration(TimeSpan.FromMilliseconds(200));
            _ink.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(light ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xF2, 0xF3, 0xF5), d));
            _inkMuted.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(light ? Color.FromArgb(0xB0, 0x3C, 0x3C, 0x43) : Color.FromRgb(0xA0, 0xA3, 0xAA), d));
            _pill.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(light ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x59, 0x00, 0x00, 0x00), d));
        }

        // ---- The menu: the city, refresh ----------------------------------------------------------

        /// <summary>Its own city; a new one starts on the prayer widget's city, else Riyadh.</summary>
        private PrayerWidget.City CurrentCity
        {
            get
            {
                string name = Info?.Options.TryGetValue("city", out var n) == true ? n : "";
                if (name.Length == 0)
                    name = Widgets.All.FirstOrDefault(i => i.Type == "prayer" && i.Options.ContainsKey("city"))?.Options["city"] ?? "";
                if (name == "الكويت") name = "مدينة الكويت";
                return PrayerWidget.Cities.FirstOrDefault(c => c.Name == name) ?? PrayerWidget.Cities[0];
            }
        }

        public override IEnumerable<Control> MenuItems()
        {
            yield return PrayerWidget.CityMenu(() => CurrentCity, c =>
            {
                if (Info == null) return;
                Info.Options["city"] = c.Name;
                Widgets.Save();
                _ = Refresh();
            });
            var refresh = new MenuItem { Header = Lang.T("تحديث الحين") };
            refresh.Click += (_, _) => _ = Refresh();
            yield return refresh;
        }

        // ---- Getting it ---------------------------------------------------------------------------

        private sealed record Day(DateTime Date, int Code, double Max, double Min);

        private sealed record Forecast(double Temp, double Feels, int Humidity, double Wind, int Code, bool IsDay, Day[] Days);

        private async Task Refresh()
        {
            if (_loading) return;
            _loading = true;
            var city = CurrentCity;
            if (city.Name != _forCity) { _data = null; _forCity = city.Name; ShowStatus(Lang.T("أجيب الطقس…")); }
            _city.Text = city.Shown;
            try
            {
                var data = await Task.Run(() => Fetch(city));
                if (CurrentCity.Name != city.Name) return;   // changed while it was loading
                _data = data;
                Show(data);
                _timer.Interval = TimeSpan.FromMinutes(15);
            }
            catch (Exception ex)
            {
                // No internet (or the service is down): keep what's shown and try again soon.
                if (ex is not (HttpRequestException or TaskCanceledException)) CrashLog.Write("weather widget", ex);
                if (_data == null) ShowStatus(Lang.T("ما قدرت أجيب الطقس، بحاول مرة ثانية بعد شوي"));
                _timer.Interval = TimeSpan.FromMinutes(2);
            }
            finally
            {
                _loading = false;
                if (_timer.IsEnabled) { _timer.Stop(); _timer.Start(); }
            }
        }

        private static async Task<Forecast> Fetch(PrayerWidget.City city)
        {
            string url = "https://api.open-meteo.com/v1/forecast?latitude=" + city.Lat.ToString(CultureInfo.InvariantCulture) +
                         "&longitude=" + city.Lng.ToString(CultureInfo.InvariantCulture) +
                         "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m,is_day" +
                         "&daily=weather_code,temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=6";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var now = doc.RootElement.GetProperty("current");
            var daily = doc.RootElement.GetProperty("daily");
            var dates = daily.GetProperty("time").EnumerateArray().Select(e => DateTime.ParseExact(e.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray();
            var codes = daily.GetProperty("weather_code").EnumerateArray().Select(Int).ToArray();
            var max = daily.GetProperty("temperature_2m_max").EnumerateArray().Select(Num).ToArray();
            var min = daily.GetProperty("temperature_2m_min").EnumerateArray().Select(Num).ToArray();
            var days = Enumerable.Range(0, dates.Length).Select(i => new Day(dates[i], codes[i], max[i], min[i])).ToArray();
            return new Forecast(
                Num(now.GetProperty("temperature_2m")), Num(now.GetProperty("apparent_temperature")),
                Int(now.GetProperty("relative_humidity_2m")), Num(now.GetProperty("wind_speed_10m")),
                Int(now.GetProperty("weather_code")), Int(now.GetProperty("is_day")) == 1, days);
        }

        private static double Num(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : double.NaN;
        private static int Int(JsonElement e) => e.ValueKind == JsonValueKind.Number ? (int)Math.Round(e.GetDouble()) : -1;

        // ---- Showing ------------------------------------------------------------------------------

        private void ShowStatus(string text)
        {
            _icon.Children.Clear();
            _temp.Text = "--°";
            _desc.Text = text;
            _desc.TextWrapping = TextWrapping.Wrap;
            _desc.TextAlignment = TextAlignment.Center;
            _days.Children.Clear();
        }

        private void Show(Forecast f)
        {
            SetIcon(_icon, f.Code, f.IsDay);
            _temp.Text = Deg(f.Temp);
            _desc.Text = Describe(f.Code);
            var today = f.Days.FirstOrDefault();
            string range = today == null ? "" : $"↑ {Deg(today.Max)}   ↓ {Deg(today.Min)}";
            string details = Lang.F("يحس كأنه {0} · رطوبة {1}% · رياح {2} كم/س", Deg(f.Feels), f.Humidity, Math.Round(f.Wind));
            // The rest (high / low, feels like, humidity, wind) when the mouse is over it.
            ToolTip = $"{CurrentCity.Shown}: {Deg(f.Temp)} {Describe(f.Code)}\n{range}\n{details}";
            ShowDays(f);
        }

        /// <summary>The next days (from tomorrow): name, icon, high and low.</summary>
        private void ShowDays(Forecast f)
        {
            _days.Children.Clear();
            var days = f.Days.Skip(1).Take(_dayCount).ToArray();
            _days.Columns = Math.Max(1, days.Length);
            _days.Width = days.Length * 66;   // a set width per day; the Viewbox shrinks it on narrow widgets
            foreach (var d in days)
            {
                var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, ToolTip = Describe(d.Code) };
                col.Children.Add(new TextBlock { Text = DayName(d.Date), FontSize = 13, Foreground = _inkMuted, HorizontalAlignment = HorizontalAlignment.Center });
                var icon = new Grid { Width = 24, Height = 24 };
                SetIcon(icon, d.Code, true);
                col.Children.Add(new Grid { Children = { icon }, LayoutTransform = new ScaleTransform(1.1, 1.1), Margin = new Thickness(0, 3, 0, 3), HorizontalAlignment = HorizontalAlignment.Center });
                var temps = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, FlowDirection = FlowDirection.LeftToRight };
                temps.Children.Add(new TextBlock { Text = Deg(d.Max), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = _ink });
                temps.Children.Add(new TextBlock { Text = Deg(d.Min), FontSize = 15, Foreground = _inkMuted, Margin = new Thickness(4, 0, 0, 0) });
                col.Children.Add(temps);
                _days.Children.Add(col);
            }
        }

        private static string Deg(double t) => double.IsNaN(t) ? "--°" : $"{Math.Round(t):0}°";

        private static readonly string[] DaysAr = { "الأحد", "الاثنين", "الثلاثاء", "الأربعاء", "الخميس", "الجمعة", "السبت" };
        private static readonly string[] DaysEn = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };

        private static string DayName(DateTime date) => (Lang.English ? DaysEn : DaysAr)[(int)date.DayOfWeek];

        /// <summary>The WMO weather codes Open-Meteo uses, in words.</summary>
        private static string Describe(int code) => code switch
        {
            0 => Lang.T("صحو"),
            1 => Lang.T("صحو غالبًا"),
            2 => Lang.T("غائم جزئيًا"),
            3 => Lang.T("غائم"),
            45 or 48 => Lang.T("ضباب"),
            >= 51 and <= 57 => Lang.T("رذاذ"),
            65 or 67 => Lang.T("مطر غزير"),
            >= 61 and <= 67 => Lang.T("مطر"),
            >= 71 and <= 77 => Lang.T("ثلج"),
            >= 80 and <= 82 => Lang.T("زخات مطر"),
            85 or 86 => Lang.T("زخات ثلج"),
            95 => Lang.T("عاصفة رعدية"),
            96 or 99 => Lang.T("رعد وبَرَد"),
            _ => "",
        };

        // ---- The icons (drawn on a 24 × 24 grid, outlines in the text color) ------------------------

        private const string Sun =
            "M 16,12 A 4,4 0 1 1 8,12 A 4,4 0 1 1 16,12 Z M 12,2 V 4.5 M 12,19.5 V 22 M 2,12 H 4.5 M 19.5,12 H 22 " +
            "M 4.9,4.9 L 6.7,6.7 M 17.3,17.3 L 19.1,19.1 M 4.9,19.1 L 6.7,17.3 M 17.3,6.7 L 19.1,4.9";
        private const string Moon = "M 13,3 A 8,8 0 1 0 21,13 A 6.2,6.2 0 1 1 13,3 Z";
        // A small sun / moon at the top left, for "partly cloudy".
        private const string SmallSun =
            "M 10.5,8 A 2.8,2.8 0 1 1 4.9,8 A 2.8,2.8 0 1 1 10.5,8 Z M 7.7,1.8 V 3.4 M 1.5,8 H 3.1 M 3.3,3.6 L 4.4,4.7 M 12.1,3.6 L 11,4.7 M 3.3,12.4 L 4.4,11.3";
        private const string SmallMoon = "M 8,2.5 A 5.2,5.2 0 1 0 13.2,8.6 A 4,4 0 1 1 8,2.5 Z";
        private const string Cloud = "M 7,19.5 H 17.5 A 4.2,4.2 0 0 0 17.2,11.1 A 5.8,5.8 0 0 0 6.2,12.4 A 3.6,3.6 0 0 0 7,19.5 Z";
        private const string CloudHigh = "M 7,15 H 17.5 A 4,4 0 0 0 17.2,7 A 5.6,5.6 0 0 0 6.3,8.4 A 3.3,3.3 0 0 0 7,15 Z";
        private const string Rain = "M 8.5,17.5 L 7.5,20.5 M 12.5,17.5 L 11.5,20.5 M 16.5,17.5 L 15.5,20.5";
        private const string Drizzle = "M 8.5,18 V 18.6 M 12.5,18 V 18.6 M 16.5,18 V 18.6 M 10.5,20.6 V 21.2 M 14.5,20.6 V 21.2";
        private const string Snow = "M 8.5,18.5 V 18.6 M 12.5,18.5 V 18.6 M 16.5,18.5 V 18.6 M 10.5,21.2 V 21.3 M 14.5,21.2 V 21.3";
        private const string Bolt = "M 13,16 L 10.5,19.5 H 13.5 L 11,23";
        private const string Fog = "M 4,8 H 20 M 3,12 H 21 M 5,16 H 19 M 7,20 H 17";

        private void SetIcon(Grid box, int code, bool day)
        {
            box.Children.Clear();
            void Add(string data, double thickness = 1.8, Geometry? clip = null) => box.Children.Add(new Path
            {
                Data = Geometry.Parse(data), Stroke = _ink, StrokeThickness = thickness, Clip = clip,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            });

            switch (code)
            {
                case 0:
                    Add(day ? Sun : Moon);
                    break;
                case 1 or 2:
                    // The sun / moon behind the cloud: cut where the cloud (and a little gap) covers it.
                    var cloud = Geometry.Parse(Cloud);
                    var cover = Geometry.Combine(cloud, cloud.GetWidenedPathGeometry(new Pen(Brushes.Black, 4)), GeometryCombineMode.Union, null);
                    var rest = Geometry.Combine(new RectangleGeometry(new Rect(-2, -2, 28, 28)), cover, GeometryCombineMode.Exclude, null);
                    Add(day ? SmallSun : SmallMoon, 1.6, rest);
                    Add(Cloud);
                    break;
                case 3:
                    Add(Cloud);
                    break;
                case 45 or 48:
                    Add(Fog);
                    break;
                case >= 51 and <= 57:
                    Add(CloudHigh); Add(Drizzle, 2.2);
                    break;
                case (>= 61 and <= 67) or (>= 80 and <= 82):
                    Add(CloudHigh); Add(Rain);
                    break;
                case (>= 71 and <= 77) or 85 or 86:
                    Add(CloudHigh); Add(Snow, 2.6);
                    break;
                case >= 95:
                    Add(CloudHigh); Add(Bolt);
                    break;
                default:
                    Add(Cloud);
                    break;
            }
        }
    }
}
