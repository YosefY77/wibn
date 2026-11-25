using System;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;

namespace WinPlus
{
    /// <summary>
    /// Gives memory back to Windows after a burst of glass restarts.
    ///
    /// Each glass has big pixel buffers. They go on .NET's "large object heap", which is only
    /// cleaned in a full collection, and Wibn allocates little otherwise, so those rarely happen:
    /// after widgets hide and show again, or the taskbar's pieces change size, the old buffers
    /// stayed in memory. A few seconds after the last glass stopped (so never in the middle of
    /// something), one full, compacting collection frees them and returns the memory. At most
    /// once every 20 seconds.
    /// </summary>
    internal static class MemoryTrim
    {
        private static readonly Timer _timer = new(_ => Run(), null, Timeout.Infinite, Timeout.Infinite);
        private static long _lastRunTicks;

        /// <summary>Big buffers were let go: trim a few seconds from now (each call pushes it back).</summary>
        public static void Soon()
        {
            try { _timer.Change(TimeSpan.FromSeconds(4), Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
        }

        // ---- Idle: memory nothing is using goes back to Windows ----------------------------------------
        //
        // Most of Wibn's memory is drawing (each see-through window's pictures) and pieces loaded once at
        // start-up that are rarely touched again. Every few minutes, while the mouse and keyboard have
        // been still for a bit (so never in the middle of a click or an animation), the memory not in use
        // goes back to Windows; whatever is needed again comes back by itself.

        private static readonly Timer _idle = new(_ => IdleTick(), null, Timeout.Infinite, Timeout.Infinite);
        private static long _lastIdleTrim;
        private const int IdleEveryMs = 3 * 60_000, StillForMs = 10_000;

        /// <summary>Starts the idle trimming (once, at start-up).</summary>
        public static void StartIdle()
        {
            Interlocked.Exchange(ref _lastIdleTrim, Environment.TickCount64 - IdleEveryMs + 60_000);   // the first one a minute after start-up
            try { _idle.Change(15_000, 15_000); } catch (ObjectDisposedException) { }
        }

        private static void IdleTick()
        {
            try
            {
                long now = Environment.TickCount64;
                if (now - Interlocked.Read(ref _lastIdleTrim) < IdleEveryMs) return;
                var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
                if (!GetLastInputInfo(ref li)) return;
                if ((uint)Environment.TickCount - li.dwTime < StillForMs) return;   // in use right now: later
                Interlocked.Exchange(ref _lastIdleTrim, now);
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: true);
                SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize, dwTime; }
        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

        private static void Run()
        {
            long now = Environment.TickCount64;
            long wait = 20_000 - (now - Interlocked.Read(ref _lastRunTicks));
            if (wait > 0) { _timer.Change(TimeSpan.FromMilliseconds(wait), Timeout.InfiniteTimeSpan); return; }
            Interlocked.Exchange(ref _lastRunTicks, now);
            try
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            }
            catch { }
        }
    }
}
