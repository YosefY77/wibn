using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace WinPlus
{
    /// <summary>Programs using the most RAM, grouped by program, refreshed while visible.</summary>
    public partial class RamView : UserControl
    {
        private const int MaxRows = 30;
        private const double BarMax = 136;

        public sealed class RamItem : INotifyPropertyChanged
        {
            public string Name { get; init; } = "";
            /// <summary>0 = normal program, 1 = part of Windows, 2 = closing it crashes Windows.</summary>
            public int SystemLevel { get; init; }
            public bool IsSystem => SystemLevel > 0;
            public string WarnColor => SystemLevel >= 2 ? "#FF6B6B" : "#FFB340";
            public string WarnTip => SystemLevel >= 2
                ? Lang.T("أساسي للنظام: إنهاؤه يطيّح ويندوز (شاشة زرقا)")
                : Lang.T("من النظام: إنهاؤه ممكن يخرّب شي في ويندوز أو يطفي جزء منه");
            private string _title = "", _detail = "", _sizeText = "";
            private double _barWidth;
            public long Bytes;
            public string Title { get => _title; set => Set(ref _title, value, nameof(Title)); }
            public string Detail { get => _detail; set => Set(ref _detail, value, nameof(Detail)); }
            public string SizeText { get => _sizeText; set => Set(ref _sizeText, value, nameof(SizeText)); }
            public double BarWidth { get => _barWidth; set => Set(ref _barWidth, value, nameof(BarWidth)); }
            public event PropertyChangedEventHandler? PropertyChanged;
            private void Set<T>(ref T field, T value, string name)
            {
                if (EqualityComparer<T>.Default.Equals(field, value)) return;
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }

        private readonly ObservableCollection<RamItem> _items = new();
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
        // Program descriptions ("Google Chrome" instead of "chrome"), looked up once per name.
        private static readonly Dictionary<string, string> _titles = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> _levels = new(StringComparer.OrdinalIgnoreCase);

        // Closing these takes Windows down (blue screen or instant sign-out).
        private static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
        {
            "csrss", "wininit", "winlogon", "smss", "services", "lsass", "LsaIso", "Registry",
            "Memory Compression", "svchost", "dwm", "fontdrvhost",
        };

        // Windows' own programs that don't always live in the Windows folder.
        private static readonly HashSet<string> WindowsParts = new(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "sihost", "ctfmon", "RuntimeBroker", "StartMenuExperienceHost",
            "ShellExperienceHost", "SearchHost", "SearchApp", "TextInputHost", "MsMpEng", "NisSrv",
            "MpDefenderCoreService", "SecurityHealthService", "SecurityHealthSystray", "spoolsv",
            "audiodg", "dllhost", "conhost", "taskhostw", "WmiPrvSE", "SearchIndexer",
            "ApplicationFrameHost", "SystemSettings", "LockApp", "WidgetService", "Widgets",
            "smartscreen", "CompPkgSrv", "WUDFHost", "dasHost", "unsecapp", "PhoneExperienceHost",
        };

        public RamView()
        {
            InitializeComponent();
            RamList.ItemsSource = _items;
            _timer.Tick += (_, _) => Refresh();
            // Only work while the page is on screen.
            IsVisibleChanged += (_, _) =>
            {
                if (IsVisible) { Refresh(); _timer.Start(); }
                else _timer.Stop();
            };
        }

        public void Release()
        {
            _timer.Stop();
            _items.Clear();
        }

        private void Refresh()
        {
            // Sum per program name.
            var sums = new Dictionary<string, (long bytes, int count, int pid)>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (p.Id == 0 || p.Id == 4) continue;  // Idle / System
                        long ws = p.WorkingSet64;
                        string name = p.ProcessName;
                        sums.TryGetValue(name, out var s);
                        sums[name] = (s.bytes + ws, s.count + 1, s.pid == 0 ? p.Id : s.pid);
                    }
                    catch { }
                }
            }

            var top = sums.OrderByDescending(kv => kv.Value.bytes).Take(MaxRows).ToList();
            long max = top.Count > 0 ? top[0].Value.bytes : 1;

            // Update rows in place (keeps scroll position and buttons steady).
            var existing = _items.ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < top.Count; i++)
            {
                var (name, (bytes, count, pid)) = (top[i].Key, top[i].Value);
                if (!existing.TryGetValue(name, out var item))
                {
                    item = new RamItem { Name = name, Title = TitleFor(name, pid), SystemLevel = LevelFor(name, pid) };
                    _items.Insert(Math.Min(i, _items.Count), item);
                }
                item.Bytes = bytes;
                item.Detail = count > 1 ? Lang.F("{0}.exe · {1} عمليات", name, count) : $"{name}.exe";
                item.SizeText = FormatMb(bytes);
                item.BarWidth = Math.Max(2, BarMax * bytes / max);
                int at = _items.IndexOf(item);
                if (at != i) _items.Move(at, i);
            }
            while (_items.Count > top.Count) _items.RemoveAt(_items.Count - 1);

            UpdateTotal();
        }

        private void UpdateTotal()
        {
            var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref ms) || ms.ullTotalPhys == 0) return;
            ulong used = ms.ullTotalPhys - ms.ullAvailPhys;
            TotalText.Text = $"{used / 1073741824.0:0.0} / {ms.ullTotalPhys / 1073741824.0:0.0} GB  ({ms.dwMemoryLoad}%)";
            double full = Math.Max(0, ((FrameworkElement)TotalBar.Parent).ActualWidth);
            TotalBar.Width = full * used / ms.ullTotalPhys;
        }

        private static string TitleFor(string name, int pid)
        {
            if (_titles.TryGetValue(name, out var t)) return t;
            t = name;
            try
            {
                using var p = Process.GetProcessById(pid);
                string? desc = p.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(desc)) t = desc.Trim();
            }
            catch { }  // system / elevated processes: keep the name
            _titles[name] = t;
            return t;
        }

        /// <summary>
        /// Is this program part of Windows? Known names, anything in the Windows
        /// folder, Windows services (session 0) and processes Windows protects so
        /// much we can't even read where they live. Worked out once per name.
        /// </summary>
        private static int LevelFor(string name, int pid)
        {
            if (_levels.TryGetValue(name, out int level)) return level;
            if (Critical.Contains(name)) level = 2;
            else if (WindowsParts.Contains(name)) level = 1;
            else
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (p.SessionId == 0) level = 1;     // a background service
                }
                catch { }
                if (level == 0)
                {
                    string? path = PathOf(pid);
                    string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    if (path == null) level = 1;          // protected by Windows
                    else if (!string.IsNullOrEmpty(win) &&
                             path.StartsWith(win.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) level = 1;
                }
            }
            _levels[name] = level;
            return level;
        }

        private static string? PathOf(int pid)
        {
            IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new System.Text.StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        private static string FormatMb(long bytes)
        {
            double mb = bytes / 1048576.0;
            return mb >= 1024 ? $"{mb / 1024:0.00} GB" : $"{mb:0} MB";
        }

        private void OnEnd(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: RamItem item }) return;
            var owner = Window.GetWindow(this)!;
            MessageBoxOptions rtl = Lang.MsgOptions;
            bool Ask(string text, string title, MessageBoxImage icon) =>
                MessageBox.Show(owner, text, title, MessageBoxButton.YesNo, icon, MessageBoxResult.No, rtl)
                == MessageBoxResult.Yes;

            if (item.SystemLevel == 0)
            {
                if (!Ask(Lang.F("تبي تقفل \"{0}\"؟\nأي شي ما انحفظ فيه بيروح.", item.Title), Lang.T("إنهاء البرنامج"), MessageBoxImage.Question))
                    return;
            }
            else
            {
                // Part of Windows: a clear warning first (No is the default button).
                string why = item.SystemLevel >= 2
                    ? Lang.T("هذا جزء أساسي من ويندوز.\nلو قفلته الجهاز بيطيح (شاشة زرقا أو يطلعك من حسابك) وكل شي مفتوح وما انحفظ بيروح.")
                    : Lang.T("هذا جزء من ويندوز أو خدمة تشتغل بالخلفية.\nقفله ممكن يخرّب شي في الجهاز (مثل التاسك بار أو الصوت أو النت أو الحماية) لين تعيد التشغيل.");
                if (!Ask(Lang.F("⚠ \"{0}\" ({1}.exe)\n\n{2}\n\nمتأكد تبي تقفله؟", item.Title, item.Name, why), Lang.T("تحذير: برنامج من النظام"), MessageBoxImage.Warning))
                    return;
                // Closing it takes Windows down: ask once more.
                if (item.SystemLevel >= 2 &&
                    !Ask(Lang.T("آخر تحذير: الجهاز بيطيح الحين لو كمّلت.\nكمّل؟"), Lang.T("تحذير: برنامج من النظام"), MessageBoxImage.Stop))
                    return;
            }

            int failed = 0;
            foreach (var p in Process.GetProcessesByName(item.Name))
            {
                using (p)
                {
                    try { p.Kill(true); } catch { failed++; }
                }
            }
            if (failed > 0)
            {
                MessageBox.Show(Window.GetWindow(this)!,
                    Lang.T("بعض العمليات ما انقفلت. غالبًا تحتاج صلاحية المسؤول أو هي من النظام."),
                    Lang.T("إنهاء البرنامج"), MessageBoxButton.OK, MessageBoxImage.Information,
                    MessageBoxResult.OK, Lang.MsgOptions);
            }
            Refresh();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                         ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, System.Text.StringBuilder name, ref int size);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);
    }
}
