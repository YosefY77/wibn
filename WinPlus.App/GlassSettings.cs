using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// The liquid glass of the floating windows (the download bar, the round menus:
    /// convert, resize, compress, archive, and the screen capture box). The look
    /// (bend, colors, frost, tint, rim) is shared by all of them and set in one place:
    /// the "Liquid Glass" page. Each one only has its own on/off switch there, and a
    /// main switch turns the glass off everywhere.
    /// </summary>
    internal sealed class GlassSettings
    {
        private const string SharedKey = @"Software\WinPlus\Glass";
        private readonly string _key, _prefix;

        private GlassSettings(string key, string prefix) { _key = key; _prefix = prefix; }

        public static readonly GlassSettings Video = new(@"Software\WinPlus\Video", "Glass");

        /// <summary>The box of "capture an area" (its bar of buttons is glass whenever the main switch is on).</summary>
        public static readonly GlassSettings Capture = new(@"Software\WinPlus\Capture", "Glass");

        /// <summary>The desktop widgets.</summary>
        public static readonly GlassSettings Widgets = new(@"Software\WinPlus\Widgets", "Glass");

        public static GlassSettings Menu(MediaTools.Mode mode) =>
            new(@"Software\WinPlus\TaskbarScroll", mode switch
            {
                MediaTools.Mode.Resize => "ResizeGlass",
                MediaTools.Mode.Compress => "CompressGlass",
                MediaTools.Mode.Archive => "ArchiveGlass",
                _ => "ConvertGlass",
            });

        // ---- Shared ------------------------------------------------------------------------------

        /// <summary>The main switch (and the feature itself): off = no glass anywhere.</summary>
        public static bool Master
        {
            get => Features.IsOn("glass") && GetShared("On", 1) != 0;
            set => SetShared("On", value ? 1 : 0);
        }

        private const int DefBend = 55, DefDispersion = 35, DefFrost = 2, DefTint = 34, DefRim = 55;

        /// <summary>0–100: how strongly the edges bend.</summary>
        public static int SharedBend { get => GetShared("Bend", DefBend); set => SetShared("Bend", value); }
        /// <summary>0–100: colors splitting at the edges.</summary>
        public static int SharedDispersion { get => GetShared("Dispersion", DefDispersion); set => SetShared("Dispersion", value); }
        /// <summary>0–12 px of blur.</summary>
        public static int SharedFrost { get => GetShared("Frost", DefFrost); set => SetShared("Frost", value); }
        /// <summary>0–80 %: dark tint.</summary>
        public static int SharedTint { get => GetShared("Tint", DefTint); set => SetShared("Tint", value); }
        /// <summary>0–100: rim shine.</summary>
        public static int SharedRim { get => GetShared("Rim", DefRim); set => SetShared("Rim", value); }

        public static void ResetShared()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(SharedKey);
                foreach (var name in new[] { "Bend", "Dispersion", "Frost", "Tint", "Rim" })
                    key.DeleteValue(name, false);
            }
            catch { }
        }

        private static int GetShared(string name, int fallback)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SharedKey);
                return key?.GetValue(name) is int v ? v : fallback;
            }
            catch { return fallback; }
        }

        private static void SetShared(string name, int value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(SharedKey);
            key.SetValue(name, value, RegistryValueKind.DWord);
        }

        // ---- This one ----------------------------------------------------------------------------

        /// <summary>This one's own switch (ignores the main switch).</summary>
        public bool Enabled
        {
            get
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(_key);
                    return key?.GetValue(_prefix + "On") is not int v || v != 0;
                }
                catch { return true; }
            }
            set
            {
                using var key = Registry.CurrentUser.CreateSubKey(_key);
                key.SetValue(_prefix + "On", value ? 1 : 0, RegistryValueKind.DWord);
            }
        }

        /// <summary>Glass for this one: the main switch and its own switch are both on.</summary>
        public bool On => Master && Enabled;
        public int Bend => SharedBend;
        public int Dispersion => SharedDispersion;
        public int Frost => SharedFrost;
        public int Tint => SharedTint;
        public int Rim => SharedRim;

        // ---- The Liquid Glass page ---------------------------------------------------------------

        private static Style St(string key) => (Style)Application.Current.FindResource(key);

        /// <summary>The content of the "Liquid Glass" page: the main switch, where it shows, and the shared look.</summary>
        public static StackPanel Page()
        {
            var panel = new StackPanel();
            var where = new List<CheckBox>();

            // ---- Main switch
            var master = new CheckBox { Style = St("Switch"), IsChecked = GetShared("On", 1) != 0 };
            master.Click += (_, _) =>
            {
                Master = master.IsChecked == true;
                foreach (var c in where) c.IsEnabled = master.IsChecked == true;
                WinPlus.Widgets.RefreshGlass();
            };
            panel.Children.Add(Row(master, "", Lang.T("زجاج سائل"),
                Lang.T("يشغّل أو يطفّي التأثير الزجاجي في كل الميزات مرة وحدة"), new Thickness(0, 0, 0, 6)));

            // ---- Where it shows
            panel.Children.Add(Section(Lang.T("وين يطلع")));
            var grid = new UniformGrid { Columns = 2, Margin = new Thickness(-3, 0, -3, 0) };
            void Where(GlassSettings s, string icon, string title, string desc)
            {
                var box = new CheckBox { Style = St("Switch"), IsChecked = s.Enabled, IsEnabled = master.IsChecked == true };
                box.Click += (_, _) => { s.Enabled = box.IsChecked == true; WinPlus.Widgets.RefreshGlass(); };
                where.Add(box);
                grid.Children.Add(Row(box, icon, title, desc));
            }
            string disc = Lang.T("الدائرة تعرض اللي تحتها منحني عند حافتها مثل زجاج أبل");
            Where(Video, "", Lang.T("شريط تحميل المقاطع"), Lang.T("الشريط يعرض اللي وراه منحني عند الأطراف مثل زجاج أبل"));
            Where(Capture, "", Lang.T("أزرار تصوير الشاشة"), Lang.T("أزرار الحفظ والنسخ زجاجية. مربع التحديد يبقى عادي بخط أبيض"));
            Where(Widgets, "\uE8A9", "Widgets", Lang.T("الويدجتات اللي على سطح المكتب تصير زجاج يبين خلفيتك منحنية عند الأطراف"));
            Where(Menu(MediaTools.Mode.Convert), "", Lang.T("قائمة تحويل الصيغ"), disc);
            Where(Menu(MediaTools.Mode.Resize), "", Lang.T("قائمة تغيير الأبعاد"), disc);
            Where(Menu(MediaTools.Mode.Compress), "", Lang.T("قائمة ضغط الملفات"), disc);
            Where(Menu(MediaTools.Mode.Archive), "", Lang.T("قائمة الملف المضغوط"), disc);
            panel.Children.Add(grid);

            // ---- The look (shared)
            panel.Children.Add(Section(Lang.T("الشكل")));
            grid = new UniformGrid { Columns = 2, Margin = new Thickness(-3, 0, -3, 0) };
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
                slider.ValueChanged += (_, e) => { set((int)e.NewValue); value.Text = (int)e.NewValue + unit; WinPlus.Widgets.RefreshGlass(); };
                var both = new StackPanel { Orientation = Orientation.Horizontal };
                both.Children.Add(slider);
                both.Children.Add(value);
                grid.Children.Add(Row(both, icon, title, text));
                sliders.Add((slider, get));
            }
            Add("", Lang.T("الانحناء"), Lang.T("قوة انحناء اللي وراه عند الأطراف"), 0, 100, () => SharedBend, v => SharedBend = v, "%");
            Add("", Lang.T("ألوان الحواف"), Lang.T("تفرّق الألوان عند الأطراف مثل المنشور"), 0, 100, () => SharedDispersion, v => SharedDispersion = v, "%");
            Add("", Lang.T("التغبيش"), Lang.T("كم يتغبش اللي ورا الزجاج"), 0, 12, () => SharedFrost, v => SharedFrost = v, "px");
            Add("", Lang.T("التغميق"), Lang.T("يغمّق الزجاج عشان الكتابة تبان"), 0, 80, () => SharedTint, v => SharedTint = v, "%");
            Add("", Lang.T("لمعة الإطار"), Lang.T("الخط اللامع على حافة الزجاج"), 0, 100, () => SharedRim, v => SharedRim = v, "%");

            var reset = new Button { Style = St("FlatButton"), Content = Lang.T("الافتراضي"), Padding = new Thickness(14, 5, 14, 5) };
            reset.Click += (_, _) =>
            {
                ResetShared();
                WinPlus.Widgets.RefreshGlass();
                foreach (var (slider, get) in sliders) slider.Value = get();
            };
            grid.Children.Add(Row(reset, "", Lang.T("الشكل الافتراضي"), Lang.T("يرجّع كل الإعدادات اللي فوق مثل ما كانت")));
            panel.Children.Add(grid);

            // ---- Try it
            panel.Children.Add(Section(Lang.T("جرّب")));
            grid = new UniformGrid { Columns = 2, Margin = new Thickness(-3, 0, -3, 0) };
            var tryBar = new Button { Style = St("AccentButton"), Content = Lang.T("جرّب"), Padding = new Thickness(14, 5, 14, 5) };
            tryBar.Click += (_, _) => VideoDownloader.ShowBar();
            grid.Children.Add(Row(tryBar, "", Lang.T("شريط تحميل المقاطع"), Lang.T("يفتح الشريط بالإعدادات الحالية")));
            var tryCapture = new Button { Style = St("AccentButton"), Content = Lang.T("جرّب"), Padding = new Thickness(14, 5, 14, 5) };
            tryCapture.Click += (_, _) => ScreenCapture.Run(ScreenCapture.Kind.Region);
            grid.Children.Add(Row(tryCapture, "", Lang.T("تحديد مكان التصوير"), Lang.T("يفتح تحديد المكان بالإعدادات الحالية. Esc يسكّره")));
            panel.Children.Add(grid);
            return panel;
        }

        private static TextBlock Section(string text) =>
            new() { Text = text, Style = St("Section"), FontSize = 18, Margin = new Thickness(2, 24, 0, 8) };

        private static Border Row(UIElement control, string icon, string title, object desc, Thickness? margin = null)
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
            return new Border { Style = St("Row"), Margin = margin ?? new Thickness(3, 0, 3, 6), Child = dock };
        }
    }
}
