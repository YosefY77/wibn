using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace WinPlus
{
    /// <summary>
    /// Volume mixer: the volume of the speakers and of every program playing sound,
    /// each with its own slider, mute button and a small level bar. Changes go to
    /// Windows itself (the same as the Windows volume mixer), so they stay after
    /// closing Wibn. Built in code; it only works while the page is open.
    /// </summary>
    internal sealed class VolumePage : StackPanel
    {
        private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
        private static Style St(string key) => (Style)Application.Current.FindResource(key);

        /// <summary>One program on the page (all its sound sessions together).</summary>
        private sealed class AppRow
        {
            public readonly List<AudioSessionControl> Sessions = new();
            public Border Root = null!;
            public Slider Slider = null!;
            public TextBlock Percent = null!;
            public Button MuteButton = null!;
            public Border Level = null!;
            public bool Seen;
        }

        private readonly StackPanel _apps = new();
        private readonly TextBlock _empty;
        private readonly TextBlock _deviceName;
        private readonly Slider _masterSlider;
        private readonly TextBlock _masterPercent;
        private readonly Button _masterMute;
        private readonly Border _masterLevel;
        private readonly Dictionary<string, AppRow> _rows = new();
        private readonly DispatcherTimer _timer;
        private MMDeviceEnumerator? _enumerator;
        private MMDevice? _device;
        private int _tick;
        private bool _updating;

        private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

        public VolumePage()
        {
            // ---- Speakers
            Children.Add(Section(Lang.T("السماعات"), 8));
            _deviceName = new TextBlock { Style = St("RowDesc") };
            _masterSlider = NewSlider();
            _masterPercent = NewPercent();
            _masterMute = NewMuteButton();
            _masterLevel = NewLevel();
            _masterSlider.ValueChanged += (_, _) =>
            {
                _masterPercent.Text = $"{(int)_masterSlider.Value}%";
                if (_updating || _device == null) return;
                try { _device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)(_masterSlider.Value / 100); }
                catch (Exception ex) { CrashLog.Write("master volume", ex); }
            };
            _masterMute.Click += (_, _) =>
            {
                if (_device == null) return;
                try
                {
                    var v = _device.AudioEndpointVolume;
                    v.Mute = !v.Mute;
                    SetMuteLook(_masterMute, v.Mute);
                }
                catch (Exception ex) { CrashLog.Write("master mute", ex); }
            };
            Children.Add(MakeRow(Glyph(""), Lang.T("الصوت العام"), _deviceName, _masterSlider, _masterPercent, _masterMute, _masterLevel));

            // ---- Programs
            Children.Add(Section(Lang.T("البرامج"), 24));
            Children.Add(_apps);
            _empty = new TextBlock
            {
                Style = St("RowDesc"), FontSize = 13, Margin = new Thickness(2, 4, 0, 0),
                Text = Lang.T("ما فيه برنامج يشغّل صوت الحين. شغّل أي صوت ويطلع هنا."),
            };
            Children.Add(_empty);
            Children.Add(new TextBlock
            {
                Style = St("RowDesc"), Margin = new Thickness(2, 16, 0, 0),
                Text = Lang.T("ويندوز يتذكر صوت كل برنامج، فيبقى مثل ما خليته حتى لو سكّرت البرنامج وفتحته."),
            });

            // Live only while the page is showing.
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
            _timer.Tick += (_, _) => OnTick();
            IsVisibleChanged += (_, _) =>
            {
                if (IsVisible)
                {
                    _tick = 0;
                    Refresh();
                    _timer.Start();
                }
                else
                {
                    _timer.Stop();
                }
            };
        }

        // ---- Updating ---------------------------------------------------------------------------

        private void OnTick()
        {
            // The list every ~1.5 s, the level bars every tick.
            if (++_tick % 20 == 0) Refresh();
            UpdateLevels();
        }

        private void Refresh()
        {
            try
            {
                _enumerator ??= new MMDeviceEnumerator();
                MMDevice device;
                try { device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
                catch (COMException)
                {
                    // No speakers at all.
                    ClearRows();
                    _device = null;
                    _deviceName.Text = Lang.T("ما فيه سماعات شغّالة");
                    _masterSlider.IsEnabled = _masterMute.IsEnabled = false;
                    _empty.Visibility = Visibility.Visible;
                    return;
                }

                if (_device == null || _device.ID != device.ID)
                {
                    ClearRows();  // other speakers: their programs are different sessions
                    _device = device;
                }
                _masterSlider.IsEnabled = _masterMute.IsEnabled = true;
                _deviceName.Text = _device.FriendlyName;

                _updating = true;
                var master = _device.AudioEndpointVolume;
                if (!_masterSlider.IsMouseCaptureWithin) SetSlider(_masterSlider, master.MasterVolumeLevelScalar * 100);
                SetMuteLook(_masterMute, master.Mute);
                _updating = false;

                RefreshSessions();
            }
            catch (Exception ex)
            {
                _updating = false;
                CrashLog.Write("volume refresh", ex);
            }
        }

        private void RefreshSessions()
        {
            var manager = _device!.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;

            foreach (var row in _rows.Values)
            {
                row.Seen = false;
                row.Sessions.Clear();
            }

            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;

                uint pid = s.GetProcessID;
                bool system = s.IsSystemSoundsSession || pid == 0;
                string? path = system ? null : ProcessPath(pid);
                if (!system && path == null && !ProcessAlive(pid)) continue;

                // Programs with many processes (browsers, Discord…) show once.
                string key = system ? "system" : path ?? "pid:" + pid;
                if (!_rows.TryGetValue(key, out var row))
                {
                    row = NewAppRow(system, path, pid);
                    _rows[key] = row;
                }
                row.Seen = true;
                row.Sessions.Add(s);
            }

            foreach (var gone in _rows.Where(r => !r.Value.Seen).Select(r => r.Key).ToList())
            {
                _rows.Remove(gone);
            }

            // Stable order: by name, the Windows sounds last.
            var ordered = _rows.OrderBy(r => r.Key == "system")
                               .ThenBy(r => ((TextBlock)r.Value.Root.Tag).Text, StringComparer.CurrentCultureIgnoreCase)
                               .Select(r => r.Value.Root).ToList();
            if (!_apps.Children.Cast<UIElement>().SequenceEqual(ordered))
            {
                _apps.Children.Clear();
                foreach (var r in ordered) _apps.Children.Add(r);
            }
            _empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Show changes made somewhere else (the Windows mixer, the program itself).
            _updating = true;
            foreach (var row in _rows.Values)
            {
                var first = row.Sessions[0].SimpleAudioVolume;
                if (!row.Slider.IsMouseCaptureWithin) SetSlider(row.Slider, first.Volume * 100);
                SetMuteLook(row.MuteButton, row.Sessions.All(x => x.SimpleAudioVolume.Mute));
            }
            _updating = false;
        }

        private void UpdateLevels()
        {
            if (_device == null) return;
            try
            {
                SetLevel(_masterLevel, _device.AudioMeterInformation.MasterPeakValue);
                foreach (var row in _rows.Values)
                {
                    float peak = 0;
                    foreach (var s in row.Sessions)
                    {
                        try { peak = Math.Max(peak, s.AudioMeterInformation.MasterPeakValue); }
                        catch (COMException) { }
                    }
                    SetLevel(row.Level, peak);
                }
            }
            catch (COMException) { }
        }

        private void ClearRows()
        {
            _rows.Clear();
            _apps.Children.Clear();
        }

        private static void SetSlider(Slider slider, double value)
        {
            if (Math.Abs(slider.Value - value) > 0.5) slider.Value = Math.Round(value);
        }

        private static void SetLevel(Border level, float peak)
        {
            if (level.Parent is not FrameworkElement track) return;
            level.Width = Math.Max(0, track.ActualWidth * Math.Clamp(peak, 0, 1));
        }

        // ---- Rows ---------------------------------------------------------------------------------

        private AppRow NewAppRow(bool system, string? path, uint pid)
        {
            var row = new AppRow
            {
                Slider = NewSlider(),
                Percent = NewPercent(),
                MuteButton = NewMuteButton(),
                Level = NewLevel(),
            };

            string name = system ? Lang.T("أصوات ويندوز") : AppName(path, pid);
            FrameworkElement icon = system || path == null ? Glyph(system ? "" : "") : AppIcon(path);

            row.Slider.ValueChanged += (_, _) =>
            {
                row.Percent.Text = $"{(int)row.Slider.Value}%";
                if (_updating) return;
                foreach (var s in row.Sessions)
                {
                    try
                    {
                        s.SimpleAudioVolume.Volume = (float)(row.Slider.Value / 100);
                        // Moving the slider up brings the sound back.
                        if (s.SimpleAudioVolume.Mute && row.Slider.Value > 0) s.SimpleAudioVolume.Mute = false;
                    }
                    catch (COMException) { }
                }
                SetMuteLook(row.MuteButton, row.Sessions.Count > 0 && row.Sessions.All(x => x.SimpleAudioVolume.Mute));
            };
            row.MuteButton.Click += (_, _) =>
            {
                bool mute = !row.Sessions.All(x => x.SimpleAudioVolume.Mute);
                foreach (var s in row.Sessions)
                {
                    try { s.SimpleAudioVolume.Mute = mute; }
                    catch (COMException) { }
                }
                SetMuteLook(row.MuteButton, mute);
            };

            var desc = new TextBlock { Style = St("RowDesc"), Text = system ? Lang.T("التنبيهات وأصوات النظام") : Path.GetFileName(path ?? "") };
            row.Root = MakeRow(icon, name, desc, row.Slider, row.Percent, row.MuteButton, row.Level);
            row.Root.Tag = ((StackPanel)((DockPanel)row.Root.Child).Children[1]).Children[0];  // the name, for sorting
            return row;
        }

        /// <summary>Icon | name, description, slider, level | percent, mute.</summary>
        private static Border MakeRow(FrameworkElement icon, string title, TextBlock desc, Slider slider,
                                      TextBlock percent, Button mute, Border level)
        {
            var dock = new DockPanel();
            icon.Margin = new Thickness(0, 0, 16, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(icon, Dock.Left);
            dock.Children.Add(icon);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = title, Style = St("RowTitle"), TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(desc);

            var controls = new DockPanel { Margin = new Thickness(0, 6, 0, 0), FlowDirection = FlowDirection.LeftToRight };
            // In Arabic the page is right-to-left: keep the mute button on the reading side's end.
            DockPanel.SetDock(mute, Lang.English ? Dock.Right : Dock.Left);
            DockPanel.SetDock(percent, Lang.English ? Dock.Right : Dock.Left);
            controls.Children.Add(mute);
            controls.Children.Add(percent);
            var sliderBox = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            sliderBox.Children.Add(slider);
            var track = new Border
            {
                Height = 3, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(9, 1, 9, 0),
                Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                Child = level,
            };
            sliderBox.Children.Add(track);
            controls.Children.Add(sliderBox);
            text.Children.Add(controls);

            dock.Children.Add(text);
            return new Border { Style = St("Row"), Margin = new Thickness(0, 0, 0, 6), Child = dock };
        }

        private static Slider NewSlider() => new()
        {
            Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 10,
            FlowDirection = FlowDirection.LeftToRight, VerticalAlignment = VerticalAlignment.Center,
        };

        private static TextBlock NewPercent() => new()
        {
            Width = 48, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Foreground = Res("Fg"), FlowDirection = FlowDirection.LeftToRight, Text = "0%",
        };

        private static Button NewMuteButton()
        {
            var b = new Button
            {
                Style = St("FlatButton"), Width = 36, Height = 32, Padding = new Thickness(0),
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center,
            };
            SetMuteLook(b, false);
            return b;
        }

        private static void SetMuteLook(Button b, bool muted)
        {
            b.Content = muted ? "" : "";
            b.ToolTip = muted ? Lang.T("رجّع الصوت") : Lang.T("كتم");
            b.Foreground = muted ? new SolidColorBrush(Color.FromRgb(0xFF, 0x5F, 0x57)) : Res("Fg");
        }

        private static Border NewLevel() => new()
        {
            Width = 0, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(1.5),
            Background = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59)),
        };

        private static TextBlock Glyph(string glyph) => new()
        {
            Text = glyph, Width = 28, FontSize = 22, TextAlignment = TextAlignment.Center,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Foreground = Res("Fg"),
        };

        private static TextBlock Section(string text, double top) =>
            new() { Text = text, Style = St("Section"), FontSize = 18, Margin = new Thickness(2, top, 0, 8) };

        // ---- Programs ------------------------------------------------------------------------------

        /// <summary>The program's icon (cached), or null.</summary>
        internal static ImageSource? IconOf(string path)
        {
            AppIcon(path);
            return IconCache.TryGetValue(path, out var i) ? i : null;
        }

        private static FrameworkElement AppIcon(string path)
        {
            if (!IconCache.TryGetValue(path, out var image))
            {
                try
                {
                    using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                    if (icon != null)
                    {
                        var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        source.Freeze();
                        image = source;
                    }
                }
                catch { image = null; }
                IconCache[path] = image;
            }
            return image == null ? Glyph("") : new Image { Source = image, Width = 28, Height = 28 };
        }

        internal static string AppName(string? path, uint pid)
        {
            if (path != null)
            {
                try
                {
                    string desc = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? "";
                    if (desc.Length > 0) return desc;
                }
                catch { }
                return Path.GetFileNameWithoutExtension(path);
            }
            try { return Process.GetProcessById((int)pid).ProcessName; }
            catch { return "PID " + pid; }
        }

        internal static bool ProcessAlive(uint pid)
        {
            try { using var p = Process.GetProcessById((int)pid); return !p.HasExited; }
            catch { return false; }
        }

        /// <summary>The exe path, also for programs started as admin.</summary>
        internal static string? ProcessPath(uint pid)
        {
            IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
