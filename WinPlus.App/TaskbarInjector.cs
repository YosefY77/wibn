using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// Loads TaskbarTap.dll into explorer.exe (taskbar + tray overflow) and into
    /// StartMenuExperienceHost.exe (Start menu) through the official XAML
    /// diagnostics API.
    ///
    /// The DLL is copied to %LOCALAPPDATA%\WinPlus\bin under a name that
    /// contains its hash, so the build folder is never locked and each build
    /// has its own name. Whether a process already has the DLL (and which
    /// build) is read from its loaded modules.
    ///
    /// Explorer is never restarted automatically: if an older build is inside,
    /// the user gets a button for it.
    /// </summary>
    internal static class TaskbarInjector
    {
        // Must match CLSID_WinPlusTap in TaskbarTap/common.h
        private static readonly Guid TapClsid = new("ceae01ac-5556-49a0-bf40-57309780a14b");

        // Old builds listened to this event; new ones watch the registry.
        private const string RefreshEventName = @"Local\WinPlus.TaskbarScroll.Refresh";

        // "ALL APPLICATION PACKAGES": lets the Start menu (a packaged app) read
        // the DLL, the settings and write the log.
        private static readonly SecurityIdentifier AllAppPackages = new("S-1-15-2-1");

        private static readonly SemaphoreSlim ExplorerGate = new(1, 1);
        private static readonly SemaphoreSlim StartGate = new(1, 1);
        private static bool _startRestartedOnce;
        private static uint _startFailedPid;  // don't keep retrying a process that refused

        [DllImport("Windows.UI.Xaml.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int InitializeXamlDiagnosticsEx(
            string endPointName, uint pid, string? wszDllXamlDiagnostics,
            string wszTAPDllName, Guid tapClsid, string? wszInitializationData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        /// <summary>
        /// The DLL to load: next to WinPlus.exe when built normally; in the single-file
        /// version (just WinPlus.exe, e.g. sent to a friend) it's inside the exe and is
        /// written out to %LOCALAPPDATA%\WinPlus\bin once.
        /// </summary>
        public static string SourceDllPath => _sourceDll ??= ResolveSourceDll();
        private static string? _sourceDll;

        private static string ResolveSourceDll()
        {
            string beside = Path.Combine(AppContext.BaseDirectory, "TaskbarTap.dll");
            if (File.Exists(beside)) return beside;
            try
            {
                using var res = typeof(TaskbarInjector).Assembly.GetManifestResourceStream("WinPlus.TaskbarTap.dll");
                if (res == null) return beside;
                using var ms = new MemoryStream();
                res.CopyTo(ms);
                byte[] bytes = ms.ToArray();
                Directory.CreateDirectory(BinDir);
                // Not "TaskbarTap-*.dll": those are the loaded copies that get cleaned up.
                string path = Path.Combine(BinDir, "source-TaskbarTap.dll");
                if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length ||
                    !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                {
                    File.WriteAllBytes(path, bytes);
                }
                return path;
            }
            catch
            {
                return beside;
            }
        }

        private static string DataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPlus");

        private static string BinDir => Path.Combine(DataDir, "bin");

        /// <summary>True when an older build is inside explorer and it must restart.</summary>
        public static bool NeedsExplorerRestart { get; private set; }

        // ---- Helpers -------------------------------------------------------

        private static void AppLog(string text)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.AppendAllText(Path.Combine(DataDir, "taskbar.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} [app] {text}\r\n");
            }
            catch { }
        }

        /// <summary>FNV-1a 64 of a file, used to name each build.</summary>
        private static string Fnv1a64(string path)
        {
            ulong hash = 14695981039346656037UL;
            foreach (byte b in File.ReadAllBytes(path))
            {
                hash ^= b;
                hash = unchecked(hash * 1099511628211UL);
            }
            return hash.ToString("x16");
        }

        public static bool IsTaskbarHorizontal()
        {
            IntPtr hwnd = TrayHost.ExplorerTray();   // explorer's, not Wibn's window of that name
            if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var r))
            {
                return (r.Right - r.Left) >= (r.Bottom - r.Top);
            }
            return true;
        }

        /// <summary>Settings are picked up from the registry by the DLL; this
        /// only wakes up DLLs from older builds.</summary>
        public static void SignalRefresh()
        {
            if (EventWaitHandle.TryOpenExisting(RefreshEventName, out var ev))
            {
                ev.Set();
                ev.Dispose();
            }
        }

        private static uint ExplorerPid()
        {
            IntPtr hwnd = TrayHost.ExplorerTray();   // explorer's, not Wibn's window of that name
            return hwnd != IntPtr.Zero && GetWindowThreadProcessId(hwnd, out uint pid) != 0 ? pid : 0;
        }

        private static uint StartMenuPid()
        {
            var processes = Process.GetProcessesByName("StartMenuExperienceHost");
            uint pid = processes.Length > 0 ? (uint)processes[0].Id : 0;
            foreach (var p in processes) p.Dispose();
            return pid;
        }

        /// <summary>File name of our DLL inside a process, or null.</summary>
        private static string? LoadedTap(uint pid)
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                foreach (ProcessModule m in p.Modules)
                {
                    if (m.ModuleName.StartsWith("TaskbarTap", StringComparison.OrdinalIgnoreCase))
                    {
                        return m.ModuleName;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog($"Can't list modules of {pid}: {ex.Message}");
            }
            return null;
        }

        /// <summary>Logs which XAML framework a process uses (to understand failures).</summary>
        private static void LogXamlModules(uint pid)
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                var names = new System.Collections.Generic.List<string>();
                foreach (ProcessModule m in p.Modules)
                {
                    if (m.ModuleName.Contains("Xaml", StringComparison.OrdinalIgnoreCase))
                        names.Add(m.ModuleName);
                }
                AppLog($"Process {pid} ({p.ProcessName}) XAML modules: {string.Join(", ", names)}");
            }
            catch (Exception ex)
            {
                AppLog($"Can't list modules of {pid}: {ex.Message}");
            }
        }

        /// <summary>Gives the Start menu's sandbox access to our folder and settings.</summary>
        private static void GrantSandboxAccess(string dllPath)
        {
            try
            {
                var dir = new DirectoryInfo(DataDir);
                var dirSec = dir.GetAccessControl();
                dirSec.AddAccessRule(new FileSystemAccessRule(AllAppPackages, FileSystemRights.Modify,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
                dir.SetAccessControl(dirSec);

                var file = new FileInfo(dllPath);
                var fileSec = file.GetAccessControl();
                fileSec.AddAccessRule(new FileSystemAccessRule(AllAppPackages,
                    FileSystemRights.ReadAndExecute, AccessControlType.Allow));
                file.SetAccessControl(fileSec);
            }
            catch (Exception ex)
            {
                AppLog($"Folder access for the Start menu failed: {ex.Message}");
            }

            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\WinPlus", true);
                var regSec = key.GetAccessControl();
                regSec.AddAccessRule(new RegistryAccessRule(AllAppPackages,
                    RegistryRights.ReadKey | RegistryRights.Notify,
                    InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
                key.SetAccessControl(regSec);
            }
            catch (Exception ex)
            {
                AppLog($"Settings access for the Start menu failed: {ex.Message}");
            }
        }

        /// <summary>Copies the DLL to its hash-named location. Returns (path, file name).</summary>
        private static (string path, string name) PrepareDll()
        {
            string name = $"TaskbarTap-{Fnv1a64(SourceDllPath)}.dll";
            Directory.CreateDirectory(BinDir);
            string target = Path.Combine(BinDir, name);
            if (!File.Exists(target))
            {
                File.Copy(SourceDllPath, target, true);
            }
            GrantSandboxAccess(target);

            // Remove older builds that nothing uses any more.
            foreach (string old in Directory.GetFiles(BinDir, "TaskbarTap-*.dll"))
            {
                if (!string.Equals(old, target, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(old); } catch { /* still loaded somewhere */ }
                }
            }
            return (target, name);
        }

        /// <summary>Child-process entry: does the actual InitializeXamlDiagnosticsEx call.</summary>
        public static int RunInjectHelper(string[] args)
        {
            try
            {
                uint pid = uint.Parse(args[1]);
                string dll = args[2];
                int endpoint = int.Parse(args[3]);
                return InitializeXamlDiagnosticsEx($"VisualDiagConnection{endpoint}", pid, null, dll, TapClsid, null);
            }
            catch (Exception ex)
            {
                CrashLog.Write("inject helper", ex);
                return unchecked((int)0x80004005);  // E_FAIL
            }
        }

        /// <summary>Loads the DLL into a process (through a helper child process);
        /// waits until it shows up in its modules.</summary>
        private static int Inject(uint pid, string dllPath, string dllName, int endpointBase)
        {
            int hr = 0;
            string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "WinPlus.exe");
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    using var helper = Process.Start(new ProcessStartInfo(exe,
                        $"--inject {pid} \"{dllPath}\" {endpointBase + attempt}")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    if (helper == null) return unchecked((int)0x80004005);
                    if (!helper.WaitForExit(15000))
                    {
                        try { helper.Kill(); } catch { }
                        AppLog($"Loading into {pid} timed out");
                        return unchecked((int)0x800705B4);  // ERROR_TIMEOUT
                    }
                    hr = helper.ExitCode;
                }
                catch (Exception ex)
                {
                    AppLog($"Helper failed: {ex.Message}");
                    return unchecked((int)0x80004005);
                }

                if (hr == 0)
                {
                    for (int i = 0; i < 50 && LoadedTap(pid) == null; i++) Thread.Sleep(100);
                    AppLog($"Loaded {dllName} into {pid}");
                    return 0;
                }
                AppLog($"Loading into {pid} attempt {attempt}: 0x{hr:X8}");
                Thread.Sleep(1000);
            }
            return hr;
        }

        // ---- Explorer (taskbar) -------------------------------------------

        /// <summary>Restarts only the explorer process that owns the taskbar.
        /// Called only from the user's button.</summary>
        public static Task RestartExplorerAsync() => Task.Run(() =>
        {
            uint pid = ExplorerPid();
            if (pid != 0)
            {
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    p.Kill();
                    p.WaitForExit(5000);
                }
                catch { }
            }

            for (int i = 0; i < 25 && ExplorerPid() == 0; i++) Thread.Sleep(200);
            if (ExplorerPid() == 0)
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            }
            for (int i = 0; i < 75 && ExplorerPid() == 0; i++) Thread.Sleep(200);
            Thread.Sleep(2500);

            // The Start menu may hold an old build too.
            KillStartMenuIfOutdated(force: true);
        });

        /// <summary>Loads the DLL into explorer if needed. Returns a status message (Arabic).</summary>
        public static async Task<string> EnsureLoadedAsync()
        {
            await ExplorerGate.WaitAsync();
            try
            {
                string status = await Task.Run(EnsureExplorer);
                _ = EnsureStartMenuAsync();
                return status;
            }
            finally
            {
                ExplorerGate.Release();
            }
        }

        private static string EnsureExplorer()
        {
            NeedsExplorerRestart = false;
            if (!File.Exists(SourceDllPath)) return Lang.T("ما لقيت TaskbarTap.dll جنب البرنامج");

            string dllPath, dllName;
            try { (dllPath, dllName) = PrepareDll(); }
            catch (Exception ex) { return Lang.T("ما قدرت أجهز الـDLL: ") + ex.Message; }

            uint pid = 0;
            for (int i = 0; i < 10 && (pid = ExplorerPid()) == 0; i++) Thread.Sleep(1000);
            if (pid == 0) return Lang.T("ما لقيت التاسك بار");

            string? loaded = LoadedTap(pid);
            if (loaded != null)
            {
                if (string.Equals(loaded, dllName, StringComparison.OrdinalIgnoreCase))
                {
                    return Lang.T("متصل بالتاسك بار ✓");
                }
                // Never load a second copy, never restart explorer by ourselves.
                AppLog($"Explorer has {loaded}, expected {dllName}; waiting for the user");
                SignalRefresh();
                NeedsExplorerRestart = true;
                return Lang.T("فيه نسخة أقدم شغالة. اضغط \"تحديث التاسك بار\" عشان تشتغل الجديدة.");
            }

            int hr = Inject(pid, dllPath, dllName, 0);
            return hr == 0 ? Lang.T("متصل بالتاسك بار ✓") : Lang.F("فشل الاتصال بالتاسك بار (0x{0:X8})", hr);
        }

        // ---- Start menu ----------------------------------------------------

        private static void KillStartMenuIfOutdated(bool force)
        {
            uint pid = StartMenuPid();
            if (pid == 0) return;
            string? loaded = LoadedTap(pid);
            if (loaded == null) return;

            string expected;
            try { expected = $"TaskbarTap-{Fnv1a64(SourceDllPath)}.dll"; }
            catch { return; }
            if (string.Equals(loaded, expected, StringComparison.OrdinalIgnoreCase)) return;

            // Windows starts the Start menu again by itself. Only once per run,
            // unless the user asked for it.
            if (!force && _startRestartedOnce) return;
            _startRestartedOnce = true;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                p.Kill();
                p.WaitForExit(5000);
                AppLog("Restarted the Start menu to load the new build");
            }
            catch { }
        }

        /// <summary>Loads the DLL into the Start menu when the custom color is on.</summary>
        public static async Task EnsureStartMenuAsync()
        {
            if (!SettingsStore.ColorEnabled || !SettingsStore.AppActive) return;
            if (!await StartGate.WaitAsync(0)) return;  // one at a time, never queue up
            try
            {
                await Task.Run(() =>
                {
                    KillStartMenuIfOutdated(force: false);

                    uint pid = 0;
                    for (int i = 0; i < 10 && (pid = StartMenuPid()) == 0; i++) Thread.Sleep(500);
                    if (pid == 0 || pid == _startFailedPid || LoadedTap(pid) != null) return;

                    var (dllPath, dllName) = PrepareDll();
                    if (Inject(pid, dllPath, dllName, 20) != 0)
                    {
                        _startFailedPid = pid;
                        LogXamlModules(pid);
                    }
                });
            }
            catch (Exception ex)
            {
                AppLog($"Start menu: {ex.Message}");
            }
            finally
            {
                StartGate.Release();
            }
        }
    }
}
