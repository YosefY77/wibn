using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// The liquid glass settings of one floating window (the download bar, or one of
    /// the round menus: convert, resize, compress). Each has its own.
    /// </summary>
    internal sealed class GlassSettings
    {
        private readonly string _key, _prefix;

        private GlassSettings(string key, string prefix) { _key = key; _prefix = prefix; }

        public static readonly GlassSettings Video = new(@"Software\WinPlus\Video", "Glass");

        public static GlassSettings Menu(MediaTools.Mode mode) =>
            new(@"Software\WinPlus\TaskbarScroll", mode switch
            {
                MediaTools.Mode.Resize => "ResizeGlass",
                MediaTools.Mode.Compress => "CompressGlass",
                MediaTools.Mode.Archive => "ArchiveGlass",
                _ => "ConvertGlass",
            });

        public bool On { get => Get("On", 1) != 0; set => Set("On", value ? 1 : 0); }
        /// <summary>0–100: how strongly the edges bend.</summary>
        public int Bend { get => Get("Bend", 55); set => Set("Bend", value); }
        /// <summary>0–100: colors splitting at the edges.</summary>
        public int Dispersion { get => Get("Dispersion", 35); set => Set("Dispersion", value); }
        /// <summary>0–12 px of blur.</summary>
        public int Frost { get => Get("Frost", 2); set => Set("Frost", value); }
        /// <summary>0–80 %: dark tint.</summary>
        public int Tint { get => Get("Tint", 34); set => Set("Tint", value); }
        /// <summary>0–100: rim shine.</summary>
        public int Rim { get => Get("Rim", 55); set => Set("Rim", value); }

        public void Reset()
        {
            using var key = Registry.CurrentUser.CreateSubKey(_key);
            foreach (var name in new[] { "On", "Bend", "Dispersion", "Frost", "Tint", "Rim" })
                key.DeleteValue(_prefix + name, false);
        }

        private int Get(string name, int fallback)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(_key);
                return key?.GetValue(_prefix + name) is int v ? v : fallback;
            }
            catch { return fallback; }
        }

        private void Set(string name, int value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(_key);
            key.SetValue(_prefix + name, value, RegistryValueKind.DWord);
        }

        // ---- The settings section on a page --------------------------------------------------

        private static Style St(string key) => (Style)Application.Current.FindResource(key);

        /// <summary>
        /// "التأثير الزجاجي": on/off, the five sliders and Default (and "Try it" when
        /// <paramref name="preview"/> is given).
        /// </summary>
        public static StackPanel Section(GlassSettings settings, Action? preview, string desc)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = Lang.T("التأثير الزجاجي"), Style = St("Section"), FontSize = 18, Margin = new Thickness(2, 24, 0, 8) });
            var grid = new UniformGrid { Columns = 2, Margin = new Thickness(-3, 0, -3, 0) };

            var on = new CheckBox { Style = St("Switch"), IsChecked = settings.On };
            on.Click += (_, _) => settings.On = on.IsChecked == true;
            grid.Children.Add(Row(on, "", Lang.T("زجاج سائل"), desc));

            var reset = new Button { Style = St("FlatButton"), Content = Lang.T("الافتراضي"), Padding = new Thickness(14, 5, 14, 5) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(reset);
            if (preview != null)
            {
                reset.Margin = new Thickness(0, 0, 8, 0);
                var tryIt = new Button { Style = St("AccentButton"), Content = Lang.T("جرّب"), Padding = new Thickness(14, 5, 14, 5) };
                tryIt.Click += (_, _) => preview();
                buttons.Children.Add(tryIt);
            }
            grid.Children.Add(Row(buttons, "", Lang.T("شوف الشكل"),
                Lang.T(preview != null ? "يفتح الشريط بالإعدادات الحالية" : "التغيير يبان من المرة الجاية اللي تفتح فيها القائمة")));

            var sliders = new List<(Slider slider, Func<int> get)>();
            void Add(string icon, string title, string text, int min, int max, Func<int> get, Action<int> set, string unit)
            {
                var value = new TextBlock
                {
                    Style = St("RowDesc"), Width = 44, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0), FlowDirection = FlowDirection.LeftToRight,
                };
                var slider = new Slider
                {
                    Minimum = min, Maximum = max, Value = get(), Width = 150, IsSnapToTickEnabled = true, TickFrequency = 1,
                    VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight,
                };
                value.Text = (int)slider.Value + unit;
                slider.ValueChanged += (_, e) => { set((int)e.NewValue); value.Text = (int)e.NewValue + unit; };
                var both = new StackPanel { Orientation = Orientation.Horizontal };
                both.Children.Add(slider);
                both.Children.Add(value);
                grid.Children.Add(Row(both, icon, title, text));
                sliders.Add((slider, get));
            }
            Add("", Lang.T("الانحناء"), Lang.T("قوة انحناء اللي وراه عند الأطراف"), 0, 100, () => settings.Bend, v => settings.Bend = v, "%");
            Add("", Lang.T("ألوان الحواف"), Lang.T("تفرّق الألوان عند الأطراف مثل المنشور"), 0, 100, () => settings.Dispersion, v => settings.Dispersion = v, "%");
            Add("", Lang.T("التغبيش"), Lang.T("كم يتغبش اللي ورا الزجاج"), 0, 12, () => settings.Frost, v => settings.Frost = v, "px");
            Add("", Lang.T("التغميق"), Lang.T("يغمّق الزجاج عشان الكتابة تبان"), 0, 80, () => settings.Tint, v => settings.Tint = v, "%");
            Add("", Lang.T("لمعة الإطار"), Lang.T("الخط اللامع على حافة الزجاج"), 0, 100, () => settings.Rim, v => settings.Rim = v, "%");

            reset.Click += (_, _) =>
            {
                settings.Reset();
                on.IsChecked = settings.On;
                foreach (var (slider, get) in sliders) slider.Value = get();
            };
            panel.Children.Add(grid);
            return panel;
        }

        private static Border Row(UIElement control, string icon, string title, object desc)
        {
            DockPanel.SetDock(control, Dock.Right);
            if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            text.Children.Add(new TextBlock { Text = title, Style = St("RowTitle") });
            text.Children.Add(desc as UIElement ?? new TextBlock { Text = desc as string ?? "", Style = St("RowDesc"), TextWrapping = TextWrapping.Wrap });
            var dock = new DockPanel();
            dock.Children.Add(control);
            dock.Children.Add(new TextBlock { Text = icon, Style = St("RowIcon") });
            dock.Children.Add(text);
            return new Border { Style = St("Row"), Margin = new Thickness(3, 0, 3, 6), Child = dock };
        }
    }
}
