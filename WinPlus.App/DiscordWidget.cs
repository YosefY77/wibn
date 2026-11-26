using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinPlus
{
    /// <summary>
    /// Discord's notifications (see <see cref="DiscordNotes"/>): how many, and the latest ones, who sent each and what
    /// it says. A click opens Discord; a click on one notification opens Discord and takes it off the list. On an extra
    /// taskbar: an icon with the count and the latest one beside it ("name: message").
    /// </summary>
    internal sealed class DiscordWidget : WidgetContent
    {
        private static readonly Brush Blurple = new SolidColorBrush(Color.FromRgb(0x58, 0x65, 0xF2));
        private static readonly Brush Unread = new SolidColorBrush(Color.FromRgb(0xF2, 0x3F, 0x43));

        /// <summary>Discord's logo (24 × 24).</summary>
        private static readonly Geometry Logo = Geometry.Parse(
            "M20.317 4.3698a19.7913 19.7913 0 00-4.8851-1.5152.0741.0741 0 00-.0785.0371c-.211.3753-.4447.8648-.6083 1.2495-1.8447-.2762-3.68-.2762-5.4868 0-.1636-.3933-.4058-.8742-.6177-1.2495a.077.077 0 00-.0785-.037 19.7363 19.7363 0 00-4.8852 1.515.0699.0699 0 00-.0321.0277C.5334 9.0458-.319 13.5799.0992 18.0578a.0824.0824 0 00.0312.0561c2.0528 1.5076 4.0413 2.4228 5.9929 3.0294a.0777.0777 0 00.0842-.0276c.4616-.6304.8731-1.2952 1.226-1.9942a.076.076 0 00-.0416-.1057c-.6528-.2476-1.2743-.5495-1.8722-.8923a.077.077 0 01-.0076-.1277c.1258-.0943.2517-.1923.3718-.2914a.0743.0743 0 01.0776-.0105c3.9278 1.7933 8.18 1.7933 12.0614 0a.0739.0739 0 01.0785.0095c.1202.099.246.1981.3728.2924a.077.077 0 01-.0066.1276 12.2986 12.2986 0 01-1.873.8914.0766.0766 0 00-.0407.1067c.3604.698.7719 1.3628 1.225 1.9932a.076.076 0 00.0842.0286c1.961-.6067 3.9495-1.5219 6.0023-3.0294a.077.077 0 00.0313-.0552c.5004-5.177-.8382-9.6739-3.5485-13.6604a.061.061 0 00-.0312-.0286zM8.02 15.3312c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9555-2.4189 2.157-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.9555 2.4189-2.1569 2.4189zm7.9748 0c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9554-2.4189 2.1569-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.946 2.4189-2.1568 2.4189Z");

        static DiscordWidget() { Unread.Freeze(); Logo.Freeze(); }

        /// <summary>The Discord logo, size × size, in the given color.</summary>
        private static FrameworkElement LogoIcon(double size, Brush fill) => new System.Windows.Shapes.Path
        {
            Data = Logo, Fill = fill, Stretch = Stretch.Uniform, Width = size, Height = size,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
        private readonly SolidColorBrush _ink = new(Color.FromRgb(0xF2, 0xF3, 0xF5));
        private readonly SolidColorBrush _inkMuted = new(Color.FromRgb(0xA0, 0xA3, 0xAA));
        private readonly SolidColorBrush _hover = new(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));

        private readonly TextBlock _title, _count, _empty;
        private readonly StackPanel _list = new();
        private readonly Button _allow;
        private bool _started;
        private int _rowsFit = 4;

        /// <summary>The list changed (on the window thread): the bar's small form follows.</summary>
        private event Action? Shown;

        public DiscordWidget()
        {
            var panel = new DockPanel();
            var head = new DockPanel { Margin = new Thickness(2, 0, 2, 6), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            head.MouseLeftButtonUp += (_, e) => { e.Handled = true; DiscordNotes.OpenDiscord(); };
            var icon = LogoIcon(15, Blurple);
            icon.Margin = new Thickness(0, 0, 6, 0);
            _title = new TextBlock { Text = "Discord", FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = _ink, VerticalAlignment = VerticalAlignment.Center };
            _count = new TextBlock { FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(6, 1, 6, 1), FlowDirection = FlowDirection.LeftToRight };
            var badge = new Border { Background = Blurple, CornerRadius = new CornerRadius(9), Child = _count, VerticalAlignment = VerticalAlignment.Center, MinWidth = 18 };
            DockPanel.SetDock(icon, Dock.Left);
            DockPanel.SetDock(badge, Dock.Right);
            head.Children.Add(icon);
            head.Children.Add(badge);
            head.Children.Add(_title);
            DockPanel.SetDock(head, Dock.Top);
            panel.Children.Add(head);

            _empty = new TextBlock { FontSize = 11.5, Foreground = _inkMuted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 2, 6) };
            _allow = new Button { Content = Lang.T("اسمح لـ Wibn"), Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 0) };
            _allow.Click += (_, _) => DiscordNotes.AskAccess();
            var body = new StackPanel();
            body.Children.Add(_list);
            body.Children.Add(_empty);
            body.Children.Add(_allow);
            panel.Children.Add(body);
            Child = panel;
            Show();
        }

        public override (int W, int H)[]? SizeChoices => new[] { (3, 2), (3, 3), (4, 3), (4, 4), (5, 4) };

        public override void SetCells(int w, int h) { _rowsFit = Math.Max(1, h * 2 - 2); Show(); }

        public override void Start()
        {
            if (_started) return;
            _started = true;
            DiscordNotes.Changed += OnChanged;
            DiscordNotes.Start();
        }

        public override void Stop()
        {
            if (!_started) return;
            _started = false;
            DiscordNotes.Changed -= OnChanged;
            DiscordNotes.Stop();
        }

        private void OnChanged() => Dispatcher.BeginInvoke(new Action(Show));

        private void Show()
        {
            var state = DiscordNotes.Status;
            var notes = DiscordNotes.Notes;
            _count.Text = notes.Count > 99 ? "99+" : notes.Count.ToString();
            ((Border)_count.Parent).Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _allow.Visibility = state == DiscordNotes.State.NoAccess ? Visibility.Visible : Visibility.Collapsed;
            _empty.Text = state switch
            {
                DiscordNotes.State.NoAccess => Lang.T("Wibn يحتاج إذن عشان يقرأ إشعارات ويندوز"),
                DiscordNotes.State.NotAvailable => Lang.T("نسخة ويندوز هذي ما تعطي الإشعارات للبرامج"),
                DiscordNotes.State.Loading => Lang.T("أشوف الإشعارات…"),
                _ => Lang.T("ما في إشعارات من ديسكورد"),
            };
            _empty.Visibility = notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _list.Children.Clear();
            foreach (var n in notes.Take(_rowsFit)) _list.Children.Add(Row(n));
            Shown?.Invoke();
        }

        /// <summary>One notification: who, what, when. A click opens Discord and takes it off the list.</summary>
        private FrameworkElement Row(DiscordNotes.Note n)
        {
            var top = new DockPanel();
            var when = new TextBlock { Text = DiscordNotes.Ago(n.Time), FontSize = 10.5, Foreground = _inkMuted, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(when, Dock.Right);
            top.Children.Add(when);
            top.Children.Add(new TextBlock { Text = n.Sender, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = _ink, TextTrimming = TextTrimming.CharacterEllipsis });
            var stack = new StackPanel();
            stack.Children.Add(top);
            if (n.Text.Length > 0)
                stack.Children.Add(new TextBlock { Text = n.Text, FontSize = 11.5, Foreground = _inkMuted, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
            var row = new Border { Child = stack, Padding = new Thickness(6, 4, 6, 4), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(-4, 0, -4, 2) };
            row.MouseEnter += (_, _) => row.Background = _hover;
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; DiscordNotes.OpenDiscord(); DiscordNotes.Remove(new[] { n }); };
            row.ToolTip = n.Text.Length > 0 ? $"{n.Sender}\n{n.Text}" : n.Sender;
            return row;
        }

        /// <summary>On a bar: the icon with the count, and the latest notification beside it ("name: message").</summary>
        public override FrameworkElement? BarView(BarLook l)
        {
            var row = BarRow(l);
            // Discord's logo with a red dot on its top right corner while there's any notification (like Discord's own).
            var size = l.Font + 4;
            var dotSize = Math.Max(6, Math.Round(size * 0.42));
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = dotSize, Height = dotSize, Fill = Unread, HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -dotSize * 0.3, -dotSize * 0.35, 0),
                IsHitTestVisible = false,
            };
            var glyph = new Grid
            {
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = l.Vertical ? new Thickness(0, 3, 0, 2) : new Thickness(3, 1, 4, 0),
                FlowDirection = FlowDirection.LeftToRight,
            };
            glyph.Children.Add(LogoIcon(size, l.Ink));
            glyph.Children.Add(dot);
            var count = new TextBlock { FontSize = l.Small, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(5, 0, 5, 0), FlowDirection = FlowDirection.LeftToRight };
            var badge = new Border { Background = Blurple, CornerRadius = new CornerRadius(8), Child = count, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 16, Margin = l.Vertical ? new Thickness(0, 1, 0, 1) : new Thickness(0, 0, 4, 0) };
            var latest = BarText(l);
            latest.MaxWidth = 260;
            row.Children.Add(glyph);
            row.Children.Add(badge);
            if (!l.Vertical) row.Children.Add(latest);
            void Fill()
            {
                var notes = DiscordNotes.Notes;
                count.Text = notes.Count > 99 ? "99+" : notes.Count.ToString();
                badge.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                dot.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                var n = notes.FirstOrDefault();
                latest.Text = n == null ? "" : n.Text.Length > 0 ? $"{n.Sender}: {n.Text}" : n.Sender;
                latest.Visibility = n == null ? Visibility.Collapsed : Visibility.Visible;
                row.ToolTip = notes.Count == 0
                    ? Lang.T("ما في إشعارات من ديسكورد")
                    : string.Join("\n", notes.Take(8).Select(x => x.Text.Length > 0 ? $"{x.Sender}: {x.Text}" : x.Sender));
            }
            Fill();
            Shown += Fill;
            var button = BarButton(l, row, DiscordNotes.OpenDiscord);
            button.Unloaded += (_, _) => Shown -= Fill;
            button.Loaded += (_, _) => { Shown -= Fill; Shown += Fill; Fill(); };
            return button;
        }

        public override IEnumerable<Control> MenuItems()
        {
            var open = new MenuItem { Header = Lang.T("افتح ديسكورد") };
            open.Click += (_, _) => DiscordNotes.OpenDiscord();
            yield return open;
            var clear = new MenuItem { Header = Lang.T("امسح الإشعارات"), IsEnabled = DiscordNotes.Notes.Count > 0 };
            clear.Click += (_, _) => DiscordNotes.Remove(DiscordNotes.Notes);
            yield return clear;
        }

        public override void SetLightBehind(bool light)
        {
            var d = new Duration(TimeSpan.FromMilliseconds(200));
            _ink.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(light ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xF2, 0xF3, 0xF5), d));
            _inkMuted.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(light ? Color.FromArgb(0xB0, 0x3C, 0x3C, 0x43) : Color.FromRgb(0xA0, 0xA3, 0xAA), d));
            _hover.Color = light ? Color.FromArgb(0x16, 0, 0, 0) : Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF);
        }
    }
}
