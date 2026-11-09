using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace WinPlus
{
    /// <summary>
    /// The "now playing" widget: what's playing in any app that shows in Windows' own
    /// media controls (Spotify, a browser playing YouTube, most players): the cover,
    /// the title and artist, where it is (drag to jump), and previous / play-pause / next.
    /// Wide sizes put the cover on the side, square and tall ones put it on top.
    /// </summary>
    internal sealed class MediaWidget : WidgetContent
    {
        private const string GPrev = "", GNext = "", GPlay = "", GPause = "", GNote = "";

        private readonly SolidColorBrush _accent = new(Color.FromRgb(0x4C, 0x8D, 0xFF));
        private readonly SolidColorBrush _ink = new(Color.FromRgb(0xF2, 0xF3, 0xF5));
        private readonly SolidColorBrush _inkMuted = new(Color.FromRgb(0xA0, 0xA3, 0xAA));
        private readonly SolidColorBrush _coverBack = new(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

        private readonly Grid _layout = new();
        private readonly Border _cover;
        private readonly ImageBrush _coverImage = new() { Stretch = Stretch.UniformToFill };
        private readonly TextBlock _coverNote;
        private readonly StackPanel _info = new() { VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _title, _artist, _app, _pos, _len;
        private readonly Border _seek, _fill;
        private readonly ScaleTransform _fillScale = new(0, 1);
        private bool _seeking, _seekEnabled = true;
        private double _seekTo;
        private readonly FrameworkElement _seekRow;
        private readonly Button _prev, _play, _next;
        private readonly TextBlock _playGlyph;
        private readonly StackPanel _buttons;
        private readonly TextBlock _nothing;
        private readonly DispatcherTimer _timer;

        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _session;
        private bool _running, _playing, _wide, _small;
        private TimeSpan _position, _length;
        private DateTimeOffset _positionAt;
        private string _coverKey = "";
        private int _version, _ticks;
        private string _sessionId = "";   // a newer update wins over an older one still loading

        public MediaWidget()
        {
            _coverNote = new TextBlock
            {
                Text = GNote, FontFamily = Icons, FontSize = 26, Foreground = _inkMuted,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            _cover = new Border
            {
                CornerRadius = new CornerRadius(8), Background = _coverBack, Child = _coverNote, ClipToBounds = true,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };

            _title = new TextBlock { FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = _ink, TextTrimming = TextTrimming.CharacterEllipsis };
            _artist = new TextBlock { FontSize = 12, Foreground = _inkMuted, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
            _app = new TextBlock { FontSize = 10.5, Foreground = _inkMuted, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0), Opacity = 0.8 };

            // The bar: click or drag on it to jump. (A Slider here got WPF's layout stuck in a loop.)
            _fill = new Border { CornerRadius = new CornerRadius(2), Background = _accent, RenderTransform = _fillScale };
            var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = _coverBack, Child = _fill, ClipToBounds = true };
            _seek = new Border { Background = Brushes.Transparent, Padding = new Thickness(0, 7, 0, 7), Child = track, Cursor = Cursors.Hand };
            _seek.MouseLeftButtonDown += (_, e) =>
            {
                if (!_seekEnabled) return;
                _seeking = true;
                _seek.CaptureMouse();
                DragTo(e.GetPosition(_seek).X);
                e.Handled = true;   // not a drag of the widget
            };
            _seek.MouseMove += (_, e) => { if (_seeking) DragTo(e.GetPosition(_seek).X); };
            _seek.MouseLeftButtonUp += (_, e) =>
            {
                if (!_seeking) return;
                _seeking = false;
                _seek.ReleaseMouseCapture();
                Seek();
                e.Handled = true;
            };
            _seek.LostMouseCapture += (_, _) => _seeking = false;
            _pos = new TextBlock { FontSize = 10.5, Foreground = _inkMuted, FlowDirection = FlowDirection.LeftToRight };
            _len = new TextBlock { FontSize = 10.5, Foreground = _inkMuted, FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Right };
            var times = new Grid { Margin = new Thickness(0, -4, 0, 0) };
            times.Children.Add(_pos);
            times.Children.Add(_len);
            var seekRow = new StackPanel { FlowDirection = FlowDirection.LeftToRight };
            seekRow.Children.Add(_seek);
            seekRow.Children.Add(times);
            _seekRow = seekRow;

            _playGlyph = Glyph(GPlay, 20);
            _prev = NewButton(Glyph(GPrev, 15), 32);
            _play = NewButton(_playGlyph, 40);
            _next = NewButton(Glyph(GNext, 15), 32);
            _prev.ToolTip = Lang.T("السابق");
            _next.ToolTip = Lang.T("التالي");
            _prev.Click += (_, _) => Do(s => s.TrySkipPreviousAsync().AsTask());
            _play.Click += (_, _) => Do(s => s.TryTogglePlayPauseAsync().AsTask());
            _next.Click += (_, _) => Do(s => s.TrySkipNextAsync().AsTask());
            _buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
                FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(0, 2, 0, 0),
            };
            _buttons.Children.Add(_prev);
            _buttons.Children.Add(_play);
            _buttons.Children.Add(_next);

            _info.Children.Add(_title);
            _info.Children.Add(_artist);
            _info.Children.Add(_app);
            _info.Children.Add(_seekRow);
            _info.Children.Add(_buttons);

            _nothing = new TextBlock
            {
                Text = Lang.T("ما فيه شي يشتغل"), FontSize = 12, Foreground = _inkMuted, TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };

            // One or the other is the widget's content (swapped, not hidden: a hidden grid
            // whose children change doesn't get laid out again when it's shown).
            Child = _nothing;
            SizeChanged += (_, _) => SizeCover();
            _info.SizeChanged += (_, _) => SizeCover();
            Arrange(false, false);
            ShowNothing();

            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += (_, _) =>
            {
                ShowPosition();
                if (++_ticks % 4 == 0) PickSession();   // every 2 s: is something else playing now?
            };
        }

        private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

        // ---- On / off ---------------------------------------------------------------------------

        public override async void Start()
        {
            if (_running) return;
            _running = true;
            _timer.Start();
            try
            {
                _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask();   // AsTask: back on the window's thread
                if (!_running) return;
                _manager.CurrentSessionChanged += OnCurrentSessionChanged;
                PickSession();
            }
            catch (Exception ex) { CrashLog.Write("media widget", ex); }
        }

        public override void Stop()
        {
            _running = false;
            _timer.Stop();
            if (_manager != null) _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            UseSession(null);
        }

        private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager m, CurrentSessionChangedEventArgs e) =>
            Dispatcher.BeginInvoke(() => { if (_running) PickSession(); });

        /// <summary>
        /// What to show: Windows' current one if it's playing, else whatever is playing,
        /// else Windows' current one (paused). Windows sometimes keeps a paused browser
        /// tab as "current" while music plays in another app.
        /// </summary>
        private void PickSession()
        {
            if (_manager == null || !_running) return;
            try
            {
                var current = _manager.GetCurrentSession();
                GlobalSystemMediaTransportControlsSession? pick = current;
                if (current == null || !IsPlaying(current))
                {
                    foreach (var s in _manager.GetSessions())
                        if (IsPlaying(s)) { pick = s; break; }
                }
                string id = pick?.SourceAppUserModelId ?? "";
                if (pick != null && _session != null && id == _sessionId) return;   // the same one
                if (pick == null && _session == null) return;
                _sessionId = id;
                UseSession(pick);
            }
            catch (Exception ex) { CrashLog.Write("media widget", ex); }
        }

        private static bool IsPlaying(GlobalSystemMediaTransportControlsSession s)
        {
            try { return s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
            catch { return false; }
        }

        private void UseSession(GlobalSystemMediaTransportControlsSession? session)
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnMediaChanged;
                _session.PlaybackInfoChanged -= OnPlaybackChanged;
                _session.TimelinePropertiesChanged -= OnTimelineChanged;
            }
            _session = session;
            if (session == null) { ShowNothing(); return; }
            session.MediaPropertiesChanged += OnMediaChanged;
            session.PlaybackInfoChanged += OnPlaybackChanged;
            session.TimelinePropertiesChanged += OnTimelineChanged;
            _ = ShowMedia();
            ShowPlayback();
            ShowTimeline();
        }

        // Windows calls these on its own threads.
        private void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) =>
            Dispatcher.BeginInvoke(() => { if (s == _session) _ = ShowMedia(); });
        private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) =>
            Dispatcher.BeginInvoke(() => { if (s == _session) ShowPlayback(); });
        private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) =>
            Dispatcher.BeginInvoke(() => { if (s == _session) ShowTimeline(); });

        private async void Do(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
        {
            var s = _session;
            if (s == null) return;
            try { await action(s); }
            catch (Exception ex) { CrashLog.Write("media widget", ex); }
        }

        // ---- Showing ----------------------------------------------------------------------------

        private void ShowNothing()
        {
            Child = _nothing;
            _coverKey = "";
            _coverImage.ImageSource = null;
        }

        private async Task ShowMedia()
        {
            var s = _session;
            if (s == null) return;
            int version = ++_version;
            try
            {
                var p = await s.TryGetMediaPropertiesAsync().AsTask();
                if (version != _version || s != _session || p == null) return;
                string title = p.Title ?? "";
                string artist = string.IsNullOrWhiteSpace(p.Artist) ? p.AlbumArtist ?? "" : p.Artist;
                _title.Text = title.Length > 0 ? title : Lang.T("بدون عنوان");
                _artist.Text = artist;
                _artist.Visibility = artist.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                _app.Text = AppName(s.SourceAppUserModelId);
                _app.Visibility = _app.Text.Length > 0 && !_small ? Visibility.Visible : Visibility.Collapsed;
                if (Child != _layout) Child = _layout;

                // With whether there's a picture yet: some apps (Harmoni, other WebView2 apps)
                // send the name first and the picture a moment later.
                string key = s.SourceAppUserModelId + "|" + title + "|" + artist + "|" + (p.Thumbnail != null);
                if (key == _coverKey) return;
                _coverKey = key;
                ImageSource? image = null;
                if (p.Thumbnail != null)
                {
                    using var stream = await p.Thumbnail.OpenReadAsync().AsTask();
                    using var net = stream.AsStreamForRead();
                    var copy = new MemoryStream();
                    await net.CopyToAsync(copy);
                    copy.Position = 0;
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 300;
                    bmp.StreamSource = copy;
                    bmp.EndInit();
                    bmp.Freeze();
                    image = bmp;
                }
                if (version != _version) return;
                _coverImage.ImageSource = image;
                _cover.Background = image != null ? _coverImage : _coverBack;
                _coverNote.Visibility = image != null ? Visibility.Collapsed : Visibility.Visible;
            }
            catch (Exception ex) { CrashLog.Write("media widget", ex); }
        }

        private void ShowPlayback()
        {
            var s = _session;
            if (s == null) return;
            try
            {
                var info = s.GetPlaybackInfo();
                _playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                _playGlyph.Text = _playing ? GPause : GPlay;
                _play.ToolTip = _playing ? Lang.T("إيقاف مؤقت") : Lang.T("شغّل");
                var c = info.Controls;
                _prev.IsEnabled = c.IsPreviousEnabled;
                _next.IsEnabled = c.IsNextEnabled;
                _play.IsEnabled = c.IsPlayPauseToggleEnabled || c.IsPlayEnabled || c.IsPauseEnabled;
                _seekEnabled = c.IsPlaybackPositionEnabled;
                _seek.Cursor = _seekEnabled ? Cursors.Hand : null;
                ShowTimeline();
            }
            catch (Exception ex) { CrashLog.Write("media widget", ex); }
        }

        private void ShowTimeline()
        {
            var s = _session;
            if (s == null) return;
            try
            {
                var t = s.GetTimelineProperties();
                _length = t.EndTime - t.StartTime;
                _position = t.Position - t.StartTime;
                _positionAt = t.LastUpdatedTime;
                ShowPosition();
            }
            catch (Exception ex) { CrashLog.Write("media widget", ex); }
        }

        /// <summary>The bar and the times. Apps only say where they are now and then: in between it's worked out from the clock.</summary>
        private void ShowPosition()
        {
            bool known = _length > TimeSpan.FromSeconds(1);
            _seekRow.Visibility = known && !_small ? Visibility.Visible : Visibility.Collapsed;
            if (!known) return;
            var pos = _position;
            if (_playing && _positionAt > DateTimeOffset.MinValue)
            {
                var gone = DateTimeOffset.Now - _positionAt;
                if (gone > TimeSpan.Zero && gone < TimeSpan.FromHours(12)) pos += gone;
            }
            if (pos > _length) pos = _length;
            if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
            if (!_seeking) _seekTo = pos.TotalSeconds / _length.TotalSeconds;
            _fillScale.ScaleX = _seekTo;
            _pos.Text = Time(TimeSpan.FromSeconds(_seekTo * _length.TotalSeconds));
            _len.Text = Time(_length);
        }

        /// <summary>While dragging on the bar: where it would jump to (0–1).</summary>
        private void DragTo(double x)
        {
            double w = _seek.ActualWidth;
            if (w <= 0) return;
            _seekTo = Math.Clamp(x / w, 0, 1);
            ShowPosition();
        }

        private void Seek()
        {
            var s = _session;
            if (s == null || !_seekEnabled || _length <= TimeSpan.Zero) return;
            var to = TimeSpan.FromSeconds(_seekTo * _length.TotalSeconds);
            _position = to;
            _positionAt = DateTimeOffset.Now;
            Do(x => x.TryChangePlaybackPositionAsync(to.Ticks).AsTask());
        }

        private static string Time(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

        /// <summary>A readable name from the app's id ("Spotify.exe", "MSEdge", "SpotifyAB.SpotifyMusic_…!Spotify").</summary>
        private static string AppName(string? id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            string s = id.ToLowerInvariant();
            if (s.Contains("spotify")) return "Spotify";
            if (s.Contains("msedgewebview2")) return "";   // an app built on Edge (like Harmoni): which one isn't said
            if (s.Contains("msedge")) return "Microsoft Edge";
            if (s.Contains("chrome")) return "Google Chrome";
            if (s.Contains("firefox")) return "Firefox";
            if (s.Contains("opera")) return "Opera";
            if (s.Contains("brave")) return "Brave";
            if (s.Contains("zen")) return "Zen";
            if (s.Contains("applemusic") || s.Contains("apple.music")) return "Apple Music";
            if (s.Contains("zunemusic") || s.Contains("media player")) return "Media Player";
            if (s.Contains("vlc")) return "VLC";
            // Some browsers give only a code (like "F0DC299D809B9700"): nothing to show.
            if (System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9A-Fa-f]{12,}$")) return "";
            string name = id;
            int bang = name.LastIndexOf('!');
            if (bang >= 0) name = name[(bang + 1)..];
            int slash = name.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0) name = name[(slash + 1)..];
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            return name;
        }

        // ---- Layout -----------------------------------------------------------------------------

        public override void SetCells(int w, int h) => Arrange(w > h, w <= 2 && h <= 2);

        /// <summary>Wide: the cover on the side. Square or tall: the cover on top. Small: no bar, no app name.</summary>
        private void Arrange(bool wide, bool small)
        {
            _wide = wide;
            _small = small;
            _layout.Children.Clear();
            _layout.RowDefinitions.Clear();
            _layout.ColumnDefinitions.Clear();
            if (wide)
            {
                _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                _cover.Margin = new Thickness(0, 0, 12, 0);
                Grid.SetColumn(_info, 1); Grid.SetRow(_info, 0);
                Grid.SetColumn(_cover, 0); Grid.SetRow(_cover, 0);
                _info.HorizontalAlignment = HorizontalAlignment.Stretch;
                _title.TextAlignment = _artist.TextAlignment = _app.TextAlignment = TextAlignment.Left;
                _buttons.HorizontalAlignment = HorizontalAlignment.Left;
            }
            else
            {
                _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                _cover.Margin = new Thickness(0, 2, 0, 8);
                Grid.SetColumn(_info, 0); Grid.SetRow(_info, 1);
                Grid.SetColumn(_cover, 0); Grid.SetRow(_cover, 0);
                _info.HorizontalAlignment = HorizontalAlignment.Stretch;
                _title.TextAlignment = _artist.TextAlignment = _app.TextAlignment = TextAlignment.Center;
                _buttons.HorizontalAlignment = HorizontalAlignment.Center;
            }
            _app.Visibility = small || _app.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            _layout.Children.Add(_cover);
            _layout.Children.Add(_info);
            ShowPosition();
            SizeCover();
        }

        /// <summary>The cover is a square: as tall as the widget (wide), or what's left above the text.</summary>
        private void SizeCover()
        {
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            double side;
            if (_wide) side = ActualHeight;
            else side = Math.Min(ActualWidth, ActualHeight - _info.ActualHeight - _cover.Margin.Top - _cover.Margin.Bottom);
            side = Math.Max(24, Math.Floor(side));
            _cover.Width = _cover.Height = side;
            _coverNote.FontSize = Math.Max(16, side * 0.35);
        }

        // ---- Pieces -----------------------------------------------------------------------------

        private TextBlock Glyph(string glyph, double size) => new()
        {
            Text = glyph, FontFamily = Icons, FontSize = size, Foreground = _ink,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };

        private static Button NewButton(FrameworkElement content, double size)
        {
            var b = new Button
            {
                Style = (Style)Application.Current.FindResource("FlatButton"),
                Width = size, Height = size, Padding = new Thickness(0), Content = content,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
                Margin = new Thickness(4, 0, 4, 0),
            };
            b.IsEnabledChanged += (_, _) => b.Opacity = b.IsEnabled ? 1 : 0.35;
            return b;
        }

        public override void SetAccent(Color color) =>
            _accent.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, new Duration(TimeSpan.FromMilliseconds(200))));

        public override void SetLightBehind(bool light)
        {
            var d = new Duration(TimeSpan.FromMilliseconds(200));
            _ink.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(light ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xF2, 0xF3, 0xF5), d));
            _inkMuted.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(light ? Color.FromArgb(0xB0, 0x3C, 0x3C, 0x43) : Color.FromRgb(0xA0, 0xA3, 0xAA), d));
            _coverBack.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(light ? Color.FromArgb(0x30, 0x00, 0x00, 0x00) : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), d));
        }
    }
}
