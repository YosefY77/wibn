using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// The PC's specs, all of them: Windows, the PC and its board, the processor, every memory
    /// stick, the graphics cards (their real memory, not WMI's 4 GB cap), the drives (SSD / HDD /
    /// NVMe), the screens and the network. Read once on a background thread (WMI takes a second
    /// or two); "Copy all" puts it all on the clipboard as plain text to send to someone.
    /// </summary>
    internal sealed class SpecsPage : StackPanel
    {
        private static Style St(string key) => (Style)Application.Current.FindResource(key);

        private sealed record Section(string Icon, string Title, List<(string Key, string Value)> Rows);

        private readonly StackPanel _body = new();
        private readonly TextBlock _status;
        private List<Section> _sections = new();
        private bool _loading;

        public SpecsPage()
        {
            var copy = new Button { Style = St("AccentButton"), Content = Lang.T("انسخ الكل"), Margin = new Thickness(0, 0, 8, 0) };
            copy.Click += (_, _) => CopyAll(copy);
            var refresh = new Button { Style = St("FlatButton"), Content = Lang.T("تحديث") };
            refresh.Click += (_, _) => Load();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            buttons.Children.Add(copy);
            buttons.Children.Add(refresh);
            Children.Add(buttons);

            _status = new TextBlock { Style = St("RowDesc"), FontSize = 13, Margin = new Thickness(2, 0, 0, 10), Text = Lang.T("أجمع المواصفات…") };
            Children.Add(_status);
            Children.Add(_body);
            Loaded += (_, _) => { if (_sections.Count == 0) Load(); };
        }

        private async void Load()
        {
            if (_loading) return;
            _loading = true;
            _status.Text = Lang.T("أجمع المواصفات…");
            _status.Visibility = Visibility.Visible;
            try
            {
                // WMI wants its own (MTA) thread; it's also the slow part.
                _sections = await Task.Run(Gather);
                Show();
                _status.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                CrashLog.Write("specs", ex);
                _status.Text = Lang.T("ما قدرت أقرا المواصفات: ") + ex.Message;
            }
            finally { _loading = false; }
        }

        private void Show()
        {
            _body.Children.Clear();
            foreach (var s in _sections.Where(s => s.Rows.Count > 0))
            {
                var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 14, 0, 8) };
                head.Children.Add(new TextBlock { Style = St("RowIcon"), Text = s.Icon, VerticalAlignment = VerticalAlignment.Center });
                head.Children.Add(new TextBlock { Style = St("Section"), FontSize = 18, Text = s.Title, VerticalAlignment = VerticalAlignment.Center });
                _body.Children.Add(head);

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                for (int i = 0; i < s.Rows.Count; i++)
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    var (key, value) = s.Rows[i];
                    bool divider = key.Length == 0;   // a sub-heading row (e.g. "Stick 2")
                    var k = new TextBlock
                    {
                        Style = St(divider ? "RowTitle" : "RowDesc"), Text = divider ? value : key, FontSize = 13,
                        Margin = new Thickness(0, divider && i > 0 ? 10 : 3, 12, 3), TextWrapping = TextWrapping.Wrap,
                    };
                    Grid.SetRow(k, i);
                    if (divider) Grid.SetColumnSpan(k, 2);
                    grid.Children.Add(k);
                    if (divider) continue;
                    var v = new TextBox
                    {
                        // Selectable, so one value can be copied on its own.
                        Text = value, IsReadOnly = true, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent,
                        Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("Fg"), FontSize = 13,
                        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3), Padding = new Thickness(0),
                        FlowDirection = FlowDirection.LeftToRight, HorizontalContentAlignment = Lang.Flow == FlowDirection.RightToLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    };
                    v.Style = null;
                    Grid.SetRow(v, i);
                    Grid.SetColumn(v, 1);
                    grid.Children.Add(v);
                }
                _body.Children.Add(new Border { Style = St("Row"), Margin = new Thickness(0, 0, 0, 6), Child = grid });
            }
        }

        private void CopyAll(Button button)
        {
            var sb = new StringBuilder();
            foreach (var s in _sections.Where(s => s.Rows.Count > 0))
            {
                sb.AppendLine("== " + s.Title + " ==");
                foreach (var (k, v) in s.Rows) sb.AppendLine(k.Length == 0 ? "-- " + v : $"{k}: {v}");
                sb.AppendLine();
            }
            try
            {
                Clipboard.SetText(sb.ToString().TrimEnd());
                button.Content = Lang.T("انتسخ ✓");
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                t.Tick += (_, _) => { t.Stop(); button.Content = Lang.T("انسخ الكل"); };
                t.Start();
            }
            catch (COMException) { }
        }

        // ---- Gathering (background thread) ----------------------------------------------------------

        private static List<Section> Gather()
        {
            // All sections at once (each WMI query waits on its own), kept in this order.
            var parts = new (string Icon, string Title, Func<List<(string, string)>> Rows)[]
            {
                ("", Lang.T("ويندوز"), WindowsRows),
                ("", Lang.T("الجهاز"), SystemRows),
                ("", Lang.T("المعالج"), CpuRows),
                ("", Lang.T("الرام"), RamRows),
                ("", Lang.T("كرت الشاشة"), GpuRows),
                ("", Lang.T("التخزين"), DiskRows),
                ("", Lang.T("الشاشات"), ScreenRows),
                ("", Lang.T("الشبكة"), NetworkRows),
                ("", Lang.T("الصوت"), AudioRows),
            };
            var tasks = parts.Select(p => Task.Run(() =>
            {
                try { return p.Rows(); }
                catch (Exception ex) { CrashLog.Write("specs " + p.Title, ex); return new List<(string, string)>(); }
            })).ToArray();
            Task.WaitAll(tasks);
            return parts.Select((p, i) => new Section(p.Icon, p.Title, tasks[i].Result)).ToList();
        }

        private static IEnumerable<ManagementObject> Wmi(string query, string scope = @"root\cimv2")
        {
            using var searcher = new ManagementObjectSearcher(scope, query);
            using var results = searcher.Get();
            return results.Cast<ManagementObject>().ToList();
        }

        private static string S(ManagementObject o, string name)
        {
            try { return (o[name]?.ToString() ?? "").Trim(); } catch { return ""; }
        }

        private static ulong U(ManagementObject o, string name)
        {
            try { return o[name] is { } v ? Convert.ToUInt64(v) : 0; } catch { return 0; }
        }

        private static string Size(double bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int u = 0;
            while (bytes >= 1024 && u < units.Length - 1) { bytes /= 1024; u++; }
            return $"{bytes:0.#} {units[u]}";
        }

        /// <summary>Drive makers sell in 1000s: "512 GB" on the box, 476 GiB in Windows. Shows both.</summary>
        private static string DiskSize(ulong bytes) =>
            $"{bytes / 1e9:0} GB  ({Size(bytes)})";

        private static void Row(List<(string, string)> rows, string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) rows.Add((Lang.T(key), value));
        }

        private static List<(string, string)> WindowsRows()
        {
            var r = new List<(string, string)>();
            var os = Wmi("SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime, RegisteredUser FROM Win32_OperatingSystem").FirstOrDefault();
            using var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string display = nt?.GetValue("DisplayVersion") as string ?? "";
            string ubr = nt?.GetValue("UBR") is int u ? "." + u : "";
            if (os != null)
            {
                Row(r, "النسخة", S(os, "Caption").Replace("Microsoft ", ""));
                Row(r, "الإصدار", (display.Length > 0 ? display + " · " : "") + Lang.F("بناء {0}", S(os, "BuildNumber") + ubr));
                Row(r, "النظام", S(os, "OSArchitecture"));
                if (S(os, "InstallDate") is { Length: > 0 } inst) Row(r, "تاريخ التثبيت", ManagementDateTimeConverter.ToDateTime(inst).ToString("d"));
                if (S(os, "LastBootUpTime") is { Length: > 0 } boot)
                {
                    var up = DateTime.Now - ManagementDateTimeConverter.ToDateTime(boot);
                    Row(r, "شغال من", Lang.F("{0} يوم و{1} ساعة", (int)up.TotalDays, up.Hours));
                }
            }
            Row(r, "اسم الجهاز", Environment.MachineName);
            Row(r, "المستخدم", Environment.UserName);
            return r;
        }

        private static List<(string, string)> SystemRows()
        {
            var r = new List<(string, string)>();
            if (Wmi("SELECT Manufacturer, Model, SystemFamily, PCSystemType FROM Win32_ComputerSystem").FirstOrDefault() is { } cs)
            {
                Row(r, "الشركة", S(cs, "Manufacturer"));
                Row(r, "الموديل", S(cs, "Model"));
                Row(r, "النوع", U(cs, "PCSystemType") switch { 1 => Lang.T("مكتبي"), 2 => Lang.T("لابتوب"), 3 => Lang.T("محطة عمل"), _ => "" });
            }
            if (Wmi("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard").FirstOrDefault() is { } bb)
                Row(r, "اللوحة الأم", $"{S(bb, "Manufacturer")} {S(bb, "Product")}".Trim());
            if (Wmi("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS").FirstOrDefault() is { } bios)
            {
                string date = S(bios, "ReleaseDate") is { Length: > 0 } d ? ManagementDateTimeConverter.ToDateTime(d).ToString("d") : "";
                Row(r, "البايوس", $"{S(bios, "Manufacturer")} {S(bios, "SMBIOSBIOSVersion")}".Trim() + (date.Length > 0 ? $"  ({date})" : ""));
            }
            using (var sb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"))
                if (sb?.GetValue("UEFISecureBootEnabled") is int on) Row(r, "Secure Boot", on == 1 ? Lang.T("شغال") : Lang.T("طافي"));
            try
            {
                // Needs administrator rights on most PCs: left out quietly when it's not allowed.
                if (Wmi("SELECT SpecVersion FROM Win32_Tpm", @"root\cimv2\Security\MicrosoftTpm").FirstOrDefault() is { } tpm)
                    Row(r, "TPM", S(tpm, "SpecVersion").Split(',').FirstOrDefault() ?? "");
            }
            catch (ManagementException) { }
            catch (UnauthorizedAccessException) { }
            return r;
        }

        private static List<(string, string)> CpuRows()
        {
            var r = new List<(string, string)>();
            var cpus = Wmi("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation FROM Win32_Processor").ToList();
            foreach (var c in cpus)
            {
                if (cpus.Count > 1) r.Add(("", S(c, "Name")));
                Row(r, "الاسم", S(c, "Name"));
                Row(r, "الأنوية", Lang.F("{0} نواة · {1} مسار", U(c, "NumberOfCores"), U(c, "NumberOfLogicalProcessors")));
                Row(r, "السرعة الأساسية", $"{U(c, "MaxClockSpeed") / 1000.0:0.00} GHz");
                string cache = string.Join(" · ", new[] { ("L2", U(c, "L2CacheSize")), ("L3", U(c, "L3CacheSize")) }
                    .Where(x => x.Item2 > 0).Select(x => $"{x.Item1} {Size(x.Item2 * 1024.0)}"));
                Row(r, "الكاش", cache);
                Row(r, "السوكت", S(c, "SocketDesignation"));
            }
            return r;
        }

        private static List<(string, string)> RamRows()
        {
            var r = new List<(string, string)>();
            var sticks = Wmi("SELECT Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, SMBIOSMemoryType, DeviceLocator, FormFactor FROM Win32_PhysicalMemory").ToList();
            ulong total = (ulong)sticks.Sum(s => (double)U(s, "Capacity"));
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            ulong usable = GlobalMemoryStatusEx(ref mem) ? mem.ullTotalPhys : 0;
            Row(r, "المجموع", total > 0 ? Size(total) + (usable > 0 ? "  " + Lang.F("({0} قابلة للاستخدام)", Size(usable)) : "") : Size(usable));
            string type = sticks.Select(s => MemoryType(U(s, "SMBIOSMemoryType"))).FirstOrDefault(t => t.Length > 0) ?? "";
            Row(r, "النوع", type);
            var speed = sticks.Select(s => U(s, "ConfiguredClockSpeed") > 0 ? U(s, "ConfiguredClockSpeed") : U(s, "Speed")).Where(x => x > 0).DefaultIfEmpty().Max();
            if (speed > 0) Row(r, "السرعة", $"{speed} MT/s");
            var slots = Wmi("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray").Sum(a => (double)U(a, "MemoryDevices"));
            if (slots > 0) Row(r, "المداخل", Lang.F("{0} مستخدمة من {1}", sticks.Count, slots));
            int n = 1;
            foreach (var s in sticks)
            {
                r.Add(("", Lang.F("القطعة {0}", n++) + (S(s, "DeviceLocator") is { Length: > 0 } loc ? $"  ·  {loc}" : "")));
                Row(r, "الحجم", Size(U(s, "Capacity")));
                ulong sp = U(s, "ConfiguredClockSpeed") > 0 ? U(s, "ConfiguredClockSpeed") : U(s, "Speed");
                Row(r, "السرعة", sp > 0 ? $"{sp} MT/s" + (U(s, "Speed") > sp ? "  " + Lang.F("(تتحمل {0})", U(s, "Speed")) : "") : "");
                string maker = S(s, "Manufacturer");
                if (maker.All(ch => ch == '0' || char.IsWhiteSpace(ch)) || maker.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase)) maker = "";
                Row(r, "الشركة", maker);
                Row(r, "رقم القطعة", S(s, "PartNumber"));
            }
            return r;
        }

        private static string MemoryType(ulong t) => t switch
        {
            20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 27 => "LPDDR", 28 => "LPDDR2",
            29 => "LPDDR3", 30 => "LPDDR4", 34 => "DDR5", 35 => "LPDDR5", _ => "",
        };

        private static List<(string, string)> GpuRows()
        {
            var r = new List<(string, string)>();
            var all = Wmi("SELECT Name, AdapterRAM, DriverVersion, DriverDate, VideoProcessor, PNPDeviceID FROM Win32_VideoController").ToList();
            // Real cards sit on the PCI bus; virtual screens (Parsec, Meta, remote desktop…) don't. Only those when there's nothing else.
            var gpus = all.Where(g => S(g, "PNPDeviceID").StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)).ToList();
            if (gpus.Count == 0) gpus = all;
            foreach (var g in gpus)
            {
                string name = S(g, "Name");
                if (gpus.Count > 1) r.Add(("", name));
                Row(r, "الاسم", name);
                // WMI's AdapterRAM stops at 4 GB; the driver's own number (in the registry) is the real one.
                ulong vram = RealVram(name);
                if (vram == 0) vram = U(g, "AdapterRAM");
                if (vram > 0) Row(r, "الذاكرة", Size(vram));
                string date = S(g, "DriverDate") is { Length: > 0 } d ? ManagementDateTimeConverter.ToDateTime(d).ToString("d") : "";
                Row(r, "التعريف", S(g, "DriverVersion") + (date.Length > 0 ? $"  ({date})" : ""));
            }
            return r;
        }

        private static ulong RealVram(string name)
        {
            try
            {
                using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                if (cls == null) return 0;
                foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
                {
                    using var k = cls.OpenSubKey(sub);
                    if (k?.GetValue("DriverDesc") as string != name) continue;
                    object? v = k.GetValue("HardwareInformation.qwMemorySize") ?? k.GetValue("HardwareInformation.MemorySize");
                    return v switch
                    {
                        long l => (ulong)l,
                        int i => (uint)i,
                        byte[] b when b.Length >= 8 => BitConverter.ToUInt64(b, 0),
                        byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                        _ => 0,
                    };
                }
            }
            catch { }
            return 0;
        }

        private static List<(string, string)> DiskRows()
        {
            var r = new List<(string, string)>();
            var disks = Wmi("SELECT FriendlyName, MediaType, BusType, Size FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage").ToList();
            foreach (var d in disks)
            {
                r.Add(("", S(d, "FriendlyName")));
                Row(r, "الحجم", DiskSize(U(d, "Size")));
                string bus = U(d, "BusType") switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 12 => "SD", _ => "" };
                string media = U(d, "MediaType") switch { 4 => "SSD", 3 => "HDD", 5 => "SCM", _ => "" };
                Row(r, "النوع", string.Join(" · ", new[] { media, bus }.Where(x => x.Length > 0)));
            }
            var volumes = System.IO.DriveInfo.GetDrives().Where(v => v.IsReady && v.DriveType is System.IO.DriveType.Fixed or System.IO.DriveType.Removable).ToList();
            if (volumes.Count > 0) r.Add(("", Lang.T("الأقسام")));
            foreach (var v in volumes)
            {
                string label = v.VolumeLabel.Length > 0 ? $" ({v.VolumeLabel})" : "";
                Row(r, v.Name.TrimEnd('\\') + label,
                    Lang.F("{0} فاضي من {1}", Size(v.TotalFreeSpace), Size(v.TotalSize)) + $"  ·  {v.DriveFormat}");
            }
            return r;
        }

        private static List<(string, string)> ScreenRows()
        {
            var r = new List<(string, string)>();
            var names = MonitorNames();
            int n = 0;
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                bool got = EnumDisplaySettings(screen.DeviceName, -1 /* current */, ref mode);
                string title = n < names.Count ? names[n] : screen.DeviceName.TrimStart('\\', '.');
                r.Add(("", title + (screen.Primary ? "  ·  " + Lang.T("الرئيسية") : "")));
                Row(r, "الدقة", got ? $"{mode.dmPelsWidth} × {mode.dmPelsHeight}" : $"{screen.Bounds.Width} × {screen.Bounds.Height}");
                if (got && mode.dmDisplayFrequency > 1) Row(r, "التحديث", $"{mode.dmDisplayFrequency} Hz");
                if (got && mode.dmBitsPerPel > 0) Row(r, "الألوان", $"{mode.dmBitsPerPel} bit");
                n++;
            }
            return r;
        }

        /// <summary>The screens' own names (e.g. "HP 27mx"), from what they tell Windows.</summary>
        private static List<string> MonitorNames()
        {
            var list = new List<string>();
            try
            {
                foreach (var m in Wmi("SELECT UserFriendlyName, ManufacturerName FROM WmiMonitorID", @"root\wmi"))
                {
                    static string Text(object? o) => o is ushort[] a ? new string(a.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim() : "";
                    string name = Text(m["UserFriendlyName"]);
                    list.Add(name.Length > 0 ? name : Text(m["ManufacturerName"]));
                }
            }
            catch { }
            return list;
        }

        private static List<(string, string)> NetworkRows()
        {
            var r = new List<(string, string)>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                string d = ni.Description.ToLowerInvariant();
                if (new[] { "virtual", "hyper-v", "vmware", "virtualbox", "wan miniport", "wintun", "tap-", "filter", "pseudo", "bluetooth" }.Any(d.Contains)) continue;
                if (ni.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet)) continue;
                bool up = ni.OperationalStatus == OperationalStatus.Up;
                r.Add(("", ni.Description));
                Row(r, "النوع", ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? Lang.T("واي فاي") : Lang.T("سلك (Ethernet)"));
                Row(r, "الحالة", up ? Lang.T("متصل") : Lang.T("مو متصل"));
                if (up && ni.Speed > 0) Row(r, "السرعة", ni.Speed >= 1_000_000_000 ? $"{ni.Speed / 1e9:0.#} Gbps" : $"{ni.Speed / 1e6:0} Mbps");
                if (up)
                {
                    var ip = ni.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                    if (ip != null) Row(r, "العنوان", ip.Address.ToString());
                }
                string mac = string.Join("-", ni.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
                Row(r, "MAC", mac);
            }
            return r;
        }

        private static List<(string, string)> AudioRows()
        {
            var r = new List<(string, string)>();
            // Real sound hardware (on the board, the graphics card's HDMI, USB, Bluetooth); programs' virtual ones are under ROOT / SW.
            foreach (var name in Wmi("SELECT Name, PNPDeviceID FROM Win32_SoundDevice")
                         .Where(s => !S(s, "PNPDeviceID").StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) && !S(s, "PNPDeviceID").StartsWith("SW", StringComparison.OrdinalIgnoreCase))
                         .Select(s => S(s, "Name")).Where(n => n.Length > 0).Distinct())
                r.Add((Lang.T("جهاز"), name));
            return r;
        }

        // ---- Native ---------------------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
    }
}
