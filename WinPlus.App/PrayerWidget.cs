using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace WinPlus
{
    /// <summary>
    /// The prayer times widget: today's times for the chosen city by the Umm al-Qura
    /// method (the one Saudi Arabia uses: Fajr at 18.5°, Isha 90 minutes after Maghrib,
    /// 120 in Ramadan), the next prayer with a countdown to the second, and the Hijri date (tooltip).
    /// Worked out on the device, no internet. The city is picked from its right-click menu.
    /// </summary>
    internal sealed class PrayerWidget : WidgetContent
    {
        public sealed record City(string Name, string En, double Lat, double Lng, double Zone)
        {
            public string Shown => Lang.English ? En : Name;
        }

        public sealed record Region(string Name, string En, City[] Cities)
        {
            public string Shown => Lang.English ? En : Name;
        }

        private static City Sa(string name, string en, double lat, double lng) => new(name, en, lat, lng, 3);

        /// <summary>The 13 regions of Saudi Arabia, then the Gulf countries, each with its cities.</summary>
        public static readonly Region[] Regions =
        {
            new("منطقة الرياض", "Riyadh Region", new[]
            {
                Sa("الرياض", "Riyadh", 24.7136, 46.6753), Sa("الخرج", "Al-Kharj", 24.1556, 47.3120),
                Sa("الدرعية", "Diriyah", 24.7340, 46.5750), Sa("المجمعة", "Al-Majmaah", 25.9039, 45.3459),
                Sa("الزلفي", "Az-Zulfi", 26.2994, 44.8150), Sa("شقراء", "Shaqra", 25.2481, 45.2517),
                Sa("الدوادمي", "Ad-Dawadmi", 24.5070, 44.3922), Sa("عفيف", "Afif", 23.9065, 42.9172),
                Sa("القويعية", "Al-Quwayiyah", 24.0731, 45.2806), Sa("حوطة بني تميم", "Hotat Bani Tamim", 23.5200, 46.8400),
                Sa("الأفلاج", "Al-Aflaj", 22.2800, 46.7300), Sa("وادي الدواسر", "Wadi ad-Dawasir", 20.4580, 44.7900),
            }),
            new("منطقة مكة المكرمة", "Makkah Region", new[]
            {
                Sa("مكة المكرمة", "Makkah", 21.4225, 39.8262), Sa("جدة", "Jeddah", 21.4858, 39.1925),
                Sa("الطائف", "Taif", 21.2703, 40.4158), Sa("الجموم", "Al-Jamoum", 21.6170, 39.6980),
                Sa("رابغ", "Rabigh", 22.7986, 39.0349), Sa("خليص", "Khulais", 22.1500, 39.3167),
                Sa("الليث", "Al-Lith", 20.1500, 40.2667), Sa("القنفذة", "Al-Qunfudhah", 19.1264, 41.0789),
                Sa("تربة", "Turabah", 21.2140, 41.6330), Sa("الخرمة", "Al-Khurmah", 21.9200, 42.0400),
                Sa("رنية", "Ranyah", 21.2600, 42.8500),
            }),
            new("منطقة المدينة المنورة", "Madinah Region", new[]
            {
                Sa("المدينة المنورة", "Madinah", 24.4672, 39.6112), Sa("ينبع", "Yanbu", 24.0895, 38.0618),
                Sa("العلا", "AlUla", 26.6085, 37.9232), Sa("بدر", "Badr", 23.7800, 38.7900),
                Sa("خيبر", "Khaybar", 25.6990, 39.2920), Sa("الحناكية", "Al-Hanakiyah", 24.8833, 40.5167),
                Sa("المهد", "Mahd adh-Dhahab", 23.4950, 40.8580),
            }),
            new("منطقة القصيم", "Qassim Region", new[]
            {
                Sa("بريدة", "Buraydah", 26.3260, 43.9750), Sa("عنيزة", "Unaizah", 26.0840, 43.9940),
                Sa("الرس", "Ar-Rass", 25.8694, 43.4973), Sa("البكيرية", "Al-Bukayriyah", 26.1393, 43.6590),
                Sa("البدائع", "Al-Badai", 25.9830, 43.7330), Sa("المذنب", "Al-Mithnab", 25.8601, 44.2222),
                Sa("الأسياح", "Al-Asyah", 26.9000, 44.1500),
            }),
            new("المنطقة الشرقية", "Eastern Province", new[]
            {
                Sa("الدمام", "Dammam", 26.4207, 50.0888), Sa("الخبر", "Khobar", 26.2172, 50.1971),
                Sa("الظهران", "Dhahran", 26.2361, 50.0393), Sa("القطيف", "Qatif", 26.5196, 50.0115),
                Sa("رأس تنورة", "Ras Tanura", 26.6440, 50.1590), Sa("الجبيل", "Jubail", 27.0174, 49.6225),
                Sa("الأحساء", "Al-Ahsa", 25.3833, 49.5866), Sa("بقيق", "Abqaiq", 25.9350, 49.6680),
                Sa("النعيرية", "An-Nairyah", 27.4690, 48.4880), Sa("الخفجي", "Khafji", 28.4391, 48.4913),
                Sa("حفر الباطن", "Hafar al-Batin", 28.4328, 45.9708),
            }),
            new("منطقة عسير", "Asir Region", new[]
            {
                Sa("أبها", "Abha", 18.2164, 42.5053), Sa("خميس مشيط", "Khamis Mushait", 18.3000, 42.7333),
                Sa("أحد رفيدة", "Ahad Rafidah", 18.1990, 42.8510), Sa("محايل عسير", "Muhayil", 18.5440, 42.0530),
                Sa("النماص", "An-Namas", 19.1210, 42.1300), Sa("بلقرن", "Balqarn", 19.5833, 41.9500),
                Sa("بيشة", "Bisha", 19.9839, 42.6052), Sa("سراة عبيدة", "Sarat Abidah", 18.0900, 43.1300),
                Sa("ظهران الجنوب", "Dhahran al-Janub", 17.6650, 43.5140),
            }),
            new("منطقة تبوك", "Tabuk Region", new[]
            {
                Sa("تبوك", "Tabuk", 28.3835, 36.5662), Sa("ضباء", "Duba", 27.3510, 35.6900),
                Sa("الوجه", "Al-Wajh", 26.2370, 36.4530), Sa("أملج", "Umluj", 25.0210, 37.2680),
                Sa("تيماء", "Tayma", 27.6290, 38.5440), Sa("حقل", "Haql", 29.2880, 34.9410),
            }),
            new("منطقة حائل", "Hail Region", new[]
            {
                Sa("حائل", "Hail", 27.5114, 41.6900), Sa("بقعاء", "Baqaa", 27.8900, 42.4100),
                Sa("الغزالة", "Al-Ghazalah", 26.7800, 41.3200), Sa("الشنان", "Ash-Shinan", 27.1800, 42.4400),
            }),
            new("منطقة الحدود الشمالية", "Northern Borders", new[]
            {
                Sa("عرعر", "Arar", 30.9753, 41.0381), Sa("رفحاء", "Rafha", 29.6260, 43.4930),
                Sa("طريف", "Turaif", 31.6720, 38.6630), Sa("العويقيلة", "Al-Uwayqilah", 30.3420, 42.2500),
            }),
            new("منطقة جازان", "Jazan Region", new[]
            {
                Sa("جازان", "Jazan", 16.8892, 42.5511), Sa("صبيا", "Sabya", 17.1500, 42.6250),
                Sa("أبو عريش", "Abu Arish", 16.9690, 42.8320), Sa("صامطة", "Samtah", 16.5960, 42.9440),
                Sa("بيش", "Baish", 17.3830, 42.5430), Sa("فرسان", "Farasan", 16.7020, 42.1180),
            }),
            new("منطقة نجران", "Najran Region", new[]
            {
                Sa("نجران", "Najran", 17.5656, 44.2289), Sa("شرورة", "Sharurah", 17.4850, 47.1150),
                Sa("حبونا", "Hubuna", 17.8440, 44.5750), Sa("بدر الجنوب", "Badr al-Janub", 17.8300, 43.8100),
            }),
            new("منطقة الباحة", "Al-Baha Region", new[]
            {
                Sa("الباحة", "Al-Baha", 20.0129, 41.4677), Sa("بلجرشي", "Baljurashi", 19.8580, 41.5580),
                Sa("المندق", "Al-Mandaq", 20.1600, 41.2900), Sa("المخواة", "Al-Mikhwah", 19.7690, 41.4380),
                Sa("قلوة", "Qilwah", 19.9480, 41.2680),
            }),
            new("منطقة الجوف", "Al-Jouf Region", new[]
            {
                Sa("سكاكا", "Sakaka", 29.9697, 40.2064), Sa("دومة الجندل", "Dumat al-Jandal", 29.8140, 39.8680),
                Sa("القريات", "Al-Qurayyat", 31.3317, 37.3428), Sa("طبرجل", "Tabarjal", 30.4990, 38.2160),
            }),
            new("الكويت", "Kuwait", new[]
            {
                Sa("مدينة الكويت", "Kuwait City", 29.3759, 47.9774), Sa("الجهراء", "Al-Jahra", 29.3375, 47.6581),
                Sa("الأحمدي", "Al-Ahmadi", 29.0769, 48.0839),
            }),
            new("البحرين", "Bahrain", new[]
            {
                Sa("المنامة", "Manama", 26.2285, 50.5860), Sa("المحرق", "Muharraq", 26.2572, 50.6119),
            }),
            new("قطر", "Qatar", new[]
            {
                Sa("الدوحة", "Doha", 25.2854, 51.5310), Sa("الخور", "Al-Khor", 25.6839, 51.4969),
            }),
            new("الإمارات", "UAE", new City[]
            {
                new("أبوظبي", "Abu Dhabi", 24.4539, 54.3773, 4), new("دبي", "Dubai", 25.2048, 55.2708, 4),
                new("الشارقة", "Sharjah", 25.3463, 55.4209, 4), new("العين", "Al Ain", 24.2075, 55.7447, 4),
                new("رأس الخيمة", "Ras Al Khaimah", 25.8007, 55.9762, 4), new("الفجيرة", "Fujairah", 25.1288, 56.3265, 4),
            }),
            new("عُمان", "Oman", new City[]
            {
                new("مسقط", "Muscat", 23.5880, 58.3829, 4), new("صحار", "Sohar", 24.3460, 56.7090, 4),
                new("نزوى", "Nizwa", 22.9333, 57.5333, 4), new("صلالة", "Salalah", 17.0151, 54.0924, 4),
            }),
        };

        public static readonly City[] Cities = Regions.SelectMany(r => r.Cities).ToArray();

        private static readonly string[] Names = { "الفجر", "الشروق", "الظهر", "العصر", "المغرب", "العشاء" };
        internal static readonly string[] HijriMonths =
        {
            "محرم", "صفر", "ربيع الأول", "ربيع الآخر", "جمادى الأولى", "جمادى الآخرة",
            "رجب", "شعبان", "رمضان", "شوال", "ذو القعدة", "ذو الحجة",
        };
        internal static readonly string[] HijriMonthsEn =
        {
            "Muharram", "Safar", "Rabi al-Awwal", "Rabi al-Thani", "Jumada al-Ula", "Jumada al-Akhirah",
            "Rajab", "Shaban", "Ramadan", "Shawwal", "Dhu al-Qadah", "Dhu al-Hijjah",
        };

        private readonly SolidColorBrush _accent = new(Color.FromRgb(0x4C, 0x8D, 0xFF));
        private readonly SolidColorBrush _accentSoft = new(Color.FromArgb(0x33, 0x4C, 0x8D, 0xFF));
        private readonly SolidColorBrush _ink = new(Color.FromRgb(0xF2, 0xF3, 0xF5));
        private readonly SolidColorBrush _inkMuted = new(Color.FromRgb(0xA0, 0xA3, 0xAA));
        private readonly SolidColorBrush _pill = new(Color.FromArgb(0x59, 0x00, 0x00, 0x00));

        // The icon of each prayer, drawn on a 24 × 24 grid.
        private const string Moon = "M 13,3 A 8,8 0 1 0 21,13 A 6.2,6.2 0 1 1 13,3 Z";
        private static readonly string[] Icons =
        {
            // Fajr: the moon going down over the horizon.
            "M 12,2.5 A 7,7 0 1 0 19,10.5 A 5.4,5.4 0 1 1 12,2.5 Z M 3,21 H 21",
            // Sunrise: half a sun coming up.
            "M 6,18 A 6,6 0 0 1 18,18 Z M 2,18 H 22 M 12,4 V 7 M 4.5,9.5 L 6.5,11.5 M 19.5,9.5 L 17.5,11.5",
            // Dhuhr: the full sun.
            "M 16,12 A 4,4 0 1 1 8,12 A 4,4 0 1 1 16,12 Z M 12,2 V 4.5 M 12,19.5 V 22 M 2,12 H 4.5 M 19.5,12 H 22 " +
            "M 4.9,4.9 L 6.7,6.7 M 17.3,17.3 L 19.1,19.1 M 4.9,19.1 L 6.7,17.3 M 17.3,6.7 L 19.1,4.9",
            // Asr: the sun lower, over the horizon.
            "M 15.5,13 A 3.5,3.5 0 1 1 8.5,13 A 3.5,3.5 0 1 1 15.5,13 Z M 12,4.5 V 6.5 M 4.5,13 H 6 M 18,13 H 19.5 " +
            "M 6.7,7.7 L 8,9 M 17.3,7.7 L 16,9 M 3,21 H 21",
            // Maghrib: half a sun going down.
            "M 6,17 A 6,6 0 0 1 18,17 Z M 2,17 H 22 M 12,3 V 9 M 9.5,6.5 L 12,9 L 14.5,6.5 M 6,21 H 18",
            // Isha: the moon and a star.
            Moon + " M 19,2 L 19.6,3.4 L 21,4 L 19.6,4.6 L 19,6 L 18.4,4.6 L 17,4 L 18.4,3.4 Z",
        };

        private readonly System.Windows.Shapes.Path _icon;
        private readonly ScaleTransform _iconScale = new(1.35, 1.35);
        private readonly StackPanel _middle;
        private readonly TextBlock _label, _count, _city;
        private readonly StackPanel _list = new() { Margin = new Thickness(0, 6, 0, 0) };
        private readonly (Border Row, TextBlock Name, TextBlock Time)[] _rows = new (Border, TextBlock, TextBlock)[6];
        private readonly DispatcherTimer _timer;
        private DateTime[] _today = Array.Empty<DateTime>(), _tomorrow = Array.Empty<DateTime>();
        private DateTime _forDate;
        private string _forCity = "";
        private int _shown = -1;

        public PrayerWidget()
        {
            // The middle: icon, "Fajr prayer after", the countdown, the city. It grows with the widget.
            _icon = new System.Windows.Shapes.Path
            {
                Fill = _ink, Stroke = _ink, StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            };
            var iconBox = new Grid
            {
                Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Center,
                LayoutTransform = _iconScale, Margin = new Thickness(0, 0, 0, 6),
            };
            iconBox.Children.Add(_icon);
            _label = new TextBlock { FontSize = 13, Foreground = _ink, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
            _count = new TextBlock
            {
                FontSize = 36, FontWeight = FontWeights.SemiBold, Foreground = _ink, HorizontalAlignment = HorizontalAlignment.Center,
                FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(0, -3, 0, 2),
            };
            _count.Typography.NumeralAlignment = FontNumeralAlignment.Tabular;   // the digits don't jump each second
            _city = new TextBlock { FontSize = 12, Foreground = _ink };
            var cityPill = new Border
            {
                Background = _pill, CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 1, 9, 2),
                HorizontalAlignment = HorizontalAlignment.Center, Child = _city,
            };
            var middle = _middle = new StackPanel { Width = 170, Margin = new Thickness(0, 4, 0, 4) };
            middle.Children.Add(iconBox);
            middle.Children.Add(_label);
            middle.Children.Add(_count);
            middle.Children.Add(cityPill);
            var main = new Viewbox { Child = middle, Stretch = Stretch.Uniform, MaxHeight = 190 };

            // All of today's times, under it on tall sizes.
            for (int i = 0; i < 6; i++)
            {
                var name = new TextBlock { Text = Lang.T(Names[i]), FontSize = 12, Foreground = _ink, VerticalAlignment = VerticalAlignment.Center };
                var time = new TextBlock { FontSize = 12, Foreground = _ink, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight };
                var dock = new DockPanel();
                DockPanel.SetDock(time, Dock.Right);
                dock.Children.Add(time);
                dock.Children.Add(name);
                var row = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 1, 0, 1), Child = dock };
                _rows[i] = (row, name, time);
                _list.Children.Add(row);
            }

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_list, 1);
            grid.Children.Add(main);
            grid.Children.Add(_list);
            Child = grid;

            _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => Update();
        }

        public override void Start()
        {
            Update();
            _timer.Start();
        }

        public override void Stop() => _timer.Stop();

        /// <summary>The list of all the times only when there's room for it under the countdown.</summary>
        public override void SetCells(int w, int h)
        {
            _list.Visibility = h >= 5 && w >= 3 ? Visibility.Visible : Visibility.Collapsed;
            // 2 × 2 is small: bigger text for its size (and a narrower middle, so it all grows more).
            bool small = w <= 2 || h <= 2;
            _middle.Width = small ? 140 : 170;
            _label.FontSize = small ? 16 : 13;
            _count.FontSize = small ? 40 : 36;
            _city.FontSize = small ? 15 : 12;
            _iconScale.ScaleX = _iconScale.ScaleY = small ? 1.15 : 1.35;
        }

        public override void SetAccent(Color color)
        {
            var d = new Duration(TimeSpan.FromMilliseconds(200));
            _accent.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, d));
            _accentSoft.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Color.FromArgb(0x33, color.R, color.G, color.B), d));
        }

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

        // ---- The menu: the city -------------------------------------------------------------------

        private City CurrentCity
        {
            get
            {
                string name = Info?.Options.TryGetValue("city", out var n) == true ? n : "";
                if (name == "الكويت") name = "مدينة الكويت";   // its old name
                return Cities.FirstOrDefault(c => c.Name == name) ?? Cities[0];
            }
        }

        public override IEnumerable<Control> MenuItems()
        {
            yield return CityMenu(() => CurrentCity, c =>
            {
                if (Info == null) return;
                Info.Options["city"] = c.Name;
                Widgets.Save();
                Update();
            });
        }

        /// <summary>The region, then its cities (the chosen one ticked). Also used by the weather widget.</summary>
        public static MenuItem CityMenu(Func<City> current, Action<City> pick)
        {
            var top = new MenuItem { Header = Lang.T("المنطقة") };
            foreach (var region in Regions)
            {
                var sub = new MenuItem { Header = region.Shown };
                foreach (var city in region.Cities)
                {
                    var c = city;
                    var choice = new MenuItem { Header = c.Shown, Tag = c.Name };
                    choice.Click += (_, _) => pick(c);
                    sub.Items.Add(choice);
                }
                top.Items.Add(sub);
            }
            top.SubmenuOpened += (_, e) =>
            {
                if (e.OriginalSource != top) return;
                string now = current().Name;
                foreach (MenuItem sub in top.Items)
                {
                    bool here = false;
                    foreach (MenuItem c in sub.Items) here |= c.IsChecked = (string)c.Tag == now;
                    sub.FontWeight = here ? FontWeights.SemiBold : FontWeights.Normal;
                }
            };
            return top;
        }

        // ---- Showing ------------------------------------------------------------------------------

        private void Update()
        {
            var city = CurrentCity;
            // The city's own clock (the Gulf has no daylight saving).
            DateTime now = DateTime.UtcNow.AddHours(city.Zone);
            if (_forDate != now.Date || _forCity != city.Name)
            {
                _forDate = now.Date;
                _forCity = city.Name;
                _today = PrayerTimes.For(now.Date, city);
                _tomorrow = PrayerTimes.For(now.Date.AddDays(1), city);
                _city.Text = city.Shown;
                ToolTip = Hijri(now.Date);
                for (int i = 0; i < 6; i++) _rows[i].Time.Text = Clock(_today[i]);
                _shown = -1;
            }

            // The next prayer (sunrise isn't one); after Isha it's tomorrow's Fajr.
            int next = -1;
            for (int i = 0; i < 6; i++)
                if (i != 1 && _today[i] > now) { next = i; break; }
            DateTime at = next >= 0 ? _today[next] : _tomorrow[0];
            int shown = next >= 0 ? next : 0;

            if (shown != _shown)
            {
                _shown = shown;
                _icon.Data = Geometry.Parse(Icons[shown]);
                _label.Text = Lang.F("صلاة {0} بعد", Lang.T(Names[shown]));
                for (int i = 0; i < 6; i++)
                {
                    bool on = next >= 0 && i == next;
                    _rows[i].Row.Background = on ? _accentSoft : Brushes.Transparent;
                    _rows[i].Name.FontWeight = _rows[i].Time.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
                    _rows[i].Name.Foreground = _rows[i].Time.Foreground = i == 1 ? _inkMuted : _ink;
                }
            }

            var left = at - now;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            _count.Text = $"{(int)left.TotalHours}:{left.Minutes:00}:{left.Seconds:00}";
        }

        private static string Clock(DateTime t)
        {
            int h = t.Hour % 12 == 0 ? 12 : t.Hour % 12;
            string half = t.Hour < 12 ? Lang.T("ص") : Lang.T("م");
            return $"{h}:{t.Minute:00} {half}";
        }

        private static string Hijri(DateTime date)
        {
            var cal = new UmAlQuraCalendar();
            try
            {
                int d = cal.GetDayOfMonth(date), m = cal.GetMonth(date), y = cal.GetYear(date);
                return Lang.English ? $"{d} {HijriMonthsEn[m - 1]} {y} AH" : $"{d} {HijriMonths[m - 1]} {y}هـ";
            }
            catch (ArgumentOutOfRangeException) { return ""; }
        }
    }

    /// <summary>
    /// Prayer times from the sun's position (the well known PrayTimes method), with the
    /// Umm al-Qura settings. Times are in the city's local time, rounded to the minute.
    /// </summary>
    internal static class PrayerTimes
    {
        private const double FajrAngle = 18.5, Horizon = 0.833;

        /// <summary>Fajr, sunrise, Dhuhr, Asr, Maghrib, Isha.</summary>
        public static DateTime[] For(DateTime date, PrayerWidget.City city)
        {
            double jd = Julian(date.Year, date.Month, date.Day) - city.Lng / (15 * 24.0);
            double lat = city.Lat;

            // First guesses (hours), then two rounds of working them out from the sun at those times.
            double fajr = 5, sunrise = 6, dhuhr = 12, asr = 13, sunset = 18;
            for (int round = 0; round < 2; round++)
            {
                fajr = SunAngleTime(jd, lat, FajrAngle, fajr / 24, true);
                sunrise = SunAngleTime(jd, lat, Horizon, sunrise / 24, true);
                dhuhr = MidDay(jd, dhuhr / 24);
                asr = AsrTime(jd, lat, 1, asr / 24);
                sunset = SunAngleTime(jd, lat, Horizon, sunset / 24, false);
            }

            bool ramadan = false;
            try { ramadan = new UmAlQuraCalendar().GetMonth(date) == 9; } catch (ArgumentOutOfRangeException) { }
            double isha = sunset + (ramadan ? 120 : 90) / 60.0;

            double shift = city.Zone - city.Lng / 15;
            return new[] { fajr, sunrise, dhuhr, asr, sunset, isha }
                .Select(h => At(date, h + shift)).ToArray();
        }

        private static DateTime At(DateTime date, double hours)
        {
            double minutes = Math.Round(hours * 60);   // to the nearest minute
            return date.Date.AddMinutes(minutes);
        }

        private static double Julian(int y, int m, int d)
        {
            if (m <= 2) { y--; m += 12; }
            double a = Math.Floor(y / 100.0), b = 2 - a + Math.Floor(a / 4);
            return Math.Floor(365.25 * (y + 4716)) + Math.Floor(30.6001 * (m + 1)) + d + b - 1524.5;
        }

        /// <summary>The sun's declination and the equation of time (hours) at a Julian date.</summary>
        private static (double Decl, double Eqt) Sun(double jd)
        {
            double d = jd - 2451545.0;
            double g = Fix(357.529 + 0.98560028 * d, 360);
            double q = Fix(280.459 + 0.98564736 * d, 360);
            double l = Fix(q + 1.915 * Sin(g) + 0.020 * Sin(2 * g), 360);
            double e = 23.439 - 0.00000036 * d;
            double ra = ATan2(Cos(e) * Sin(l), Cos(l)) / 15;
            double eqt = q / 15 - Fix(ra, 24);
            double decl = ASin(Sin(e) * Sin(l));
            return (decl, eqt);
        }

        private static double MidDay(double jd, double t) => Fix(12 - Sun(jd + t).Eqt, 24);

        private static double SunAngleTime(double jd, double lat, double angle, double t, bool beforeNoon)
        {
            double decl = Sun(jd + t).Decl, noon = MidDay(jd, t);
            double cos = (-Sin(angle) - Sin(decl) * Sin(lat)) / (Cos(decl) * Cos(lat));
            double span = ACos(Math.Clamp(cos, -1, 1)) / 15;
            return noon + (beforeNoon ? -span : span);
        }

        private static double AsrTime(double jd, double lat, double factor, double t)
        {
            double decl = Sun(jd + t).Decl;
            double angle = -ACot(factor + Tan(Math.Abs(lat - decl)));
            return SunAngleTime(jd, lat, angle, t, false);
        }

        private static double Fix(double a, double b) { a -= b * Math.Floor(a / b); return a < 0 ? a + b : a; }
        private static double Rad(double d) => d * Math.PI / 180;
        private static double Deg(double r) => r * 180 / Math.PI;
        private static double Sin(double d) => Math.Sin(Rad(d));
        private static double Cos(double d) => Math.Cos(Rad(d));
        private static double Tan(double d) => Math.Tan(Rad(d));
        private static double ASin(double x) => Deg(Math.Asin(x));
        private static double ACos(double x) => Deg(Math.Acos(x));
        private static double ATan2(double y, double x) => Deg(Math.Atan2(y, x));
        private static double ACot(double x) => Deg(Math.Atan(1 / x));
    }
}
