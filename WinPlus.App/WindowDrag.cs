using System;
using System.Runtime.InteropServices;

namespace WinPlus
{
    /// <summary>
    /// Is a window being moved or resized right now (dragged by its title bar or edge)? Windows says so
    /// itself (move/size start and end). Meanwhile Wibn does less: the taskbar's live glass copies the
    /// screen less often and the widgets don't check every step of the move, so the window being
    /// dragged stays smooth.
    /// </summary>
    internal static class WindowDrag
    {
        private const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A, EVENT_SYSTEM_MOVESIZEEND = 0x000B;

        private static WinEventProc? _proc;   // kept alive while hooked
        private static IntPtr _hook;
        private static volatile bool _active;

        /// <summary>A window is being dragged now (any thread).</summary>
        public static bool Active => _active;

        /// <summary>The drag ended (on the UI thread).</summary>
        public static event Action? Ended;

        /// <summary>Call once, on the UI thread (the events come to its message loop).</summary>
        public static void Start()
        {
            if (_hook != IntPtr.Zero) return;
            _proc = OnEvent;
            _hook = SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero, _proc, 0, 0, 0 /* OUTOFCONTEXT */);
        }

        public static void Stop()
        {
            if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
            _active = false;
        }

        private static void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (ev == EVENT_SYSTEM_MOVESIZESTART) _active = true;
            else if (ev == EVENT_SYSTEM_MOVESIZEEND)
            {
                _active = false;
                try { Ended?.Invoke(); } catch (Exception ex) { CrashLog.Write("window drag", ex); }
            }
        }

        private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    }
}
