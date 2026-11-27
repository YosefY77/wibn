using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// "What's new": the first time a new version runs, a window lists everything in it.
    /// The list is WhatsNew.txt (Arabic) / WhatsNew.en.txt (English), built into the exe;
    /// release.ps1 also puts it on GitHub as the release's notes.
    /// </summary>
    internal static class WhatsNew
    {
        private const string OurKey = @"Software\WinPlus\TaskbarScroll";
        private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF5));
        private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0xA0, 0xA3, 0xAA));
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));

        /// <summary>The changes in this version, one per line (empty: nothing written).</summary>
        public static string[] Items()
        {
            string text = Read(Lang.English ? "WinPlus.WhatsNew.en.txt" : "WinPlus.WhatsNew.txt");
            if (text.Length == 0) text = Read("WinPlus.WhatsNew.txt");
            return text.Split('\n')
                .Select(l => l.Trim().TrimStart('-', '*', '•').Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("#"))
                .ToArray();
        }

        private static string Read(string name)
        {
            try
            {
                using var s = typeof(WhatsNew).Assembly.GetManifestResourceStream(name);
                if (s == null) return "";
                using var r = new StreamReader(s);
                return r.ReadToEnd();
            }
            catch { return ""; }
        }

        /// <summary>Call at start: shows the window once per new version.</summary>
        public static void ShowIfNew()
        {
            string current = UpdateChecker.CurrentText;
            string? seen;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(OurKey);
                seen = key?.GetValue("WhatsNewSeen") as string;
            }
            catch { return; }
            if (seen == current) return;
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(OurKey);
                key.SetValue("WhatsNewSeen", current);
            }
            catch { }
            if (Items().Length > 0) Show(null);
        }

        public static void Show(Window? owner)
        {
            var items = Items();
            if (items.Length == 0) return;
            var w = new WhatsNewWindow(items);
            if (owner != null && owner.IsVisible) { w.Owner = owner; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            w.Show();
            w.Activate();
            w.Topmost = false;   // it came in front; after that it's a normal window like the others
        }

        private sealed class WhatsNewWindow : Window
        {
            public WhatsNewWindow(string[] items)
            {
                Title = Lang.T("ما الجديد");
                WindowStyle = WindowStyle.None;
                AllowsTransparency = true;
                Background = Brushes.Transparent;
                ResizeMode = ResizeMode.NoResize;
                ShowInTaskbar = true;
                Topmost = true;
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                Width = 560;
                SizeToContent = SizeToContent.Height;
                MaxHeight = SystemParameters.WorkArea.Height * 0.85;
                FlowDirection = Lang.Flow;
                FontFamily = Application.Current.MainWindow?.FontFamily ?? new FontFamily("Segoe UI");
                try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/app.ico")); } catch { }

                // Header: title, version
                var header = new StackPanel { Margin = new Thickness(26, 22, 26, 8) };
                header.Children.Add(new TextBlock
                {
                    Text = "✨ " + Lang.T("ما الجديد"), FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = Fg,
                });
                header.Children.Add(new TextBlock
                {
                    Text = Lang.F("Wibn {0}: كل ما في هذا التحديث", UpdateChecker.CurrentText),
                    FontSize = 13.5, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
                });

                // The changes
                var list = new StackPanel { Margin = new Thickness(20, 6, 20, 6) };
                foreach (var item in items) list.Children.Add(Row(item));
                var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
                SmoothScroll.Attach(scroll);

                // "Got it"
                var ok = new Button
                {
                    Content = Lang.T("حسنًا"), MinWidth = 110, HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(26, 10, 26, 22), IsDefault = true,
                };
                if (Application.Current.TryFindResource("AccentButton") is Style style) ok.Style = style;
                ok.Click += (_, _) => Close();

                var root = new DockPanel();
                DockPanel.SetDock(header, Dock.Top);
                DockPanel.SetDock(ok, Dock.Bottom);
                root.Children.Add(header);
                root.Children.Add(ok);
                root.Children.Add(scroll);

                var card = new Border
                {
                    CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x22)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
                    Margin = new Thickness(12), Child = root,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.5, Color = Colors.Black },
                };
                Content = card;

                card.MouseLeftButtonDown += (_, e) => { if (!e.Handled) try { DragMove(); } catch (InvalidOperationException) { } };
                KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

                var lift = new TranslateTransform(0, 10);
                card.RenderTransform = lift;
                card.Opacity = 0;
                Loaded += (_, _) =>
                {
                    var d = TimeSpan.FromMilliseconds(220);
                    card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, d));
                    lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, d) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                };
            }

            private static FrameworkElement Row(string text)
            {
                var dot = new Border
                {
                    Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Accent,
                    VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 7, 12, 0),
                };
                var row = new DockPanel();
                DockPanel.SetDock(dot, Dock.Left);
                row.Children.Add(dot);
                row.Children.Add(new TextBlock { Text = text, FontSize = 14.5, Foreground = Fg, TextWrapping = TextWrapping.Wrap });
                return new Border
                {
                    CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2D, 0x31)),
                    Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8), Child = row,
                };
            }
        }
    }
}
