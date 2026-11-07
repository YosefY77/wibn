using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WinPlus
{
    /// <summary>
    /// The round "convert to…" menu that opens under the mouse while a file is being
    /// dragged (Space). Drop the file on a format to convert it.
    ///
    /// Look and motion follow Apple's guidelines: a dark translucent material, one
    /// accent color for the hovered choice, generous touch targets, clear labels,
    /// and short spring-like animations (open, hover, convert, done) that never
    /// block the user.
    /// </summary>
    internal sealed class RadialMenu : Window
    {
        private const double Size = 340, Outer = 150, Inner = 60, Center = Size / 2;
        private const double Gap = 3.0;  // px between choices, the same all the way along
        private const double Corner = 7.0;  // roundness of each choice's corners

        private static readonly Color Material = Color.FromArgb(0xE6, 0x1F, 0x1F, 0x22);
        // Solid (not see-through): the rounded outline is drawn over the fill, and a
        // see-through color showed up twice there as a lighter rim.
        private static readonly Color SegmentIdle = Color.FromRgb(0x37, 0x37, 0x3A);
        private static readonly Color Accent = Color.FromRgb(0x0A, 0x84, 0xFF);   // Apple system blue (dark)
        private static readonly Color Primary = Color.FromRgb(0xF5, 0xF5, 0xF7);
        private static readonly Color Secondary = Color.FromArgb(0x99, 0xEB, 0xEB, 0xF5);
        private static readonly FontFamily Font = new("Segoe UI Variable Display, Segoe UI");

        private readonly Point _screenPoint;
        private readonly Grid _root = new() { Width = Size, Height = Size };
        private readonly ScaleTransform _rootScale = new(0.82, 0.82);
        private readonly Canvas _ring = new() { Width = Size, Height = Size };
        private readonly Ellipse _hub = new();
        private readonly TextBlock _title = new();
        private readonly TextBlock _subtitle = new();
        private readonly System.Windows.Shapes.Path _progressArc = new();
        private readonly List<Segment> _segments = new();
        private Ellipse _disc = null!;
        private LiquidGlass? _glass;
        private bool _glassy;   // the disc is liquid glass: choices are see-through until hovered

        private string[]? _files;
        private Converter.Kind _kind;
        private int _hover = -1;
        private bool _converting, _closing;
        private CancellationTokenSource? _cts;

        private sealed class Segment
        {
            public Converter.Target Target = null!;
            public System.Windows.Shapes.Path Shape = null!;
            public SolidColorBrush Fill = null!;
            public ScaleTransform Scale = null!;
            public Grid Group = null!;
            public TextBlock Label = null!;
            public TextBlock? Note;
        }

        private readonly MediaTools.Mode _mode;

        public RadialMenu(Point screenPoint, MediaTools.Mode mode = MediaTools.Mode.Convert)
        {
            _screenPoint = screenPoint;
            _mode = mode;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Width = Size;
            Height = Size;
            Left = -20000;   // placed under the mouse once the DPI is known
            Top = -20000;
            AllowDrop = true;
            FlowDirection = FlowDirection.LeftToRight;
            FontFamily = Font;

            BuildChrome();
            Content = _root;

            SourceInitialized += (_, _) => PlaceAndStyle();
            Loaded += (_, _) => { AnimateOpen(); StartGlass(); };
            Closed += (_, _) => _glass?.Dispose();
            DragEnter += OnDragEnter;
            DragOver += OnDragOver;
            DragLeave += (_, _) => SetHover(-1);
            Drop += OnDrop;
        }

        // ---- Build -------------------------------------------------------------------

        private void BuildChrome()
        {
            _root.RenderTransformOrigin = new Point(0.5, 0.5);
            _root.RenderTransform = _rootScale;
            _root.Opacity = 0;

            // The material disc (with a soft shadow) everything sits on.
            _disc = new Ellipse
            {
                Width = Outer * 2 + 12, Height = Outer * 2 + 12,
                Fill = new SolidColorBrush(Material),
                Effect = new DropShadowEffect { BlurRadius = 34, ShadowDepth = 8, Direction = 270, Opacity = 0.45, Color = Colors.Black },
            };
            _root.Children.Add(_disc);
            _root.Children.Add(_ring);

            _hub.Width = _hub.Height = Inner * 2 - 10;
            _hub.Fill = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            _root.Children.Add(_hub);

            _progressArc.Stroke = new SolidColorBrush(Accent);
            _progressArc.StrokeThickness = 4;
            _progressArc.StrokeStartLineCap = _progressArc.StrokeEndLineCap = PenLineCap.Round;
            _progressArc.Visibility = Visibility.Collapsed;
            _progressArc.Width = _progressArc.Height = Size;
            _root.Children.Add(_progressArc);

            var text = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            _title.Foreground = new SolidColorBrush(Primary);
            _title.FontSize = 20;
            _title.FontWeight = FontWeights.SemiBold;
            _title.TextAlignment = TextAlignment.Center;
            _title.HorizontalAlignment = HorizontalAlignment.Center;
            _subtitle.Foreground = new SolidColorBrush(Secondary);
            _subtitle.FontSize = 11;
            _subtitle.TextAlignment = TextAlignment.Center;
            _subtitle.HorizontalAlignment = HorizontalAlignment.Center;
            _subtitle.Margin = new Thickness(0, 2, 0, 0);
            _subtitle.MaxWidth = Inner * 2 - 24;
            _subtitle.TextWrapping = TextWrapping.Wrap;
            text.Children.Add(_title);
            text.Children.Add(_subtitle);
            _root.Children.Add(text);

            SetCenter("…", "");
        }

        private void SetCenter(string title, string subtitle)
        {
            _title.Text = title;
            _subtitle.Text = subtitle;
            _subtitle.Visibility = subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BuildSegments(Converter.Target[] targets)
        {
            _ring.Children.Clear();
            _segments.Clear();
            int n = targets.Length;
            double step = 360.0 / n;
            for (int i = 0; i < n; i++)
            {
                // First choice centered at the top, then clockwise.
                double a0 = -90 - step / 2 + i * step;
                double a1 = a0 + step;
                var fill = new SolidColorBrush(_glassy ? Colors.White : SegmentIdle);
                // Rounded corners: the shape is drawn a bit smaller and outlined in its own
                // color with round joins, which rounds every corner by Corner px.
                var shape = new System.Windows.Shapes.Path
                {
                    Data = Sector(a0, a1), Fill = fill, Stroke = fill,
                    StrokeThickness = Corner * 2, StrokeLineJoin = PenLineJoin.Round,
                    // On glass: a faint frosted pane (the whole shape fades as one, so the
                    // rounded outline doesn't show as a lighter rim).
                    Opacity = _glassy ? GlassIdleOpacity : 1,
                };

                double mid = (a0 + a1) / 2 * Math.PI / 180, rad = (Inner + Outer) / 2 + 2;
                var label = new TextBlock
                {
                    Text = targets[i].Label, FontSize = 15, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Primary), HorizontalAlignment = HorizontalAlignment.Center,
                };
                var labels = new StackPanel { IsHitTestVisible = false, Width = 80 };
                labels.Children.Add(label);
                TextBlock? note = null;
                if (targets[i].Note != null)
                {
                    note = new TextBlock
                    {
                        Text = targets[i].Note, FontSize = 11, Foreground = new SolidColorBrush(Secondary),
                        HorizontalAlignment = HorizontalAlignment.Center, FlowDirection = Lang.Flow,
                    };
                    labels.Children.Add(note);
                }
                labels.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(labels, Center + Math.Cos(mid) * rad - 40);
                Canvas.SetTop(labels, Center + Math.Sin(mid) * rad - labels.DesiredSize.Height / 2);

                var scale = new ScaleTransform(0.9, 0.9, Center, Center);
                var group = new Grid { Width = Size, Height = Size, RenderTransform = scale, Opacity = 0 };
                var canvas = new Canvas { Width = Size, Height = Size };
                canvas.Children.Add(shape);
                canvas.Children.Add(labels);
                group.Children.Add(canvas);
                _ring.Children.Add(group);

                var seg = new Segment { Target = targets[i], Shape = shape, Fill = fill, Scale = scale, Group = group, Label = label, Note = note };
                _segments.Add(seg);

                // Staggered entrance, like a dial unfolding.
                var delay = TimeSpan.FromMilliseconds(18 * i);
                Animate(group, OpacityProperty, 1, 220, delay);
                Animate(scale, ScaleTransform.ScaleXProperty, 1, 260, delay, new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut });
                Animate(scale, ScaleTransform.ScaleYProperty, 1, 260, delay, new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut });
            }
        }

        private static Geometry Sector(double a0, double a1)
        {
            Point P(double r, double deg) => new(Center + r * Math.Cos(deg * Math.PI / 180), Center + r * Math.Sin(deg * Math.PI / 180));
            // Trim each side by half the gap measured in pixels (not degrees), so the
            // line between two choices is as thick near the middle as at the edge.
            // (Everything is inset by Corner; the round outline adds it back.)
            double outer = Outer - Corner, inner = Inner + Corner, half = Gap / 2 + Corner;
            double Trim(double r) => Math.Asin(half / r) * 180 / Math.PI;
            double o0 = a0 + Trim(outer), o1 = a1 - Trim(outer);
            double i0 = a0 + Trim(inner), i1 = a1 - Trim(inner);
            var fig = new PathFigure { StartPoint = P(outer, o0), IsClosed = true };
            fig.Segments.Add(new ArcSegment(P(outer, o1), new System.Windows.Size(outer, outer), 0, o1 - o0 > 180, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(P(inner, i1), true));
            fig.Segments.Add(new ArcSegment(P(inner, i0), new System.Windows.Size(inner, inner), 0, i1 - i0 > 180, SweepDirection.Counterclockwise, true));
            var g = new PathGeometry();
            g.Figures.Add(fig);
            g.Freeze();
            return g;
        }

        // ---- Window placement / style ----------------------------------------------------

        private void PlaceAndStyle()
        {
            var source = PresentationSource.FromVisual(this);
            var m = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var p = m.Transform(_screenPoint);
            Left = p.X - Size / 2;
            Top = p.Y - Size / 2;

            // Never take focus from the app you're dragging from; no Alt+Tab entry.
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(hwnd, -20);
            SetWindowLong(hwnd, -20, ex | 0x08000000 /* NOACTIVATE */ | 0x00000080 /* TOOLWINDOW */);
        }

        private void AnimateOpen()
        {
            Animate(_root, OpacityProperty, 1, 180);
            var spring = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut };
            Animate(_rootScale, ScaleTransform.ScaleXProperty, 1, 300, null, spring);
            Animate(_rootScale, ScaleTransform.ScaleYProperty, 1, 300, null, spring);
        }

        public void CloseAnimated(int delayMs = 0)
        {
            if (_closing) return;
            _closing = true;
            _cts?.Cancel();
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, delayMs)) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
                Animate(_rootScale, ScaleTransform.ScaleXProperty, 0.9, 160, null, ease);
                Animate(_rootScale, ScaleTransform.ScaleYProperty, 0.9, 160, null, ease);
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease };
                fade.Completed += (_, _) => Close();
                _root.BeginAnimation(OpacityProperty, fade);
            };
            t.Start();
        }

        /// <summary>The mouse button went up somewhere: if nothing was dropped on us, go away.</summary>
        public void OnMouseReleased()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            t.Tick += (_, _) => { t.Stop(); if (!_converting) CloseAnimated(); };
            t.Start();
        }

        /// <summary>Space again: close (a conversion already running carries on).</summary>
        public void Dismiss()
        {
            if (!_converting) CloseAnimated();
        }

        /// <summary>Esc: stop.</summary>
        public void Cancel()
        {
            if (_converting) SetCenter("✕", "");
            CloseAnimated(_converting ? 400 : 0);
        }

        // ---- Drag & drop --------------------------------------------------------------------

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (_converting || _closing) { e.Effects = DragDropEffects.None; return; }
            if (_files == null)
            {
                // Archive (Z) takes folders too; the other menus only files.
                var files = (e.Data.GetData(DataFormats.FileDrop) as string[])
                    ?.Where(p => File.Exists(p) || (_mode == MediaTools.Mode.Archive && Directory.Exists(p))).ToArray();
                if (files == null || files.Length == 0)
                {
                    SetCenter(Lang.T("؟"), "");
                    CloseAnimated(900);
                    return;
                }
                if (_mode == MediaTools.Mode.Archive)
                {
                    // All of them go into one archive.
                    _files = files;
                    SetCenter(files.Length == 1 ? Lang.T("ملف مضغوط") : Lang.F("{0} عناصر", files.Length), "");
                    BuildSegments(MediaTools.OptionsFor(_mode, files[0]));
                    OnDragOver(sender, e);
                    return;
                }
                if (_mode != MediaTools.Mode.Convert)
                {
                    // Resize / compress: the choices depend on the file type.
                    var options = MediaTools.OptionsFor(_mode, files[0]);
                    string first = System.IO.Path.GetExtension(files[0]).ToLowerInvariant();
                    if (options.Length == 0)
                    {
                        SetCenter(first.TrimStart('.').ToUpperInvariant(), "");
                        CloseAnimated(1200);
                        return;
                    }
                    var labels = string.Join("|", options.Select(o => o.Ext));
                    _files = files.Where(f => string.Join("|", MediaTools.OptionsFor(_mode, f).Select(o => o.Ext)) == labels).ToArray();
                    SetCenter(first.TrimStart('.').ToUpperInvariant(), "");
                    BuildSegments(options);
                    OnDragOver(sender, e);
                    return;
                }
                var kind = Converter.EffectiveKind(files[0]);
                if (kind == null)
                {
                    SetCenter(System.IO.Path.GetExtension(files[0]).TrimStart('.').ToUpperInvariant(), "");
                    CloseAnimated(1200);
                    return;
                }
                _kind = kind.Value;
                _files = files.Where(f => Converter.EffectiveKind(f) == _kind).ToArray();
                string ext = System.IO.Path.GetExtension(_files[0]).TrimStart('.').ToUpperInvariant();
                SetCenter(ext, "");  // just the file's own format
                BuildSegments(Converter.TargetsFor(_kind, _files[0]));
            }
            OnDragOver(sender, e);
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            int index = _converting || _closing || _files == null ? -1 : SegmentAt(e.GetPosition(_root));
            SetHover(index);
            e.Effects = index >= 0 ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private int SegmentAt(Point p)
        {
            double dx = p.X - Center, dy = p.Y - Center;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < Inner || dist > Outer + 8 || _segments.Count == 0) return -1;
            double step = 360.0 / _segments.Count;
            double angle = Math.Atan2(dy, dx) * 180 / Math.PI;      // -180..180, 0 = right
            double fromTop = (angle + 90 + step / 2 + 720) % 360;   // 0 at the start of the first segment
            return Math.Min(_segments.Count - 1, (int)(fromTop / step));
        }

        private void SetHover(int index)
        {
            if (index == _hover) return;
            if (_hover >= 0 && _hover < _segments.Count) Highlight(_segments[_hover], false);
            _hover = index;
            if (index >= 0) Highlight(_segments[index], true);
        }

        private const double GlassIdleOpacity = 0.16;

        /// <summary>Liquid glass behind the choices: what's under the menu, live, bent at the rim.</summary>
        private void StartGlass()
        {
            try
            {
                var settings = GlassSettings.Menu(_mode);
                if (!settings.On) return;
                double offset = (Size - _disc.Width) / 2;
                _glass = new LiquidGlass(this, this, _disc, new Thickness(offset, offset, 0, 0), _disc.Width / 2, settings);
                if (!_glass.Available) { _glass = null; return; }
                _glassy = true;
                _disc.Stroke = new LinearGradientBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 90);
                _disc.StrokeThickness = 1;
                foreach (var s in _segments) { s.Fill.Color = Colors.White; s.Shape.Opacity = GlassIdleOpacity; }
                // After the window has moved under the mouse (so the right area is copied).
                Dispatcher.BeginInvoke(new Action(() => _glass?.Start()), DispatcherPriority.Background);
            }
            catch (Exception ex) { CrashLog.Write("liquid glass (menu)", ex); _glass = null; _glassy = false; }
        }

        private void Highlight(Segment s, bool on)
        {
            var color = new ColorAnimation(on ? Accent : (_glassy ? Colors.White : SegmentIdle), TimeSpan.FromMilliseconds(120));
            s.Fill.BeginAnimation(SolidColorBrush.ColorProperty, color);
            if (_glassy)
                s.Shape.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : GlassIdleOpacity, TimeSpan.FromMilliseconds(120)));
            var ease = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut };
            Animate(s.Scale, ScaleTransform.ScaleXProperty, on ? 1.045 : 1, 180, null, ease);
            Animate(s.Scale, ScaleTransform.ScaleYProperty, on ? 1.045 : 1, 180, null, ease);
            s.Label.Foreground = new SolidColorBrush(on ? Colors.White : Primary);
            if (s.Note != null) s.Note.Foreground = new SolidColorBrush(on ? Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF) : Secondary);
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            int index = _files == null || _converting ? -1 : SegmentAt(e.GetPosition(_root));
            e.Effects = index >= 0 ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            if (index < 0) { CloseAnimated(); return; }
            var target = _segments[index].Target;
            var files = _files!;
            var kind = _kind;
            var mode = _mode;
            // The menu goes away at once; the work happens quietly in the background.
            CloseAnimated();
            Application.Current.Dispatcher.BeginInvoke(new Action(() => _ = mode switch
            {
                MediaTools.Mode.Convert => ConvertInBackground(files, kind, target),
                MediaTools.Mode.Archive => ArchiveInBackground(files, target),
                _ => ToolInBackground(mode, files, target),
            }));
        }

        /// <summary>Packs everything into one archive next to it. Only says something if it didn't work.</summary>
        private static async Task ArchiveInBackground(string[] items, Converter.Target format)
        {
            try
            {
                string made;
                try { made = await Archiver.CreateAsync(items, format.Ext); }
                catch (Archiver.SevenZipMissingException)
                {
                    await Archiver.Download7zAsync(null, default);   // about 600 KB, only once
                    made = await Archiver.CreateAsync(items, format.Ext);
                }
                FileUndo.RecordNew(made);   // Ctrl+Z removes it again
            }
            catch (Exception ex)
            {
                CrashLog.Write($"archive {items.Length} item(s) → {format.Label}", ex);
                MessageBox.Show(ex is HttpRequestException
                                    ? Lang.T("ملف 7Z يحتاج أداة 7-Zip الصغيرة وما قدرت أحمّلها. تأكد من النت.")
                                    : Lang.T("ما زبط الضغط. التفاصيل في السجل."),
                                "Wibn", MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK, Lang.MsgOptions);
            }
        }

        /// <summary>Resize / compress in the background. Only says something if it didn't work.</summary>
        private static async Task ToolInBackground(MediaTools.Mode mode, string[] files, Converter.Target option)
        {
            using var undo = FileUndo.Batch();   // all the files of one drop: one Ctrl+Z
            int failed = 0, notSmaller = 0;
            for (int i = 0; i < files.Length; i++)
            {
                try
                {
                    var spot = DesktopIcons.GetPosition(files[i]);
                    string result = await MediaTools.RunAsync(mode, files[i], option);
                    if (spot is DesktopIcons.POINT pt && !string.Equals(result, files[i], StringComparison.OrdinalIgnoreCase))
                        _ = PlaceIconAsync(result, pt);
                }
                catch (Converter.NotSmallerException) { notSmaller++; }
                catch (Converter.FfmpegMissingException)
                {
                    if (!await DownloadFfmpegQuietlyAsync()) return;
                    i--;
                }
                catch (Exception ex)
                {
                    failed++;
                    CrashLog.Write($"{mode} {System.IO.Path.GetFileName(files[i])} → {option.Label}", ex);
                }
            }
            string? text = null;
            if (failed > 0) text = failed == files.Length ? Lang.T("ما زبط. التفاصيل في السجل.") : Lang.F("ما زبط {0} من {1}. التفاصيل في السجل.", failed, files.Length);
            else if (notSmaller > 0) text = notSmaller == 1 && files.Length == 1
                ? Lang.T("الملف مضغوط أصلًا، وأي ضغط أكثر ما يصغّره. خليته مثل ما هو.")
                : Lang.F("{0} ملف مضغوط أصلًا وخليتها مثل ما هي.", notSmaller);
            if (text != null)
                MessageBox.Show(text, "Wibn", MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK,
                                Lang.MsgOptions);
        }

        /// <summary>Converts without any window. Only says something if it fails.</summary>
        private static async Task ConvertInBackground(string[] files, Converter.Kind kind, Converter.Target target)
        {
            using var undo = FileUndo.Batch();   // all the files of one drop: one Ctrl+Z
            int failed = 0;
            for (int i = 0; i < files.Length; i++)
            {
                try
                {
                    var spot = DesktopIcons.GetPosition(files[i]);
                    string result = await Converter.ConvertAsync(files[i], kind, target, null, CancellationToken.None);
                    if (spot is DesktopIcons.POINT pt) _ = PlaceIconAsync(result, pt);
                }
                catch (Converter.FfmpegMissingException)
                {
                    if (!await DownloadFfmpegQuietlyAsync()) return;
                    i--;  // try this file again
                }
                catch (Exception ex)
                {
                    failed++;
                    CrashLog.Write("Convert " + System.IO.Path.GetFileName(files[i]) + " → " + target.Label, ex);
                }
            }
            if (failed > 0)
            {
                MessageBox.Show(failed == files.Length ? Lang.T("ما زبط التحويل. التفاصيل في السجل.")
                                                       : Lang.F("ما زبط تحويل {0} من {1}. التفاصيل في السجل.", failed, files.Length),
                                "Wibn", MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK,
                                Lang.MsgOptions);
            }
        }

        private static async Task<bool> DownloadFfmpegQuietlyAsync()
        {
            var answer = MessageBox.Show(
                Lang.T("التحويل هذا يحتاج FFmpeg (برنامج تحويل مجاني).\nأحمّله الحين؟ حجمه تقريبًا 140 ميقا، ويتحمّل مرة وحدة بس، وبعدها يكمّل التحويل لحاله."),
                "Wibn", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes,
                Lang.MsgOptions);
            if (answer != MessageBoxResult.Yes) return false;
            try
            {
                await Converter.DownloadFfmpegAsync(null, default);
                return true;
            }
            catch (Exception ex)
            {
                CrashLog.Write("FFmpeg download", ex);
                MessageBox.Show(Lang.T("ما تحمّل FFmpeg. تأكد من النت."), "Wibn", MessageBoxButton.OK, MessageBoxImage.Warning,
                                MessageBoxResult.OK, Lang.MsgOptions);
                return false;
            }
        }

        // ---- Convert --------------------------------------------------------------------

        private async Task ConvertAsync(Converter.Target target)
        {
            _cts = new CancellationTokenSource();
            ShowProgressMode();
            using var undo = FileUndo.Batch();
            var files = _files!;
            int done = 0, failed = 0;
            try
            {
                for (int i = 0; i < files.Length; i++)
                {
                    int index = i;
                    SetCenter(target.Label, "");
                    var progress = new Progress<double>(v => SetProgress((index + v) / files.Length));
                    try
                    {
                        // On the desktop: remember where the icon is, and put the
                        // converted file's icon right there.
                        var spot = DesktopIcons.GetPosition(files[i]);
                        string result = await Converter.ConvertAsync(files[i], _kind, target, progress, _cts.Token);
                        done++;
                        if (spot is DesktopIcons.POINT pt) _ = PlaceIconAsync(result, pt);
                    }
                    catch (Converter.FfmpegMissingException)
                    {
                        if (!await GetFfmpegAsync()) { CloseAnimated(); return; }
                        i--;  // try this file again
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        failed++;
                        CrashLog.Write("Convert " + System.IO.Path.GetFileName(files[i]) + " → " + target.Label, ex);
                    }
                }
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
            }

            if (failed == 0) ShowDone(done == 1 ? Lang.T("تم") : Lang.F("تم {0}", done), target.Label);
            else ShowDone(done == 0 ? Lang.T("ما زبط") : Lang.F("تم {0} من {1}", done, files.Length), Lang.T("التفاصيل في السجل"), error: true);
        }

        /// <summary>The desktop shows the new file a moment later; then move its icon.</summary>
        private static async Task PlaceIconAsync(string path, DesktopIcons.POINT pt)
        {
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(100);
                if (DesktopIcons.SetPosition(path, pt)) return;
            }
        }

        private async Task<bool> GetFfmpegAsync()
        {
            var answer = MessageBox.Show(
                Lang.T("التحويل هذا يحتاج FFmpeg (برنامج تحويل مجاني).\nأحمّله الحين؟ حجمه تقريبًا 140 ميقا، ويتحمّل مرة وحدة بس."),
                "Wibn", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes,
                Lang.MsgOptions);
            if (answer != MessageBoxResult.Yes) return false;
            SetCenter("↓", "");
            try
            {
                await Converter.DownloadFfmpegAsync(new Progress<double>(SetProgress), _cts?.Token ?? default);
                SetProgress(0);
                return true;
            }
            catch (Exception ex)
            {
                CrashLog.Write("FFmpeg download", ex);
                ShowDone(Lang.T("ما تحمّل"), Lang.T("تأكد من النت"), error: true);
                return false;
            }
        }

        private void ShowProgressMode()
        {
            SetHover(-1);
            foreach (var s in _segments)
            {
                Animate(s.Group, OpacityProperty, 0.0, 200);
                Animate(s.Scale, ScaleTransform.ScaleXProperty, 0.92, 220);
                Animate(s.Scale, ScaleTransform.ScaleYProperty, 0.92, 220);
            }
            _progressArc.Visibility = Visibility.Visible;
            SetProgress(0);
        }

        private void SetProgress(double value)
        {
            value = Math.Clamp(value, 0, 0.9999);
            double r = Inner - 2;
            if (value <= 0) { _progressArc.Data = null; return; }
            double a = -90 + 360 * value;
            Point P(double deg) => new(Center + r * Math.Cos(deg * Math.PI / 180), Center + r * Math.Sin(deg * Math.PI / 180));
            var fig = new PathFigure { StartPoint = P(-90) };
            fig.Segments.Add(new ArcSegment(P(a), new System.Windows.Size(r, r), 0, value > 0.5, SweepDirection.Clockwise, true));
            var g = new PathGeometry();
            g.Figures.Add(fig);
            _progressArc.Data = g;
        }

        private void ShowDone(string title, string subtitle, bool error = false)
        {
            SetProgress(0);
            _progressArc.Visibility = Visibility.Collapsed;
            SetCenter(error ? "!" : "✓", "");
            _hub.Fill = new SolidColorBrush(error ? Color.FromRgb(0xFF, 0x45, 0x3A) : Color.FromRgb(0x30, 0xD1, 0x58));  // Apple red / green
            // A small "pop" to confirm.
            var ease = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut };
            _rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _rootScale.ScaleX = _rootScale.ScaleY = 0.94;
            Animate(_rootScale, ScaleTransform.ScaleXProperty, 1, 260, null, ease);
            Animate(_rootScale, ScaleTransform.ScaleYProperty, 1, 260, null, ease);
            _converting = false;
            CloseAnimated(error ? 2200 : 1000);
        }

        // ---- Helpers -----------------------------------------------------------------------

        private static void Animate(IAnimatable target, DependencyProperty prop, double to, int ms,
                                    TimeSpan? delay = null, IEasingFunction? ease = null)
        {
            var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = ease ?? new CubicEase { EasingMode = EasingMode.EaseOut },
                BeginTime = delay ?? TimeSpan.Zero,
            };
            target.BeginAnimation(prop, anim);
        }

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    }
}
