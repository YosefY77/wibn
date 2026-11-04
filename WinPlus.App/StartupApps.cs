using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// Programs that start with Windows, and their on/off switch.
    ///
    /// Read from the same places Task Manager's "Startup apps" uses:
    ///   * HKCU / HKLM ...\CurrentVersion\Run (and the 32-bit HKLM one)
    ///   * the user's and everyone's Startup folders
    /// Turning one off doesn't delete anything: like Task Manager, we write the
    /// "StartupApproved" switch (first byte 2 = on, 3 = off), so it can be turned
    /// back on any time and Task Manager shows the same state. Entries for all
    /// users live in HKLM, which needs admin rights (Windows asks once).
    /// </summary>
    public static class StartupApps
    {
        private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string Run32Path = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
        private const string ApprovedBase = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

        public sealed class Entry
        {
            public string Name = "";          // value name / file name (the key in StartupApproved)
            public string Title = "";         // shown name
            public string Command = "";       // what runs
            public string? ExePath;           // for icon / publisher
            public string Publisher = "";
            public bool AllUsers;             // HKLM → needs admin to change
            public string ApprovedKey = "";   // "Run", "Run32" or "StartupFolder"
            public bool Enabled;
        }

        public static List<Entry> Load()
        {
            var list = new List<Entry>();
            ReadRun(list, Registry.CurrentUser, RunPath, "Run", false);
            ReadRun(list, Registry.LocalMachine, RunPath, "Run", true);
            ReadRun(list, Registry.LocalMachine, Run32Path, "Run32", true);
            ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), false);
            ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), true);

            foreach (var e in list)
            {
                e.Enabled = IsEnabled(e);
                Describe(e);
            }
            return list.OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static void ReadRun(List<Entry> list, RegistryKey hive, string path, string approved, bool allUsers)
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key == null) return;
                foreach (var name in key.GetValueNames())
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    string cmd = key.GetValue(name) as string ?? "";
                    if (cmd.Length == 0) continue;
                    list.Add(new Entry
                    {
                        Name = name, Title = name, Command = cmd, ExePath = ExeFromCommand(cmd),
                        AllUsers = allUsers, ApprovedKey = approved,
                    });
                }
            }
            catch { }
        }

        private static void ReadFolder(List<Entry> list, string folder, bool allUsers)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    string name = Path.GetFileName(file);
                    if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(new Entry
                    {
                        Name = name, Title = Path.GetFileNameWithoutExtension(file), Command = file,
                        ExePath = file, AllUsers = allUsers, ApprovedKey = "StartupFolder",
                    });
                }
            }
            catch { }
        }

        /// <summary>The program's own description / company, when the file can be read.</summary>
        private static void Describe(Entry e)
        {
            if (e.ExePath == null || !File.Exists(e.ExePath) ||
                !e.ExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                var info = FileVersionInfo.GetVersionInfo(e.ExePath);
                if (!string.IsNullOrWhiteSpace(info.FileDescription)) e.Title = info.FileDescription.Trim();
                if (!string.IsNullOrWhiteSpace(info.CompanyName)) e.Publisher = info.CompanyName.Trim();
            }
            catch { }
        }

        /// <summary>"C:\x\app.exe" --flag → C:\x\app.exe</summary>
        public static string? ExeFromCommand(string cmd)
        {
            cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
            string path;
            if (cmd.StartsWith('"'))
            {
                int end = cmd.IndexOf('"', 1);
                path = end > 1 ? cmd[1..end] : cmd.Trim('"');
            }
            else
            {
                int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                path = exe > 0 ? cmd[..(exe + 4)] : cmd.Split(' ')[0];
            }
            if (!Path.IsPathRooted(path))
            {
                // e.g. "rundll32.exe ..." → System32
                string sys = Path.Combine(Environment.SystemDirectory, path);
                if (File.Exists(sys)) return sys;
            }
            return path;
        }

        // ---- On / off -----------------------------------------------------------------

        public static bool IsWinPlus(Entry e) =>
            !e.AllUsers && e.ApprovedKey == "Run" && e.Name == "WinPlus";

        private static bool IsEnabled(Entry e)
        {
            try
            {
                using var key = (e.AllUsers ? Registry.LocalMachine : Registry.CurrentUser)
                    .OpenSubKey(ApprovedBase + e.ApprovedKey);
                // No entry = on. Odd first byte (3, 7…) = turned off.
                return key?.GetValue(e.Name) is not byte[] state || state.Length == 0 || (state[0] & 1) == 0;
            }
            catch { return true; }
        }

        /// <summary>Turns an entry on or off. Returns false if it needs admin and the user said no.</summary>
        public static bool SetEnabled(Entry e, bool enabled)
        {
            // WinPlus itself: use its own setting (it re-checks this on every start).
            if (IsWinPlus(e))
            {
                SettingsStore.StartWithWindows = enabled;
                e.Enabled = enabled;
                return true;
            }
            if (!e.AllUsers)
            {
                Write(Registry.CurrentUser, e.ApprovedKey, e.Name, enabled);
                e.Enabled = enabled;
                return true;
            }

            // All-users entries: a short elevated copy of WinPlus writes the switch.
            try
            {
                var psi = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = "--startup-approve " + e.ApprovedKey + " \"" + e.Name.Replace("\"", "\\\"") + "\" " + (enabled ? "1" : "0"),
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(30_000);
                if (p == null || p.ExitCode != 0) return false;
                e.Enabled = enabled;
                return true;
            }
            catch
            {
                return false;  // UAC cancelled
            }
        }

        /// <summary>Runs inside the elevated helper (WinPlus.exe --startup-approve ...).</summary>
        public static int RunElevatedHelper(string[] args)
        {
            try
            {
                string approved = args[1];
                if (approved is not ("Run" or "Run32" or "StartupFolder")) return 2;
                Write(Registry.LocalMachine, approved, args[2], args[3] == "1");
                return 0;
            }
            catch
            {
                return 1;
            }
        }

        private static void Write(RegistryKey hive, string approved, string name, bool enabled)
        {
            using var key = hive.CreateSubKey(ApprovedBase + approved);
            byte[] state = new byte[12];
            state[0] = enabled ? (byte)2 : (byte)3;
            if (!enabled)
            {
                // When it was turned off (Task Manager shows this).
                BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(state, 4);
            }
            key.SetValue(name, state, RegistryValueKind.Binary);
        }
    }
}
