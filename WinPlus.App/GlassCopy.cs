using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace WinPlus
{
    /// <summary>
    /// The screen behind a live glass (the taskbar's), copied without Wibn's own windows while they still
    /// show in screenshots and screen shares (Discord, OBS…). Windows' magnifier can do that: it leaves
    /// some windows out of its own copy only. It isn't safe next to WPF's drawing in one process (it
    /// crashes now and then), so it runs in a small helper process (WinPlus.exe --glass-copy) with no
    /// windows of its own, and the pictures come back through shared memory.
    /// If the helper can't work, the glass goes back to leaving its window out of every screen copy.
    /// </summary>
    internal static class GlassCopy
    {
        // Shared memory: a header, then the picture.
        //   ints: 0 x, 1 y, 2 w, 3 h, 4 list version, 5 list count, 6 ok; from byte 64: up to 64 windows (8 bytes each)
        private const int HeaderBytes = 1024, MaxWindows = 64;
        private const int MaxPixels = 4 * 1024 * 1024;
        private const long Size = HeaderBytes + MaxPixels * 4L;

        private static readonly object Lock = new();
        private static MemoryMappedFile? _map;
        private static MemoryMappedViewAccessor? _view;
        private static EventWaitHandle? _request, _done;
        private static Process? _helper;
        private static int _starts, _sentVersion = -1;
        private static readonly System.Collections.Generic.List<DateTime> _restarts = new();
        private static volatile bool _failed;

        public static bool Failed => _failed;

        /// <summary>The helper's process (its copying window moves over the bars: that's no change behind them).</summary>
        public static volatile uint HelperPid;

        /// <summary>
        /// The screen at (x, y), w × h pixels, into dst (w wide), leaving out these windows.
        /// True = copied; false = not this time; null = it can't work on this PC.
        /// </summary>
        public static bool? Copy(int x, int y, int w, int h, int[] dst, IntPtr[] exclude, int version)
        {
            if (_failed) return null;
            if (w <= 0 || h <= 0 || (long)w * h > MaxPixels || dst.Length < w * h) return false;
            lock (Lock)
            {
                if (!EnsureHelper()) return _failed ? null : false;
                var v = _view!;
                v.Write(0, x); v.Write(4, y); v.Write(8, w); v.Write(12, h);
                if (version != _sentVersion)
                {
                    int n = Math.Min(exclude.Length, MaxWindows);
                    for (int i = 0; i < n; i++) v.Write(64 + i * 8, exclude[i].ToInt64());
                    v.Write(20, n);
                    v.Write(16, version);
                    _sentVersion = version;
                }
                v.Write(24, 0);
                _done!.Reset();
                _request!.Set();
                if (!_done.WaitOne(500))
                {
                    // Stuck or gone: it starts again next time.
                    if (_helper is { HasExited: true } || !_done.WaitOne(1000)) StopHelper();
                    return false;
                }
                if (v.ReadInt32(24) != 1) return false;
                v.ReadArray(HeaderBytes, dst, 0, w * h);
                return true;
            }
        }

        private static bool EnsureHelper()
        {
            if (_helper is { HasExited: false }) return true;
            if (_helper != null)
            {
                // It died (Windows' magnifier crashes now and then): it starts again, unless that keeps
                // happening (6 times in 10 minutes), then the glass goes back to its other way of copying.
                StopHelper();
                var now = DateTime.UtcNow;
                _restarts.RemoveAll(t => (now - t).TotalMinutes > 10);
                _restarts.Add(now);
                if (_restarts.Count > 5) { _failed = true; CrashLog.Note("glass copy helper: crashed too often, off"); return false; }
            }
            _starts++;
            try
            {
                string? exe = Environment.ProcessPath;
                if (exe == null) { _failed = true; return false; }
                string name = "WibnGlass_" + Environment.ProcessId + "_" + _starts;
                _map = MemoryMappedFile.CreateNew(name, Size);
                _view = _map.CreateViewAccessor();
                _request = new EventWaitHandle(false, EventResetMode.AutoReset, name + "_req");
                _done = new EventWaitHandle(false, EventResetMode.ManualReset, name + "_done");
                _sentVersion = -1;
                _helper = Process.Start(new ProcessStartInfo(exe, $"--glass-copy {Environment.ProcessId} {name}")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                });
                if (_helper == null) { StopHelper(); return false; }
                HelperPid = (uint)_helper.Id;
                return true;
            }
            catch (Exception ex)
            {
                CrashLog.Write("glass copy helper", ex);
                StopHelper();
                return false;
            }
        }

        private static void StopHelper()
        {
            try { if (_helper is { HasExited: false }) _helper.Kill(); } catch { }
            _helper?.Dispose(); _helper = null;
            _view?.Dispose(); _view = null;
            _map?.Dispose(); _map = null;
            _request?.Dispose(); _request = null;
            _done?.Dispose(); _done = null;
        }

        /// <summary>Wibn is closing: the helper goes too (it also goes by itself when Wibn is gone).</summary>
        public static void Shutdown()
        {
            lock (Lock) StopHelper();
        }

        // ---- The helper process ----------------------------------------------------------------------

        /// <summary>WinPlus.exe --glass-copy &lt;wibn's pid&gt; &lt;name&gt;: copies the screen when asked, until Wibn is gone.</summary>
        public static int RunHelper(string[] args)
        {
            // If the magnifier crashes it (it does, now and then, inside Windows), the helper just goes, quietly:
            // no "System Error" box. Wibn starts a new one.
            try { SetErrorMode(0x0001 /* FAILCRITICALERRORS */ | 0x0002 /* NOGPFAULTERRORBOX */ | 0x8000 /* NOOPENFILEERRORBOX */); } catch { }
            try { WerSetFlags(0x20 /* WER_FAULT_REPORTING_NO_UI */); } catch { }
            // Windows still showed "Exception Processing Message 0xc0000005" when the magnifier crashed: now
            // a crash there ends the helper on the spot, before anything can show a box. Wibn starts a new one.
            try { _onCrash = OnCrash; AddVectoredExceptionHandler(1, Marshal.GetFunctionPointerForDelegate(_onCrash)); } catch { }
            try
            {
                Process parent;
                try { parent = Process.GetProcessById(int.Parse(args[1])); }
                catch (ArgumentException) { return 0; }   // Wibn already closed
                using var _parent = parent;
                string name = args[2];
                using var map = MemoryMappedFile.OpenExisting(name);
                using var view = map.CreateViewAccessor();
                using var request = EventWaitHandle.OpenExisting(name + "_req");
                using var done = EventWaitHandle.OpenExisting(name + "_done");
                SetThreadDpiAwarenessContext(new IntPtr(-4));   // real pixels
                // One magnifier for each place it copies (each bar), kept there: one moved from bar to bar had to
                // wait for the screen to be drawn again after each move, so the glass came late.
                var first = MagCopy.TryCreate();
                if (first == null) return 1;
                var pool = new System.Collections.Generic.Dictionary<(int, int, int, int), MagCopy>();
                var applied = new System.Collections.Generic.Dictionary<MagCopy, int>();
                MagCopy? spare = first;
                int listVersion = 0;
                var parentWait = new ManualResetEvent(false) { SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(parent.Handle, false) };
                int version = -1;
                int[] buffer = Array.Empty<int>();
                IntPtr[] list = Array.Empty<IntPtr>();
                var waits = new WaitHandle[] { request, parentWait };
                // It needs very little: what starting up took goes back to Windows, and again now and then.
                Trim();
                var trimmed = Stopwatch.StartNew();
                while (true)
                {
                    int got = WaitHandle.WaitAny(waits, 5000);
                    if (got == 1) return 0;   // Wibn is gone
                    if (trimmed.Elapsed.TotalSeconds > 30) { Trim(); trimmed.Restart(); }
                    if (got == WaitHandle.WaitTimeout) { if (parent.HasExited) return 0; continue; }
                    int x = view.ReadInt32(0), y = view.ReadInt32(4), w = view.ReadInt32(8), h = view.ReadInt32(12);
                    int v = view.ReadInt32(16);
                    if (v != version)
                    {
                        int n = Math.Clamp(view.ReadInt32(20), 0, MaxWindows);
                        list = new IntPtr[n];
                        for (int i = 0; i < n; i++) list[i] = new IntPtr(view.ReadInt64(64 + i * 8));
                        version = v;
                        listVersion++;
                    }
                    // A left-out window that's closed since (a menu, a popup…) makes the magnifier crash:
                    // it's taken off the list first.
                    if (Array.Exists(list, h0 => !IsWindow(h0)))
                    {
                        list = Array.FindAll(list, h0 => IsWindow(h0));
                        listVersion++;
                    }
                    bool ok = false;
                    if (w > 0 && h > 0 && (long)w * h <= MaxPixels)
                    {
                        if (buffer.Length != w * h) buffer = new int[w * h];
                        var key = (x, y, w, h);
                        if (!pool.TryGetValue(key, out var copy))
                        {
                            if (pool.Count >= 12)
                            {
                                // Bars moved around a lot: start over.
                                foreach (var old in pool.Values) old.Dispose();
                                pool.Clear(); applied.Clear();
                            }
                            copy = spare ?? MagCopy.TryCreate();
                            spare = null;
                            if (copy != null) pool[key] = copy;
                        }
                        if (copy != null)
                        {
                            if (!applied.TryGetValue(copy, out int had) || had != listVersion) { copy.Exclude(list); applied[copy] = listVersion; }
                            ok = copy.Copy(x, y, w, h, buffer);
                        }
                        if (ok) view.WriteArray(HeaderBytes, buffer, 0, w * h);
                    }
                    view.Write(24, ok ? 1 : 0);
                    done.Set();
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write("glass copy helper", ex);
                return 1;
            }
        }

        private delegate int CrashHandler(IntPtr info);
        private static CrashHandler? _onCrash;

        private static int OnCrash(IntPtr info)
        {
            // EXCEPTION_POINTERS -> EXCEPTION_RECORD -> code
            IntPtr record = Marshal.ReadIntPtr(info);
            if (record != IntPtr.Zero && unchecked((uint)Marshal.ReadInt32(record)) == 0xC0000005)
                TerminateProcess(GetCurrentProcess(), 3);
            return 0;   // EXCEPTION_CONTINUE_SEARCH
        }

        private static void Trim()
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            try { SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1); } catch { }
        }

        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
        [DllImport("kernel32.dll")] private static extern int WerSetFlags(uint flags);
        [DllImport("kernel32.dll")] private static extern IntPtr AddVectoredExceptionHandler(uint first, IntPtr handler);
        [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

        // ---- The magnifier (in the helper) -------------------------------------------------------------

        private sealed class MagCopy : IDisposable
        {
            private static bool _inited;
            private IntPtr _host, _mag;
            private readonly ScaledCallback _cb;
            private int _mx = int.MinValue, _my, _mw, _mh;
            private int[]? _dst;
            private int _dstW, _dx, _dy;
            private bool _got;

            public static MagCopy? TryCreate()
            {
                var m = new MagCopy();
                if (m._mag != IntPtr.Zero) return m;
                m.Dispose();
                return null;
            }

            private MagCopy()
            {
                _cb = OnScaled;
                if (!_inited) { if (!MagInitialize()) return; _inited = true; }
                IntPtr inst = GetModuleHandle(null);
                _host = CreateWindowEx(0x80000 /* LAYERED */ | 0x20 /* TRANSPARENT */ | 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */,
                                       "Static", "Wibn glass", unchecked((int)0x80000000) /* POPUP */, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
                if (_host == IntPtr.Zero) return;
                SetLayeredWindowAttributes(_host, 0, 0, 2 /* LWA_ALPHA */);   // nothing of it shows
                ShowWindow(_host, 4 /* SW_SHOWNOACTIVATE */);
                _mag = CreateWindowEx(0, "Magnifier", "", 0x40000000 /* CHILD */ | 0x10000000 /* VISIBLE */, 0, 0, 1, 1, _host, IntPtr.Zero, inst, IntPtr.Zero);
                if (_mag == IntPtr.Zero) return;
                if (!MagSetImageScalingCallback(_mag, _cb)) { DestroyWindow(_mag); _mag = IntPtr.Zero; }
            }

            public void Exclude(IntPtr[] windows)
            {
                if (_mag != IntPtr.Zero) MagSetWindowFilterList(_mag, 0 /* MW_FILTERMODE_EXCLUDE */, windows.Length, windows);
            }

            /// <summary>The screen at (x, y), w × h pixels, into dst (w wide); the parts off the screens are left as they were.</summary>
            public bool Copy(int x, int y, int w, int h, int[] dst)
            {
                if (_mag == IntPtr.Zero) return false;
                int vl = GetSystemMetrics(76), vt = GetSystemMetrics(77);
                int vr = vl + GetSystemMetrics(78), vb = vt + GetSystemMetrics(79);
                int l = Math.Max(x, vl), t = Math.Max(y, vt), r = Math.Min(x + w, vr), b = Math.Min(y + h, vb);
                if (r - l < 1 || b - t < 1) return true;   // nothing on a screen: nothing to copy
                int cw = r - l, ch = b - t;
                if (cw != _mw || ch != _mh || l != _mx || t != _my)
                {
                    SetWindowPos(_host, IntPtr.Zero, l, t, cw, ch, 0x0010 /* NOACTIVATE */ | 0x0004 /* NOZORDER */);
                    SetWindowPos(_mag, IntPtr.Zero, 0, 0, cw, ch, 0x0010 | 0x0004);
                    _mx = l; _my = t; _mw = cw; _mh = ch;
                }
                _dst = dst; _dstW = w; _dx = l - x; _dy = t - y; _got = false;
                // The same place as last time gives back the same (old) picture: the magnifier only copies the
                // screen again when its source changes. So a place a pixel off first, then the real one.
                int nudge = l > vl ? -1 : 1;
                MagSetWindowSource(_mag, new RECT { Left = l + nudge, Top = t, Right = r + nudge, Bottom = b });
                if (!MagSetWindowSource(_mag, new RECT { Left = l, Top = t, Right = r, Bottom = b })) return false;
                InvalidateRect(_mag, IntPtr.Zero, false);
                UpdateWindow(_mag);
                while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */)) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
                _dst = null;
                return _got;
            }

            private bool OnScaled(IntPtr hwnd, IntPtr src, MAGIMAGEHEADER sh, IntPtr dest, MAGIMAGEHEADER dh, RECT unclipped, RECT clipped, IntPtr dirty)
            {
                var dst = _dst;
                if (dst == null || src == IntPtr.Zero) return true;
                int rows = Math.Min((int)sh.height, _mh), cols = Math.Min((int)sh.width, _mw);
                for (int row = 0; row < rows; row++)
                {
                    int at = (row + _dy) * _dstW + _dx;
                    if (at < 0 || at + cols > dst.Length) break;
                    Marshal.Copy(src + (int)sh.offset + row * (int)sh.stride, dst, at, cols);
                }
                _got = true;
                return true;
            }

            public void Dispose()
            {
                if (_mag != IntPtr.Zero) { DestroyWindow(_mag); _mag = IntPtr.Zero; }
                if (_host != IntPtr.Zero) { DestroyWindow(_host); _host = IntPtr.Zero; }
                GC.KeepAlive(_cb);
            }

            [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
            [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
            [StructLayout(LayoutKind.Sequential)]
            private struct MAGIMAGEHEADER { public uint width, height; public Guid format; public uint stride, offset; public UIntPtr cbSize; }
            [StructLayout(LayoutKind.Sequential)]
            private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
            private delegate bool ScaledCallback(IntPtr hwnd, IntPtr src, MAGIMAGEHEADER srcHeader, IntPtr dest, MAGIMAGEHEADER destHeader, RECT unclipped, RECT clipped, IntPtr dirty);

            [DllImport("Magnification.dll")] private static extern bool MagInitialize();
            [DllImport("Magnification.dll")] private static extern bool MagSetWindowSource(IntPtr hwnd, RECT rect);
            [DllImport("Magnification.dll")] private static extern bool MagSetWindowFilterList(IntPtr hwnd, int mode, int count, IntPtr[] list);
            [DllImport("Magnification.dll")] private static extern bool MagSetImageScalingCallback(IntPtr hwnd, ScaledCallback cb);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
            [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
            [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
            [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
            [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
            [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
            [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr hwnd);
            [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
            [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
            [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
            [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
        }
    }
}
