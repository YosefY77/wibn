using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace WinPlus
{
    /// <summary>
    /// The internet speed widget: download (big), upload and ping, measured against
    /// Cloudflare's speed test servers. A click (a press without moving it) runs a test;
    /// the number counts live while it runs. The last result is kept in the widget's
    /// options, and it can re-test on its own every hour.
    /// </summary>
    internal sealed class SpeedWidget : WidgetContent
    {
        private const string Server = "https://speed.cloudflare.com/";
        private static readonly TimeSpan PhaseTime = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(1.5);   // TCP is still speeding up: not counted
        private const int Streams = 6;

        private static readonly HttpClient Http = new(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = Streams + 2,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.None,
        })
        { Timeout = Timeout.InfiniteTimeSpan, DefaultRequestHeaders = { { "User-Agent", "WinPlus" } } };

        private readonly SolidColorBrush _accent = new(Color.FromRgb(0x4C, 0x8D, 0xFF));
        private readonly SolidColorBrush _ink = new(Color.FromRgb(0xF2, 0xF3, 0xF5));
        private readonly SolidColorBrush _inkMuted = new(Color.FromRgb(0xA0, 0xA3, 0xAA));
        private readonly SolidColorBrush _track = new(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

        private readonly TextBlock _title, _ago, _big, _unit, _bigLabel, _up, _ping, _hint;
        private readonly FrameworkElement _stats;
        private readonly ScaleTransform _progress = new(0, 1);
        private readonly Border _progressTrack;
        private readonly DispatcherTimer _ui = new() { Interval = TimeSpan.FromMilliseconds(150) };
        private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };

        private CancellationTokenSource? _cts;
        private Point? _pressAt;
        private Window? _window;

        // What the running test is at (written by the test, read by the UI timer).
        private enum Phase { Ping, Down, Up }
        private volatile int _phase;
        private double _phaseProgress, _live;
        private double? _testPing, _testDown;

        public SpeedWidget()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Title … how long ago
            var top = new DockPanel { Margin = new Thickness(2, 0, 2, 0) };
            _ago = new TextBlock { FontSize = 11, Foreground = _inkMuted, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(_ago, Dock.Right);
            top.Children.Add(_ago);
            _title = new TextBlock
            {
                Text = Lang.T("سرعة الإنترنت"), FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = _ink,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            top.Children.Add(_title);
            grid.Children.Add(top);

            // The big number: download (or, while testing, whatever is being measured)
            _big = new TextBlock { Text = "—", FontSize = 46, FontWeight = FontWeights.SemiBold, Foreground = _ink, HorizontalAlignment = HorizontalAlignment.Center };
            _unit = new TextBlock { Text = "Mbps", FontSize = 14, Foreground = _inkMuted, Margin = new Thickness(6, 0, 0, 10), VerticalAlignment = VerticalAlignment.Bottom };
            var number = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, FlowDirection = FlowDirection.LeftToRight };
            number.Children.Add(_big);
            number.Children.Add(_unit);
            _bigLabel = new TextBlock { FontSize = 13, Foreground = _inkMuted, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -4, 0, 0) };
            var middle = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            middle.Children.Add(number);
            middle.Children.Add(_bigLabel);
            var fit = new Viewbox { Child = middle, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, Margin = new Thickness(4, 2, 4, 2), MaxHeight = 110 };
            Grid.SetRow(fit, 1);
            grid.Children.Add(fit);

            // The progress while testing
            var bar = new Border { CornerRadius = new CornerRadius(1.5), Background = _accent, RenderTransform = _progress };
            _progressTrack = new Border
            {
                Height = 3, CornerRadius = new CornerRadius(1.5), Background = _track, Margin = new Thickness(6, 2, 6, 6),
                Child = bar, ClipToBounds = true, Visibility = Visibility.Hidden,
            };
            Grid.SetRow(_progressTrack, 2);
            grid.Children.Add(_progressTrack);

            // ↑ upload   ◷ ping   (or the hint when there's nothing yet)
            _up = new TextBlock();
            _ping = new TextBlock();
            var stats = new Grid { Margin = new Thickness(2, 0, 2, 0) };
            stats.ColumnDefinitions.Add(new ColumnDefinition());
            stats.ColumnDefinitions.Add(new ColumnDefinition());
            stats.Children.Add(Stat(Lang.T("الرفع"), _up));
            var pingCell = Stat(Lang.T("البنق"), _ping);
            Grid.SetColumn(pingCell, 1);
            stats.Children.Add(pingCell);
            _stats = stats;
            _hint = new TextBlock
            {
                FontSize = 12, Foreground = _inkMuted, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var bottom = new Grid();
            bottom.Children.Add(_stats);
            bottom.Children.Add(_hint);
            Grid.SetRow(bottom, 3);
            grid.Children.Add(bottom);

            Child = grid;
            Background = Brushes.Transparent;   // a click anywhere in it counts
            Cursor = Cursors.Hand;
            ToolTip = Lang.T("اضغط عشان تقيس السرعة");

            _ui.Tick += (_, _) => ShowLive();
            _clock.Tick += (_, _) => { ShowAgo(); if (Auto && !Testing && Age() is { } a && a >= TimeSpan.FromHours(1)) Run(); };
            PreviewMouseLeftButtonDown += (_, e) => _pressAt = PointToScreen(e.GetPosition(this));
        }

        /// <summary>A small label over its value.</summary>
        private FrameworkElement Stat(string name, TextBlock value)
        {
            value.FontSize = 15;
            value.FontWeight = FontWeights.SemiBold;
            value.Foreground = _ink;
            value.Text = "—";
            value.HorizontalAlignment = HorizontalAlignment.Center;
            var p = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
            p.Children.Add(new TextBlock { Text = name, FontSize = 11, Foreground = _inkMuted, HorizontalAlignment = HorizontalAlignment.Center });
            p.Children.Add(value);
            return p;
        }

        // ---- The widget's life ------------------------------------------------------------------

        public override void Start()
        {
            ShowResult();
            _clock.Start();
            if (_window == null && Window.GetWindow(this) is { } w)
            {
                _window = w;
                // The widget's window takes the mouse while it might be dragged, so the let-go is heard there.
                w.PreviewMouseLeftButtonUp += (_, e) =>
                {
                    if (_pressAt is not { } at) return;
                    _pressAt = null;
                    var now = PointToScreen(e.GetPosition(this));
                    if (Math.Abs(now.X - at.X) < 5 && Math.Abs(now.Y - at.Y) < 5 && !Testing) Run();
                };
            }
            if (Auto && !Testing && (Age() is not { } age || age >= TimeSpan.FromHours(1))) Run();
        }

        public override void Stop()
        {
            _clock.Stop();
            _cts?.Cancel();
        }

        public override void SetCells(int w, int h)
        {
            _title.Visibility = h >= 3 || w >= 4 ? Visibility.Visible : Visibility.Collapsed;
            _ago.Visibility = w >= 3 ? Visibility.Visible : Visibility.Collapsed;
            _bigLabel.Visibility = h >= 3 ? Visibility.Visible : Visibility.Collapsed;
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
            _track.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(light ? Color.FromArgb(0x30, 0x00, 0x00, 0x00) : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), d));
        }

        public override IEnumerable<Control> MenuItems()
        {
            if (Testing)
            {
                var stop = new MenuItem { Header = Lang.T("وقّف القياس") };
                stop.Click += (_, _) => _cts?.Cancel();
                yield return stop;
            }
            else
            {
                var run = new MenuItem { Header = Lang.T("قِس الحين") };
                run.Click += (_, _) => Run();
                yield return run;
            }
            var auto = new MenuItem { Header = Lang.T("قِس كل ساعة"), IsCheckable = true, IsChecked = Auto };
            auto.Click += (_, _) => { Auto = auto.IsChecked; if (Auto && !Testing && (Age() is not { } a || a >= TimeSpan.FromHours(1))) Run(); };
            yield return auto;
        }

        // ---- The saved result ---------------------------------------------------------------------

        private string Opt(string key) => Info?.Options.TryGetValue(key, out var v) == true ? v : "";

        private double? Num(string key) =>
            double.TryParse(Opt(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

        private bool Auto
        {
            get => Opt("auto") == "1";
            set { if (Info == null) return; Info.Options["auto"] = value ? "1" : "0"; Widgets.Save(); }
        }

        private TimeSpan? Age() =>
            long.TryParse(Opt("at"), out var ticks) ? DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) : null;

        private void Keep(double ping, double down, double up)
        {
            if (Info == null) return;
            Info.Options["ping"] = ping.ToString("0.#", CultureInfo.InvariantCulture);
            Info.Options["down"] = down.ToString("0.##", CultureInfo.InvariantCulture);
            Info.Options["up"] = up.ToString("0.##", CultureInfo.InvariantCulture);
            Info.Options["at"] = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
            Widgets.Save();
        }

        // ---- Showing --------------------------------------------------------------------------------

        private static string Mbps(double v) =>
            v >= 100 ? Math.Round(v).ToString("0", CultureInfo.InvariantCulture) : v.ToString(v >= 10 ? "0.0" : "0.00", CultureInfo.InvariantCulture);

        private void ShowResult(string? problem = null)
        {
            _progressTrack.Visibility = Visibility.Hidden;
            double? down = Num("down"), up = Num("up"), ping = Num("ping");
            _big.Text = down is { } d ? Mbps(d) : "—";
            _bigLabel.Text = Lang.T("التحميل");
            _up.Text = up is { } u ? Mbps(u) : "—";
            _ping.Text = ping is { } p ? $"{Math.Round(p)} ms" : "—";
            bool none = down == null;
            _stats.Visibility = none && problem == null ? Visibility.Hidden : Visibility.Visible;
            _hint.Visibility = none || problem != null ? Visibility.Visible : Visibility.Collapsed;
            if (problem != null) { _stats.Visibility = Visibility.Hidden; _hint.Text = problem; }
            else _hint.Text = Lang.T("اضغط عشان تقيس");
            ShowAgo();
        }

        private void ShowAgo()
        {
            if (Testing) { _ago.Text = Lang.T("يقيس…"); return; }
            if (Age() is not { } a) { _ago.Text = ""; return; }
            _ago.Text = a.TotalMinutes < 1 ? Lang.T("الحين")
                : a.TotalHours < 1 ? Lang.F("قبل {0} د", (int)a.TotalMinutes)
                : a.TotalDays < 1 ? Lang.F("قبل {0} س", (int)a.TotalHours)
                : Lang.F("قبل {0} يوم", (int)a.TotalDays);
        }

        /// <summary>The running test's numbers, a few times a second.</summary>
        private void ShowLive()
        {
            var phase = (Phase)_phase;
            double done = Volatile.Read(ref _phaseProgress);
            double overall = phase switch { Phase.Ping => done * 0.1, Phase.Down => 0.1 + done * 0.45, _ => 0.55 + done * 0.45 };
            _progress.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(Math.Clamp(overall, 0, 1), TimeSpan.FromMilliseconds(150)));

            double live = Volatile.Read(ref _live);
            switch (phase)
            {
                case Phase.Ping:
                    _big.Text = "…";
                    _bigLabel.Text = Lang.T("البنق");
                    break;
                case Phase.Down:
                    _big.Text = live > 0 ? Mbps(live) : "…";
                    _bigLabel.Text = Lang.T("التحميل");
                    break;
                case Phase.Up:
                    if (_testDown is { } d) _big.Text = Mbps(d);
                    _bigLabel.Text = Lang.T("التحميل");
                    _up.Text = live > 0 ? Mbps(live) : "…";
                    break;
            }
            if (_testPing is { } p) _ping.Text = $"{Math.Round(p)} ms";
        }

        // ---- The test --------------------------------------------------------------------------------

        private bool Testing => _cts != null;

        private async void Run()
        {
            if (Testing) return;
            var cts = new CancellationTokenSource();
            _cts = cts;
            _testPing = _testDown = null;
            _live = 0; _phaseProgress = 0; _phase = (int)Phase.Ping;
            _progress.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _progress.ScaleX = 0;
            _progressTrack.Visibility = Visibility.Visible;
            _hint.Visibility = Visibility.Collapsed;
            _stats.Visibility = Visibility.Visible;
            _up.Text = "—"; _ping.Text = "—";
            ShowAgo();
            _ui.Start();
            string? problem = null;
            try
            {
                var ct = cts.Token;
                double ping = await Task.Run(() => MeasurePing(ct), ct);
                _testPing = ping;
                _phase = (int)Phase.Down; _phaseProgress = 0; _live = 0;
                double down = await Task.Run(() => MeasureFlow(false, ct), ct);
                _testDown = down;
                _phase = (int)Phase.Up; _phaseProgress = 0; _live = 0;
                double up = await Task.Run(() => MeasureFlow(true, ct), ct);
                Keep(ping, down, up);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                CrashLog.Write("speed widget", ex);
                problem = Lang.T("ما قدرت أوصل للإنترنت");
            }
            finally
            {
                _ui.Stop();
                _cts = null;
                cts.Dispose();
                ShowResult(problem);
            }
        }

        /// <summary>The middle of several tiny requests on an open connection (the first one,
        /// which also sets up the connection, isn't counted).</summary>
        private async Task<double> MeasurePing(CancellationToken ct)
        {
            const int Count = 9;
            var times = new List<double>();
            for (int i = 0; i <= Count; i++)
            {
                var sw = Stopwatch.StartNew();
                using (var r = await Http.GetAsync(Server + "__down?bytes=0", HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    sw.Stop();
                    r.EnsureSuccessStatusCode();
                }
                if (i > 0) times.Add(sw.Elapsed.TotalMilliseconds);
                Volatile.Write(ref _phaseProgress, i / (double)Count);
            }
            times.Sort();
            return times[times.Count / 2];
        }

        /// <summary>Several connections downloading (or uploading) at once for a few seconds.
        /// The speed is what moved after the warm-up, divided by the time since then.</summary>
        private async Task<double> MeasureFlow(bool upload, CancellationToken outer)
        {
            long bytes = 0;
            using var stopAt = CancellationTokenSource.CreateLinkedTokenSource(outer);
            stopAt.CancelAfter(PhaseTime);
            var ct = stopAt.Token;
            var clock = Stopwatch.StartNew();
            Exception? firstError = null;
            int worked = 0;

            async Task Worker()
            {
                var buffer = new byte[64 * 1024];
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        if (upload)
                        {
                            var content = new CountingContent(10_000_000, n => Interlocked.Add(ref bytes, n));
                            using var r = await Http.PostAsync(Server + "__up", content, ct);
                            r.EnsureSuccessStatusCode();
                        }
                        else
                        {
                            using var r = await Http.GetAsync(Server + "__down?bytes=25000000", HttpCompletionOption.ResponseHeadersRead, ct);
                            r.EnsureSuccessStatusCode();
                            await using var s = await r.Content.ReadAsStreamAsync(ct);
                            int n;
                            while ((n = await s.ReadAsync(buffer, ct)) > 0) Interlocked.Add(ref bytes, n);
                        }
                        Interlocked.Exchange(ref worked, 1);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (IOException) when (ct.IsCancellationRequested) { }
                catch (HttpRequestException) when (ct.IsCancellationRequested) { }
                catch (Exception ex) { Interlocked.CompareExchange(ref firstError, ex, null); }
            }

            var workers = Enumerable.Range(0, Streams).Select(_ => Task.Run(Worker)).ToArray();

            // Watch it: the live speed over the last second, and where the warm-up ended.
            var samples = new Queue<(double t, long b)>();
            double warmT = 0; long warmB = -1;
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(100, ct); } catch (OperationCanceledException) { break; }
                double t = clock.Elapsed.TotalSeconds;
                long b = Interlocked.Read(ref bytes);
                samples.Enqueue((t, b));
                while (samples.Count > 1 && t - samples.Peek().t > 1.0) samples.Dequeue();
                var (t0, b0) = samples.Peek();
                if (t - t0 > 0.2) Volatile.Write(ref _live, (b - b0) * 8 / 1e6 / (t - t0));
                if (warmB < 0 && t >= Warmup.TotalSeconds) { warmT = t; warmB = b; }
                Volatile.Write(ref _phaseProgress, t / PhaseTime.TotalSeconds);
            }
            double end = clock.Elapsed.TotalSeconds;
            long total = Interlocked.Read(ref bytes);
            await Task.WhenAll(workers);
            outer.ThrowIfCancellationRequested();
            if (total == 0) throw firstError ?? new IOException("nothing moved");

            if (warmB < 0 || end - warmT < 0.5) { warmT = 0; warmB = 0; }
            return (total - warmB) * 8 / 1e6 / (end - warmT);
        }

        /// <summary>A body of the given size that counts its bytes as they're sent.</summary>
        private sealed class CountingContent : HttpContent
        {
            private static readonly byte[] Chunk = MakeChunk();
            private readonly long _size;
            private readonly Action<int> _sent;

            public CountingContent(long size, Action<int> sent)
            {
                _size = size;
                _sent = sent;
                Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            }

            private static byte[] MakeChunk()
            {
                var b = new byte[64 * 1024];
                Random.Shared.NextBytes(b);   // not compressible
                return b;
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
                SerializeToStreamAsync(stream, context, CancellationToken.None);

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
            {
                long left = _size;
                while (left > 0)
                {
                    int n = (int)Math.Min(Chunk.Length, left);
                    await stream.WriteAsync(Chunk.AsMemory(0, n), ct);
                    _sent(n);
                    left -= n;
                }
            }

            protected override bool TryComputeLength(out long length)
            {
                length = _size;
                return true;
            }
        }
    }
}
