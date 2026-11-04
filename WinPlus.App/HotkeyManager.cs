using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>One custom shortcut.</summary>
    public sealed class HotkeyItem
    {
        public uint Modifiers { get; set; }   // MOD_ALT | MOD_CONTROL | MOD_SHIFT | MOD_WIN
        public uint Key { get; set; }         // virtual-key code
        public string Action { get; set; } = "open";
        public string Target { get; set; } = "";
    }

    /// <summary>
    /// System-wide keyboard shortcuts with RegisterHotKey. Costs nothing while
    /// idle: Windows sends one message when a shortcut is pressed.
    /// </summary>
    internal static class HotkeyManager
    {
        public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8;
        private const uint MOD_NOREPEAT = 0x4000;
        private const int WM_HOTKEY = 0x0312;
        private const string KeyPath = @"Software\WinPlus\Hotkeys";

        /// <summary>(action id, Arabic label) in the order shown in the UI.</summary>
        public static readonly (string Id, string Label)[] Actions =
        {
            ("open", Lang.T("فتح برنامج / ملف / رابط")),
            ("mute", Lang.T("كتم الصوت")),
            ("volume_up", Lang.T("رفع الصوت")),
            ("volume_down", Lang.T("خفض الصوت")),
            ("play_pause", Lang.T("تشغيل / إيقاف الموسيقى")),
            ("next_track", Lang.T("المقطع التالي")),
            ("prev_track", Lang.T("المقطع السابق")),
            ("monitor_off", Lang.T("إطفاء الشاشة")),
            ("lock", Lang.T("قفل الجهاز")),
            ("toggle_scroll", Lang.T("تشغيل / إيقاف سكرول التاسك بار")),
            ("toggle_color", Lang.T("تشغيل / إيقاف لون التاسك بار")),
            ("show_winplus", Lang.T("فتح Wibn")),
        };

        public static event Action? ShowAppRequested;

        private static HwndSource? _window;
        private static readonly List<HotkeyItem> Registered = new();

        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool LockWorkStation();

        // ---- Storage --------------------------------------------------------

        public static List<HotkeyItem> Load()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
                if (key?.GetValue("List") is string json)
                {
                    return JsonSerializer.Deserialize<List<HotkeyItem>>(json) ?? new();
                }
            }
            catch { }
            return new();
        }

        public static void Save(List<HotkeyItem> items)
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue("List", JsonSerializer.Serialize(items));
        }

        // ---- Registration ---------------------------------------------------

        public static void Init(HwndSource window)
        {
            _window = window;
            window.AddHook(WndProc);
            Apply(Load());
        }

        /// <summary>Registers the shortcuts. Returns the ones Windows refused
        /// (usually because another program already uses them).</summary>
        public static List<HotkeyItem> Apply(List<HotkeyItem> items)
        {
            var failed = new List<HotkeyItem>();
            if (_window == null) return failed;

            for (int i = 0; i < Registered.Count; i++) UnregisterHotKey(_window.Handle, i + 1);
            Registered.Clear();

            foreach (var item in items)
            {
                Registered.Add(item);
                if (!RegisterHotKey(_window.Handle, Registered.Count, item.Modifiers | MOD_NOREPEAT, item.Key))
                {
                    failed.Add(item);
                }
            }
            return failed;
        }

        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int index = wParam.ToInt32() - 1;
                if (index >= 0 && index < Registered.Count)
                {
                    handled = true;
                    Run(Registered[index]);
                }
            }
            return IntPtr.Zero;
        }

        // ---- Actions ----------------------------------------------------------

        private static void Tap(byte vk)
        {
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, 2 /* KEYEVENTF_KEYUP */, UIntPtr.Zero);
        }

        public static void Run(HotkeyItem item)
        {
            try
            {
                switch (item.Action)
                {
                    case "open":
                        if (!string.IsNullOrWhiteSpace(item.Target))
                            Process.Start(new ProcessStartInfo(item.Target) { UseShellExecute = true });
                        break;
                    case "mute": Tap(0xAD); break;
                    case "volume_down": Tap(0xAE); break;
                    case "volume_up": Tap(0xAF); break;
                    case "next_track": Tap(0xB0); break;
                    case "prev_track": Tap(0xB1); break;
                    case "play_pause": Tap(0xB3); break;
                    case "monitor_off":
                        // HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, 2 = off
                        PostMessage((IntPtr)0xFFFF, 0x0112, (IntPtr)0xF170, (IntPtr)2);
                        break;
                    case "lock": LockWorkStation(); break;
                    case "toggle_scroll":
                        SettingsStore.Enabled = !SettingsStore.Enabled;
                        TaskbarInjector.SignalRefresh();
                        break;
                    case "toggle_color":
                        SettingsStore.ColorEnabled = !SettingsStore.ColorEnabled;
                        TaskbarInjector.SignalRefresh();
                        break;
                    case "show_winplus": ShowAppRequested?.Invoke(); break;
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write("hotkey", ex);
            }
        }

        // ---- Display ------------------------------------------------------------

        public static string KeyText(uint modifiers, uint vk)
        {
            var parts = new List<string>();
            if ((modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((modifiers & MOD_ALT) != 0) parts.Add("Alt");
            if ((modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
            if ((modifiers & MOD_WIN) != 0) parts.Add("Win");
            parts.Add(KeyName(vk));
            return string.Join(" + ", parts);
        }

        private static string KeyName(uint vk)
        {
            var key = KeyInterop.KeyFromVirtualKey((int)vk);
            string name = key.ToString();
            if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) return name[1].ToString();
            if (name.StartsWith("NumPad")) return "Num " + name.Substring(6);
            return name switch
            {
                "OemPlus" => "+",
                "OemMinus" => "-",
                "OemComma" => ",",
                "OemPeriod" => ".",
                "Oem2" or "OemQuestion" => "/",
                "Oem1" or "OemSemicolon" => ";",
                "Oem3" or "OemTilde" => "`",
                "Oem4" or "OemOpenBrackets" => "[",
                "Oem6" or "OemCloseBrackets" => "]",
                "Oem5" or "OemPipe" => "\\",
                "Oem7" or "OemQuotes" => "'",
                "Return" => "Enter",
                "Next" => "PageDown",
                "Prior" => "PageUp",
                "Capital" => "CapsLock",
                "Snapshot" => "PrintScreen",
                _ => name,
            };
        }

        public static string ActionLabel(string id) =>
            Actions.FirstOrDefault(a => a.Id == id).Label ?? id;
    }
}
