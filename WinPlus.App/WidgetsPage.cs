using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinPlus
{
    /// <summary>The Widgets page: the widgets you can add, and the ones on the desktop.</summary>
    internal sealed class WidgetsPage : StackPanel
    {
        private static Style St(string key) => (Style)Application.Current.FindResource(key);
        private readonly StackPanel _onDesktop = new();
        private readonly TextBlock _none;

        public WidgetsPage()
        {
            Children.Add(new TextBlock { Style = St("Section"), FontSize = 18, Margin = new Thickness(2, 8, 0, 8), Text = Lang.T("أضف ويدجت") });
            foreach (var kind in Widgets.Kinds)
            {
                var add = new Button { Style = St("AccentButton"), Content = Lang.T("أضف لسطح المكتب"), VerticalAlignment = VerticalAlignment.Center };
                add.Click += (_, _) => Widgets.Add(kind.Type);
                Children.Add(Row(add, kind.Icon, kind.Name, kind.Description));
            }

            Children.Add(new TextBlock { Style = St("Section"), FontSize = 18, Margin = new Thickness(2, 24, 0, 8), Text = Lang.T("شكل الويدجت") });
            AddBackColor();
            AddFont();

            Children.Add(new TextBlock { Style = St("Section"), FontSize = 18, Margin = new Thickness(2, 24, 0, 8), Text = Lang.T("على سطح المكتب") });
            _none = new TextBlock { Style = St("RowDesc"), FontSize = 13, Margin = new Thickness(2, 0, 0, 0), Text = Lang.T("ما فيه ويدجت على سطح المكتب للحين") };
            Children.Add(_none);
            Children.Add(_onDesktop);

            Children.Add(new TextBlock
            {
                Style = St("RowDesc"), Margin = new Thickness(2, 16, 0, 0),
                Text = Lang.T("اسحب الويدجت من أي مكان فاضي فيه ويتحاذى مع أيقونات سطح المكتب. اسحب زاويته تحت عشان تغيّر حجمه من 2×2 إلى 6×6 أيقونات. كليك يمين عليه للحجم أو تشيله."),
            });

            Loaded += (_, _) => { Widgets.Changed += OnChanged; Refresh(); };
            Unloaded += (_, _) => Widgets.Changed -= OnChanged;
        }

        /// <summary>The color of every widget's card (not its text): default, the taskbar's, or one
        /// picked here. Changes show on the desktop as you pick.</summary>
        private void AddBackColor()
        {
            var box = new ComboBox { Width = 190, VerticalAlignment = VerticalAlignment.Center };
            box.Items.Add(new ComboBoxItem { Content = Lang.T("الافتراضي") });
            box.Items.Add(new ComboBoxItem { Content = Lang.T("نفس لون التاسك بار") });
            box.Items.Add(new ComboBoxItem { Content = Lang.T("لون أختاره") });
            box.SelectedIndex = (int)Widgets.Back;
            Children.Add(Row(box, "", Lang.T("لون الويدجت"),
                Lang.T("لون خلفية الويدجتات كلها. لون الكتابة يتغير لحاله عشان تبان")));

            var picker = new ColorPicker { Color = Widgets.BackCustom };
            var pickerRow = new Border
            {
                Style = St("Row"), Margin = new Thickness(0, 0, 0, 6), Child = picker,
                Visibility = Widgets.Back == Widgets.BackMode.Custom ? Visibility.Visible : Visibility.Collapsed,
            };
            Children.Add(pickerRow);

            // Redoing the glass of every widget is not free: wait for the picker to settle a little.
            var apply = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(150) };
            uint pending = Widgets.BackCustom;
            apply.Tick += (_, _) =>
            {
                apply.Stop();
                Widgets.BackCustom = pending;
                Widgets.RefreshGlass();
            };
            picker.ColorChanged += argb => { pending = argb; apply.Stop(); apply.Start(); };

            box.SelectionChanged += (_, _) =>
            {
                Widgets.Back = (Widgets.BackMode)box.SelectedIndex;
                pickerRow.Visibility = Widgets.Back == Widgets.BackMode.Custom ? Visibility.Visible : Visibility.Collapsed;
                Widgets.RefreshGlass();
            };
        }

        /// <summary>Fonts that look good on widgets, first (the ones on this PC), then every other font.</summary>
        public static readonly string[] Picks =
        {
            "Segoe UI", "Segoe UI Variable Display", "Segoe UI Semibold", "Segoe UI Light", "Bahnschrift",
            "Tahoma", "Arial", "Calibri", "Candara", "Corbel", "Trebuchet MS", "Verdana",
            "Sakkal Majalla", "Traditional Arabic", "Simplified Arabic", "Arabic Typesetting", "Aldhabi", "Urdu Typesetting",
            "Georgia", "Cambria", "Constantia", "Palatino Linotype",
            "Cascadia Code", "Consolas", "Segoe Print", "Segoe Script", "Ink Free", "Comic Sans MS",
        };

        private void AddFont()
        {
            var installed = Fonts.SystemFontFamilies
                .Select(f => f.Source)
                .Where(n => !n.Contains("Symbol") && !n.Contains("MDL2") && !n.Contains("Fluent Icons") && !n.StartsWith("Webdings") && !n.StartsWith("Wingdings") && !n.StartsWith("Marlett"))
                .Distinct(System.StringComparer.OrdinalIgnoreCase)
                .ToList();
            var first = Picks.Where(p => installed.Contains(p, System.StringComparer.OrdinalIgnoreCase)).ToList();
            var rest = installed.Except(first, System.StringComparer.OrdinalIgnoreCase).OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase).ToList();

            // Each name in its own font, so you see it before you pick it. Hundreds of fonts: only
            // the ones on screen in the list are made.
            var box = new ComboBox { Width = 190, VerticalAlignment = VerticalAlignment.Center, MaxDropDownHeight = 420 };
            box.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
            VirtualizingPanel.SetIsVirtualizing(box, true);
            ScrollViewer.SetCanContentScroll(box, true);
            ComboBoxItem Item(string name) => new() { Content = name, Tag = name, FontFamily = new FontFamily(name), FontSize = 14 };
            foreach (var n in first) box.Items.Add(Item(n));
            if (rest.Count > 0) box.Items.Add(new Separator());
            foreach (var n in rest) box.Items.Add(Item(n));

            string now = Widgets.FontName;
            box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, now, System.StringComparison.OrdinalIgnoreCase))
                               ?? box.Items.OfType<ComboBoxItem>().FirstOrDefault();
            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedItem is ComboBoxItem i) Widgets.FontName = (string)i.Tag;
            };

            var reset = new Button { Style = St("FlatButton"), Content = Lang.T("الافتراضي"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            reset.Click += (_, _) =>
                box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == Widgets.DefaultFont);
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            controls.Children.Add(reset);
            controls.Children.Add(box);
            Children.Add(Row(controls, "", Lang.T("الخط"),
                Lang.T("خط الكتابة في الويدجتات كلها. الحروف اللي مو موجودة في الخط (مثل العربي في بعض الخطوط الإنجليزية) تطلع بخط ويندوز")));
        }

        private void OnChanged() => Dispatcher.BeginInvoke(Refresh);

        private void Refresh()
        {
            _onDesktop.Children.Clear();
            foreach (var info in Widgets.All.ToList())
            {
                var kind = Widgets.KindOf(info.Type);
                if (kind == null) continue;
                var remove = new Button { Style = St("FlatButton"), Content = Lang.T("شيله"), VerticalAlignment = VerticalAlignment.Center };
                string id = info.Id;
                remove.Click += (_, _) => Widgets.Remove(id);
                _onDesktop.Children.Add(Row(remove, kind.Icon, kind.Name, $"{info.W} × {info.H}"));
            }
            _none.Visibility = _onDesktop.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static Border Row(FrameworkElement control, string icon, string title, string desc)
        {
            var dock = new DockPanel();
            DockPanel.SetDock(control, Dock.Right);
            dock.Children.Add(control);
            var glyph = new TextBlock { Style = St("RowIcon"), Text = icon };
            DockPanel.SetDock(glyph, Dock.Left);
            dock.Children.Add(glyph);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            text.Children.Add(new TextBlock { Style = St("RowTitle"), Text = title });
            text.Children.Add(new TextBlock { Style = St("RowDesc"), Text = desc });
            dock.Children.Add(text);
            return new Border { Style = St("Row"), Margin = new Thickness(0, 0, 0, 6), Child = dock };
        }
    }
}
