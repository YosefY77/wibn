using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace WinPlus
{
    /// <summary>
    /// The numbers for the info bar: use (from <see cref="SystemWidget.Sampler"/>), the graphics card's heat
    /// (from Windows, the way Task Manager gets it), the processor's heat (from LibreHardwareMonitor /
    /// OpenHardwareMonitor when one runs, else Windows' thermal zone when it really changes), and the network.
    /// </summary>
    internal sealed class Sensors : IDisposable
    {
        public sealed record Reading(double Cpu, double? CpuTemp, double? Gpu, double? GpuTemp,
                                     long RamUsed, long RamTotal, long DiskUsed, long DiskTotal, double NetDown, double NetUp);

        private readonly SystemWidget.Sampler _sampler = new();
        private readonly object _lock = new();

        public Reading Read(bool cpuTemp, bool gpuTemp, bool net)
        {
            lock (_lock)
            {
                var s = _sampler.Take();
                double down = 0, up = 0;
                if (net) (down, up) = NetSpeed();
                return new Reading(s.Cpu, cpuTemp ? CpuTemp() : null, s.Gpu, gpuTemp ? GpuTemp() : null,
                                   s.RamUsed, s.RamTotal, s.DiskUsed, s.DiskTotal, down, up);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _sampler.Dispose();
                CloseAdapters();
                if (_zoneQuery != IntPtr.Zero) PdhCloseQuery(_zoneQuery);
                _zoneQuery = _zoneCounter = IntPtr.Zero;
            }
        }

        // ---- Network ----------------------------------------------------------------------------

        private long _netIn, _netOut;
        private DateTime _netAt;

        private (double Down, double Up) NetSpeed()
        {
            long rx = 0, tx = 0;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                try { var st = ni.GetIPv4Statistics(); rx += st.BytesReceived; tx += st.BytesSent; } catch { }
            }
            var now = DateTime.UtcNow;
            double secs = (now - _netAt).TotalSeconds;
            (double, double) result = _netAt == default || secs <= 0 ? (0, 0) : (Math.Max(0, (rx - _netIn) / secs), Math.Max(0, (tx - _netOut) / secs));
            _netIn = rx; _netOut = tx; _netAt = now;
            return result;
        }

        // ---- The graphics card's heat (D3DKMT, like Task Manager) ---------------------------------

        private uint[] _adapters = Array.Empty<uint>();
        private bool _adaptersTried;

        private double? GpuTemp()
        {
            if (!_adaptersTried)
            {
                _adaptersTried = true;
                try
                {
                    var e = new ENUMADAPTERS2();
                    if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return null;
                    e.pAdapters = Marshal.AllocHGlobal((int)e.NumAdapters * 20);
                    try
                    {
                        if (D3DKMTEnumAdapters2(ref e) != 0) return null;
                        var list = new List<uint>();
                        for (int i = 0; i < e.NumAdapters; i++) list.Add((uint)Marshal.ReadInt32(e.pAdapters + i * 20));
                        _adapters = list.ToArray();
                    }
                    finally { Marshal.FreeHGlobal(e.pAdapters); }
                }
                catch { return null; }
            }
            double best = 0;
            IntPtr buf = Marshal.AllocHGlobal(64);
            try
            {
                foreach (uint h in _adapters)
                {
                    for (int k = 0; k < 64; k += 8) Marshal.WriteInt64(buf + k, 0);
                    var q = new QUERYADAPTERINFO { hAdapter = h, Type = 62 /* KMTQAITYPE_ADAPTERPERFDATA */, pPrivateDriverData = buf, PrivateDriverDataSize = 64 };
                    if (D3DKMTQueryAdapterInfo(ref q) != 0) continue;
                    double c = Marshal.ReadInt32(buf + 56) / 10.0;   // deci-Celsius
                    if (c > best && c < 150) best = c;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return best > 0 ? best : null;
        }

        private void CloseAdapters()
        {
            foreach (uint h in _adapters) { var c = new CLOSEADAPTER { hAdapter = h }; try { D3DKMTCloseAdapter(ref c); } catch { } }
            _adapters = Array.Empty<uint>();
            _adaptersTried = false;
        }

        // ---- The processor's heat ---------------------------------------------------------------------

        private DateTime _wmiRetry;
        private string? _wmiSpace;
        private IntPtr _zoneQuery, _zoneCounter;
        private bool _zoneTried;
        private double _zoneMin = double.MaxValue, _zoneMax = double.MinValue;

        private double? CpuTemp()
        {
            // A hardware monitor that's running knows the real one.
            if (_wmiSpace != null || DateTime.UtcNow >= _wmiRetry)
            {
                var fromMonitor = FromMonitorApp();
                if (fromMonitor != null) return fromMonitor;
                if (_wmiSpace == null) _wmiRetry = DateTime.UtcNow.AddSeconds(30);
            }
            // Windows' thermal zone: only when it really moves (on many desktops it's a fixed made-up number).
            var zone = ThermalZone();
            if (zone is double z)
            {
                _zoneMin = Math.Min(_zoneMin, z);
                _zoneMax = Math.Max(_zoneMax, z);
                if (_zoneMax - _zoneMin >= 1.5 && z is > 15 and < 115) return z;
            }
            return null;
        }

        private double? FromMonitorApp()
        {
            foreach (var space in _wmiSpace != null ? new[] { _wmiSpace } : new[] { @"root\LibreHardwareMonitor", @"root\OpenHardwareMonitor" })
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher(space, "SELECT Name, Value, Parent FROM Sensor WHERE SensorType='Temperature'");
                    double? package = null, best = null;
                    foreach (ManagementBaseObject o in searcher.Get())
                    {
                        string parent = o["Parent"] as string ?? "", name = o["Name"] as string ?? "";
                        if (!parent.Contains("cpu", StringComparison.OrdinalIgnoreCase)) continue;
                        double v = Convert.ToDouble(o["Value"], CultureInfo.InvariantCulture);
                        if (v <= 0 || v > 150) continue;
                        if (name.Contains("Package", StringComparison.OrdinalIgnoreCase) || name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)) package = v;
                        best = best is double b ? Math.Max(b, v) : v;
                    }
                    if ((package ?? best) is double t) { _wmiSpace = space; return t; }
                }
                catch { }
            }
            _wmiSpace = null;
            return null;
        }

        private double? ThermalZone()
        {
            if (!_zoneTried)
            {
                _zoneTried = true;
                if (PdhOpenQuery(null, IntPtr.Zero, out _zoneQuery) != 0) { _zoneQuery = IntPtr.Zero; return null; }
                if (PdhAddEnglishCounter(_zoneQuery, @"\Thermal Zone Information(*)\High Precision Temperature", IntPtr.Zero, out _zoneCounter) != 0) return null;
            }
            if (_zoneCounter == IntPtr.Zero || PdhCollectQueryData(_zoneQuery) != 0) return null;
            uint size = 0;
            const uint Fmt = 0x200 /* DOUBLE */ | 0x8000 /* NOCAP100 */;
            if (PdhGetFormattedCounterArray(_zoneCounter, Fmt, ref size, out uint count, IntPtr.Zero) != unchecked((int)0x800007D2) || size == 0) return null;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArray(_zoneCounter, Fmt, ref size, out count, buf) != 0) return null;
                double? best = null;
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = buf + i * 24;
                    if ((uint)Marshal.ReadInt32(item + 8) > 1) continue;
                    double c = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + 16)) / 10.0 - 273.15;   // tenths of a kelvin
                    best = best is double b ? Math.Max(b, c) : c;
                }
                return best;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // ---- Native -----------------------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)] private struct ENUMADAPTERS2 { public uint NumAdapters; public IntPtr pAdapters; }
        [StructLayout(LayoutKind.Sequential)] private struct QUERYADAPTERINFO { public uint hAdapter; public int Type; public IntPtr pPrivateDriverData; public uint PrivateDriverDataSize; }
        [StructLayout(LayoutKind.Sequential)] private struct CLOSEADAPTER { public uint hAdapter; }
        [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(ref ENUMADAPTERS2 e);
        [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref QUERYADAPTERINFO q);
        [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref CLOSEADAPTER c);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
        [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr items);
        [DllImport("pdh.dll")] private static extern int PdhCloseQuery(IntPtr query);
    }
}
