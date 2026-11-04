using System;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// Explorer integration (current user only, no admin needed):
    ///   * right-click any file → "قفل بكلمة سر"  (WinPlus.exe --lock "file")
    ///   * double-click a .wplock file → asks for the password (WinPlus.exe --unlock "file")
    /// On Windows 11 the right-click item is under "Show more options".
    /// </summary>
    internal static class FileLockShell
    {
        private const string VerbKey = @"Software\Classes\*\shell\WinPlusLock";
        private const string DirVerbKey = @"Software\Classes\Directory\shell\WinPlusLock";
        private const string ExtKey = @"Software\Classes\" + FileLocker.Extension;
        private const string ProgId = "WinPlus.LockedFile";
        private const string ProgKey = @"Software\Classes\" + ProgId;
        private const string OurKey = @"Software\WinPlus\TaskbarScroll";

        public static bool Enabled
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(OurKey);
                return key?.GetValue("FileLockShell") is not int v || v != 0;  // on by default
            }
            set
            {
                using (var key = Registry.CurrentUser.CreateSubKey(OurKey))
                    key.SetValue("FileLockShell", value ? 1 : 0, RegistryValueKind.DWord);
                if (value) Register(); else Unregister();
            }
        }

        /// <summary>(Re)writes the keys so they point at the current exe.</summary>
        public static void Register()
        {
            string? exe = Environment.ProcessPath;
            if (exe == null) return;
            try
            {
                using (var verb = Registry.CurrentUser.CreateSubKey(VerbKey))
                {
                    verb.SetValue("", Lang.T("قفل بكلمة سر (Wibn)"));
                    verb.SetValue("Icon", "%SystemRoot%\\System32\\shell32.dll,47", RegistryValueKind.ExpandString);
                    verb.SetValue("AppliesTo", "NOT System.FileExtension:=" + FileLocker.Extension);
                    using var cmd = verb.CreateSubKey("command");
                    cmd.SetValue("", $"\"{exe}\" --lock \"%1\"");
                }
                using (var verb = Registry.CurrentUser.CreateSubKey(DirVerbKey))
                {
                    verb.SetValue("", Lang.T("قفل المجلد بكلمة سر (Wibn)"));
                    verb.SetValue("Icon", "%SystemRoot%\\System32\\shell32.dll,47", RegistryValueKind.ExpandString);
                    using var cmd = verb.CreateSubKey("command");
                    cmd.SetValue("", $"\"{exe}\" --lock \"%1\"");
                }
                using (var ext = Registry.CurrentUser.CreateSubKey(ExtKey))
                {
                    ext.SetValue("", ProgId);
                }
                using (var prog = Registry.CurrentUser.CreateSubKey(ProgKey))
                {
                    prog.SetValue("", Lang.T("ملف مقفول (Wibn)"));
                    using (var icon = prog.CreateSubKey("DefaultIcon"))
                        icon.SetValue("", "%SystemRoot%\\System32\\shell32.dll,47", RegistryValueKind.ExpandString);
                    using var open = prog.CreateSubKey(@"shell\open");
                    open.SetValue("", Lang.T("فتح بكلمة السر"));
                    using var cmd = open.CreateSubKey("command");
                    cmd.SetValue("", $"\"{exe}\" --unlock \"%1\"");
                }
                NotifyShell();
            }
            catch { }
        }

        public static void Unregister()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(VerbKey, false);
                Registry.CurrentUser.DeleteSubKeyTree(DirVerbKey, false);
                Registry.CurrentUser.DeleteSubKeyTree(ExtKey, false);
                Registry.CurrentUser.DeleteSubKeyTree(ProgKey, false);
                NotifyShell();
            }
            catch { }
        }

        // Tells Explorer file associations changed (refreshes icons).
        private static void NotifyShell() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
    }
}
