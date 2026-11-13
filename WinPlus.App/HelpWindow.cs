using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinPlus
{
    /// <summary>
    /// The "!" next to a page's title, and the window it opens: what the page's features do,
    /// why they're worth it, each with a drawn picture.
    /// </summary>
    internal static class Help
    {
        private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF5));
        private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0xA0, 0xA3, 0xAA));
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));

        /// <summary>Puts a "!" right after every page's title (pages that have help).</summary>
        public static void AddButtons(Window owner, FrameworkElement[] pages)
        {
            var titleStyle = (Style)Application.Current.FindResource("PageTitle");
            foreach (var page in pages)
            {
                string key = page.Name.StartsWith("Page") ? page.Name[4..].ToLowerInvariant() : "";
                if (HelpContent.For(key) == null) continue;
                if (FindTitle(page, titleStyle) is not { } title || title.Parent is not Panel parent) continue;

                int index = parent.Children.IndexOf(title);
                parent.Children.RemoveAt(index);
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = title.Margin };
                DockPanel.SetDock(row, DockPanel.GetDock(title));
                title.Margin = new Thickness(0);
                row.Children.Add(title);
                row.Children.Add(Button(() => Show(owner, key)));
                parent.Children.Insert(index, row);
            }
        }

        private static TextBlock? FindTitle(DependencyObject root, Style style)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is TextBlock t && t.Style == style) return t;
                if (child is DependencyObject d && FindTitle(d, style) is { } found) return found;
            }
            return null;
        }

        /// <summary>A round "!" that lights up under the mouse.</summary>
        private static FrameworkElement Button(Action click)
        {
            var mark = new TextBlock
            {
                Text = "!", FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Accent,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            var b = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(12, 4, 12, 0),
                BorderBrush = Accent, BorderThickness = new Thickness(1.5), Background = Brushes.Transparent,
                Child = mark, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
            };
            b.MouseEnter += (_, _) => { b.Background = Accent; mark.Foreground = Brushes.White; };
            b.MouseLeave += (_, _) => { b.Background = Brushes.Transparent; mark.Foreground = Accent; };
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
            return b;
        }

        public static void Show(Window owner, string key)
        {
            if (HelpContent.For(key) is not { } page) return;
            new HelpWindow(page) { Owner = owner }.ShowDialog();
        }

        /// <summary>The window with a page's help.</summary>
        private sealed class HelpWindow : Window
        {
            public HelpWindow(HelpPage page)
            {
                Title = page.Title;
                WindowStyle = WindowStyle.None;
                AllowsTransparency = true;
                Background = Brushes.Transparent;
                ResizeMode = ResizeMode.NoResize;
                ShowInTaskbar = false;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                Width = 720;
                Height = 640;
                FlowDirection = Lang.Flow;
                FontFamily = Application.Current.MainWindow?.FontFamily ?? new FontFamily("Segoe UI");

                // Header: "!" title … close
                var header = new DockPanel { Margin = new Thickness(24, 18, 16, 6), Background = Brushes.Transparent };
                var close = new Border
                {
                    Width = 32, Height = 32, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand,
                    Child = new TextBlock
                    {
                        Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Foreground = Fg,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                var hover = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
                close.MouseEnter += (_, _) => close.Background = hover;
                close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
                close.MouseLeftButtonDown += (_, e) => e.Handled = true;
                close.MouseLeftButtonUp += (_, e) => { e.Handled = true; Close(); };
                DockPanel.SetDock(close, Dock.Right);
                header.Children.Add(close);
                var titles = new StackPanel();
                titles.Children.Add(new TextBlock { Text = page.Title, FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = Fg });
                titles.Children.Add(new TextBlock { Text = page.Intro, FontSize = 13.5, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
                header.Children.Add(titles);

                // The features
                var list = new StackPanel { Margin = new Thickness(20, 8, 20, 20) };
                foreach (var item in page.Items) list.Children.Add(Card(item));
                var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
                SmoothScroll.Attach(scroll);

                var root = new DockPanel();
                DockPanel.SetDock(header, Dock.Top);
                root.Children.Add(header);
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

                // A little fade and rise in.
                var lift = new TranslateTransform(0, 10);
                card.RenderTransform = lift;
                card.Opacity = 0;
                Loaded += (_, _) =>
                {
                    var d = TimeSpan.FromMilliseconds(180);
                    card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, d));
                    lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, d) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                };
            }

            /// <summary>One feature: its picture, then its name, what it does and why.</summary>
            private static FrameworkElement Card(HelpItem item)
            {
                var text = new StackPanel { Margin = new Thickness(18, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = item.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Fg, TextWrapping = TextWrapping.Wrap });
                text.Children.Add(Para(Lang.T("وش يسوي"), item.What));
                text.Children.Add(Para(Lang.T("وش فايدته"), item.Why));

                var picture = item.Picture();
                picture.VerticalAlignment = VerticalAlignment.Top;
                var row = new DockPanel();
                DockPanel.SetDock(picture, Dock.Left);
                row.Children.Add(picture);
                row.Children.Add(text);
                return new Border
                {
                    CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2D, 0x31)),
                    Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 10), Child = row,
                };
            }

            private static FrameworkElement Para(string label, string body)
            {
                var p = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
                p.Children.Add(new TextBlock { Text = label, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Accent });
                p.Children.Add(new TextBlock { Text = body, FontSize = 13, Foreground = Fg, TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 2, 0, 0) });
                return p;
            }
        }
    }
}
