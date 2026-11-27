using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinPlus
{
    /// <summary>
    /// The "info bar" part of the Taskbar page: its switch, the extra bars (as many as there's room for,
    /// each with its own place, what it shows and its widgets), and what they all share.
    /// </summary>
    internal sealed class InfoBarPage : StackPanel
    {
        private readonly CheckBox _main;
        private readonly StackPanel _options = new();
        private readonly StackPanel _barOptions = new();
        private readonly StackPanel _barTabs = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        private readonly System.Collections.Generic.Dictionary<int, RadioButton> _edges = new();
        private readonly System.Collections.Generic.Dictionary<int, RadioButton> _sides = new();
        private int _barId;
        private Button? _earlier, _later;
        private bool _loading;

        private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

        public InfoBarPage()
        {
            Children.Add(new TextBlock { Text = Lang.T("تاسك بار المعلومات (إضافي)"), Style = Res<Style>("Section"), FontSize = 18, Margin = new Thickness(2, 24, 0, 8) });

            _main = new CheckBox { Style = Res<Style>("Switch"), IsChecked = InfoBar.Enabled };
            _main.Click += (_, _) => { InfoBar.Enabled = _main.IsChecked == true; Show(); };
            Children.Add(Row(_main, "\uE7F4", Lang.T("تاسك بار إضافي للمعلومات"),
                Lang.T("شريط ثاني تحطه يمين أو يسار أو فوق أو تحت، يعرض الساعة والتاريخ ومؤقت الصلاة وحرارة واستهلاك المعالج وكرت الشاشة والرام. يشتغل مع تاسك بار ويندوز أو تاسك بار Wibn")));

            Children.Add(_options);
            _options.Children.Add(Note(Lang.T("شكله نفس تاسك بار Wibn بالضبط: الشكل والخلفية واللون والسماكة وحجم الأيقونات من الإعدادات اللي فوق. اسحب طرفه عشان تقصّره أو تطوّله (ودبل كليك على الطرف يرجّعه كامل)، واضغط عليه واسحبه عشان تنقله لأي طرف أو شاشة. ولو الطرف فيه قطعة من تاسك بار Wibn (مثل الساعة لحالها فوق يمين) ينحط جنبها على نفس الخط، في الجهة اللي تفلته فيها. وكليك يمين عليه عشان تخفي أو تضيف معلومات بسرعة.")));

            // Which bar (as many as there's room for), and adding / taking one away.
            var add = new Button { Style = Res<Style>("FlatButton"), Content = Lang.T("أضف تاسك بار"), Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0) };
            add.Click += (_, _) =>
            {
                int before = InfoBar.BarIds.Count;
                InfoBarWindow.AddAnother();
                var now = InfoBar.BarIds;
                if (now.Count > before) { _barId = now[^1]; BuildBars(); Show(); }
            };
            var remove = new Button { Style = Res<Style>("FlatButton"), Content = Lang.T("شيل هالتاسك بار"), Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0) };
            remove.Click += (_, _) =>
            {
                if (InfoBar.BarIds.Count <= 1) { InfoBar.Enabled = false; _main.IsChecked = false; Show(); return; }
                InfoBar.Remove(_barId);
                _barId = InfoBar.BarIds[0];
                BuildBars();
            };
            // Its place in the order: earlier = nearer the screen's edge (on the same edge) or the taskbar piece (on its line).
            Button Arrow(string glyph, string tip, int by)
            {
                var b = new Button
                {
                    Style = Res<Style>("FlatButton"), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(4, 0, 0, 0), ToolTip = tip,
                    Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12 },
                };
                b.Click += (_, _) => { if (InfoBar.MoveInOrder(_barId, by)) BuildBars(); };
                return b;
            }
            _earlier = Arrow(Lang.Flow == FlowDirection.RightToLeft ? "" : "", Lang.T("قدّمه (أقرب لطرف الشاشة)"), -1);
            _later = Arrow(Lang.Flow == FlowDirection.RightToLeft ? "" : "", Lang.T("أخّره (أبعد عن طرف الشاشة)"), 1);
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            tabs.Children.Add(_earlier);
            tabs.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x22)), CornerRadius = new CornerRadius(8), Padding = new Thickness(3), Child = _barTabs });
            tabs.Children.Add(_later);
            tabs.Children.Add(add);
            tabs.Children.Add(remove);
            _options.Children.Add(Row(tabs, "\uE8A9", Lang.T("التاسك بارات الإضافية"),
                Lang.T("ضيف كم ما تبي، كل واحد بمكانه وباللي يعرضه، بشرط تبقى نص الشاشة على الأقل للنوافذ. اللي جنب بعض على نفس الخط ما ياخذون من مساحة النوافذ. والأسهم تبدّل ترتيبهم: اللي قبل يصير أقرب لطرف الشاشة")));
            _options.Children.Add(_barOptions);

            // For all of them
            _options.Children.Add(new TextBlock { Text = Lang.T("لكل التاسك بارات الإضافية"), Style = Res<Style>("RowTitle"), Margin = new Thickness(2, 14, 0, 6) });
            // (The clock's own bar's side is in its right-click menu, and it keeps it when moved.)
            // Seconds: with Wibn's taskbar on, its switch on the Taskbar page is for both clocks; here only without it.
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(-3, 4, -3, 0) };
            grid.Children.Add(Toggle("\uE916", Lang.T("الثواني في الساعة"), Lang.T("مثل 10:42:07"), InfoBar.Seconds, v => InfoBar.Seconds = v));
            grid.Visibility = CustomTaskbar.Enabled ? Visibility.Collapsed : Visibility.Visible;
            _options.Children.Add(grid);
            CustomTaskbar.Changed += () => Dispatcher.BeginInvoke(() => grid.Visibility = CustomTaskbar.Enabled ? Visibility.Collapsed : Visibility.Visible);

            // The prayer times' city
            var city = new Button { Style = Res<Style>("FlatButton"), Content = InfoBar.City.Shown, Padding = new Thickness(14, 5, 14, 5) };
            city.Click += (_, _) =>
            {
                var menu = new ContextMenu { FlowDirection = Lang.Flow, PlacementTarget = city };
                menu.Items.Add(PrayerWidget.CityMenu(() => InfoBar.City, c => { InfoBar.City = c; city.Content = c.Shown; }));
                menu.IsOpen = true;
            };
            _options.Children.Add(Row(city, "\uE707", Lang.T("مدينة مؤقت الصلاة"), Lang.T("أوقات الصلاة بتقويم أم القرى، محسوبة في جهازك بدون نت")));

            _barId = InfoBar.BarIds[0];
            BuildBars();
            Show();
            InfoBar.Changed += () => Dispatcher.BeginInvoke(() =>
            {
                _loading = true;
                _main.IsChecked = InfoBar.Enabled;
                var ids = InfoBar.BarIds;
                if (!ids.Contains(_barId) || _barTabs.Children.Count != ids.Count) BuildBars();
                else
                {
                    var bar = new InfoBar.Bar(_barId);
                    if (_edges.TryGetValue(bar.Edge, out var r)) r.IsChecked = true;   // moved by dragging it
                    if (_sides.TryGetValue(bar.Side, out var sd)) sd.IsChecked = true;
                }
                _loading = false;
                Show();
            });
        }

        private void Show() => _options.Visibility = InfoBar.Enabled ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>The bars' tabs (1, 2, 3…) and the chosen one's own options.</summary>
        private void BuildBars()
        {
            bool was = _loading;
            _loading = true;
            var ids = InfoBar.BarIds;
            if (!ids.Contains(_barId)) _barId = ids[0];
            _barTabs.Children.Clear();
            for (int i = 0; i < ids.Count; i++)
            {
                int id = ids[i];
                var r = new RadioButton { Style = Res<Style>("NavButton"), Content = (i + 1).ToString(), GroupName = "ib_bars", IsChecked = id == _barId, MinWidth = 34 };
                r.Checked += (_, _) => { if (_loading) return; _barId = id; BuildBarOptions(); };
                _barTabs.Children.Add(r);
            }
            BuildBarOptions();
            _loading = was;
        }

        private void BuildBarOptions()
        {
            var order = InfoBar.BarIds;
            int at = order.IndexOf(_barId);
            if (_earlier != null) { _earlier.IsEnabled = at > 0; _earlier.Visibility = order.Count > 1 ? Visibility.Visible : Visibility.Collapsed; }
            if (_later != null) { _later.IsEnabled = at < order.Count - 1; _later.Visibility = order.Count > 1 ? Visibility.Visible : Visibility.Collapsed; }
            bool was = _loading;
            _loading = true;
            var bar = new InfoBar.Bar(_barId);
            _barOptions.Children.Clear();
            _edges.Clear();
            _sides.Clear();
            _barOptions.Children.Add(Choice("\uE81D", Lang.T("مكانه"), new[] { Lang.T("تحت"), Lang.T("فوق"), Lang.T("يسار"), Lang.T("يمين") },
                bar.Edge, v =>
                {
                    if (bar.MoveTo(v, bar.ScreenPoint ?? default, bar.Side)) return;
                    MessageBox.Show(Lang.T("ما في مساحة كافية على هالطرف. خلّيته مكانه."), "Wibn", MessageBoxButton.OK, MessageBoxImage.Information);
                    Dispatcher.BeginInvoke(() =>
                    {
                        _loading = true;
                        if (_edges.TryGetValue(bar.Edge, out var back)) back.IsChecked = true;
                        _loading = false;
                    });
                }, _edges));
            // Beside a piece of Wibn's taskbar (e.g. the clock's own bar): left or right. Both on the same side = side by side.
            _barOptions.Children.Add(Choice("\uE8AB", Lang.T("جهته جنب التاسك بار"), new[] { Lang.T("يسار"), Lang.T("يمين") },
                bar.Side, v => bar.Side = v, _sides));

            // What it shows
            _barOptions.Children.Add(new TextBlock { Text = Lang.T("وش يعرض"), Style = Res<Style>("RowTitle"), Margin = new Thickness(2, 14, 0, 2) });
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(-3, 4, -3, 0) };
            foreach (var item in InfoBar.Items)
            {
                var it = item;
                grid.Children.Add(Toggle(ItemIcon(it.Id), Lang.T(it.Name), Lang.T(it.Description), bar.Shows(it.Id), v => bar.SetShows(it.Id, v)));
            }
            _barOptions.Children.Add(grid);

            // Its widgets
            _barOptions.Children.Add(new TextBlock { Text = Lang.T("الويدجتس فيه"), Style = Res<Style>("RowTitle"), Margin = new Thickness(2, 14, 0, 2) });
            _barOptions.Children.Add(Note(Lang.T("كل ويدجت تشغّلها تطلع على التاسك بار نفسه بحجم يناسبه وتتحدث لحالها. كليك يمين عليها لخياراتها")));
            var widgets = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(-3, 4, -3, 0) };
            foreach (var kind in InfoBar.WidgetKinds)
            {
                var k = kind;
                widgets.Children.Add(Toggle(k.Icon, k.Name, k.Description, bar.HasWidget(k.Type), v => bar.SetWidget(k.Type, v)));
            }
            _barOptions.Children.Add(widgets);
            _loading = was;
        }

        /// <summary>The icon of each thing a bar can show.</summary>
        private static string ItemIcon(string id) => id switch
        {
            "clock" => "\uE917", "date" => "\uE787", "hijri" => "\uE8BF", "prayer" => "\uE916",
            "cpu" => "\uE9D9", "cputemp" or "gputemp" => "\uE9CA", "gpu" => "\uE7F4", "ram" => "\uE964",
            "net" => "\uE774", "disk" => "\uEDA2", _ => "\uE713",
        };

        private UIElement Choice(string icon, string title, string[] names, int selected, Action<int> set,
                                 System.Collections.Generic.Dictionary<int, RadioButton>? keep = null)
        {
            var bar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            string group = "ib_" + title;
            for (int i = 0; i < names.Length; i++)
            {
                int v = i;
                var r = new RadioButton { Style = Res<Style>("NavButton"), Content = names[i], GroupName = group, IsChecked = i == selected };
                r.Checked += (_, _) => { if (!_loading) set(v); };
                bar.Children.Add(r);
                if (keep != null) keep[i] = r;
            }
            var bg = new Border { Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x22)), CornerRadius = new CornerRadius(8), Padding = new Thickness(3), Child = bar };
            DockPanel.SetDock(bg, Dock.Right);
            var dock = new DockPanel();
            dock.Children.Add(bg);
            dock.Children.Add(new TextBlock { Text = icon, Style = Res<Style>("RowIcon") });
            dock.Children.Add(new TextBlock { Text = title, Style = Res<Style>("RowTitle"), VerticalAlignment = VerticalAlignment.Center });
            return new Border { Style = Res<Style>("Row"), Child = dock, Margin = new Thickness(0, 0, 0, 6) };
        }

        private Border Toggle(string icon, string title, string desc, bool on, Action<bool> set)
        {
            var box = new CheckBox { Style = Res<Style>("Switch"), IsChecked = on };
            box.Click += (_, _) => set(box.IsChecked == true);
            var row = Row(box, icon, title, desc);
            row.Margin = new Thickness(3, 0, 3, 6);
            return row;
        }

        private static TextBlock Note(string text) =>
            new() { Text = text, Style = Res<Style>("RowDesc"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 0, 8) };

        private static Border Row(UIElement control, string icon, string title, string desc)
        {
            DockPanel.SetDock(control, Dock.Right);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = title, Style = Res<Style>("RowTitle") });
            text.Children.Add(new TextBlock { Text = desc, Style = Res<Style>("RowDesc"), TextWrapping = TextWrapping.Wrap });
            var dock = new DockPanel();
            dock.Children.Add(control);
            dock.Children.Add(new TextBlock { Text = icon, Style = Res<Style>("RowIcon") });
            dock.Children.Add(text);
            return new Border { Style = Res<Style>("Row"), Child = dock, Margin = new Thickness(0, 0, 0, 6) };
        }
    }
}
