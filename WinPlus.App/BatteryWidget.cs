using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinPlus
{
    /// <summary>
    /// The battery widget: the PC's battery and every mouse, keyboard, headset or controller
    /// that tells its battery (Bluetooth, or a USB receiver / cable, see <see cref="BatteryReader"/>),
    /// each with a bar that turns red at 20% and a bolt while charging. Read every 30 s on its
    /// own thread while it's on the desktop. Short sizes drop the title.
    /// </summary>
    internal sealed class BatteryWidget : WidgetContent
    {
        private sealed class Row
        {
            public FrameworkElement Root = null!;
            public TextBlock Icon = null!, Name = null!, Value = null!, Bolt = null!;
            public ScaleTransform Fill = null!;
            public Border Bar = null!;
        }

        private static readonly Brush Low = new SolidColorBrush(Color.FromRgb(0xFF, 0x5F, 0x57));
        private static readonly Brush Charge = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59));

        private readonly SolidColorBrush _accent = new(Color.FromRgb(0x4C, 0x8D, 0xFF));
        private readonly SolidColorBrush _ink = new(Color.FromRgb(0xF2, 0xF3, 0xF5));
        private readonly SolidColorBrush _inkMuted = new(Color.FromRgb(0xA0, 0xA3, 0xAA));
        private readonly SolidColorBrush _track = new(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

        private readonly TextBlock _title, _empty;
        private readonly StackPanel _list = new();
        private readonly Dictionary<string, Row> _rows = new();
        private Timer? _timer;
        private int _busy;
        private bool _read;

        public BatteryWidget()
        {
            var panel = new DockPanel();
            _title = new TextBlock
            {
                Text = Lang.T("البطاريات"), FontWeight = FontWeights.SemiBold, FontSize = 13,
                Foreground = _ink, Margin = new Thickness(2, 0, 0, 6),
            };
            DockPanel.SetDock(_title, Dock.Top);
            panel.Children.Add(_title);

            _empty = new TextBlock
            {
                Text = Lang.T("أدوّر على الأجهزة…"), FontSize = 11, Foreground = _inkMuted,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 0, 0),
            };
            var content = new StackPanel();
            content.Children.Add(_list);
            content.Children.Add(_empty);
            panel.Children.Add(new ScrollViewer
            {
                Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            });
            Child = panel;
        }

        public override void Start() => _timer ??= new Timer(_ => Tick(), null, 0, 30_000);

        public override void Stop()
        {
            _timer?.Dispose();
            _timer = null;
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

        public override void SetCells(int w, int h) =>
            _title.Visibility = h >= 3 ? Visibility.Visible : Visibility.Collapsed;

        public override IEnumerable<Control> MenuItems()
        {
            var refresh = new MenuItem { Header = Lang.T("تحديث الحين") };
            refresh.Click += (_, _) => ThreadPool.QueueUserWorkItem(_ => Tick());
            yield return refresh;
        }

        // ---- Reading (timer thread) -----------------------------------------------------------

        private void Tick()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            try
            {
                var devices = BatteryReader.ReadAll();
                Dispatcher.BeginInvoke(() => Show(devices));
            }
            catch (Exception ex) { CrashLog.Write("battery widget", ex); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }

        private void Show(List<BatteryReader.Device> devices)
        {
            if (_timer == null) return;
            _read = true;
            var seen = new HashSet<string>();
            var ordered = devices.OrderBy(d => d.Kind == BatteryReader.Kind.Pc ? 0 : 1)
                                 .ThenBy(d => d.Kind).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase);
            var roots = new List<UIElement>();
            foreach (var d in ordered)
            {
                seen.Add(d.Key);
                if (!_rows.TryGetValue(d.Key, out var row)) _rows[d.Key] = row = NewRow();
                Set(row, d);
                roots.Add(row.Root);
            }
            foreach (var gone in _rows.Keys.Where(k => !seen.Contains(k)).ToList()) _rows.Remove(gone);
            if (!_list.Children.Cast<UIElement>().SequenceEqual(roots))
            {
                _list.Children.Clear();
                foreach (var r in roots) _list.Children.Add(r);
            }
            _empty.Text = Lang.T("ما لقيت جهاز يعطي بطاريته. شغّل الماوس أو السماعة وتأكد إنها متصلة");
            _empty.Visibility = _read && _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Set(Row row, BatteryReader.Device d)
        {
            row.Icon.Text = d.Kind switch
            {
                BatteryReader.Kind.Pc => "",        // laptop
                BatteryReader.Kind.Mouse => "",
                BatteryReader.Kind.Keyboard => "",
                BatteryReader.Kind.Headset => "",
                BatteryReader.Kind.Gamepad => "",
                _ => "",                            // bluetooth
            };
            row.Name.Text = d.Name;
            row.Root.ToolTip = $"{d.Name}: {d.Percent}%" + (d.Charging ? " · " + Lang.T("يشحن") : "");
            row.Value.Text = $"{d.Percent}%";
            row.Bolt.Visibility = d.Charging ? Visibility.Visible : Visibility.Collapsed;
            row.Bar.Background = d.Charging ? Charge : d.Percent <= 20 ? Low : _accent;
            row.Value.Foreground = !d.Charging && d.Percent <= 20 ? Low : _ink;
            row.Fill.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(d.Percent / 100.0, new Duration(TimeSpan.FromMilliseconds(400))) { EasingFunction = new QuadraticEase() });
        }

        // ---- Pieces ---------------------------------------------------------------------------

        /// <summary>Icon  name … bolt value, and the bar under them.</summary>
        private Row NewRow()
        {
            var row = new Row();
            var top = new DockPanel { LastChildFill = true };
            row.Icon = new TextBlock
            {
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13, Foreground = _ink,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            };
            row.Value = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = _ink, Margin = new Thickness(4, 0, 0, 0) };
            row.Bolt = new TextBlock
            {
                Text = "", FontFamily = row.Icon.FontFamily, FontSize = 11, Foreground = Charge,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0), Visibility = Visibility.Collapsed,
            };
            row.Name = new TextBlock { FontSize = 12, Foreground = _ink, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(row.Icon, Dock.Left);
            DockPanel.SetDock(row.Value, Dock.Right);
            DockPanel.SetDock(row.Bolt, Dock.Right);
            top.Children.Add(row.Icon);
            top.Children.Add(row.Value);
            top.Children.Add(row.Bolt);
            top.Children.Add(row.Name);

            row.Fill = new ScaleTransform(0, 1);
            row.Bar = new Border { CornerRadius = new CornerRadius(2.5), Background = _accent, RenderTransform = row.Fill };
            var track = new Border
            {
                Height = 5, CornerRadius = new CornerRadius(2.5), Background = _track, Margin = new Thickness(0, 4, 0, 0),
                Child = row.Bar, ClipToBounds = true,
            };

            var root = new StackPanel { Margin = new Thickness(2, 3, 2, 7) };
            root.Children.Add(top);
            root.Children.Add(track);
            row.Root = root;
            return row;
        }
    }
}
