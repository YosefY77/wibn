using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WinPlus
{
    /// <summary>
    /// The small card in the corner after a capture: the picture (or a video
    /// symbol), what happened, and a click opens it. Goes away by itself.
    /// Never shows up in screenshots or recordings.
    /// </summary>
    internal sealed class CaptureToast : Window
    {
        private static CaptureToast? _current;
        private readonly DispatcherTimer _timer;

        public static void ShowPicture(string path, bool copied) =>
            Open(path, Lang.T("انحفظت الصورة"), copied ? Lang.T("ونسختها. اضغط عشان تفتحها") : Lang.T("اضغط عشان تفتحها"), true);

        public static void ShowVideo(string path, string title) =>
            Open(path, title, Lang.T("اضغط عشان تفتحه"), false);

        public static void Show(string title, string text) => Open(null, title, text, false);

        private static void Open(string? path, string title, string text, bool picture)
        {
            var app = Application.Current;
            if (app == null) return;
            app.Dispatcher.InvokeAsync(() =>
            {
                _current?.Close();
                _current = new CaptureToast(path, title, text, picture);
                _current.Closed += (s, _) => { if (_current == s) _current = null; };
                _current.Show();
            });
        }

        private CaptureToast(string? path, string title, string text, bool picture)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            FlowDirection = Lang.Flow;
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            Title = "Wibn";
            Left = -10000; Top = -10000;

            UIElement thumb;
            if (picture && path != null)
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;   // don't keep the file open
                bmp.DecodePixelWidth = 320;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                thumb = new Border
                {
                    CornerRadius = new CornerRadius(6), ClipToBounds = true, Margin = new Thickness(0, 0, 12, 0),
                    Child = new Image { Source = bmp, MaxWidth = 140, MaxHeight = 84, Stretch = Stretch.Uniform },
                };
            }
            else
            {
                thumb = new Border
                {
                    Width = 44, Height = 44, CornerRadius = new CornerRadius(22), Margin = new Thickness(0, 0, 12, 0),
                    Background = new SolidColorBrush(Color.FromArgb(0x26, 0x4C, 0x8D, 0xFF)),
                    Child = new TextBlock
                    {
                        Text = path != null ? "" : "", FontSize = 18,
                        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                        Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)),
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    },
                };
            }

            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260 };
            words.Children.Add(new TextBlock
            {
                Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF5)),
            });
            words.Children.Add(new TextBlock
            {
                Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA3, 0xAA)),
            });

            var row = new DockPanel();
            DockPanel.SetDock(thumb, Dock.Left);
            row.Children.Add(thumb);
            row.Children.Add(words);

            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2D, 0x31)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x41, 0x47)), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10), Padding = new Thickness(12), Margin = new Thickness(12),
                Cursor = path != null ? Cursors.Hand : Cursors.Arrow, Child = row,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.4 },
            };

            MouseLeftButtonUp += (_, _) =>
            {
                Close();
                if (path == null || !File.Exists(path)) return;
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
                catch { try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { } }
            };
            MouseRightButtonUp += (_, _) => Close();

            SourceInitialized += (_, _) =>
                ScreenCapture.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0x11 /* EXCLUDEFROMCAPTURE */);
            Loaded += (_, _) =>
            {
                var area = SystemParameters.WorkArea;
                Left = area.Right - ActualWidth;
                Top = area.Bottom - ActualHeight;
            };

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _timer.Tick += (_, _) => { if (!IsMouseOver) Close(); };
            _timer.Start();
            Closed += (_, _) => _timer.Stop();
        }
    }

    /// <summary>
    /// The pill at the top of the screen while a video records: time so far, the
    /// limit, and a stop button. Not in the recording itself.
    /// </summary>
    internal sealed class RecordingBadge : Window
    {
        public event Action? StopRequested;

        private readonly TimeSpan _limit;
        private readonly DateTime _started;
        private readonly TextBlock _time;
        private readonly Button _stop;
        private readonly DispatcherTimer _timer;

        public RecordingBadge(TimeSpan limit, DateTime started)
        {
            _limit = limit;
            _started = started;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            Title = "Wibn";
            Left = -10000; Top = -10000;

            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            };
            _time = new TextBlock
            {
                FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF5)),
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            };
            _stop = new Button
            {
                Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12 },
                ToolTip = Lang.T("إيقاف وحفظ"), Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(8, 4, 8, 4),
                Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)),
                BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Focusable = false,
            };
            _stop.Click += (_, _) => StopRequested?.Invoke();

            var row = new StackPanel { Orientation = Orientation.Horizontal, FlowDirection = FlowDirection.LeftToRight };
            row.Children.Add(dot);
            row.Children.Add(_time);
            row.Children.Add(_stop);
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1E, 0x1F, 0x22)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x41, 0x47)), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(18), Padding = new Thickness(14, 6, 6, 6), Margin = new Thickness(8), Child = row,
            };

            SourceInitialized += (_, _) =>
                ScreenCapture.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0x11 /* EXCLUDEFROMCAPTURE */);
            Loaded += (_, _) =>
            {
                var area = SystemParameters.WorkArea;
                Left = area.Left + (area.Width - ActualWidth) / 2;
                Top = area.Top + 4;
            };
            MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not Button) try { DragMove(); } catch { } };

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
            Closed += (_, _) => _timer.Stop();
            Tick();
        }

        private static string Clock(TimeSpan t) =>
            t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

        private void Tick()
        {
            var gone = DateTime.Now - _started;
            if (gone < TimeSpan.Zero) gone = TimeSpan.Zero;
            if (gone > _limit) gone = _limit;
            _time.Text = $"{Clock(gone)} / {Clock(_limit)}";
        }

        public void SetSaving()
        {
            _timer.Stop();
            _stop.IsEnabled = false;
            _time.Text = Lang.T("جاري الحفظ…");
        }
    }
}
