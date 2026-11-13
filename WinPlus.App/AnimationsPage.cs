using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>The animations' settings (HKCU\Software\WinPlus\Animations).</summary>
    internal static class AnimSettings
    {
        private const string Key = @"Software\WinPlus\Animations";

        public const int DefaultIntro = 450, DefaultGap = 90, DefaultOpen = 170;

        /// <summary>Widgets show from the top down when they come onto the desktop.</summary>
        public static bool IntroOn { get => Get("IntroOn", 1) != 0; set => Set("IntroOn", value ? 1 : 0); }
        /// <summary>How long a widget takes to show (ms).</summary>
        public static int IntroMs { get => Math.Clamp(Get("IntroMs", DefaultIntro), 100, 3000); set => Set("IntroMs", value); }
        /// <summary>The wait between one widget and the next (ms).</summary>
        public static int GapMs { get => Math.Clamp(Get("GapMs", DefaultGap), 0, 1000); set => Set("GapMs", value); }
        /// <summary>How long the desktop's and the calendar's windows take to open (ms; 0 = at once). Closing takes two thirds of it.</summary>
        public static int OpenMs { get => Math.Clamp(Get("OpenMs", DefaultOpen), 0, 1000); set => Set("OpenMs", value); }

        public static TimeSpan Open => TimeSpan.FromMilliseconds(Math.Max(1, OpenMs));
        public static TimeSpan Close => TimeSpan.FromMilliseconds(Math.Max(1, OpenMs * 2 / 3));

        private static int Get(string name, int def)
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(Key); return k?.GetValue(name) is int v ? v : def; }
            catch { return def; }
        }

        private static void Set(string name, int value)
        {
            try { using var k = Registry.CurrentUser.CreateSubKey(Key); k.SetValue(name, value, RegistryValueKind.DWord); } catch { }
        }
    }

    /// <summary>The "Animations" page: how the widgets come onto the desktop, and how fast windows open.</summary>
    internal sealed class AnimationsPage : StackPanel
    {
        private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

        public AnimationsPage()
        {
            // ---- Widgets coming in ----
            Children.Add(Section(Lang.T("ظهور الويدجتات")));
            var on = new CheckBox { Style = Res<Style>("Switch"), IsChecked = AnimSettings.IntroOn };
            Children.Add(Row(on, "", Lang.T("حركة ظهور الويدجتات"),
                Lang.T("الويدجتات تطلع من فوق لتحت لما يشتغل Wibn أو تضيف ويدجت. بعد ما تخلص الحركة ما تاخذ شي من الرام")));

            var options = new StackPanel();
            var (introSlider, introRow) = SliderRow("", Lang.T("مدة الحركة"), Lang.T("كم ياخذ كل ويدجت لين يطلع كامل. أقل = أسرع"),
                100, 3000, AnimSettings.IntroMs, v => AnimSettings.IntroMs = v, Seconds);
            options.Children.Add(introRow);

            // Quick choices for the speed
            var presets = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 8) };
            foreach (var (name, ms) in new[] { ("سريعة", 250), ("عادية", AnimSettings.DefaultIntro), ("بطيئة", 900), ("بطيئة جدًا", 1600) })
            {
                var b = new Button { Style = Res<Style>("FlatButton"), Content = Lang.T(name), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
                int v = ms;
                b.Click += (_, _) => introSlider.Value = v;
                presets.Children.Add(b);
            }
            options.Children.Add(presets);

            options.Children.Add(SliderRow("", Lang.T("الفرق بين كل ويدجت"), Lang.T("الويدجتات تطلع وحدة ورا الثانية من أعلى الشاشة لتحت. 0 = كلها مع بعض"),
                0, 1000, AnimSettings.GapMs, v => AnimSettings.GapMs = v, Seconds).Row);

            var test = new Button { Style = Res<Style>("FlatButton"), Content = Lang.T("جرّبها الحين"), Padding = new Thickness(16, 7, 16, 7), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 4, 0, 0) };
            test.Click += (_, _) => Widgets.ReplayIntro();
            options.Children.Add(test);
            Children.Add(options);

            void ShowOptions() { options.IsEnabled = on.IsChecked == true; options.Opacity = options.IsEnabled ? 1 : 0.45; }
            on.Click += (_, _) => { AnimSettings.IntroOn = on.IsChecked == true; ShowOptions(); };
            ShowOptions();

            // ---- Windows opening ----
            Children.Add(Section(Lang.T("فتح النوافذ")));
            Children.Add(SliderRow("", Lang.T("سرعة فتح نافذة سطح المكتب والتقويم"),
                Lang.T("كم تاخذ النافذة لين تفتح (وتتسكّر بأسرع من كذا شوي). 0 = تطلع على طول بدون حركة"),
                0, 1000, AnimSettings.OpenMs, v => AnimSettings.OpenMs = v, Seconds).Row);

            var reset = new Button { Style = Res<Style>("FlatButton"), Content = Lang.T("رجّع الافتراضي"), Padding = new Thickness(16, 7, 16, 7), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 18, 0, 0) };
            reset.Click += (_, _) =>
            {
                AnimSettings.IntroOn = true;
                AnimSettings.IntroMs = AnimSettings.DefaultIntro;
                AnimSettings.GapMs = AnimSettings.DefaultGap;
                AnimSettings.OpenMs = AnimSettings.DefaultOpen;
                if (Parent is ContentControl host) host.Content = new AnimationsPage();   // the page again, with the defaults
            };
            Children.Add(reset);
        }

        private static string Seconds(int ms) =>
            ms == 0 ? Lang.T("بدون") : Lang.F("{0} ث", (ms / 1000.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));

        /// <summary>A row with a slider and its value; saves as it moves.</summary>
        private static (Slider Slider, Border Row) SliderRow(string icon, string title, string desc, int min, int max, int value, Action<int> save, Func<int, string> show)
        {
            var slider = new Slider
            {
                Minimum = min, Maximum = max, Value = value, Width = 240, VerticalAlignment = VerticalAlignment.Center,
                IsMoveToPointEnabled = true, SmallChange = 10, LargeChange = 50, TickFrequency = 10, IsSnapToTickEnabled = true,
            };
            var text = new TextBlock { Text = show(value), Style = Res<Style>("RowDesc"), MinWidth = 54, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            slider.ValueChanged += (_, _) =>
            {
                int v = (int)slider.Value;
                text.Text = show(v);
                save(v);
            };
            var box = new StackPanel { Orientation = Orientation.Horizontal, FlowDirection = Lang.Flow };
            box.Children.Add(slider);
            box.Children.Add(text);
            return (slider, Row(box, icon, title, desc));
        }

        private static TextBlock Section(string text) =>
            new() { Text = text, Style = Res<Style>("Section"), FontSize = 18, Margin = new Thickness(2, 24, 0, 8) };

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
            return new Border { Style = Res<Style>("Row"), Child = dock, Margin = new Thickness(0, 0, 0, 8) };
        }
    }
}
