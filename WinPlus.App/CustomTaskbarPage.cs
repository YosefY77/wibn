using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinPlus
{
    /// <summary>The "Wibn taskbar" part at the top of the Taskbar page: its switch and how it looks.</summary>
    internal sealed class CustomTaskbarPage : StackPanel
    {
        private readonly CheckBox _main;
        private readonly StackPanel _options = new();
        private readonly System.Windows.Controls.Primitives.UniformGrid _tray = new() { Columns = 2, Margin = new Thickness(-3, 0, -3, 0) };
        private bool _loading;

        private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

        public CustomTaskbarPage()
        {
            Children.Add(Section(Lang.T("تاسك بار Wibn")));

            _main = new CheckBox { Style = Res<Style>("Switch"), IsChecked = CustomTaskbar.Enabled };
            _main.Click += (_, _) =>
            {
                CustomTaskbar.Enabled = _main.IsChecked == true;
            };
            Children.Add(Row(_main, "", Lang.T("تاسك بار خاص بدل تاسك بار ويندوز"),
                Lang.T("يخفي تاسك بار ويندوز ويحط بداله تاسك بار تقدر تغيّر شكله كله. ويندوز نفسه ما يتغيّر فيه شي، وتاسك باره يرجع لما تطفيه أو يتسكّر Wibn")));

            Children.Add(_options);
            _options.Children.Add(Note(Lang.T("اسحب برنامج أو اختصار على التاسك بار عشان تثبّته، واسحب الأيقونات المثبتة عشان ترتبها.")));

            // Shape
            _options.Children.Add(Choice(Lang.T("الشكل"), new[] { Lang.T("كامل"), Lang.T("عائم"), Lang.T("دوك بالنص") },
                CustomTaskbar.Shape, v => CustomTaskbar.Shape = v));
            _options.Children.Add(Choice(Lang.T("مكان الأيقونات"), new[] { Lang.T("بالنص"), Lang.T("بالبداية") },
                CustomTaskbar.Align, v => CustomTaskbar.Align = v));
            _options.Children.Add(Choice(Lang.T("السماكة"), new[] { Lang.T("مثل ويندوز"), Lang.T("أمتن"), Lang.T("أمتن أكثر") },
                CustomTaskbar.Thickness, v => CustomTaskbar.Thickness = v));
            _options.Children.Add(Choice(Lang.T("حجم الأيقونات"), new[] { Lang.T("صغير"), Lang.T("وسط"), Lang.T("كبير") },
                CustomTaskbar.IconSize, v => CustomTaskbar.IconSize = v));
            Border? glassRow = null;
            FrameworkElement? strengthRow = null;
            void ShowFor(int look)
            {
                // The color is for every look; how strong it is: see-through only (glass takes it from Liquid Glass).
                if (strengthRow != null) strengthRow.Visibility = look == 2 ? Visibility.Visible : Visibility.Collapsed;
                if (glassRow != null) glassRow.Visibility = look == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            _options.Children.Add(Choice(Lang.T("الخلفية"), new[] { Lang.T("زجاج"), Lang.T("لون"), Lang.T("شفاف") },
                CustomTaskbar.Look, v => { CustomTaskbar.Look = v; ShowFor(v); }));

            // Glass: its look comes from the Liquid Glass page, like everywhere else in Wibn.
            var openGlass = new Button { Style = Res<Style>("FlatButton"), Content = Lang.T("إعدادات الزجاج"), Padding = new Thickness(14, 5, 14, 5) };
            openGlass.Click += (_, _) => (Application.Current as App)?.ShowPage("glass");
            glassRow = Row(openGlass, "\uE790", Lang.T("الزجاج من صفحة Liquid Glass"),
                Lang.T("التغبيش والانحناء والتغميق نفس اللي حاطه في صفحة Liquid Glass، ولونه لون التاسك بار اللي تحت (مثل الويدجتات)"));
            _options.Children.Add(glassRow);

            // Color and how strong it is
            var picker = new ColorPicker { Width = 320, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 4, 0, 0) };
            picker.Color = 0xFF000000u | (uint)CustomTaskbar.BackColor;
            // The bar is rebuilt a little after the last change, not on every step of a drag.
            var later = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            Action? pending = null;
            later.Tick += (_, _) => { later.Stop(); pending?.Invoke(); pending = null; };
            void Soon(Action a) { pending = a; later.Stop(); later.Start(); }
            picker.ColorChanged += c => { if (!_loading) Soon(() => CustomTaskbar.BackColor = (int)(c & 0xFFFFFF)); };
            var slider = new Slider { Minimum = 0, Maximum = 100, Value = CustomTaskbar.Opacity, Width = 260, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true };
            var percent = new TextBlock { Text = $"{CustomTaskbar.Opacity}%", Style = Res<Style>("RowDesc"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            slider.ValueChanged += (_, _) =>
            {
                percent.Text = $"{(int)slider.Value}%";
                int v = (int)slider.Value;
                if (!_loading) Soon(() => CustomTaskbar.Opacity = v);
            };
            var strength = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 8, 0, 0) };
            strength.Children.Add(new TextBlock { Text = Lang.T("قوة اللون (للشفاف)"), Style = Res<Style>("RowTitle"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            strength.Children.Add(slider);
            strength.Children.Add(percent);
            var colorBox = new StackPanel();
            colorBox.Children.Add(new TextBlock { Text = Lang.T("لون التاسك بار"), Style = Res<Style>("RowTitle"), Margin = new Thickness(2, 10, 0, 0) });
            colorBox.Children.Add(picker);
            colorBox.Children.Add(strength);
            strengthRow = strength;
            _options.Children.Add(new Border { Style = Res<Style>("Row"), Child = colorBox });
            ShowFor(CustomTaskbar.Look);

            // Switches, in three groups (they were one long list).
            _options.Children.Add(new TextBlock { Text = Lang.T("الأزرار"), Style = Res<Style>("RowTitle"), Margin = new Thickness(2, 14, 0, 2) });
            var buttons = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(-3, 2, -3, 0) };
            buttons.Children.Add(Toggle("\uE782", Lang.T("زر ويندوز (ستارت)"), Lang.T("الشعار اللي يفتح قائمة ابدأ. بدونه تفتحها من زر الويندوز في الكيبورد"), CustomTaskbar.StartButton, v => CustomTaskbar.StartButton = v));
            buttons.Children.Add(Toggle("", Lang.T("زر البحث (العدسة)"), Lang.T("يفتح بحث ويندوز"), CustomTaskbar.SearchButton, v => CustomTaskbar.SearchButton = v));
            buttons.Children.Add(Toggle("", Lang.T("زر عرض المهام"), Lang.T("كل النوافذ وأسطح المكتب"), CustomTaskbar.TaskViewButton, v => CustomTaskbar.TaskViewButton = v));
            buttons.Children.Add(Toggle("", Lang.T("زاوية سطح المكتب"), Lang.T("كليك على طرف التاسك بار يصغّر كل النوافذ"), CustomTaskbar.DesktopCorner, v => CustomTaskbar.DesktopCorner = v));
            _options.Children.Add(buttons);
            _options.Children.Add(new TextBlock { Text = Lang.T("الشكل والساعة"), Style = Res<Style>("RowTitle"), Margin = new Thickness(2, 14, 0, 2) });
            var look = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(-3, 2, -3, 0) };
            look.Children.Add(Toggle("", Lang.T("أسماء البرامج"), Lang.T("اسم النافذة جنب أيقونتها"), CustomTaskbar.Labels, v => CustomTaskbar.Labels = v));
            look.Children.Add(Toggle("", Lang.T("أيقونات بيضاء"), Lang.T("كل الأيقونات بالأبيض وشفافة شوي، شكل موحّد وهادي"), CustomTaskbar.WhiteIcons, v => CustomTaskbar.WhiteIcons = v));
            look.Children.Add(Toggle("", Lang.T("التاريخ تحت الساعة"), Lang.T("بنفس صيغة ويندوز"), CustomTaskbar.ShowDate, v => CustomTaskbar.ShowDate = v));
            look.Children.Add(Toggle("", Lang.T("الثواني في الساعة"), Lang.T("مثل 10:42:07، في الساعة وفي التاسك بار الإضافي"), CustomTaskbar.Seconds, v => CustomTaskbar.Seconds = v));
            look.Children.Add(Toggle("\uE8A0", Lang.T("الساعة في تاسك بار منفصل"), Lang.T("البرامج في تاسك بار، والساعة واللي حولها في تاسك بار ثاني لحاله"), CustomTaskbar.Split, v => CustomTaskbar.Split = v));
            _options.Children.Add(look);
            _options.Children.Add(new TextBlock { Text = Lang.T("الإخفاء"), Style = Res<Style>("RowTitle"), Margin = new Thickness(2, 14, 0, 2) });
            var hiding = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(-3, 2, -3, 0) };
            hiding.Children.Add(Toggle("", Lang.T("يختفي مع ملء الشاشة والتكبير"), Lang.T("لما تكبّر نافذة أو يكون برنامج أو لعبة بملء الشاشة. ودّ الماوس لآخر الشاشة تحت ويطلع"), CustomTaskbar.HideInFullscreen, v => CustomTaskbar.HideInFullscreen = v));
            hiding.Children.Add(Toggle("", Lang.T("يختفي لما تكون نافذة تحته"), Lang.T("الشريط اللي فوق النافذة بس ينسحب عشان تبان. الصق الماوس على الحافة فوقه ويطلع"), CustomTaskbar.HideOverWindow, v => CustomTaskbar.HideOverWindow = v));
            hiding.Children.Add(Toggle("", Lang.T("بالألعاب يطلع بزر ويندوز بس"), Lang.T("وأنت تلعب، الماوس تحت ما يطلّع التاسك بار. يطلع بس لما تضغط زر الويندوز"), CustomTaskbar.GamesKeyOnly, v => CustomTaskbar.GamesKeyOnly = v));
            _options.Children.Add(hiding);

            // The Start button's icon and color
            _options.Children.Add(StartSection(Soon));

            // The icons next to the clock: which ones show on the bar (the rest are under the arrow).
            _options.Children.Add(new TextBlock { Text = Lang.T("الأيقونات اللي جنب الساعة"), Style = Res<Style>("RowTitle"), Margin = new Thickness(2, 14, 0, 2) });
            _options.Children.Add(Note(Lang.T("شغّل اللي تبيه يبان على التاسك بار، والباقي يكون تحت السهم.")));
            _options.Children.Add(_tray);
            FillTray();
            TrayHost.Changed += () => Dispatcher.BeginInvoke(FillTray);

            _options.Children.Add(Note(Lang.T("لما يكون شغّال، الخيارات اللي تحت تخص تاسك بار ويندوز (المخفي).")));

            CustomTaskbar.Changed += () => Dispatcher.BeginInvoke(() => { _loading = true; _main.IsChecked = CustomTaskbar.Enabled; _loading = false; });
        }

        private readonly WrapPanel _startIcons = new() { Margin = new Thickness(0, 6, 0, 0) };

        /// <summary>The Start button: pick its icon (built-in or a picture) and its color.</summary>
        private Border StartSection(Action<Action> soon)
        {
            var box = new StackPanel();
            box.Children.Add(new TextBlock { Text = Lang.T("شكل زر ويندوز (ستارت)"), Style = Res<Style>("RowTitle"), Margin = new Thickness(2, 4, 0, 0) });
            box.Children.Add(new TextBlock { Text = Lang.T("اختر أيقونة، أو حط صورة من جهازك. اللون يتغيّر على الأيقونات، والصور تبقى بألوانها."), Style = Res<Style>("RowDesc"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 0) });
            box.Children.Add(_startIcons);
            FillStartIcons();

            var add = new Button { Style = Res<Style>("FlatButton"), Content = Lang.T("صورة من جهازك…"), Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(2, 4, 8, 0) };
            add.Click += (_, _) => PickStartImage();
            var reset = new Button { Style = Res<Style>("FlatButton"), Content = Lang.T("اللون الأصلي"), Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 4, 0, 0) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(add);
            buttons.Children.Add(reset);
            box.Children.Add(buttons);

            box.Children.Add(new TextBlock { Text = Lang.T("لون الأيقونة"), Style = Res<Style>("RowTitle"), Margin = new Thickness(2, 12, 0, 0) });
            var picker = new ColorPicker { Width = 320, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 4, 0, 0) };
            int color = CustomTaskbar.StartColor;
            picker.Color = 0xFF000000u | (uint)(color < 0 ? 0x0078D4 : color);
            picker.ColorChanged += c =>
            {
                if (_loading) return;
                soon(() => { CustomTaskbar.StartColor = (int)(c & 0xFFFFFF); FillStartIcons(); });
            };
            reset.Click += (_, _) =>
            {
                CustomTaskbar.StartColor = -1;
                _loading = true; picker.Color = 0xFF0078D4; _loading = false;
                FillStartIcons();
            };
            box.Children.Add(picker);
            return new Border { Style = Res<Style>("Row"), Child = box, Margin = new Thickness(0, 6, 0, 6) };
        }

        private void FillStartIcons()
        {
            _startIcons.Children.Clear();
            int selected = CustomTaskbar.StartIcon, color = CustomTaskbar.StartColor;
            var choices = new System.Collections.Generic.List<int> { 0, 1 };
            for (int i = 0; i < StartLogo.Glyphs.Length; i++) choices.Add(i + 2);
            if (StartLogo.HasImage) choices.Add(-1);
            foreach (int icon in choices)
            {
                int v = icon;
                bool on = v == selected;
                var tile = new Border
                {
                    Width = 44, Height = 44, CornerRadius = new CornerRadius(8), Margin = new Thickness(2, 2, 6, 6),
                    Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x22)),
                    BorderThickness = new Thickness(2),
                    BorderBrush = on ? new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)) : Brushes.Transparent,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Child = new Viewbox { Width = 24, Height = 24, Child = StartLogo.Build(v, color, 24) },
                    ToolTip = v switch { 0 => Lang.T("شعار ويندوز"), 1 => Lang.T("شعار Wibn"), -1 => Lang.T("صورتك"), _ => null },
                };
                tile.MouseLeftButtonUp += (_, _) => { CustomTaskbar.StartIcon = v; FillStartIcons(); };
                _startIcons.Children.Add(tile);
            }
        }

        private void PickStartImage()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = Lang.T("اختر صورة لزر ستارت"),
                Filter = Lang.T("صور") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico;*.webp;*.tif;*.tiff",
            };
            if (dlg.ShowDialog() != true) return;
            if (!StartLogo.SaveImage(dlg.FileName, out string error))
            {
                MessageBox.Show(Lang.T("ما قدرت أفتح الصورة: ") + error, "Wibn", MessageBoxButton.OK, MessageBoxImage.Warning,
                                MessageBoxResult.OK, Lang.MsgOptions);
                return;
            }
            CustomTaskbar.StartIcon = -1;   // rebuilds the bars, so the new picture shows
            FillStartIcons();
        }

        private void FillTray()
        {
            _tray.Children.Clear();
            if (!TrayHost.Active)
            {
                _tray.Children.Add(new TextBlock { Text = Lang.T("تطلع هنا لما يكون تاسك بار Wibn شغّال."), Style = Res<Style>("RowDesc"), Margin = new Thickness(5, 0, 0, 6) });
                return;
            }
            var seen = new System.Collections.Generic.HashSet<string>();
            foreach (var icon in TrayHost.Icons)
            {
                if (icon.Hidden || icon.Image == null || !seen.Add(icon.Key)) continue;
                var i = icon;
                var box = new CheckBox { Style = Res<Style>("Switch"), IsChecked = TrayHost.OnBar(i) };
                box.Click += (_, _) => TrayHost.SetOnBar(i, box.IsChecked == true);
                string name = string.IsNullOrWhiteSpace(i.Tip) ? System.IO.Path.GetFileNameWithoutExtension(i.Exe) : i.Tip.Split('\n')[0];
                var img = new Image { Source = i.Image, Width = 18, Height = 18, Margin = new Thickness(4, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(box, Dock.Right);
                var dock = new DockPanel();
                dock.Children.Add(box);
                dock.Children.Add(img);
                dock.Children.Add(new TextBlock { Text = name, Style = Res<Style>("RowTitle"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                _tray.Children.Add(new Border { Style = Res<Style>("Row"), Child = dock, Margin = new Thickness(3, 0, 3, 6) });
            }
        }

        private UIElement Choice(string title, string[] names, int selected, Action<int> set)
        {
            var bar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            string group = "ct_" + title;
            for (int i = 0; i < names.Length; i++)
            {
                int v = i;
                var r = new RadioButton { Style = Res<Style>("NavButton"), Content = names[i], GroupName = group, IsChecked = i == selected };
                r.Checked += (_, _) => { if (!_loading) set(v); };
                bar.Children.Add(r);
            }
            var bg = new Border { Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x22)), CornerRadius = new CornerRadius(8), Padding = new Thickness(3), Child = bar };
            DockPanel.SetDock(bg, Dock.Right);
            var dock = new DockPanel();
            dock.Children.Add(bg);
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

        private static TextBlock Section(string text) =>
            new() { Text = text, Style = Res<Style>("Section"), FontSize = 18, Margin = new Thickness(2, 24, 0, 8) };

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
