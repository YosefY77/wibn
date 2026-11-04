using System;
using System.IO;

namespace WinPlus
{
    /// <summary>
    /// Decides which files belong to Windows / the system, so the cleanup views
    /// can show them but never let the user delete them.
    /// </summary>
    internal static class SystemFiles
    {
        private static readonly string[] ProtectedFolders = BuildFolders();

        // Files in the root of a drive that Windows needs.
        private static readonly string[] RootFiles =
        {
            "pagefile.sys", "hiberfil.sys", "swapfile.sys", "dumpstack.log", "dumpstack.log.tmp",
            "bootmgr", "bootnxt", "bootsect.bak", "boot.ini", "ntldr", "ntdetect.com",
        };

        private static string[] BuildFolders()
        {
            string windows = Slash(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            string programFiles = Slash(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            string programFilesX86 = Slash(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            string programData = Slash(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

            return new[]
            {
                windows,
                programFiles + "WindowsApps\\",
                programFiles + "Windows Defender\\",
                programFiles + "Windows Defender Advanced Threat Protection\\",
                programFiles + "Windows NT\\",
                programFiles + "Windows Security\\",
                programFiles + "Common Files\\microsoft shared\\",
                programFilesX86 + "Windows Defender\\",
                programFilesX86 + "Windows NT\\",
                programFilesX86 + "Common Files\\microsoft shared\\",
                programData + "Microsoft\\",
            };
        }

        // Folders that are system ones on every drive.
        private static readonly string[] DriveFolders =
        {
            "Windows.old\\", "$WinREAgent\\", "$Windows.~BT\\", "$Windows.~WS\\", "$Recycle.Bin\\",
            "System Volume Information\\", "Recovery\\", "Config.Msi\\", "Boot\\", "EFI\\",
            "$SysReset\\", "$GetCurrent\\",
        };

        private static string Slash(string path) =>
            string.IsNullOrEmpty(path) ? "\0" : path.TrimEnd('\\') + "\\";

        /// <summary>True for a Windows / system file that must not be deleted.</summary>
        public static bool IsProtected(string path, FileAttributes attributes)
        {
            if ((attributes & FileAttributes.System) != 0) return true;
            return IsProtected(path);
        }

        /// <summary>Path rules only (no disk access), fast enough for every file of a scan.</summary>
        public static bool IsProtected(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;

            foreach (string folder in ProtectedFolders)
            {
                if (path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return true;
            }

            string root = Path.GetPathRoot(path) ?? "";
            if (root.Length > 0 && path.Length > root.Length)
            {
                string rest = path.Substring(root.Length);
                foreach (string folder in DriveFolders)
                {
                    if (rest.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return true;
                }
                if (!rest.Contains('\\'))
                {
                    foreach (string name in RootFiles)
                    {
                        if (rest.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
            }

            // The user's registry hive.
            string fileName = Path.GetFileName(path);
            if (fileName.StartsWith("ntuser.", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("usrclass.", StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        /// <summary>Last check right before deleting: path rules plus the attributes on disk now.</summary>
        public static bool IsProtectedOnDisk(string path)
        {
            if (IsProtected(path)) return true;
            try { return (File.GetAttributes(path) & FileAttributes.System) != 0; }
            catch { return false; }
        }
    }
}
