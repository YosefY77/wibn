using System;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// Settings live in the registry so the DLL inside explorer can read them.
    /// </summary>
    internal static class SettingsStore
    {
        private const string KeyPath = @"Software\WinPlus\TaskbarScroll";
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "WinPlus";

        private static int Get(string name, int fallback)
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(name) is int v ? v : fallback;
        }

        private static void Set(string name, int value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(name, value, RegistryValueKind.DWord);
        }

        /// <summary>The scroll, icon size, floating and "fit to the apps" options were removed:
        /// keep them off for people who had them on. The list engine only runs for magnify.</summary>
        public static void RetireRemovedTaskbarOptions()
        {
            if (Get("Floating", 0) != 0) Floating = false;
            if (Get("FitApps", 0) != 0) FitApps = false;
            if (Get("RunningFirst", 0) != 0) RunningFirst = false;
            if (Get("IconSize", 0) != 0) IconSize = 0;
            if (Enabled != Magnify) Enabled = Magnify;
        }

        public static bool Enabled
        {
            get => Get("Enabled", 1) != 0;
            set => Set("Enabled", value ? 1 : 0);
        }

        public static bool AppActive
        {
            get => Get("AppActive", 1) != 0;
            set => Set("AppActive", value ? 1 : 0);
        }

        public static int ScrollStep
        {
            get => Math.Clamp(Get("ScrollStep", 1), 1, 10);
            set => Set("ScrollStep", Math.Clamp(value, 1, 10));
        }

        public static bool Reverse
        {
            get => Get("Reverse", 0) != 0;
            set => Set("Reverse", value ? 1 : 0);
        }

        public static bool Smooth
        {
            get => Get("Smooth", 1) != 0;
            set => Set("Smooth", value ? 1 : 0);
        }

        public static bool ColorEnabled
        {
            get => Get("ColorEnabled", 0) != 0;
            set => Set("ColorEnabled", value ? 1 : 0);
        }

        /// <summary>Taskbar color as 0xAARRGGBB.</summary>
        public static uint Color
        {
            get => unchecked((uint)Get("Color", unchecked((int)0xFF202020)));
            set => Set("Color", unchecked((int)value));
        }

        public static bool ShowYear
        {
            get => Get("ShowYear", 0) != 0;
            set => Set("ShowYear", value ? 1 : 0);
        }

        /// <summary>0 = solid color, 1 = glass (blur), 2 = see-through.</summary>
        public static int TaskbarStyle { get => Get("TaskbarStyle", 0); set => Set("TaskbarStyle", value); }
        /// <summary>0-100: how much color over the glass / see-through.</summary>
        public static int TaskbarOpacity { get => Get("TaskbarOpacity", 60); set => Set("TaskbarOpacity", value); }
        public static bool Floating { get => Get("Floating", 0) != 0; set => Set("Floating", value ? 1 : 0); }
        /// <summary>The taskbar's background only as wide as its apps (the tray gets its own piece).</summary>
        public static bool FitApps { get => Get("FitApps", 0) != 0; set => Set("FitApps", value ? 1 : 0); }
        public static bool Magnify { get => Get("Magnify", 0) != 0; set => Set("Magnify", value ? 1 : 0); }
        public static bool IndicatorColor { get => Get("IndicatorColor", 0) != 0; set => Set("IndicatorColor", value ? 1 : 0); }

        /// <summary>Running apps shown before pinned-only ones on the taskbar.</summary>
        public static bool RunningFirst
        {
            get => Get("RunningFirst", 0) != 0;
            set => Set("RunningFirst", value ? 1 : 0);
        }

        /// <summary>Taskbar icon size in px; 0 = Windows default.</summary>
        public static int IconSize
        {
            get => Get("IconSize", 0);
            set => Set("IconSize", value);
        }

        /// <summary>
        /// Windows' own "show smaller taskbar buttons" (makes the taskbar
        /// thinner): 0 = always, 1 = never, 2 = when the taskbar is full.
        /// </summary>
        public static int SmallButtons
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(AdvancedPath);
                return key?.GetValue("IconSizePreference") is int v ? v : 2;
            }
            set
            {
                using (var key = Registry.CurrentUser.CreateSubKey(AdvancedPath))
                {
                    key.SetValue("IconSizePreference", value, RegistryValueKind.DWord);
                }
                BroadcastTraySettings();
            }
        }

        private const string AdvancedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
                                                        uint flags, uint timeout, out IntPtr result);

        /// <summary>Tells explorer that taskbar settings changed (like the Settings app does).</summary>
        private static void BroadcastTraySettings()
        {
            // HWND_BROADCAST, WM_SETTINGCHANGE, SMTO_ABORTIFHUNG
            SendMessageTimeout((IntPtr)0xFFFF, 0x001A, IntPtr.Zero, "TraySettings", 0x0002, 1000, out _);
        }

        private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        /// <summary>
        /// Start with Windows. On by default. Besides the Run entry, Windows keeps a
        /// separate on/off switch (Task Manager / Settings → Startup apps) in
        /// StartupApproved; if that one is off, the app silently doesn't start, so
        /// both are checked and fixed.
        /// </summary>
        public static bool StartWithWindows
        {
            get => GetInt("StartWithWindows", 1) != 0;
            set
            {
                SetInt("StartWithWindows", value ? 1 : 0);
                EnsureStartup();
            }
        }

        /// <summary>Makes the startup entry match the setting and point at this exe.
        /// Called on every start, so moving the app folder doesn't break it.</summary>
        public static void EnsureStartup()
        {
            try
            {
                using var run = Registry.CurrentUser.CreateSubKey(RunKeyPath);
                using var approved = Registry.CurrentUser.CreateSubKey(ApprovedPath);
                if (GetInt("StartWithWindows", 1) != 0)
                {
                    string exe = Environment.ProcessPath ?? "";
                    string command = $"\"{exe}\" --tray";
                    if (run.GetValue(RunValueName) as string != command) run.SetValue(RunValueName, command);
                    // First byte 2 = enabled, 3 = disabled by the user in Task Manager.
                    if (approved.GetValue(RunValueName) is byte[] state && state.Length > 0 && state[0] != 2)
                    {
                        approved.SetValue(RunValueName, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
                                          RegistryValueKind.Binary);
                    }
                }
                else
                {
                    run.DeleteValue(RunValueName, false);
                    approved.DeleteValue(RunValueName, false);
                }
            }
            catch { }
        }

        private static int GetInt(string name, int fallback)
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(name) is int v ? v : fallback;
        }

        private static void SetInt(string name, int value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(name, value, RegistryValueKind.DWord);
        }

        /// <summary>
        /// Windows 11 "Taskbar alignment": 0 = left, 1 (or missing) = center.
        /// </summary>
        public static bool IsTaskbarCentered
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
                return !(key?.GetValue("TaskbarAl") is int v && v == 0);
            }
        }

        public static void AlignTaskbarLeft()
        {
            using var key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            key.SetValue("TaskbarAl", 0, RegistryValueKind.DWord);
        }
    }
}
