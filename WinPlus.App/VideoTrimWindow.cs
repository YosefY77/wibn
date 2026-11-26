using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace WinPlus
{
    /// <summary>
    /// "Trim a video before downloading": paste a YouTube (or other) link, see its
    /// title and picture, pick where it starts and ends on a timeline, and download
    /// only that part. Three numbered steps, each lit up once the one before is done.
    /// </summary>
    internal sealed class VideoTrimWindow : Window
    {
        private static VideoTrimWindow? _open;

        /// <summary>Opens the window (or brings the open one forward), optionally with a link in it.</summary>
        public static void ShowWindow(Window? owner, string? link = null)
        {
            if (_open != null)
            {
                if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
                _open.Activate();
                if (!string.IsNullOrWhiteSpace(link)) { _open._link.Text = link; _ = _open.FetchAsync(); }
                return;
            }
            _open = new VideoTrimWindow();
            if (owner != null && owner.IsVisible) _open.Owner = owner;
            _open.Closed += (_, _) => _open = null;
            _open.Show();
            if (!string.IsNullOrWhiteSpace(link)) { _open._link.Text = link; _ = _open.FetchAsync(); }
        }

        private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));
        private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x40));
        private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C));
        private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
        private static Style St(string key) => (Style)Application.Current.FindResource(key);

        private readonly TextBox _link;
        private readonly Button _fetch;
        private readonly TextBlock _fetchStatus;

        private readonly Border _step2, _step3;
        private readonly Image _thumb;
        private readonly TextBlock _title, _meta;
        private readonly RangeBar _range;
        private readonly TextBox _startBox, _endBox;
        private readonly TextBlock _lengthText;
        private readonly Button _preview;

        private readonly ComboBox _kind;
        private readonly Button _download;
        private readonly ProgressBar _progress;
        private readonly TextBlock _status;

        private VideoDownloader.VideoInfo? _info;
        private bool _busy;
        private int _fetchId;

        private VideoTrimWindow()
        {
            Title = Lang.T("قص مقطع قبل التحميل");
            Width = 760; SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.CanMinimize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FlowDirection = Lang.Flow;
            Background = Res("Bg");
            try { Icon = new BitmapImage(new Uri("pack://application:,,,/app.ico")); } catch { }
            SourceInitialized += (_, _) => DarkTitle();

            var root = new StackPanel { Margin = new Thickness(22, 18, 22, 22) };

            // ---- Heading
            root.Children.Add(new TextBlock { Text = Lang.T("قص مقطع قبل التحميل"), FontSize = 22, FontWeight = FontWeights.SemiBold });
            root.Children.Add(new TextBlock
            {
                Text = Lang.T("الصق رابط المقطع، حدّد من وين يبدأ ووين ينتهي، وحمّل الجزء اللي تبيه بس."),
                Foreground = Res("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 16),
            });

            // ---- 1. The link
            _link = Field();
            _link.FlowDirection = FlowDirection.LeftToRight;
            var hint = new TextBlock
            {
                Text = "https://www.youtube.com/watch?v=…", Foreground = Res("Muted"), Opacity = 0.7, FontSize = 14,
                FlowDirection = FlowDirection.LeftToRight, IsHitTestVisible = false,
                Margin = new Thickness(13, 0, 13, 0), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
            };
            _link.TextChanged += (_, _) => hint.Visibility = _link.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var linkBox = new Grid();
            linkBox.Children.Add(_link);
            linkBox.Children.Add(hint);
            _link.KeyDown += (_, e) => { if (e.Key == Key.Enter) _ = FetchAsync(); };
            DataObject.AddPastingHandler(_link, (_, _) => Dispatcher.BeginInvoke(new Action(() => _ = FetchAsync())));
            _fetch = new Button { Style = St("AccentButton"), Content = Lang.T("جيب المقطع"), Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(10, 0, 0, 0) };
            _fetch.Click += (_, _) => _ = FetchAsync();
            var paste = new Button { Style = St("FlatButton"), Content = Lang.T("لصق"), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(10, 0, 0, 0) };
            paste.Click += (_, _) =>
            {
                try { if (Clipboard.ContainsText()) { _link.Text = Clipboard.GetText().Trim(); _ = FetchAsync(); } } catch { }
            };
            var linkRow = new DockPanel();
            DockPanel.SetDock(_fetch, Dock.Right);
            DockPanel.SetDock(paste, Dock.Right);
            linkRow.Children.Add(_fetch);
            linkRow.Children.Add(paste);
            linkRow.Children.Add(linkBox);
            _fetchStatus = new TextBlock { Foreground = Res("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
            var s1 = new StackPanel();
            s1.Children.Add(linkRow);
            s1.Children.Add(_fetchStatus);
            root.Children.Add(Step("1", Lang.T("رابط المقطع"), s1));

            // ---- 2. The part to keep
            _thumb = new Image { Width = 160, Height = 90, Stretch = Stretch.UniformToFill };
            var thumbBox = new Border
            {
                Width = 160, Height = 90, CornerRadius = new CornerRadius(6), Background = Res("Card"),
                Margin = new Thickness(0, 0, 14, 0), ClipToBounds = true, Child = _thumb,
            };
            thumbBox.Loaded += (_, _) => thumbBox.Clip = new RectangleGeometry(new Rect(0, 0, 160, 90), 6, 6);
            _title = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxHeight = 44, TextTrimming = TextTrimming.CharacterEllipsis };
            _meta = new TextBlock { Foreground = Res("Muted"), Margin = new Thickness(0, 4, 0, 0) };
            _preview = new Button { Style = St("FlatButton"), Content = Lang.T("▶ شوف البداية في يوتيوب"), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            _preview.Click += (_, _) => OpenAt(_range.Start);
            var infoText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            infoText.Children.Add(_title);
            infoText.Children.Add(_meta);
            infoText.Children.Add(_preview);
            var info = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
            DockPanel.SetDock(thumbBox, Dock.Left);
            info.Children.Add(thumbBox);
            info.Children.Add(infoText);

            _range = new RangeBar();
            _range.Changed += () => { if (!_typing) ShowTimes(); };

            _startBox = TimeField();
            _endBox = TimeField();
            _startBox.LostFocus += (_, _) => ApplyTyped(_startBox, start: true);
            _endBox.LostFocus += (_, _) => ApplyTyped(_endBox, start: false);
            _startBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyTyped(_startBox, start: true); };
            _endBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyTyped(_endBox, start: false); };

            _lengthText = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = AccentBrush, HorizontalAlignment = HorizontalAlignment.Center };
            var lengthBox = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            lengthBox.Children.Add(new TextBlock { Text = Lang.T("طول الجزء"), Foreground = Res("Muted"), HorizontalAlignment = HorizontalAlignment.Center });
            lengthBox.Children.Add(_lengthText);
            var reset = new Button { Style = St("FlatButton"), Content = Lang.T("كامل المقطع"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            reset.Click += (_, _) => { _range.Set(0, _range.Duration); ShowTimes(); };
            lengthBox.Children.Add(reset);

            // The times always read left to right, like the timeline.
            var times = new Grid { FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(0, 14, 0, 0) };
            times.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            times.ColumnDefinitions.Add(new ColumnDefinition());
            times.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var startPanel = TimePanel(Lang.T("البداية"), _startBox, start: true);
            var endPanel = TimePanel(Lang.T("النهاية"), _endBox, start: false);
            Grid.SetColumn(lengthBox, 1);
            Grid.SetColumn(endPanel, 2);
            times.Children.Add(startPanel);
            times.Children.Add(lengthBox);
            times.Children.Add(endPanel);

            var s2 = new StackPanel();
            s2.Children.Add(info);
            s2.Children.Add(new TextBlock
            {
                Text = Lang.T("اسحب الدائرتين على الشريط، أو اكتب الوقت (مثل 1:30)، أو استخدم أزرار ± الثواني."),
                Foreground = Res("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            });
            s2.Children.Add(_range);
            s2.Children.Add(times);
            _step2 = Step("2", Lang.T("حدّد الجزء اللي تبيه"), s2);
            root.Children.Add(_step2);

            // ---- 3. Download
            _kind = new ComboBox { Width = 190, VerticalAlignment = VerticalAlignment.Center };
            _kind.Items.Add(Lang.T("فيديو (أعلى جودة)"));
            _kind.Items.Add(Lang.T("صوت فقط (MP3)"));
            _kind.SelectedIndex = VideoDownloader.AudioOnly ? 1 : 0;
            _download = new Button
            {
                Style = St("AccentButton"), Content = Lang.T("✂  قص وحمّل"), FontSize = 15, FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(26, 9, 26, 9), Margin = new Thickness(12, 0, 0, 0),
            };
            _download.Click += (_, _) => _ = DownloadAsync();
            var folder = new Button { Style = St("FlatButton"), Content = Lang.T("افتح المجلد"), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(8, 0, 0, 0) };
            folder.Click += (_, _) => { try { Process.Start("explorer.exe", "\"" + VideoDownloader.SaveFolder + "\""); } catch { } };
            var row3 = new DockPanel();
            DockPanel.SetDock(_kind, Dock.Left);
            row3.Children.Add(_kind);
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            right.Children.Add(folder);
            right.Children.Add(_download);
            row3.Children.Add(right);
            _progress = new ProgressBar
            {
                Height = 4, IsIndeterminate = true, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed,
                Foreground = AccentBrush, Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x44)), BorderThickness = new Thickness(0),
            };
            _status = new TextBlock { Foreground = Res("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            var s3 = new StackPanel();
            s3.Children.Add(row3);
            s3.Children.Add(_progress);
            s3.Children.Add(_status);
            _step3 = Step("3", Lang.T("حمّل"), s3);
            root.Children.Add(_step3);

            Content = root;
            SetReady(false);
            Loaded += (_, _) =>
            {
                _link.Focus();
                // A link already copied? Put it in (not fetched until asked: it may be old).
                try
                {
                    if (_link.Text.Length == 0 && Clipboard.ContainsText() && VideoDownloader.IsLink(Clipboard.GetText(), out var u) && u != null)
                        _link.Text = u.ToString();
                }
                catch { }
            };
            KeyDown += (_, e) => { if (e.Key == Key.Escape && !_busy) Close(); };
        }

        // ---- Steps -------------------------------------------------------------------------------

        /// <summary>Lights steps 2 and 3 up once there's a video to trim.</summary>
        private void SetReady(bool ready)
        {
            foreach (var step in new[] { _step2, _step3 })
            {
                step.IsEnabled = ready;
                step.Opacity = ready ? 1 : 0.45;
            }
            if (!ready) _status.Text = Lang.T("جيب المقطع أول.");
        }

        private async System.Threading.Tasks.Task FetchAsync()
        {
            if (_busy) return;
            string text = _link.Text.Trim();
            if (!VideoDownloader.IsLink(text, out var uri) || uri == null)
            {
                FetchMessage(Lang.T("هذا مو رابط. انسخ رابط المقطع من يوتيوب والصقه هنا."), WarnBrush);
                return;
            }
            int id = ++_fetchId;
            _fetch.IsEnabled = false;
            SetReady(false);
            FetchMessage(VideoDownloader.IsInstalled ? Lang.T("يجيب معلومات المقطع…") : Lang.T("أول مرة: ينزّل أداة التحميل (yt-dlp)، لحظات…"), Res("Muted"));
            try
            {
                var info = await VideoDownloader.GetInfoAsync(uri);
                if (id != _fetchId) return;
                _info = info;
                _title.Text = info.Title;
                _meta.Text = (info.Uploader != null ? info.Uploader + "  ·  " : "") + Lang.F("الطول {0}", Fmt(info.Duration));
                _preview.Visibility = info.IsYouTube ? Visibility.Visible : Visibility.Collapsed;
                _thumb.Source = null;
                if (info.Thumbnail != null)
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(info.Thumbnail);
                        bmp.DecodePixelWidth = 320;
                        bmp.EndInit();
                        _thumb.Source = bmp;
                    }
                    catch { }
                }
                _range.Duration = info.Duration;
                _range.Set(0, info.Duration);
                ShowTimes();
                _fetchStatus.Visibility = Visibility.Collapsed;
                SetReady(true);
                _status.Text = "";
                _download.Focus();
            }
            catch (VideoDownloader.UserMessage ex)
            {
                if (id == _fetchId) FetchMessage("⚠ " + ex.Message, WarnBrush);
            }
            catch (Exception ex)
            {
                CrashLog.Write("trim fetch", ex);
                if (id == _fetchId) FetchMessage("⚠ " + Lang.T("صار خطأ. تأكد من النت ومن الرابط."), WarnBrush);
            }
            finally
            {
                if (id == _fetchId) _fetch.IsEnabled = true;
            }
        }

        private void FetchMessage(string text, Brush color)
        {
            _fetchStatus.Text = text;
            _fetchStatus.Foreground = color;
            _fetchStatus.Visibility = Visibility.Visible;
        }

        private async System.Threading.Tasks.Task DownloadAsync()
        {
            if (_info == null || _busy) return;
            _busy = true;
            _download.IsEnabled = false;
            _fetch.IsEnabled = false;
            _step2.IsEnabled = false;
            _progress.Visibility = Visibility.Visible;
            _status.Foreground = Res("Muted");
            try
            {
                double start = _range.Start, end = _range.End;
                bool whole = start <= 0.5 && end >= _info.Duration - 0.5;
                VideoDownloader.Cut? cut = whole ? null : new VideoDownloader.Cut(start, end);

                // Cutting needs FFmpeg: get it here, with its own progress.
                if (cut != null && Converter.FindFfmpeg() == null)
                {
                    _status.Text = Lang.T("القص يحتاج FFmpeg (تقريبًا 140 ميقا، مرة وحدة بس). ينزّله الحين…");
                    _progress.IsIndeterminate = false;
                    _progress.Maximum = 1;
                    await Converter.DownloadFfmpegAsync(new Progress<double>(v => _progress.Value = v), CancellationToken.None);
                    _progress.IsIndeterminate = true;
                }

                bool ok = await VideoDownloader.DownloadAsync(_info.Link, _kind.SelectedIndex == 1, cut,
                    s => Dispatcher.BeginInvoke(new Action(() => _status.Text = s)));
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);   // let the last status land first
                if (ok)
                {
                    _status.Foreground = OkBrush;
                    _status.Text = Lang.T("✓ تم! انحفظ الملف وانفتح مجلده.");
                }
                else
                {
                    _status.Foreground = WarnBrush;
                    if (!_status.Text.StartsWith("⚠")) _status.Text = "⚠ " + Lang.T("ما تحمّل. تأكد من النت وجرّب مرة ثانية.");
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write("trim download", ex);
                _status.Foreground = WarnBrush;
                _status.Text = "⚠ " + Lang.T("ما تحمّل. تأكد من النت وجرّب مرة ثانية.");
            }
            finally
            {
                _progress.Visibility = Visibility.Collapsed;
                _busy = false;
                _download.IsEnabled = true;
                _fetch.IsEnabled = true;
                _step2.IsEnabled = true;
            }
        }

        // ---- Times ---------------------------------------------------------------------------------

        private bool _typing;

        private void ShowTimes()
        {
            _startBox.Text = Fmt(_range.Start);
            _endBox.Text = Fmt(_range.End);
            _lengthText.Text = Fmt(_range.End - _range.Start);
        }

        private void ApplyTyped(TextBox box, bool start)
        {
            if (TryParse(box.Text, out double t))
            {
                _typing = true;
                if (start) _range.Set(t, _range.End); else _range.Set(_range.Start, t);
                _typing = false;
            }
            ShowTimes();
        }

        private void Nudge(bool start, double by)
        {
            if (start) _range.Set(_range.Start + by, _range.End); else _range.Set(_range.Start, _range.End + by);
            ShowTimes();
        }

        /// <summary>"1:05", or "1:02:03" past an hour.</summary>
        internal static string Fmt(double seconds)
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
            return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes}:{ts.Seconds:00}";
        }

        /// <summary>"90", "1:30", "1:02:03" (Arabic digits and "." work too).</summary>
        internal static bool TryParse(string text, out double seconds)
        {
            seconds = 0;
            var sb = new System.Text.StringBuilder();
            foreach (char c in text.Trim())
            {
                if (c >= '٠' && c <= '٩') sb.Append((char)('0' + (c - '٠')));
                else if (c is '.' or '٫' or '：') sb.Append(':');
                else sb.Append(c);
            }
            var parts = sb.ToString().Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length is 0 or > 3) return false;
            foreach (var p in parts)
            {
                if (!int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out int n)) return false;
                seconds = seconds * 60 + n;
            }
            return true;
        }

        private void OpenAt(double seconds)
        {
            if (_info == null || !_info.IsYouTube) return;
            try
            {
                Process.Start(new ProcessStartInfo($"https://www.youtube.com/watch?v={_info.Id}&t={(int)seconds}s") { UseShellExecute = true });
            }
            catch { }
        }

        // ---- Building blocks -----------------------------------------------------------------------

        /// <summary>A numbered card: "1  The link" and what goes in it.</summary>
        private static Border Step(string number, string title, UIElement body)
        {
            var circle = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = AccentBrush, Margin = new Thickness(0, 0, 10, 0),
                Child = new TextBlock { Text = number, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            head.Children.Add(circle);
            head.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var panel = new StackPanel();
            panel.Children.Add(head);
            panel.Children.Add(body);
            return new Border
            {
                Background = Res("RowBg"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10), Padding = new Thickness(18, 14, 18, 16), Margin = new Thickness(0, 0, 0, 12), Child = panel,
            };
        }

        private static TextBox Field() => new()
        {
            Background = Res("Bg"), Foreground = Res("Fg"), CaretBrush = Res("Fg"), SelectionBrush = AccentBrush,
            BorderBrush = Res("Line"), BorderThickness = new Thickness(1), Padding = new Thickness(10, 7, 10, 7),
            FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center,
        };

        private static TextBox TimeField()
        {
            var t = Field();
            t.Width = 92;
            t.FontSize = 16;
            t.HorizontalContentAlignment = HorizontalAlignment.Center;
            t.FlowDirection = FlowDirection.LeftToRight;
            t.GotKeyboardFocus += (_, _) => t.SelectAll();
            return t;
        }

        /// <summary>"Start" over its time, with −5 −1 +1 +5 second buttons.</summary>
        private StackPanel TimePanel(string label, TextBox box, bool start)
        {
            var panel = new StackPanel { HorizontalAlignment = start ? HorizontalAlignment.Left : HorizontalAlignment.Right };
            panel.Children.Add(new TextBlock
            {
                Text = label, Foreground = Res("Muted"), Margin = new Thickness(0, 0, 0, 4),
                HorizontalAlignment = start ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            });
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            Button B(string text, double by)
            {
                var b = new Button { Style = St("FlatButton"), Content = text, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(3, 0, 3, 0), MinWidth = 36 };
                b.Click += (_, _) => Nudge(start, by);
                return b;
            }
            row.Children.Add(B("−5", -5));
            row.Children.Add(B("−1", -1));
            row.Children.Add(box);
            row.Children.Add(B("+1", 1));
            row.Children.Add(B("+5", 5));
            panel.Children.Add(row);
            return panel;
        }

        private void DarkTitle()
        {
            try
            {
                int on = 1;
                DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
            }
            catch { }
        }

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // ---- The timeline ------------------------------------------------------------------------

        /// <summary>
        /// A timeline with two handles: the part between them (lit up) is what's kept.
        /// Drag a handle, or click the bar to move the nearer handle there.
        /// </summary>
        private sealed class RangeBar : Grid
        {
            private const double Pad = 12;   // room for the handles at the ends
            private readonly Canvas _canvas = new() { Height = 44, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            private readonly Rectangle _track = new() { Height = 8, RadiusX = 4, RadiusY = 4, Fill = new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x44)) };
            private readonly Rectangle _fill = new() { Height = 8, RadiusX = 4, RadiusY = 4, Fill = AccentBrush };
            private readonly Ellipse _a = Handle(), _b = Handle();
            private readonly TextBlock _zero = Tick(), _total = Tick();
            private int _dragging;   // 0 none, 1 start, 2 end

            public double Duration { get; set; } = 1;
            public double Start { get; private set; }
            public double End { get; private set; } = 1;
            public event Action? Changed;

            public RangeBar()
            {
                FlowDirection = FlowDirection.LeftToRight;
                RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                _canvas.Children.Add(_track);
                _canvas.Children.Add(_fill);
                _canvas.Children.Add(_a);
                _canvas.Children.Add(_b);
                Children.Add(_canvas);
                var ticks = new DockPanel { Margin = new Thickness(Pad - 4, 0, Pad - 4, 0) };
                DockPanel.SetDock(_zero, Dock.Left);
                DockPanel.SetDock(_total, Dock.Right);
                ticks.Children.Add(_zero);
                ticks.Children.Add(_total);
                ticks.Children.Add(new FrameworkElement());
                SetRow(ticks, 1);
                Children.Add(ticks);

                _canvas.SizeChanged += (_, _) => Layout();
                _canvas.MouseLeftButtonDown += (_, e) =>
                {
                    double t = TimeAt(e.GetPosition(_canvas).X);
                    _dragging = Math.Abs(t - Start) <= Math.Abs(t - End) ? 1 : 2;
                    if (Math.Abs(Start - End) < 0.01) _dragging = t < Start ? 1 : 2;
                    _canvas.CaptureMouse();
                    MoveTo(t);
                    e.Handled = true;
                };
                _canvas.MouseMove += (_, e) => { if (_dragging != 0) MoveTo(TimeAt(e.GetPosition(_canvas).X)); };
                _canvas.MouseLeftButtonUp += (_, _) => { _dragging = 0; _canvas.ReleaseMouseCapture(); };
                _canvas.LostMouseCapture += (_, _) => _dragging = 0;
            }

            private static Ellipse Handle() => new()
            {
                Width = 22, Height = 22, Fill = Brushes.White, Stroke = AccentBrush, StrokeThickness = 3,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.5 },
            };

            private static TextBlock Tick() => new() { FontSize = 11, Foreground = (Brush)Application.Current.FindResource("Muted") };

            /// <summary>Puts the handles at these times (kept inside the video, at least a second apart).</summary>
            public void Set(double start, double end)
            {
                start = Math.Round(Math.Clamp(start, 0, Duration));
                end = Math.Round(Math.Clamp(end, 0, Duration));
                if (end > Duration - 0.5) end = Duration;   // the real end, not a rounded one
                if (end - start < 1)
                {
                    if (start != Start) start = Math.Max(0, end - 1);
                    else end = Math.Min(Duration, start + 1);
                }
                Start = start; End = end;
                Layout();
                Changed?.Invoke();
            }

            private void MoveTo(double t)
            {
                if (_dragging == 1) Set(Math.Min(t, End - 1), End);
                else if (_dragging == 2) Set(Start, Math.Max(t, Start + 1));
            }

            private double Width0 => Math.Max(1, _canvas.ActualWidth - 2 * Pad);
            private double XOf(double t) => Pad + Width0 * (Duration > 0 ? t / Duration : 0);
            private double TimeAt(double x) => Math.Clamp((x - Pad) / Width0, 0, 1) * Duration;

            private void Layout()
            {
                double mid = _canvas.Height / 2;
                _track.Width = Width0;
                Canvas.SetLeft(_track, Pad);
                Canvas.SetTop(_track, mid - 4);
                double xa = XOf(Start), xb = XOf(End);
                _fill.Width = Math.Max(0, xb - xa);
                Canvas.SetLeft(_fill, xa);
                Canvas.SetTop(_fill, mid - 4);
                Canvas.SetLeft(_a, xa - 11); Canvas.SetTop(_a, mid - 11);
                Canvas.SetLeft(_b, xb - 11); Canvas.SetTop(_b, mid - 11);
                _zero.Text = "0:00";
                _total.Text = Fmt(Duration);
            }
        }
    }
}
