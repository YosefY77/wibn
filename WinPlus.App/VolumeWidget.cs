using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace WinPlus
{
    /// <summary>
    /// The volume widget: the speakers and every program playing sound, each with a
    /// slider. Clicking an icon mutes / unmutes it. Small sizes drop the percentages
    /// and the title. Updates every 1.5 s while it's on the desktop.
    /// </summary>
    internal sealed class VolumeWidget : WidgetContent
    {
        private sealed class Row
        {
            public readonly List<AudioSessionControl> Sessions = new();
            public FrameworkElement Root = null!;
            public Slider Slider = null!;
            public TextBlock Percent = null!;
            public Button Icon = null!;
            public string Name = "";
            public bool Seen;
        }

        private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
        private static readonly Brush MutedRed = new SolidColorBrush(Color.FromRgb(0xFF, 0x5F, 0x57));

        private readonly SolidColorBrush _accent = new(Color.FromRgb(0x4C, 0x8D, 0xFF));
        private readonly SolidColorBrush _ink = new(Color.FromRgb(0xF2, 0xF3, 0xF5));
        private readonly SolidColorBrush _inkMuted = new(Color.FromRgb(0xA0, 0xA3, 0xAA));
        private readonly TextBlock _title;
        private readonly StackPanel _apps = new();
        private readonly TextBlock _empty;
        private readonly Slider _master;
        private readonly TextBlock _masterPercent;
        private readonly Button _masterIcon;
        private readonly Dictionary<string, Row> _rows = new();
        private readonly DispatcherTimer _timer;
        private MMDeviceEnumerator? _enumerator;
        private MMDevice? _device;
        private bool _updating, _wide = true, _tall = true;

        public VolumeWidget()
        {
            var panel = new DockPanel();

            _title = new TextBlock
            {
                Text = Lang.T("الأصوات"), FontWeight = FontWeights.SemiBold, FontSize = 13,
                Foreground = _ink, Margin = new Thickness(2, 0, 0, 6),
            };
            DockPanel.SetDock(_title, Dock.Top);
            panel.Children.Add(_title);

            _master = NewSlider();
            _masterPercent = NewPercent();
            _masterIcon = NewIcon(Glyph(""));
            _masterIcon.ToolTip = Lang.T("الصوت العام");
            _master.ValueChanged += (_, _) =>
            {
                _masterPercent.Text = $"{(int)_master.Value}%";
                if (_updating || _device == null) return;
                try { _device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)(_master.Value / 100); } catch { }
            };
            _masterIcon.Click += (_, _) =>
            {
                if (_device == null) return;
                try
                {
                    var v = _device.AudioEndpointVolume;
                    v.Mute = !v.Mute;
                    SetMuted(_masterIcon, v.Mute);
                }
                catch { }
            };
            var master = MakeRow(_masterIcon, _master, _masterPercent);
            master.Margin = new Thickness(0, 0, 0, 4);
            DockPanel.SetDock(master, Dock.Top);
            panel.Children.Add(master);

            var line = new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 2, 0, 6) };
            DockPanel.SetDock(line, Dock.Top);
            panel.Children.Add(line);

            _empty = new TextBlock
            {
                Text = Lang.T("ما فيه برنامج يشغّل صوت"), FontSize = 11, Foreground = _inkMuted,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 0, 0),
            };
            var list = new StackPanel();
            list.Children.Add(_apps);
            list.Children.Add(_empty);
            panel.Children.Add(new ScrollViewer
            {
                Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            });

            Child = panel;

            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1500) };
            _timer.Tick += (_, _) => Refresh();
        }

        public override void Start()
        {
            Refresh();
            _timer.Start();
        }

        public override void Stop() => _timer.Stop();

        /// <summary>Glass over a light wallpaper: dark text so it stays readable.</summary>
        public override void SetLightBehind(bool light)
        {
            var d = new Duration(TimeSpan.FromMilliseconds(200));
            _ink.BeginAnimation(SolidColorBrush.ColorProperty,
                new System.Windows.Media.Animation.ColorAnimation(light ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xF2, 0xF3, 0xF5), d));
            _inkMuted.BeginAnimation(SolidColorBrush.ColorProperty,
                new System.Windows.Media.Animation.ColorAnimation(light ? Color.FromArgb(0xB0, 0x3C, 0x3C, 0x43) : Color.FromRgb(0xA0, 0xA3, 0xAA), d));
        }

        public override void SetAccent(Color color) =>
            _accent.BeginAnimation(SolidColorBrush.ColorProperty,
                new System.Windows.Media.Animation.ColorAnimation(color, new Duration(TimeSpan.FromMilliseconds(200))));

        public override void SetCells(int w, int h)
        {
            _wide = w >= 3;
            _tall = h >= 3;
            _title.Visibility = _tall ? Visibility.Visible : Visibility.Collapsed;
            var v = _wide ? Visibility.Visible : Visibility.Collapsed;
            _masterPercent.Visibility = v;
            foreach (var r in _rows.Values) r.Percent.Visibility = v;
        }

        // ---- Updating -------------------------------------------------------------------------

        private void Refresh()
        {
            try
            {
                _enumerator ??= new MMDeviceEnumerator();
                MMDevice device;
                try { device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
                catch (COMException)
                {
                    _rows.Clear();
                    _apps.Children.Clear();
                    _device = null;
                    _master.IsEnabled = _masterIcon.IsEnabled = false;
                    _empty.Visibility = Visibility.Visible;
                    return;
                }
                if (_device == null || _device.ID != device.ID)
                {
                    _rows.Clear();
                    _apps.Children.Clear();
                    _device?.Dispose();
                    _device = device;
                }
                else device.Dispose();
                _master.IsEnabled = _masterIcon.IsEnabled = true;
                _masterIcon.ToolTip = _device.FriendlyName;

                _updating = true;
                var ep = _device.AudioEndpointVolume;
                if (!_master.IsMouseCaptureWithin) SetSlider(_master, ep.MasterVolumeLevelScalar * 100);
                SetMuted(_masterIcon, ep.Mute);
                _updating = false;

                RefreshSessions();
            }
            catch (Exception ex)
            {
                _updating = false;
                CrashLog.Write("volume widget", ex);
            }
        }

        private void RefreshSessions()
        {
            var manager = _device!.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            foreach (var r in _rows.Values) { r.Seen = false; r.Sessions.Clear(); }

            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
                uint pid = s.GetProcessID;
                bool system = s.IsSystemSoundsSession || pid == 0;
                string? path = system ? null : VolumePage.ProcessPath(pid);
                if (!system && path == null && !VolumePage.ProcessAlive(pid)) continue;
                string key = system ? "system" : path ?? "pid:" + pid;
                if (!_rows.TryGetValue(key, out var row))
                {
                    row = NewRow(system, path, pid);
                    _rows[key] = row;
                }
                row.Seen = true;
                row.Sessions.Add(s);
            }
            foreach (var gone in _rows.Where(r => !r.Value.Seen).Select(r => r.Key).ToList()) _rows.Remove(gone);

            var ordered = _rows.OrderBy(r => r.Key == "system")
                               .ThenBy(r => r.Value.Name, StringComparer.CurrentCultureIgnoreCase)
                               .Select(r => r.Value.Root).ToList();
            if (!_apps.Children.Cast<UIElement>().SequenceEqual(ordered))
            {
                _apps.Children.Clear();
                foreach (var r in ordered) _apps.Children.Add(r);
            }
            _empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            _updating = true;
            foreach (var row in _rows.Values)
            {
                try
                {
                    if (!row.Slider.IsMouseCaptureWithin) SetSlider(row.Slider, row.Sessions[0].SimpleAudioVolume.Volume * 100);
                    SetMuted(row.Icon, row.Sessions.All(x => x.SimpleAudioVolume.Mute));
                }
                catch (COMException) { }
            }
            _updating = false;
        }

        private Row NewRow(bool system, string? path, uint pid)
        {
            var row = new Row
            {
                Slider = NewSlider(),
                Percent = NewPercent(),
                Name = system ? Lang.T("أصوات ويندوز") : VolumePage.AppName(path, pid),
            };
            FrameworkElement icon = Glyph(system ? "" : "");
            if (!system && path != null && VolumePage.IconOf(path) is { } img)
                icon = new Image { Source = img, Width = 18, Height = 18 };
            row.Icon = NewIcon(icon);
            row.Icon.ToolTip = row.Name;
            row.Percent.Visibility = _wide ? Visibility.Visible : Visibility.Collapsed;

            row.Slider.ValueChanged += (_, _) =>
            {
                row.Percent.Text = $"{(int)row.Slider.Value}%";
                if (_updating) return;
                foreach (var s in row.Sessions)
                {
                    try
                    {
                        s.SimpleAudioVolume.Volume = (float)(row.Slider.Value / 100);
                        if (s.SimpleAudioVolume.Mute && row.Slider.Value > 0) s.SimpleAudioVolume.Mute = false;
                    }
                    catch (COMException) { }
                }
                try { SetMuted(row.Icon, row.Sessions.Count > 0 && row.Sessions.All(x => x.SimpleAudioVolume.Mute)); } catch (COMException) { }
            };
            row.Icon.Click += (_, _) =>
            {
                try
                {
                    bool mute = !row.Sessions.All(x => x.SimpleAudioVolume.Mute);
                    foreach (var s in row.Sessions) s.SimpleAudioVolume.Mute = mute;
                    SetMuted(row.Icon, mute);
                }
                catch (COMException) { }
            };
            row.Root = MakeRow(row.Icon, row.Slider, row.Percent);
            return row;
        }

        // ---- Pieces ---------------------------------------------------------------------------

        /// <summary>Icon | slider | percent (left to right, the same in both languages).</summary>
        private static FrameworkElement MakeRow(Button icon, Slider slider, TextBlock percent)
        {
            var dock = new DockPanel { FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(0, 1, 0, 1) };
            DockPanel.SetDock(icon, Dock.Left);
            DockPanel.SetDock(percent, Dock.Right);
            dock.Children.Add(icon);
            dock.Children.Add(percent);
            dock.Children.Add(slider);
            return dock;
        }

        private Slider NewSlider() => new()
        {
            Foreground = _accent,
            Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 10,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 2, 0),
        };

        private TextBlock NewPercent() => new()
        {
            Width = 34, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11, Foreground = _inkMuted, Text = "0%",
        };

        private static Button NewIcon(FrameworkElement content)
        {
            var b = new Button
            {
                Style = (Style)Application.Current.FindResource("FlatButton"),
                Width = 28, Height = 28, Padding = new Thickness(0), Content = content,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            };
            return b;
        }

        private void SetMuted(Button b, bool muted)
        {
            b.Opacity = muted ? 0.45 : 1;
            if (b.Content is TextBlock t) t.Foreground = muted ? MutedRed : _ink;
            b.BorderBrush = muted ? MutedRed : Brushes.Transparent;
            b.BorderThickness = new Thickness(muted ? 1 : 0);
        }

        private TextBlock Glyph(string glyph) => new()
        {
            Text = glyph, FontSize = 15, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Foreground = _ink,
        };

        private static void SetSlider(Slider slider, double value)
        {
            if (Math.Abs(slider.Value - value) > 0.5) slider.Value = Math.Round(value);
        }
    }
}
