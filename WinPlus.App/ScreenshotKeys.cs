using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace WinPlus
{
    /// <summary>
    /// Notices a screenshot starting (PrintScreen, Win + Shift + S): the live-glass bars are left out of screen
    /// copies (so their glass doesn't copy itself), and for that moment they're put back in, so they're in the
    /// screenshot (see <see cref="LiquidGlass.ShowInScreenshot"/>).
    ///
    /// Through raw input, not a keyboard hook: Windows sends a copy of each key here and never waits for Wibn,
    /// so the keyboard can't be held up by it (a hook made the mouse stutter whenever Wibn was busy).
    /// </summary>
    internal static class ScreenshotKeys
    {
        private static HwndSource? _source;

        /// <summary>Starts listening (once; on the window thread).</summary>
        public static void Start()
        {
            if (_source != null) return;
            try
            {
                _source = new HwndSource(new HwndSourceParameters("Wibn screenshot keys") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */, WindowStyle = 0 });
                _source.AddHook(Hook);
                var dev = new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 6 /* keyboard */, dwFlags = 0x100 /* RIDEV_INPUTSINK: also in the background */, hwndTarget = _source.Handle };
                if (!RegisterRawInputDevices(new[] { dev }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
                    CrashLog.Note("screenshot keys: raw input not available");
            }
            catch (Exception ex) { CrashLog.Write("screenshot keys", ex); }
        }

        private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != 0x00FF /* WM_INPUT */) return IntPtr.Zero;
            try
            {
                uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>(), size = 0;
                GetRawInputData(lParam, 0x10000003 /* RID_INPUT */, IntPtr.Zero, ref size, headerSize);
                if (size == 0 || size > 256) return IntPtr.Zero;
                IntPtr buf = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (GetRawInputData(lParam, 0x10000003, buf, ref size, headerSize) != size) return IntPtr.Zero;
                    var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
                    if (header.dwType != 1 /* RIM_TYPEKEYBOARD */) return IntPtr.Zero;
                    var kb = Marshal.PtrToStructure<RAWKEYBOARD>(buf + (int)headerSize);
                    if ((kb.Flags & 1) != 0) return IntPtr.Zero;   // a key let go
                    bool win = (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;
                    bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
                    if (kb.VKey == 0x2C /* PRINT SCREEN */ || (kb.VKey == 0x53 /* S */ && win && shift))
                        LiquidGlass.ShowInScreenshot();
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch (Exception ex) { CrashLog.Write("screenshot keys", ex); }
            return IntPtr.Zero;   // (not handled: Windows cleans up the raw input itself)
        }

        [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
        [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }
        [StructLayout(LayoutKind.Sequential)] private struct RAWKEYBOARD { public ushort MakeCode, Flags, Reserved, VKey; public uint Message, ExtraInformation; }
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
        [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    }
}
