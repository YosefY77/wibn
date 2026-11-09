using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinPlus
{
    /// <summary>
    /// The device usage widget: processor, memory, graphics card and the Windows drive,
    /// each with a bar. Measured once a second on its own thread while it's on the desktop.
    /// Narrow sizes drop the details (like "8.1 / 16 GB"), short ones drop the title.
    /// </summary>
    internal sealed class SystemWidget : WidgetContent
    {
        private sealed class Meter
        {
            public FrameworkElement Root = null!;
            public TextBlock Value = null!, Detail = null!;
            public ScaleTransform Fill = null!;
            public Border Bar = null!;
        }

        private readonly SolidColorBrush _accent = new(Color.FromRgb(0x4C, 0x8D, 0xFF));
        private readonly SolidColorBrush _ink = new(Color.FromRgb(0xF2, 0xF3, 0xF5));
        private readonly SolidColorBrush _inkMuted = new(Color.FromRgb(0xA0, 0xA3, 0xAA));
        private readonly SolidColorBrush _track = new(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        private static readonly Brush Hot = new SolidColorBrush(Color.FromRgb(0xFF, 0x5F, 0x57));

        private readonly TextBlock _title;
        private readonly Meter _cpu, _ram, _gpu, _disk;
        private readonly StackPanel _list = new();
        private Timer? _timer;
        private readonly Sampler _sampler = new();
        private bool _wide = true;

        public SystemWidget()
        {
            var panel = new DockPanel();
            _title = new TextBlock
            {
                Text = Lang.T("استهلاك الجهاز"), FontWeight = FontWeights.SemiBold, FontSize = 13,
                Foreground = _ink, Margin = new Thickness(2, 0, 0, 6),
            };
            DockPanel.SetDock(_title, Dock.Top);
            panel.Children.Add(_title);

            _cpu = NewMeter(Lang.T("المعالج"));
            _ram = NewMeter(Lang.T("الرام"));
            _gpu = NewMeter(Lang.T("كرت الشاشة"));
            _disk = NewMeter(Lang.T("القرص") + " " + SystemDrive().TrimEnd('\\'));
            _gpu.Root.Visibility = Visibility.Collapsed;   // until it's known to be measurable
            foreach (var m in new[] { _cpu, _ram, _gpu, _disk }) _list.Children.Add(m.Root);
            panel.Children.Add(new ScrollViewer
            {
                Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            });
            Child = panel;
        }

        public override void Start()
        {
            _timer ??= new Timer(_ => Tick(), null, 0, 1000);
        }

        public override void Stop()
        {
            _timer?.Dispose();
            _timer = null;
            _sampler.Dispose();
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

        public override void SetCells(int w, int h)
        {
            _wide = w >= 3;
            _title.Visibility = h >= 3 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var m in new[] { _cpu, _ram, _gpu, _disk })
                m.Detail.Visibility = _wide ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- Measuring (timer thread) ---------------------------------------------------------

        private int _busy;

        private void Tick()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            try
            {
                var s = _sampler.Take();
                Dispatcher.BeginInvoke(() => Show(s));
            }
            catch (Exception ex) { CrashLog.Write("system widget", ex); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }

        private void Show(Sample s)
        {
            if (_timer == null) return;
            Set(_cpu, s.Cpu, "");
            Set(_ram, s.RamUsed / (double)Math.Max(1, s.RamTotal) * 100, $"{Gb(s.RamUsed)} / {Gb(s.RamTotal)} GB");
            if (s.Gpu is double gpu)
            {
                _gpu.Root.Visibility = Visibility.Visible;
                Set(_gpu, gpu, "");
            }
            Set(_disk, s.DiskUsed / (double)Math.Max(1, s.DiskTotal) * 100, $"{Gb(s.DiskUsed, 0)} / {Gb(s.DiskTotal, 0)} GB");
        }

        private static string Gb(long bytes, int digits = 1) => Math.Round(bytes / 1073741824.0, digits).ToString(digits == 0 ? "0" : "0.0");

        private void Set(Meter m, double percent, string detail)
        {
            percent = Math.Clamp(percent, 0, 100);
            m.Value.Text = $"{Math.Round(percent)}%";
            m.Detail.Text = detail;
            m.Bar.Background = percent >= 90 ? Hot : _accent;
            m.Fill.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(percent / 100, new Duration(TimeSpan.FromMilliseconds(400))) { EasingFunction = new QuadraticEase() });
        }

        // ---- Pieces ---------------------------------------------------------------------------

        /// <summary>Name … details  value, and the bar under them.</summary>
        private Meter NewMeter(string name)
        {
            var m = new Meter();
            var top = new DockPanel { LastChildFill = true };
            m.Value = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = _ink, Text = "0%", Margin = new Thickness(6, 0, 0, 0) };
            m.Detail = new TextBlock { FontSize = 10.5, Foreground = _inkMuted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            DockPanel.SetDock(m.Value, Dock.Right);
            DockPanel.SetDock(m.Detail, Dock.Right);
            top.Children.Add(m.Value);
            top.Children.Add(m.Detail);
            top.Children.Add(new TextBlock { FontSize = 12, Foreground = _ink, Text = name, TextTrimming = TextTrimming.CharacterEllipsis });

            m.Fill = new ScaleTransform(0, 1);
            m.Bar = new Border { CornerRadius = new CornerRadius(2.5), Background = _accent, RenderTransform = m.Fill };
            var track = new Border
            {
                Height = 5, CornerRadius = new CornerRadius(2.5), Background = _track, Margin = new Thickness(0, 4, 0, 0),
                Child = m.Bar, ClipToBounds = true,
            };

            var root = new StackPanel { Margin = new Thickness(2, 3, 2, 7) };
            root.Children.Add(top);
            root.Children.Add(track);
            m.Root = root;
            return m;
        }

        private static string SystemDrive() =>
            Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";

        // ---- The numbers ----------------------------------------------------------------------

        private readonly record struct Sample(double Cpu, long RamUsed, long RamTotal, double? Gpu, long DiskUsed, long DiskTotal);

        /// <summary>Reads the numbers. CPU from the system times, memory from Windows,
        /// the graphics card from its performance counters (when there are any).</summary>
        private sealed class Sampler : IDisposable
        {
            private ulong _idle, _kernel, _user;
            private IntPtr _query, _gpuCounter;
            private bool _gpuTried, _gpuReady;
            private readonly object _lock = new();

            public Sample Take()
            {
                lock (_lock)
                {
                    double cpu = 0;
                    if (GetSystemTimes(out var idle, out var kernel, out var user))
                    {
                        ulong di = idle - _idle, dk = kernel - _kernel, du = user - _user;
                        if (_kernel != 0 && dk + du > 0) cpu = (1 - di / (double)(dk + du)) * 100;   // kernel time includes idle
                        _idle = idle; _kernel = kernel; _user = user;
                    }

                    var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                    long ramTotal = 0, ramUsed = 0;
                    if (GlobalMemoryStatusEx(ref mem))
                    {
                        ramTotal = (long)mem.ullTotalPhys;
                        ramUsed = (long)(mem.ullTotalPhys - mem.ullAvailPhys);
                    }

                    long diskTotal = 0, diskUsed = 0;
                    try
                    {
                        var d = new DriveInfo(SystemDrive());
                        diskTotal = d.TotalSize;
                        diskUsed = d.TotalSize - d.TotalFreeSpace;
                    }
                    catch { }

                    return new Sample(cpu, ramUsed, ramTotal, Gpu(), diskUsed, diskTotal);
                }
            }

            /// <summary>Like Task Manager: the busiest kind of engine (3D, video, copy…), all programs added up.</summary>
            private double? Gpu()
            {
                if (!_gpuTried)
                {
                    _gpuTried = true;
                    if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) return null;
                    if (PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpuCounter) != 0) return null;
                    PdhCollectQueryData(_query);
                    _gpuReady = true;
                    return null;   // a rate needs two readings
                }
                if (!_gpuReady || PdhCollectQueryData(_query) != 0) return null;

                uint size = 0, count;
                const uint Fmt = 0x200 /* DOUBLE */ | 0x8000 /* NOCAP100 */;
                int rc = PdhGetFormattedCounterArray(_gpuCounter, Fmt, ref size, out count, IntPtr.Zero);
                if (rc != unchecked((int)0x800007D2) /* MORE_DATA */ || size == 0) return null;
                IntPtr buf = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (PdhGetFormattedCounterArray(_gpuCounter, Fmt, ref size, out count, buf) != 0) return null;
                    var byType = new Dictionary<string, double>();
                    const int itemSize = 24;   // name, then the value (8-byte aligned): status at 8, number at 16
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr item = buf + i * itemSize;
                        string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                        uint status = (uint)Marshal.ReadInt32(item + 8);
                        if (status > 1) continue;   // not a valid reading
                        double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + 16));
                        int at = name.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
                        string type = at >= 0 ? name[(at + 8)..] : name;
                        byType[type] = byType.TryGetValue(type, out var sum) ? sum + v : v;
                    }
                    double max = 0;
                    foreach (var v in byType.Values) max = Math.Max(max, v);
                    return Math.Min(max, 100);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }

            public void Dispose()
            {
                lock (_lock)
                {
                    if (_query != IntPtr.Zero) PdhCloseQuery(_query);
                    _query = _gpuCounter = IntPtr.Zero;
                    _gpuTried = _gpuReady = false;
                    _idle = _kernel = _user = 0;
                }
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct MEMORYSTATUSEX
            {
                public uint dwLength, dwMemoryLoad;
                public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
            }

            [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
            [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
            [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
            [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
            [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(IntPtr query);
            [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
            private static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr items);
            [DllImport("pdh.dll")] private static extern int PdhCloseQuery(IntPtr query);
        }
    }
}
