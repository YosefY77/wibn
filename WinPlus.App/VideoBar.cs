using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Forms = System.Windows.Forms;

namespace WinPlus
{
    /// <summary>
    /// The "paste a link" bar that the video shortcut opens in the middle of the
    /// screen, in the spirit of Spotlight: one rounded field, nothing else.
    /// Pasting a link closes it right away and the download starts; Enter does
    /// the same for a typed link. Esc, a click elsewhere or the shortcut again
    /// close it.
    /// </summary>
    internal sealed class VideoBar : Window
    {
        public event Action<Uri>? LinkEntered;

        private readonly Border _card;
        private readonly TextBox _box;
        private readonly TextBlock _placeholder;
        private readonly Border _chip;
        private readonly TextBlock _chipText;
        private readonly ScaleTransform _scale = new(1, 1);
        private readonly TranslateTransform _shift = new();
        private readonly TranslateTransform _shake = new();
        private bool _closing;
        private readonly Border _host;
        private LiquidGlass? _glass;

        // Text and button colors: light on a dark glass, dark when what's behind is light.
        private readonly SolidColorBrush _ink = new(Color.FromRgb(0xF5, 0xF5, 0xF7));
        private readonly SolidColorBrush _inkMuted = new(Color.FromArgb(0x8C, 0xEB, 0xEB, 0xF5));
        private readonly SolidColorBrush _buttonIdle = new(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        private readonly SolidColorBrush _buttonHover = new(Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF));

        private static readonly bool Motion = SystemParameters.ClientAreaAnimation;

        public VideoBar()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Width = 560;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.Manual;
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            Title = "Wibn";
            Opacity = Motion ? 0 : 1;
            Left = -10000; Top = -10000;   // placed on the right screen once it has a DPI

            var white = _ink;
            var muted = _inkMuted;
            var accent = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));

            // Link symbol
            var icon = new TextBlock
            {
                Text = "\uE71B", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 16, Foreground = muted, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };

            _box = new TextBox
            {
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = white,
                CaretBrush = accent, SelectionBrush = accent, FontSize = 17, Padding = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight,
                FocusVisualStyle = null,
            };
            _placeholder = new TextBlock
            {
                Text = Lang.T("الصق الرابط هنا"), FontSize = 17, Foreground = muted, IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Center, FlowDirection = Lang.Flow,
                HorizontalAlignment = Lang.English ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            };
            var field = new Grid { Height = 30 };
            field.Children.Add(_placeholder);
            field.Children.Add(_box);

            _chipText = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
            _chip = new Border
            {
                Background = accent, CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 2, 8, 3),
                Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed, Child = _chipText,
            };

            // Paste button: puts what was copied in the field (and a link starts right away).
            var pasteText = new StackPanel { Orientation = Orientation.Horizontal };
            pasteText.Children.Add(new TextBlock
            {
                Text = "\uE77F", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 13, Foreground = white, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0),
            });
            pasteText.Children.Add(new TextBlock { Text = Lang.T("لصق"), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = white, VerticalAlignment = VerticalAlignment.Center });
            var idle = _buttonIdle;
            var hover = _buttonHover;
            var paste = new Border
            {
                Background = idle, CornerRadius = new CornerRadius(9), Padding = new Thickness(12, 6, 12, 7),
                Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand, Child = pasteText, ToolTip = "Ctrl+V",
            };
            paste.MouseEnter += (_, _) => paste.Background = hover;
            paste.MouseLeave += (_, _) => paste.Background = idle;
            paste.MouseLeftButtonDown += (_, e) => { e.Handled = true; PasteFromClipboard(); };

            var row = new DockPanel { FlowDirection = Lang.Flow };
            DockPanel.SetDock(icon, Dock.Left);      // (RTL: the right side)
            DockPanel.SetDock(paste, Dock.Right);
            DockPanel.SetDock(_chip, Dock.Right);
            row.Children.Add(icon);
            row.Children.Add(paste);
            row.Children.Add(_chip);
            row.Children.Add(field);

            _card = new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1C, 0x1C, 0x1E)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(16, 8, 8, 8),
                Child = row,
                Effect = new DropShadowEffect { BlurRadius = 36, ShadowDepth = 10, Direction = 270, Opacity = 0.45, Color = Colors.Black },
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new TransformGroup { Children = new TransformCollection { _scale, _shift, _shake } },
            };
            if (!Lang.English) _card.Padding = new Thickness(8, 8, 16, 8);   // mirrored: the button side is the left
            // Room around the card for its shadow.
            _host = new Border { Padding = new Thickness(40, 30, 40, 50), Child = _card };
            Content = _host;

            _box.TextChanged += OnTextChanged;
            PreviewKeyDown += OnKey;
            Deactivated += (_, _) => Dismiss();
            Loaded += OnLoaded;
            Closed += (_, _) => _glass?.Dispose();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // In the middle of the screen the mouse is on, a bit above center (like Spotlight).
            var screen = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(this);
            double w = ActualWidth, h = ActualHeight;
            Left = screen.X / dpi.DpiScaleX + (screen.Width / dpi.DpiScaleX - w) / 2;
            Top = screen.Y / dpi.DpiScaleY + screen.Height / dpi.DpiScaleY * 0.28 - 30;

            // Liquid glass: what's behind, live, bent at the edges. Without it (older
            // Windows) the plain dark background stays.
            try
            {
                if (GlassSettings.Video.On)
                    _glass = new LiquidGlass(this, _host, _card, new Thickness(40, 30, 0, 0), 14, GlassSettings.Video);
                if (_glass != null && _glass.Available)
                {
                    _card.BorderBrush = new LinearGradientBrush(
                        Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 90);
                    _glass.LightBehindChanged += OnLightBehind;
                    // After the window has moved to its place (so the right area is copied).
                    Dispatcher.BeginInvoke(new Action(() => _glass?.Start()), System.Windows.Threading.DispatcherPriority.Background);
                }
            }
            catch (Exception ex) { CrashLog.Write("liquid glass", ex); }

            // Get the keyboard (the shortcut gives us the right to come to the front).
            var hwnd = new WindowInteropHelper(this).Handle;
            SetForegroundWindow(hwnd);
            Activate();
            _box.Focus();
            Keyboard.Focus(_box);

            if (!Motion) return;
            var spring = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut };
            var smooth = new CubicEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(160)) { EasingFunction = smooth });
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, Ms(300)) { EasingFunction = spring });
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, Ms(300)) { EasingFunction = spring });
            _shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-14, 0, Ms(280)) { EasingFunction = smooth });
        }

        /// <summary>Light behind the glass: dark text (and back), smoothly.</summary>
        private void OnLightBehind(bool light)
        {
            void To(SolidColorBrush brush, Color color) =>
                brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, Ms(200)));
            To(_ink, light ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xF5, 0xF5, 0xF7));
            To(_inkMuted, light ? Color.FromArgb(0x99, 0x3C, 0x3C, 0x43) : Color.FromArgb(0x8C, 0xEB, 0xEB, 0xF5));
            To(_buttonIdle, light ? Color.FromArgb(0x1A, 0x00, 0x00, 0x00) : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            To(_buttonHover, light ? Color.FromArgb(0x2E, 0x00, 0x00, 0x00) : Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF));
        }

        private static Duration Ms(int ms) => new(TimeSpan.FromMilliseconds(ms));

        private void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            string text = _box.Text;
            _placeholder.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

            bool ok = VideoDownloader.IsLink(text, out var uri);
            string? site = ok ? VideoDownloader.SiteName(uri!) : null;
            _chipText.Text = site ?? "";
            _chip.Visibility = site != null ? Visibility.Visible : Visibility.Collapsed;

            // Pasted (not typed letter by letter) a link: go.
            bool pasted = e.Changes.Any(c => c.AddedLength > 4);
            if (ok && pasted) Submit(uri!);
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Dismiss();
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (VideoDownloader.IsLink(_box.Text, out var uri)) Submit(uri!);
                else Shake();
            }
        }

        private void PasteFromClipboard()
        {
            string text = "";
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText().Trim(); }
            catch { }   // the clipboard can be busy for a moment
            if (VideoDownloader.IsLink(text, out var uri)) { _box.Text = text; Submit(uri!); }
            else { _box.Focus(); Shake(); }
        }

        private void Submit(Uri uri)
        {
            if (_closing) return;
            Dismiss();
            LinkEntered?.Invoke(uri);
        }

        /// <summary>"That's not a link": a short side-to-side shake, like a wrong password on a Mac.</summary>
        private void Shake()
        {
            if (!Motion) { System.Media.SystemSounds.Beep.Play(); return; }
            var a = new DoubleAnimationUsingKeyFrames { Duration = Ms(360) };
            double[] xs = { 0, -12, 10, -7, 5, -2, 0 };
            for (int i = 0; i < xs.Length; i++)
                a.KeyFrames.Add(new EasingDoubleKeyFrame(xs[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(i * 60))));
            _shake.BeginAnimation(TranslateTransform.XProperty, a);
        }

        public void Dismiss()
        {
            if (_closing) return;
            _closing = true;
            if (!Motion) { Close(); return; }
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var fade = new DoubleAnimation(Opacity, 0, Ms(140)) { EasingFunction = ease };
            fade.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, fade);
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.97, Ms(140)) { EasingFunction = ease });
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.97, Ms(140)) { EasingFunction = ease });
        }

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    }
}
