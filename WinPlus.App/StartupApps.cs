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
            public string ApprovedKey = "";   // "Run", "Run32", "StartupFolder" or "Packaged"
            public bool Enabled;
            // Store / packaged apps (ChatGPT, Teams, Terminal...): their own switch.
            public string PackageFamily = "";
            public string TaskId = "";
            public string? LogoPath;
            public bool Locked;               // set by policy, can't be changed here
        }

        public static List<Entry> Load()
        {
            var list = new List<Entry>();
            ReadRun(list, Registry.CurrentUser, RunPath, "Run", false);
            ReadRun(list, Registry.LocalMachine, RunPath, "Run", true);
            ReadRun(list, Registry.LocalMachine, Run32Path, "Run32", true);
            ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), false);
            ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), true);
            int classic = list.Count;
            ReadPackaged(list);

            for (int i = 0; i < classic; i++)
            {
                list[i].Enabled = IsEnabled(list[i]);
                Describe(list[i]);
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

        // ---- Packaged (Store / MSIX) apps ---------------------------------------------
        // They don't use the Run key: each one declares a "StartupTask" in its manifest,
        // and the on/off state lives under SystemAppData\<family>\<task id>\State
        // (0/1 = off, 2 = on, 3/4 = off/on by policy). Same list Task Manager shows.

        private const string PackagedBase =
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\";

        private sealed class PackagedTask
        {
            public string Family = "", TaskId = "", Title = "", Publisher = "", Exe = "", Logo = "";
            public bool DefaultOn;
        }

        private static List<PackagedTask>? _packaged;
        private static int _packagedStamp = -1;

        private static void ReadPackaged(List<Entry> list)
        {
            List<PackagedTask> tasks;
            try { tasks = PackagedTasks(); }
            catch { return; }
            foreach (var t in tasks)
            {
                int? state = PackagedState(t.Family, t.TaskId);
                int st = state ?? (t.DefaultOn ? 2 : 0);
                list.Add(new Entry
                {
                    Name = t.Family + "!" + t.TaskId, Title = t.Title, Publisher = t.Publisher,
                    Command = t.Exe, ExePath = t.Exe, ApprovedKey = "Packaged",
                    PackageFamily = t.Family, TaskId = t.TaskId, LogoPath = t.Logo,
                    Enabled = st is 2 or 4, Locked = st is 3 or 4,
                });
            }
        }

        /// <summary>Scanning every package's manifest takes a moment, so it's cached until
        /// the set of apps changes (or a manual refresh).</summary>
        private static List<PackagedTask> PackagedTasks()
        {
            int stamp = PackagedStamp();
            if (_packaged != null && stamp == _packagedStamp) return _packaged;

            var result = new List<PackagedTask>();
            var pm = new Windows.Management.Deployment.PackageManager();
            foreach (var pkg in pm.FindPackagesForUser(""))
            {
                try
                {
                    if (pkg.IsFramework || pkg.IsResourcePackage) continue;
                    string dir = pkg.InstalledLocation?.Path ?? "";
                    string manifest = Path.Combine(dir, "AppxManifest.xml");
                    if (dir.Length == 0 || !File.Exists(manifest)) continue;
                    string text = File.ReadAllText(manifest);
                    if (!text.Contains("StartupTask", StringComparison.Ordinal)) continue;

                    var doc = System.Xml.Linq.XDocument.Parse(text);
                    foreach (var el in doc.Descendants().Where(x => x.Name.LocalName == "StartupTask"))
                    {
                        string id = (string?)el.Attribute("TaskId") ?? "";
                        if (id.Length == 0) continue;
                        // The Executable is on the <Extension> around it.
                        string exe = (string?)el.Parent?.Attribute("Executable")
                                     ?? (string?)el.Attribute("Executable") ?? "";
                        string title = "";
                        try { title = pkg.DisplayName; } catch { }
                        string? shown = (string?)el.Attribute("DisplayName");
                        if (title.Length == 0 && shown != null && !shown.StartsWith("ms-resource:")) title = shown;
                        if (title.Length == 0) title = pkg.Id.Name;
                        string publisher = "";
                        try { publisher = pkg.PublisherDisplayName; } catch { }
                        string logo = "";
                        try { logo = pkg.Logo?.LocalPath ?? ""; } catch { }
                        result.Add(new PackagedTask
                        {
                            Family = pkg.Id.FamilyName, TaskId = id, Title = title, Publisher = publisher,
                            Exe = exe.Length > 0 ? Path.Combine(dir, exe) : dir, Logo = logo,
                            DefaultOn = string.Equals((string?)el.Attribute("Enabled"), "true", StringComparison.OrdinalIgnoreCase),
                        });
                    }
                }
                catch { }
            }
            _packaged = result;
            _packagedStamp = stamp;
            return result;
        }

        /// <summary>Changes when an app is installed or removed (new SystemAppData folders).</summary>
        private static int PackagedStamp()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PackagedBase);
                return key?.SubKeyCount ?? 0;
            }
            catch { return 0; }
        }

        private static int? PackagedState(string family, string taskId)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PackagedBase + family + "\\" + taskId);
                return key?.GetValue("State") is int v ? v : null;
            }
            catch { return null; }
        }

        /// <summary>Forget the cached package list (manual refresh).</summary>
        public static void ForgetPackages() => _packaged = null;

        /// <summary>A cheap fingerprint of everything the list shows, so the page can
        /// refresh itself the moment something changes (here, in Task Manager, or an app).</summary>
        public static string Signature()
        {
            var sb = new System.Text.StringBuilder();
            void Values(RegistryKey hive, string path)
            {
                try
                {
                    using var key = hive.OpenSubKey(path);
                    if (key != null)
                        foreach (var n in key.GetValueNames())
                        {
                            sb.Append(n).Append('=');
                            var v = key.GetValue(n);
                            if (v is byte[] b) sb.Append(b.Length > 0 ? b[0] : -1);
                            else sb.Append(v);
                            sb.Append(';');
                        }
                }
                catch { }
                sb.Append('|');
            }
            Values(Registry.CurrentUser, RunPath);
            Values(Registry.LocalMachine, RunPath);
            Values(Registry.LocalMachine, Run32Path);
            foreach (var a in new[] { "Run", "Run32", "StartupFolder" })
            {
                Values(Registry.CurrentUser, ApprovedBase + a);
                Values(Registry.LocalMachine, ApprovedBase + a);
            }
            foreach (var f in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                                      Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) })
            {
                try { if (Directory.Exists(f)) foreach (var x in Directory.EnumerateFiles(f)) sb.Append(x).Append(';'); }
                catch { }
                sb.Append('|');
            }
            sb.Append(PackagedStamp()).Append('|');
            var known = _packaged;
            if (known != null)
                foreach (var t in known) sb.Append(PackagedState(t.Family, t.TaskId)).Append(';');
            return sb.ToString();
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
            if (e.ApprovedKey == "Packaged")
            {
                if (e.Locked) return false;
                try
                {
                    using var key = Registry.CurrentUser.CreateSubKey(PackagedBase + e.PackageFamily + "\\" + e.TaskId);
                    key.SetValue("State", enabled ? 2 : 1, RegistryValueKind.DWord);
                    e.Enabled = enabled;
                    return true;
                }
                catch { return false; }
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
