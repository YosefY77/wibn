using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Windows.Devices.Enumeration.Pnp;

namespace WinPlus
{
    /// <summary>
    /// Finds every battery it can read: the PC's own, Bluetooth devices (what Windows shows in
    /// Settings → Bluetooth, classic and LE), and devices on a USB receiver or cable that tell
    /// their battery: the standard HID "battery strength", Logitech (HID++ 2.0, Unifying /
    /// Bolt / Lightspeed or the cable) and Razer. Other brands keep their battery to their own app.
    /// Everything here blocks, so it runs off the UI thread.
    /// </summary>
    internal static class BatteryReader
    {
        public enum Kind { Pc, Mouse, Keyboard, Headset, Gamepad, Other }

        public sealed record Device(string Key, string Name, Kind Kind, int Percent, bool Charging);

        public static List<Device> ReadAll()
        {
            var list = new List<Device>();
            void Add(Func<IEnumerable<Device>> source, string what)
            {
                try { list.AddRange(source()); }
                catch (Exception ex) { CrashLog.Write("battery " + what, ex); }
            }
            Add(Pc, "pc");
            Add(Bluetooth, "bluetooth");
            Add(Hid, "hid");

            // The same device on two paths (e.g. a Logitech mouse on Bluetooth that Windows also
            // reads): keep the first one of each name.
            return list.GroupBy(d => d.Name.Trim().ToLowerInvariant()).Select(g => g.First()).ToList();
        }

        public static Kind GuessKind(string name)
        {
            string n = name.ToLowerInvariant();
            if (Has(n, "mouse", "mice", "ماوس", "trackball", "mx master", "mx anywhere", "viper", "deathadder", "basilisk", "g pro x superlight", "g502", "g305", "g304")) return Kind.Mouse;
            if (Has(n, "keyboard", "kb", "كيبورد", "keys", "mx mechanical", "blackwidow", "huntsman")) return Kind.Keyboard;
            if (Has(n, "bud", "pod", "head", "ear", "audio", "sound", "jbl", "sony", "wh-", "wf-", "bose", "beats", "سماع", "hands-free", "arctis", "cloud", "kraken", "barracuda", "blackshark", "g435", "g733", "g535")) return Kind.Headset;
            if (Has(n, "controller", "gamepad", "xbox", "dualsense", "dualshock", "joy-con", "pro controller")) return Kind.Gamepad;
            return Kind.Other;
        }

        private static bool Has(string s, params string[] parts) => parts.Any(s.Contains);

        // ---- The PC ---------------------------------------------------------------------------

        private static IEnumerable<Device> Pc()
        {
            if (!GetSystemPowerStatus(out var s)) yield break;
            if ((s.BatteryFlag & 128) != 0 || s.BatteryFlag == 255 || s.BatteryLifePercent > 100) yield break;   // no battery / unknown
            yield return new Device("pc", Lang.T("الجهاز"), Kind.Pc, s.BatteryLifePercent, s.ACLineStatus == 1 || (s.BatteryFlag & 8) != 0);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public int BatteryLifeTime, BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

        // ---- Bluetooth --------------------------------------------------------------------------

        private const string BtBattery = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
        private const string ContainerIdKey = "System.Devices.ContainerId";

        /// <summary>Windows keeps the battery Bluetooth devices report (hands-free headsets, LE
        /// mice and keyboards) on their device nodes; one device can have several nodes, they
        /// share a container, and the container says if it's connected right now.</summary>
        private static IEnumerable<Device> Bluetooth()
        {
            var nodes = PnpObject.FindAllAsync(PnpObjectType.Device, new[] { BtBattery, ContainerIdKey }, "")
                                 .AsTask().GetAwaiter().GetResult();
            var byContainer = new Dictionary<Guid, int>();
            foreach (var node in nodes)
            {
                if (!node.Properties.TryGetValue(BtBattery, out var b) || b == null) continue;
                int level = Convert.ToInt32(b);
                if (level < 0 || level > 100) continue;
                if (!node.Properties.TryGetValue(ContainerIdKey, out var c) || c is not Guid container) continue;
                byContainer[container] = byContainer.TryGetValue(container, out var old) ? Math.Min(old, level) : level;
            }

            foreach (var (container, level) in byContainer)
            {
                PnpObject box;
                try
                {
                    box = PnpObject.CreateFromIdAsync(PnpObjectType.DeviceContainer, container.ToString("B"),
                        new[] { "System.Devices.Connected", "System.ItemNameDisplay", "System.Devices.Category" })
                        .AsTask().GetAwaiter().GetResult();
                }
                catch { continue; }
                if (box.Properties.TryGetValue("System.Devices.Connected", out var on) && on is bool connected && !connected) continue;
                string name = box.Properties.TryGetValue("System.ItemNameDisplay", out var n) && n is string s && s.Length > 0 ? s : "Bluetooth";
                var kind = KindFromCategory(box.Properties.TryGetValue("System.Devices.Category", out var cat) ? cat as string[] : null);
                if (kind == Kind.Other) kind = GuessKind(name);
                yield return new Device("bt:" + container, name, kind, level, false);
            }
        }

        private static Kind KindFromCategory(string[]? categories)
        {
            foreach (var c in categories ?? Array.Empty<string>())
            {
                if (c.StartsWith("Input.Mouse", StringComparison.OrdinalIgnoreCase)) return Kind.Mouse;
                if (c.StartsWith("Input.Keyboard", StringComparison.OrdinalIgnoreCase)) return Kind.Keyboard;
                if (c.StartsWith("Input.Gaming", StringComparison.OrdinalIgnoreCase)) return Kind.Gamepad;
                if (c.StartsWith("Audio", StringComparison.OrdinalIgnoreCase)) return Kind.Headset;
            }
            return Kind.Other;
        }

        // ---- USB / receivers (HID) --------------------------------------------------------------

        private sealed class HidInfo
        {
            public string Path = "";
            public ushort Vid, Pid;
            public ushort UsagePage, Usage;
            public int InputLength, OutputLength, FeatureLength;
            public string Product = "";
        }

        private static IEnumerable<Device> Hid()
        {
            var all = HidInterfaces();
            var found = new List<Device>();

            // Logitech: the HID++ long-message collection of each receiver / device.
            foreach (var h in all.Where(h => h.Vid == 0x046D && h.UsagePage == 0xFF00 && h.Usage == 0x0002))
                found.AddRange(Logitech.Read(h));

            // Razer: the vendor collection that takes 90-byte feature reports.
            var razerDone = new HashSet<string>();
            foreach (var h in all.Where(h => h.Vid == 0x1532 && h.FeatureLength >= 91))
            {
                string unit = UnitOf(h.Path);
                if (razerDone.Contains(unit)) continue;
                if (Razer.Read(h) is { } d) { found.Add(d); razerDone.Add(unit); }
            }

            // Attack Shark mice (Xenta receiver): they send their battery by themselves every ~2 s,
            // so each receiver is listened to on its own thread while the rest is read.
            var sharks = all.Where(h => h.Vid == 0x1D57 && h.UsagePage == 0x000A && h.InputLength == 5)
                            .Select(h => System.Threading.Tasks.Task.Run(() => AttackShark.Read(h))).ToList();

            // AULA keyboards (Compx receiver): asked on their vendor channel.
            foreach (var h in all.Where(h => h.Vid == 0x3554 && h.UsagePage == 0xFF02 && h.OutputLength == 20))
                if (Aula.Read(h) is { } d) found.Add(d);

            // Anything that tells its battery the standard way.
            foreach (var h in all.Where(h => h.Vid != 0x046D))
                if (Standard.Read(h) is { } d) found.Add(d);

            foreach (var t in sharks)
                if (t.GetAwaiter().GetResult() is { } d) found.Add(d);

            return found.GroupBy(d => d.Key).Select(g => g.First());
        }

        /// <summary>"…vid_1532&pid_00b7&mi_00&col02#…" → the part before the collection, so
        /// the collections of one device count once.</summary>
        private static string UnitOf(string path)
        {
            string p = path.ToLowerInvariant();
            int hash = p.IndexOf('#', p.IndexOf("hid#", StringComparison.Ordinal) + 4);
            string head = hash > 0 ? p[..hash] : p;
            int col = head.IndexOf("&col", StringComparison.Ordinal);
            return col > 0 ? head[..col] : head;
        }

        private static List<HidInfo> HidInterfaces()
        {
            var result = new List<HidInfo>();
            HidD_GetHidGuid(out var guid);
            IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12 /* PRESENT | DEVICEINTERFACE */);
            if (set == new IntPtr(-1)) return result;
            try
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
                {
                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                    if (size <= 0) continue;
                    IntPtr buf = Marshal.AllocHGlobal(size);
                    try
                    {
                        Marshal.WriteInt32(buf, 8);   // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W on x64
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buf, size, out _, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringUni(buf + 4) ?? "";
                        if (Describe(path) is { } info) result.Add(info);
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return result;
        }

        private static HidInfo? Describe(string path)
        {
            using var h = Open(path, 0);
            if (h.IsInvalid) return null;
            var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
            if (!HidD_GetAttributes(h, ref attr)) return null;
            if (!HidD_GetPreparsedData(h, out var pre)) return null;
            try
            {
                if (HidP_GetCaps(pre, out var caps) != HIDP_STATUS_SUCCESS) return null;
                var name = new char[128];
                string product = HidD_GetProductString(h, name, name.Length * 2) ? new string(name).TrimEnd('\0').Trim() : "";
                return new HidInfo
                {
                    Path = path, Vid = attr.VendorID, Pid = attr.ProductID,
                    UsagePage = caps.UsagePage, Usage = caps.Usage,
                    InputLength = caps.InputReportByteLength, OutputLength = caps.OutputReportByteLength,
                    FeatureLength = caps.FeatureReportByteLength, Product = product,
                };
            }
            finally { HidD_FreePreparsedData(pre); }
        }

        private static SafeFileHandle Open(string path, uint access, bool overlapped = false) =>
            CreateFile(path, access, 3 /* share read | write */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
                       overlapped ? 0x40000000u : 0u, IntPtr.Zero);

        // ---- The standard way: Generic Device Controls → Battery Strength -----------------------

        private static class Standard
        {
            private const ushort GenericDevice = 0x06, BatteryStrength = 0x20;
            private const ushort PowerDevice = 0x85, RemainingCapacity = 0x66, Charging = 0x44;

            public static Device? Read(HidInfo info)
            {
                using var h = Open(info.Path, 0);
                if (h.IsInvalid || !HidD_GetPreparsedData(h, out var pre)) return null;
                try
                {
                    if (HidP_GetCaps(pre, out var caps) != HIDP_STATUS_SUCCESS) return null;
                    foreach (var type in new[] { HIDP_REPORT_TYPE.Feature, HIDP_REPORT_TYPE.Input })
                    {
                        int count = type == HIDP_REPORT_TYPE.Feature ? caps.NumberFeatureValueCaps : caps.NumberInputValueCaps;
                        int length = type == HIDP_REPORT_TYPE.Feature ? caps.FeatureReportByteLength : caps.InputReportByteLength;
                        if (count == 0 || length == 0) continue;
                        var values = new HIDP_VALUE_CAPS[count];
                        ushort n = (ushort)count;
                        if (HidP_GetValueCaps(type, values, ref n, pre) != HIDP_STATUS_SUCCESS) continue;
                        foreach (var v in values.Take(n))
                        {
                            bool strength = v.UsagePage == GenericDevice && v.UsageMin == BatteryStrength;
                            bool capacity = v.UsagePage == PowerDevice && v.UsageMin == RemainingCapacity;
                            if (!strength && !capacity) continue;
                            var report = new byte[length];
                            report[0] = v.ReportID;
                            bool got = type == HIDP_REPORT_TYPE.Feature
                                ? HidD_GetFeature(h, report, report.Length)
                                : HidD_GetInputReport(h, report, report.Length);
                            if (!got) continue;
                            if (HidP_GetUsageValue(type, v.UsagePage, v.LinkCollection, v.UsageMin, out uint raw, pre, report, report.Length) != HIDP_STATUS_SUCCESS) continue;
                            int min = v.LogicalMin, max = v.LogicalMax;
                            if (max <= min) { min = 0; max = 100; }
                            int percent = (int)Math.Round(((int)raw - min) * 100.0 / (max - min));
                            if (percent < 0 || percent > 100) continue;
                            string name = info.Product.Length > 0 ? info.Product : $"USB {info.Vid:X4}:{info.Pid:X4}";
                            return new Device("hid:" + UnitOf(info.Path), name, GuessKind(name), percent, false);
                        }
                    }
                    return null;
                }
                finally { HidD_FreePreparsedData(pre); }
            }
        }

        // ---- Logitech (HID++ 2.0) ---------------------------------------------------------------

        private static class Logitech
        {
            private const byte Long = 0x11, SwId = 0x0A;
            private const ushort FUnified = 0x1004, FStatus = 0x1000, FVoltage = 0x1001, FName = 0x0005;

            public static IEnumerable<Device> Read(HidInfo info)
            {
                var found = new List<Device>();
                using var h = Open(info.Path, 0xC0000000 /* read | write */, overlapped: true);
                if (h.IsInvalid) return found;
                using var io = new FileStream(h, FileAccess.ReadWrite, 0, true);
                // A device on the cable answers as index 0xFF (a receiver itself doesn't speak
                // HID++ 2.0, so it says no); a receiver holds up to 6 devices at index 1–6.
                try { if (One(io, 0xFF, info) is { } direct) { found.Add(direct); return found; } }
                catch (Exception) { }
                for (byte index = 1; index <= 6; index++)
                {
                    try { if (One(io, index, info) is { } d) found.Add(d); }
                    catch (Exception) { }
                }
                return found;
            }

            private static Device? One(FileStream io, byte index, HidInfo info)
            {
                int percent = -1; bool charging = false;
                if (Feature(io, index, FUnified) is byte u && Call(io, index, u, 1) is { } us)
                {
                    percent = us[0];
                    if (percent == 0) percent = (us[1] & 8) != 0 ? 90 : (us[1] & 4) != 0 ? 50 : (us[1] & 2) != 0 ? 20 : (us[1] & 1) != 0 ? 5 : 0;
                    charging = us[2] is 1 or 2 or 3;
                }
                else if (Feature(io, index, FStatus) is byte s && Call(io, index, s, 0) is { } ss)
                {
                    percent = ss[0];
                    charging = ss[2] is 1 or 2 or 3;
                }
                else if (Feature(io, index, FVoltage) is byte v && Call(io, index, v, 0) is { } vs)
                {
                    int mv = vs[0] << 8 | vs[1];
                    percent = VoltageToPercent(mv);
                    charging = (vs[2] & 0x80) != 0;
                }
                if (percent < 0 || percent > 100) return null;

                string name = info.Product;
                var kind = Kind.Other;
                if (Feature(io, index, FName) is byte f)
                {
                    if (Call(io, index, f, 0) is { } c)
                    {
                        int len = c[0];
                        var chars = new List<byte>();
                        while (chars.Count < len && Call(io, index, f, 1, (byte)chars.Count) is { } part)
                        {
                            int take = Math.Min(16, len - chars.Count);
                            chars.AddRange(part.Take(take));
                            if (take == 0) break;
                        }
                        if (chars.Count > 0) name = System.Text.Encoding.UTF8.GetString(chars.ToArray()).TrimEnd('\0').Trim();
                    }
                    if (Call(io, index, f, 2) is { } t)
                        kind = t[0] switch { 0 or 2 => Kind.Keyboard, 3 or 4 or 5 => Kind.Mouse, 8 => Kind.Headset, 10 => Kind.Gamepad, _ => Kind.Other };
                }
                if (kind == Kind.Other) kind = GuessKind(name);
                return new Device($"logi:{UnitOf(info.Path)}:{index}", name, kind, percent, charging);
            }

            /// <summary>Rough charge from a Logitech battery's voltage (their own 3.5–4.2 V curve).</summary>
            private static int VoltageToPercent(int mv)
            {
                int[] volts = { 4186, 4067, 3989, 3922, 3859, 3811, 3778, 3751, 3725, 3696, 3500 };
                if (mv >= volts[0]) return 100;
                for (int i = 1; i < volts.Length; i++)
                    if (mv >= volts[i])
                        return (int)Math.Round(100 - 10 * (i - 1 + (volts[i - 1] - mv) / (double)(volts[i - 1] - volts[i])));
                return 0;
            }

            /// <summary>Which slot a feature has on this device (root feature, function 0), or null.</summary>
            private static byte? Feature(FileStream io, byte index, ushort id)
            {
                var r = Call(io, index, 0x00, 0, (byte)(id >> 8), (byte)id);
                return r != null && r[0] != 0 ? r[0] : null;
            }

            /// <summary>Sends one request and waits for its answer: the 16 bytes after the header,
            /// or null for an error, no device there, or no answer in time.</summary>
            private static byte[]? Call(FileStream io, byte index, byte feature, byte function, params byte[] args)
            {
                var msg = new byte[20];
                msg[0] = Long; msg[1] = index; msg[2] = feature; msg[3] = (byte)(function << 4 | SwId);
                Array.Copy(args, 0, msg, 4, Math.Min(args.Length, 16));
                io.Write(msg, 0, msg.Length);

                var until = DateTime.UtcNow.AddMilliseconds(600);
                var buf = new byte[64];
                while (DateTime.UtcNow < until)
                {
                    using var cts = new CancellationTokenSource(until - DateTime.UtcNow);
                    int n;
                    try { n = io.ReadAsync(buf, 0, buf.Length, cts.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { return null; }
                    if (n < 7 || buf[1] != index) continue;
                    // HID++ 1.0 error (e.g. nothing paired at this index) or HID++ 2.0 error.
                    if (buf[2] == 0x8F && buf[3] == feature) return null;
                    if (buf[2] == 0xFF && buf[3] == feature && buf[4] == msg[3]) return null;
                    if (buf[2] == feature && buf[3] == msg[3]) return buf.Skip(4).Take(16).ToArray();
                }
                return null;
            }
        }

        // ---- Attack Shark (Xenta 2.4G receiver 1D57) ---------------------------------------------

        /// <summary>The receiver pushes "03 MM 40 ST NN" every ~2 s while the mouse is on: MM is
        /// the model (0x10 = X6, which sends the level in tens; others send 0–100), ST is 1 on
        /// battery, 2 / 3 on the cable (3 = charging). Every new link first sends a false 100%,
        /// so a reading counts only when it comes twice in a row. Nothing is ever sent to it.</summary>
        private static class AttackShark
        {
            public static Device? Read(HidInfo info)
            {
                using var h = Open(info.Path, 0x80000000 /* read */, overlapped: true);
                if (h.IsInvalid) return null;
                using var io = new FileStream(h, FileAccess.Read, 0, true);
                var until = DateTime.UtcNow.AddSeconds(7);
                var buf = new byte[Math.Max(5, info.InputLength)];
                (int model, int state, int level)? last = null;
                while (DateTime.UtcNow < until)
                {
                    using var cts = new CancellationTokenSource(until - DateTime.UtcNow);
                    int n;
                    try { n = io.ReadAsync(buf, 0, buf.Length, cts.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { return null; }
                    catch (IOException) { return null; }
                    if (n < 5 || buf[0] != 0x03 || buf[2] != 0x40) continue;
                    var frame = (model: (int)buf[1], state: (int)buf[3], level: (int)buf[4]);
                    if (last != frame) { last = frame; continue; }

                    int percent = frame.model == 0x10 ? frame.level * 10 : frame.level;
                    if (percent < 0 || percent > 100) return null;
                    string name = frame.model switch { 0x10 => "Attack Shark X6", 0x55 => "Attack Shark X11", _ => "Attack Shark" };
                    return new Device("shark:" + UnitOf(info.Path), name, Kind.Mouse, percent, frame.state == 0x03);
                }
                return null;
            }
        }

        // ---- AULA (Compx 2.4G receiver 3554) -----------------------------------------------------

        /// <summary>Ask "13 4A …" (20 bytes, the last one the sum of the others) on the FF02
        /// channel; the answer "13 4A 01 00 NN PP ST …" has the percent at 5 and 0x10 at 6 while
        /// charging. Asking wakes the keyboard's radio, so it's asked at most every 5 minutes and
        /// the last answer is shown in between (a sleeping keyboard doesn't answer at all).</summary>
        private static class Aula
        {
            private static readonly Dictionary<string, (DateTime At, Device? Last)> Cache = new();
            private static readonly TimeSpan Every = TimeSpan.FromMinutes(5), Keep = TimeSpan.FromMinutes(30);

            public static Device? Read(HidInfo info)
            {
                string key = "aula:" + UnitOf(info.Path);
                lock (Cache)
                    if (Cache.TryGetValue(key, out var c) && c.Last != null && DateTime.UtcNow - c.At < Every) return c.Last;

                var fresh = Ask(info, key);
                lock (Cache)
                {
                    if (fresh == null && Cache.TryGetValue(key, out var old) && old.Last != null && DateTime.UtcNow - old.At < Keep)
                        return old.Last;   // asleep: keep showing what it said last
                    Cache[key] = (DateTime.UtcNow, fresh);
                }
                return fresh;
            }

            private static Device? Ask(HidInfo info, string key)
            {
                using var h = Open(info.Path, 0xC0000000 /* read | write */, overlapped: true);
                if (h.IsInvalid) return null;
                using var io = new FileStream(h, FileAccess.ReadWrite, 0, true);
                var buf = new byte[64];
                Drain(io, buf);

                // The first ask may only wake the radio; 0xCA is the same question with the "read" bit.
                foreach (byte command in new byte[] { 0x4A, 0xCA, 0x4A, 0xCA })
                {
                    var req = new byte[20];
                    req[0] = 0x13; req[1] = command;
                    req[19] = Sum(req, 19);
                    try { io.Write(req, 0, req.Length); } catch (IOException) { return null; }

                    var until = DateTime.UtcNow.AddMilliseconds(400);
                    while (DateTime.UtcNow < until)
                    {
                        using var cts = new CancellationTokenSource(until - DateTime.UtcNow);
                        int n;
                        try { n = io.ReadAsync(buf, 0, buf.Length, cts.Token).GetAwaiter().GetResult(); }
                        catch (OperationCanceledException) { break; }
                        catch (IOException) { return null; }
                        if (n < 20 || buf[0] != 0x13 || buf[1] != 0x4A || buf[4] < 2 || buf[19] != Sum(buf, 19)) continue;
                        int percent = buf[5];
                        if (percent > 100) continue;
                        string name = info.Product.Contains("AULA", StringComparison.OrdinalIgnoreCase) ? info.Product : "AULA";
                        return new Device(key, name, Kind.Keyboard, percent, buf[6] == 0x10);
                    }
                }
                return null;
            }

            private static void Drain(FileStream io, byte[] buf)
            {
                for (int i = 0; i < 20; i++)
                {
                    using var cts = new CancellationTokenSource(30);
                    try { io.ReadAsync(buf, 0, buf.Length, cts.Token).GetAwaiter().GetResult(); }
                    catch { return; }
                }
            }

            private static byte Sum(byte[] b, int count)
            {
                int s = 0;
                for (int i = 0; i < count; i++) s += b[i];
                return (byte)s;
            }
        }

        // ---- Razer ------------------------------------------------------------------------------

        private static class Razer
        {
            private static readonly byte[] Transactions = { 0x1F, 0x3F, 0xFF, 0x08 };

            public static Device? Read(HidInfo info)
            {
                using var h = Open(info.Path, 0);
                if (h.IsInvalid) return null;
                foreach (byte t in Transactions)
                {
                    var level = Ask(h, t, 0x80);
                    if (level == null) continue;
                    bool charging = Ask(h, t, 0x84) is { } c && c[1] == 1;
                    int percent = (int)Math.Round(level[1] * 100 / 255.0);
                    string name = info.Product.Length > 0 ? info.Product : "Razer";
                    return new Device("razer:" + UnitOf(info.Path), name, GuessKind(name), percent, charging);
                }
                return null;
            }

            /// <summary>Class 0x07 (power) command; the answer's arguments, or null.</summary>
            private static byte[]? Ask(SafeFileHandle h, byte transaction, byte command)
            {
                var report = new byte[91];          // report id 0 + 90 bytes
                report[2] = transaction;
                report[6] = 0x02;                   // data size
                report[7] = 0x07;                   // command class
                report[8] = command;
                byte crc = 0;
                for (int i = 3; i < 89; i++) crc ^= report[i];
                report[89] = crc;
                if (!HidD_SetFeature(h, report, report.Length)) return null;
                for (int tries = 0; tries < 5; tries++)
                {
                    Thread.Sleep(30);
                    var answer = new byte[91];
                    if (!HidD_GetFeature(h, answer, answer.Length)) return null;
                    byte status = answer[1];
                    if (status == 0x01) continue;   // busy
                    if (status != 0x02 || answer[2] != transaction || answer[8] != 0x07 || answer[9] != command) return null;
                    return answer.Skip(9).Take(80).ToArray();
                }
                return null;
            }
        }

        // ---- Native -----------------------------------------------------------------------------

        private const int HIDP_STATUS_SUCCESS = 0x00110000;

        private enum HIDP_REPORT_TYPE { Input = 0, Output = 1, Feature = 2 }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct HIDP_VALUE_CAPS
        {
            public ushort UsagePage;
            public byte ReportID;
            [MarshalAs(UnmanagedType.U1)] public bool IsAlias;
            public ushort BitField, LinkCollection, LinkUsage, LinkUsagePage;
            [MarshalAs(UnmanagedType.U1)] public bool IsRange, IsStringRange, IsDesignatorRange, IsAbsolute, HasNull;
            public byte Reserved;
            public ushort BitSize, ReportCount;
            public ushort Reserved2a, Reserved2b, Reserved2c, Reserved2d, Reserved2e;
            public uint UnitsExp, Units;
            public int LogicalMin, LogicalMax, PhysicalMin, PhysicalMax;
            // Range / NotRange union
            public ushort UsageMin, UsageMax, StringMin, StringMax, DesignatorMin, DesignatorMax, DataIndexMin, DataIndexMax;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
        [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)] private static extern bool HidD_GetProductString(SafeFileHandle h, char[] buffer, int length);
        [DllImport("hid.dll")] private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] report, int length);
        [DllImport("hid.dll")] private static extern bool HidD_SetFeature(SafeFileHandle h, byte[] report, int length);
        [DllImport("hid.dll")] private static extern bool HidD_GetInputReport(SafeFileHandle h, byte[] report, int length);
        [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
        [DllImport("hid.dll")] private static extern int HidP_GetValueCaps(HIDP_REPORT_TYPE type, [Out] HIDP_VALUE_CAPS[] caps, ref ushort length, IntPtr data);
        [DllImport("hid.dll")]
        private static extern int HidP_GetUsageValue(HIDP_REPORT_TYPE type, ushort usagePage, ushort link, ushort usage, out uint value, IntPtr data, byte[] report, int length);

        [DllImport("setupapi.dll")] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, int flags);
        [DllImport("setupapi.dll")] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr info);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    }
}
