using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// Game booster mode: while a game runs, Windows' background services that eat the
    /// processor (Windows Update, SysMain, …) are stopped and background apps you picked are
    /// closed; when the game closes, everything comes back by itself.
    ///
    /// A game is: a program from a game library folder (Steam, Epic, Xbox, Riot, GOG, EA,
    /// Ubisoft…), one you added yourself, or anything in exclusive full screen.
    ///
    /// Stopping a service needs admin, so the first time it's turned on Windows asks once, and
    /// two scheduled tasks are made ("Wibn Game Boost On / Off") that run Wibn as admin without
    /// asking again. Only the services Wibn itself stopped are started again.
    /// </summary>
    internal static class GameBoost
    {
        private const string Key = @"Software\WinPlus\GameMode";
        private const string TaskOn = "Wibn Game Boost On", TaskOff = "Wibn Game Boost Off";

        public sealed record Service(string Name, string Label, string Description, bool DefaultOn);
        public sealed record BackgroundApp(string Id, string Label, string Description, string[] Exes, bool Restart, string Args = "");

        public static readonly Service[] Services =
        {
            new("wuauserv", "Windows Update", Lang.T("يحمّل ويثبّت التحديثات في الخلفية، ويسحب من المعالج والنت والقرص"), true),
            new("UsoSvc", Lang.T("منسّق التحديثات"), Lang.T("اللي يبدأ فحص التحديثات وتثبيتها من نفسه"), true),
            new("BITS", Lang.T("النقل الذكي في الخلفية (BITS)"), Lang.T("ينزّل ملفات التحديثات والبرامج في الخلفية"), true),
            new("DoSvc", Lang.T("تحسين التوصيل"), Lang.T("يشارك التحديثات مع أجهزة ثانية ويستهلك النت"), true),
            new("SysMain", "SysMain", Lang.T("يحمّل البرامج في الرام مسبقًا، ويشغّل القرص كثير أثناء اللعب"), true),
            new("DiagTrack", Lang.T("بيانات الاستخدام (Telemetry)"), Lang.T("يجمع بيانات عن استخدامك ويرسلها لمايكروسوفت"), true),
            new("WSearch", Lang.T("فهرسة البحث"), Lang.T("يفهرس ملفاتك للبحث. لو وقف، البحث في ستارت يصير أبطأ أثناء اللعب بس"), false),
        };

        public static readonly BackgroundApp[] Apps =
        {
            new("onedrive", "OneDrive", Lang.T("يوقف المزامنة أثناء اللعب، ويرجع بعدها"), new[] { "OneDrive" }, true, "/background"),
            new("teams", "Microsoft Teams", Lang.T("يتسكّر أثناء اللعب"), new[] { "ms-teams", "Teams" }, false),
            new("phonelink", Lang.T("ربط الجوال (Phone Link)"), Lang.T("يتسكّر، ويرجع لحاله لما تحتاجه"), new[] { "PhoneExperienceHost" }, false),
            new("widgets", Lang.T("ويدجتات ويندوز"), Lang.T("لوحة الأخبار والطقس حق ويندوز، ترجع لحالها لما تفتحها"), new[] { "Widgets", "WidgetService" }, false),
            new("adobe", "Adobe Creative Cloud", Lang.T("برامج أدوبي اللي تشتغل في الخلفية"), new[] { "Creative Cloud", "CCXProcess", "CCLibrary", "AdobeIPCBroker", "Adobe Desktop Service" }, false),
        };

        // ---- Settings -------------------------------------------------------------------------------

        private static int GetInt(string name, int fallback)
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(Key); return k?.GetValue(name) is int v ? v : fallback; }
            catch { return fallback; }
        }

        private static void SetInt(string name, int value)
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            k.SetValue(name, value, RegistryValueKind.DWord);
        }

        private static string GetString(string name)
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(Key); return k?.GetValue(name) as string ?? ""; }
            catch { return ""; }
        }

        private static void SetString(string name, string value)
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            k.SetValue(name, value, RegistryValueKind.String);
        }

        public static bool Enabled { get => GetInt("Enabled", 0) != 0; set { SetInt("Enabled", value ? 1 : 0); Refresh(); } }
        /// <summary>The game's own priority raised to "above normal".</summary>
        public static bool RaisePriority { get => GetInt("Priority", 1) != 0; set => SetInt("Priority", value ? 1 : 0); }
        /// <summary>A small note when it starts and stops.</summary>
        public static bool Notify { get => GetInt("Notify", 1) != 0; set => SetInt("Notify", value ? 1 : 0); }

        public static bool ServiceOn(Service s) => GetInt("svc_" + s.Name, s.DefaultOn ? 1 : 0) != 0;
        public static void SetServiceOn(Service s, bool on) { SetInt("svc_" + s.Name, on ? 1 : 0); WriteServiceList(); }
        public static bool AppOn(BackgroundApp a) => GetInt("app_" + a.Id, 0) != 0;
        public static void SetAppOn(BackgroundApp a, bool on) => SetInt("app_" + a.Id, on ? 1 : 0);

        /// <summary>The games you added yourself (full paths of their .exe).</summary>
        public static List<string> MyGames
        {
            get => GetString("Games").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            set => SetString("Games", string.Join("\n", value));
        }

        private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPlus");
        private static string ServiceListFile => Path.Combine(Folder, "boost-services.txt");
        private static string StoppedFile => Path.Combine(Folder, "boost-stopped.txt");
        private static string ClosedAppsFile => Path.Combine(Folder, "boost-apps.txt");

        /// <summary>The list the admin helper reads when it runs (so changing it never needs admin again).</summary>
        private static void WriteServiceList()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllLines(ServiceListFile, Services.Where(ServiceOn).Select(s => s.Name));
            }
            catch (Exception ex) { CrashLog.Write("boost list", ex); }
        }

        // ---- State --------------------------------------------------------------------------------

        /// <summary>The game it's on for (null = not boosting).</summary>
        public static string? ActiveGame { get; private set; }
        public static bool Manual { get; private set; }

        /// <summary>It started, stopped, or saw a game (for the page).</summary>
        public static event Action? Changed;

        private static CancellationTokenSource? _watch;
        private static int _goneCount;
        private static readonly object Gate = new();

        /// <summary>Watches for games (when the feature and the switch are on); otherwise puts everything back.</summary>
        public static void Refresh()
        {
            bool on = Features.IsOn("game") && Enabled;
            if (on && _watch == null)
            {
                WriteServiceList();
                _watch = new CancellationTokenSource();
                var token = _watch.Token;
                _ = Task.Run(() => WatchLoop(token));
            }
            else if (!on)
            {
                _watch?.Cancel();
                _watch = null;
                if (!Manual) Restore();
            }
        }

        /// <summary>At Wibn's start: if it was left boosted (Wibn was closed or crashed mid-game), put things back.</summary>
        public static void RecoverAtStart()
        {
            try
            {
                if (File.Exists(StoppedFile) || File.Exists(ClosedAppsFile))
                    _ = Task.Run(() => { RunTask(TaskOff); RestartApps(); });
            }
            catch { }
            Refresh();
        }

        /// <summary>Wibn is closing: everything back now.</summary>
        public static void Shutdown()
        {
            _watch?.Cancel();
            _watch = null;
            Restore();
        }

        /// <summary>The "boost now" button: on until it's pressed again (no game needed).</summary>
        public static void ToggleManual()
        {
            if (ActiveGame != null) { Manual = false; Restore(); return; }
            Manual = true;
            Boost(Lang.T("يدوي"), null);
        }

        private static async Task WatchLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var game = FindGame();
                    if (game != null)
                    {
                        _goneCount = 0;
                        if (ActiveGame == null) Boost(game.Value.Name, game.Value.Pid);
                    }
                    else if (ActiveGame != null && !Manual && ++_goneCount >= 2)   // gone for two checks (not just restarting)
                    {
                        Restore();
                    }
                }
                catch (Exception ex) { CrashLog.Write("boost watch", ex); }
                try { await Task.Delay(TimeSpan.FromSeconds(5), token); } catch (TaskCanceledException) { }
            }
        }

        private static void Boost(string game, int? pid)
        {
            lock (Gate)
            {
                if (ActiveGame != null) return;
                ActiveGame = game;
            }
            if (pid is { } id && RaisePriority)
            {
                try { using var p = Process.GetProcessById(id); if (p.PriorityClass == ProcessPriorityClass.Normal) p.PriorityClass = ProcessPriorityClass.AboveNormal; }
                catch { }   // some anti-cheats don't allow it
            }
            CloseApps();
            if (Services.Any(ServiceOn)) RunTask(TaskOn);
            if (Notify) App.Notify(Lang.T("وضع الألعاب شغّال"), Lang.F("{0}: وقفت خدمات وبرامج الخلفية", game));
            Changed?.Invoke();
        }

        private static void Restore()
        {
            string? was;
            lock (Gate)
            {
                was = ActiveGame;
                ActiveGame = null;
                Manual = false;
                _goneCount = 0;
            }
            if (was == null) return;
            RunTask(TaskOff);
            RestartApps();
            if (Notify) App.Notify(Lang.T("وضع الألعاب انتهى"), Lang.T("رجّعت كل الخدمات والبرامج مثل ما كانت"));
            Changed?.Invoke();
        }

        // ---- Background apps (yours, so no admin needed) --------------------------------------------

        private static void CloseApps()
        {
            var restart = new List<string>();
            foreach (var app in Apps.Where(AppOn))
            {
                foreach (var exe in app.Exes)
                {
                    Process[] procs;
                    try { procs = Process.GetProcessesByName(exe); } catch { continue; }
                    foreach (var p in procs)
                    {
                        try
                        {
                            if (p.SessionId != Process.GetCurrentProcess().SessionId) continue;
                            string? path = ImagePath(p.Id);
                            p.Kill(entireProcessTree: true);
                            if (app.Restart && path != null && !restart.Any(r => r.StartsWith(path + "|", StringComparison.OrdinalIgnoreCase)))
                                restart.Add(path + "|" + app.Args);
                        }
                        catch { }
                        finally { p.Dispose(); }
                    }
                }
            }
            try
            {
                Directory.CreateDirectory(Folder);
                if (restart.Count > 0) File.WriteAllLines(ClosedAppsFile, restart);
            }
            catch { }
        }

        private static void RestartApps()
        {
            try
            {
                if (!File.Exists(ClosedAppsFile)) return;
                foreach (var line in File.ReadAllLines(ClosedAppsFile))
                {
                    var parts = line.Split('|');
                    if (parts.Length < 1 || !File.Exists(parts[0])) continue;
                    try { Process.Start(new ProcessStartInfo(parts[0], parts.Length > 1 ? parts[1] : "") { UseShellExecute = true }); } catch { }
                }
                File.Delete(ClosedAppsFile);
            }
            catch (Exception ex) { CrashLog.Write("boost apps back", ex); }
        }

        // ---- Finding a game -------------------------------------------------------------------------

        private static readonly string[] LibraryParts =
        {
            @"\steamapps\common\", @"\Epic Games\", @"\XboxGames\", @"\Riot Games\", @"\GOG Galaxy\Games\", @"\GOG Games\",
            @"\EA Games\", @"\Electronic Arts\", @"\Ubisoft Game Launcher\games\", @"\Rockstar Games\", @"\Battle.net\Games\",
            @"\WindowsApps\Microsoft.MinecraftUWP", @"\Minecraft Launcher\runtime\",
        };

        private static readonly string[] NotGames =
        {
            "launcher", "crash", "unins", "setup", "redist", "vc_redist", "dxsetup", "helper", "easyanticheat", "beservice",
            "battleye", "riot client", "riotclient", "steamworks shared", "\\directx\\", "updater", "reporter", "overlay",
            "epicwebhelper", "socialclub", "cefprocess", "webview", "shadercache",
        };

        private static readonly Dictionary<int, string?> PathCache = new();

        private static (string Name, int Pid)? FindGame()
        {
            var mine = MyGames;
            var seen = new HashSet<int>();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    int pid = p.Id;
                    seen.Add(pid);
                    if (pid <= 4) continue;
                    if (!PathCache.TryGetValue(pid, out var path)) PathCache[pid] = path = ImagePath(pid);
                    if (path == null) continue;
                    if (mine.Any(g => string.Equals(g, path, StringComparison.OrdinalIgnoreCase))) return (NiceName(path), pid);
                    if (LooksLikeGame(path)) return (NiceName(path), pid);
                }
            }
            foreach (var gone in PathCache.Keys.Where(k => !seen.Contains(k)).ToList()) PathCache.Remove(gone);

            // Anything in exclusive full screen (a game started from anywhere).
            if (SHQueryUserNotificationState(out int state) == 0 && state == 3 /* QUNS_RUNNING_D3D_FULL_SCREEN */)
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out uint fg);
                if (fg != 0 && ImagePath((int)fg) is { } fgPath && !IsNotGame(fgPath)) return (NiceName(fgPath), (int)fg);
            }
            return null;
        }

        private static bool LooksLikeGame(string path) =>
            path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && LibraryParts.Any(l => path.Contains(l, StringComparison.OrdinalIgnoreCase))
            && !IsNotGame(path);

        private static bool IsNotGame(string path)
        {
            string lower = path.ToLowerInvariant();
            if (lower.Contains(@"\epic games\launcher\")) return true;
            return NotGames.Any(n => lower.Contains(n));
        }

        private static string NiceName(string path)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                string name = (info.ProductName ?? info.FileDescription ?? "").Trim();
                if (name.Length is > 0 and <= 40) return name;
            }
            catch { }
            return Path.GetFileNameWithoutExtension(path);
        }

        private static string? ImagePath(int pid)
        {
            IntPtr h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        // ---- Admin part: two scheduled tasks ---------------------------------------------------------

        /// <summary>Are the admin tasks there, pointing at this Wibn?</summary>
        public static bool TasksReady =>
            string.Equals(GetString("TaskExe"), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) && Schtasks($"/query /tn \"{TaskOn}\"") == 0;

        /// <summary>Makes the tasks (Windows asks for admin once). False if the user said no.</summary>
        public static bool InstallTasks()
        {
            WriteServiceList();
            try
            {
                using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = true, Verb = "runas",
                    Arguments = $"--game-boost install \"{Folder}\"",
                });
                p?.WaitForExit(60_000);
                if (p == null || p.ExitCode != 0) return false;
                SetString("TaskExe", Environment.ProcessPath!);
                return true;
            }
            catch { return false; }   // "No" on the admin prompt
        }

        /// <summary>Removes the tasks (from the page; asks for admin).</summary>
        public static void UninstallTasks()
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = true, Verb = "runas", Arguments = $"--game-boost uninstall \"{Folder}\"",
                });
                p?.WaitForExit(30_000);
                SetString("TaskExe", "");
            }
            catch { }
        }

        private static void RunTask(string name)
        {
            if (!File.Exists(ServiceListFile)) WriteServiceList();
            if (name == TaskOff && !File.Exists(StoppedFile)) return;   // nothing of ours to start again
            if (Schtasks($"/run /tn \"{name}\"") != 0) CrashLog.Write("boost task", new Exception("couldn't run " + name));
        }

        private static int Schtasks(string args)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                });
                if (p == null) return -1;
                p.StandardOutput.ReadToEnd();
                p.WaitForExit(15_000);
                return p.ExitCode;
            }
            catch { return -1; }
        }

        /// <summary>WinPlus.exe --game-boost install|uninstall|on|off &lt;folder&gt; (runs as admin).</summary>
        public static int RunElevatedHelper(string[] args)
        {
            try
            {
                string mode = args[1], folder = args[2];
                switch (mode)
                {
                    case "install":
                        foreach (var (name, verb) in new[] { (TaskOn, "on"), (TaskOff, "off") })
                        {
                            string xml = TaskXml($"--game-boost {verb} \"{folder}\"");
                            string file = Path.Combine(Path.GetTempPath(), $"wibn-boost-{verb}.xml");
                            File.WriteAllText(file, xml, Encoding.Unicode);
                            int code = Schtasks($"/create /tn \"{name}\" /xml \"{file}\" /f");
                            try { File.Delete(file); } catch { }
                            if (code != 0) return 3;
                        }
                        return 0;
                    case "uninstall":
                        Schtasks($"/delete /tn \"{TaskOn}\" /f");
                        Schtasks($"/delete /tn \"{TaskOff}\" /f");
                        return 0;
                    case "on": return StopServices(folder);
                    case "off": return StartServices(folder);
                }
                return 2;
            }
            catch (Exception ex) { CrashLog.Write("boost helper", ex); return 1; }
        }

        /// <summary>Stops the chosen services that are running, and writes down which (only those come back).</summary>
        private static int StopServices(string folder)
        {
            string list = Path.Combine(folder, "boost-services.txt"), stopped = Path.Combine(folder, "boost-stopped.txt");
            if (!File.Exists(list)) return 0;
            var already = File.Exists(stopped) ? File.ReadAllLines(stopped).ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Only names from our own list (the file is the user's, but never anything else).
            var names = File.ReadAllLines(list).Select(n => n.Trim()).Where(n => Services.Any(s => s.Name.Equals(n, StringComparison.OrdinalIgnoreCase)));
            foreach (var name in names)
            {
                try
                {
                    using var svc = WmiService(name);
                    if (svc == null || (string)svc["State"] != "Running") continue;
                    var result = svc.InvokeMethod("StopService", null);
                    if (Convert.ToInt32(result) == 0 && already.Add(name)) File.AppendAllLines(stopped, new[] { name });
                }
                catch (Exception ex) { CrashLog.Write("boost stop " + name, ex); }
            }
            return 0;
        }

        private static int StartServices(string folder)
        {
            string stopped = Path.Combine(folder, "boost-stopped.txt");
            if (!File.Exists(stopped)) return 0;
            foreach (var name in File.ReadAllLines(stopped).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Services.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                try
                {
                    using var svc = WmiService(name);
                    if (svc != null && (string)svc["State"] != "Running") svc.InvokeMethod("StartService", null);
                }
                catch (Exception ex) { CrashLog.Write("boost start " + name, ex); }
            }
            try { File.Delete(stopped); } catch { }
            return 0;
        }

        private static ManagementObject? WmiService(string name)
        {
            using var search = new ManagementObjectSearcher($"SELECT * FROM Win32_Service WHERE Name = '{name.Replace("'", "")}'");
            return search.Get().OfType<ManagementObject>().FirstOrDefault();
        }

        /// <summary>A task that runs Wibn as admin when asked (no prompt), on battery too, hidden.</summary>
        private static string TaskXml(string arguments)
        {
            string user = WindowsIdentity.GetCurrent().Name;
            static string X(string s) => System.Security.SecurityElement.Escape(s) ?? "";
            return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Wibn: game booster mode (stops / starts background services)</Description></RegistrationInfo>
  <Principals>
    <Principal id=""Author"">
      <UserId>{X(user)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>Queue</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
    <Priority>5</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{X(Environment.ProcessPath!)}</Command>
      <Arguments>{X(arguments)}</Arguments>
    </Exec>
  </Actions>
</Task>";
        }

        // ---- Win32 ------------------------------------------------------------------------------------

        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    }
}
