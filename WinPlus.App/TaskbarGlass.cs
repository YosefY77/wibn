using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace WinPlus
{
    /// <summary>
    /// "Glass" taskbar: blurs whatever is behind the taskbar window (the same
    /// SetWindowCompositionAttribute call TranslucentTB uses). The taskbar's own
    /// background is made see-through by the DLL, so the blur shows with our tint.
    /// Explorer puts its own setting back now and then (start menu, focus changes),
    /// so it's applied again twice a second while the look is on.
    /// </summary>
    internal static class TaskbarGlass
    {
        private static DispatcherTimer? _timer;
        private static bool _applied;

        /// <summary>Call at start and whenever the look changes.</summary>
        public static void Update()
        {
            bool on = SettingsStore.TaskbarStyle == 1 && SettingsStore.AppActive;
            if (on)
            {
                if (_timer == null)
                {
                    _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                    _timer.Tick += (_, _) => Apply(true);
                    _timer.Start();
                }
                Apply(true);
            }
            else
            {
                _timer?.Stop();
                _timer = null;
                if (_applied) Apply(false);  // give Windows its own look back
            }
        }

        /// <summary>Gives the taskbar Windows' own look back (glass was turned off).</summary>
        public static void Reset() => Apply(false);

        private static void Apply(bool blur)
        {
            ForEachTaskbar(hwnd => SetAccent(hwnd, blur ? 3 /* ACCENT_ENABLE_BLURBEHIND */ : 0 /* ACCENT_DISABLED */));
            _applied = blur;
        }

        private static void ForEachTaskbar(Action<IntPtr> action)
        {
            IntPtr primary = FindWindow("Shell_TrayWnd", null);
            if (primary != IntPtr.Zero) action(primary);
            IntPtr hwnd = IntPtr.Zero;
            while ((hwnd = FindWindowEx(IntPtr.Zero, hwnd, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                action(hwnd);
        }

        private static void SetAccent(IntPtr hwnd, int state)
        {
            var accent = new ACCENT_POLICY { AccentState = state, AccentFlags = 2, GradientColor = 0x01000000 };
            int size = Marshal.SizeOf<ACCENT_POLICY>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new WINDOWCOMPOSITIONATTRIBDATA { Attribute = 19 /* WCA_ACCENT_POLICY */, Data = ptr, SizeOfData = size };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ACCENT_POLICY
        {
            public int AccentState;
            public int AccentFlags;
            public uint GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWCOMPOSITIONATTRIBDATA
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
    }
}
