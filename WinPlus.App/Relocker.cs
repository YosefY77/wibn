using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace WinPlus
{
    /// <summary>
    /// After a locked file / folder is opened: watches it and locks it again with the
    /// same password once you're done with it.
    ///
    /// "In use" means either:
    ///   * a program has the file open (Windows' Restart Manager tells us who), or
    ///   * a window's title shows its name (photo viewers, players and editors that
    ///     don't keep the file open still put its name in the title; for a folder,
    ///     the Explorer window showing it).
    /// Once it has been in use and then stops being in use for a few seconds, it's
    /// locked again. A tray icon shows it's waiting and can lock it right away.
    /// The password is only kept in memory while waiting.
    /// </summary>
    internal sealed class Relocker
    {
        private const string OurKey = @"Software\WinPlus\TaskbarScroll";
        private static readonly List<Relocker> _active = new();

        /// <summary>Lock again after closing (on by default).</summary>
        public static bool Enabled
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(OurKey);
                return key?.GetValue("Relock") is not int v || v != 0;
            }
            set
            {
                using var key = Registry.CurrentUser.CreateSubKey(OurKey);
                key.SetValue("Relock", value ? 1 : 0, RegistryValueKind.DWord);
            }
        }

        public static bool AnyActive => _active.Count > 0;

        /// <summary>The app is closing: lock everything that's still open right away.</summary>
        public static void LockAllNow()
        {
            foreach (var r in _active.ToList())
            {
                try { FileLocker.Lock(r._path, r._password); } catch { }
                r.Finish();
            }
        }

        /// <summary>true when running as the small helper started by a double-click:
        /// the process exits once nothing is left to watch.</summary>
        public static bool ExitWhenDone;

        private readonly string _path;
        private readonly bool _folder;
        private string _password;
        private readonly string _name;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
        private readonly Forms.NotifyIcon _tray;
        private bool _seenInUse;
        private int _freeTicks;
        private bool _locking;
        private readonly DateTime _started = DateTime.Now;

        public static void Start(string path, string password)
        {
            var r = new Relocker(path, password);
            _active.Add(r);
        }

        private Relocker(string path, string password)
        {
            _path = path;
            _password = password;
            _folder = Directory.Exists(path);
            _name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));

            var menu = new Forms.ContextMenuStrip { RightToLeft = Lang.English ? Forms.RightToLeft.No : Forms.RightToLeft.Yes };
            menu.Items.Add(Lang.T("اقفله الحين"), null, (_, _) => _ = RelockAsync());
            menu.Items.Add(Lang.T("خله مفتوح (لا تقفله)"), null, (_, _) => Finish());
            _tray = new Forms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Shield,
                Text = Trim(Lang.F("Wibn: {0} يتقفل لما تسكّره", _name)),
                Visible = true,
                ContextMenuStrip = menu,
            };
            _tray.DoubleClick += (_, _) => _ = RelockAsync();

            _timer.Tick += (_, _) => Check();
            _timer.Start();

            // Lock it again if Windows is shutting down / signing out meanwhile.
            if (Application.Current != null)
                Application.Current.SessionEnding += OnSessionEnding;
        }

        private static string Trim(string s) => s.Length <= 63 ? s : s[..60] + "…";

        private void Check()
        {
            if (_locking) return;
            if (!File.Exists(_path) && !Directory.Exists(_path)) { Finish(); return; }  // deleted / renamed

            bool inUse = IsInUse();
            if (inUse)
            {
                _seenInUse = true;
                _freeTicks = 0;
                return;
            }
            if (!_seenInUse)
            {
                // Nothing showed it was open (some apps show no name and keep no lock).
                // Tell once that the tray icon can lock it.
                if ((DateTime.Now - _started).TotalSeconds > 20 && _freeTicks++ == 0)
                {
                    _tray.ShowBalloonTip(5000, "Wibn",
                        Lang.F("ما قدرت أعرف متى تسكّر \"{0}\". لما تخلص اضغط دبل كليك على أيقونة الدرع جنب الساعة عشان يتقفل.", _name),
                        Forms.ToolTipIcon.Info);
                }
                return;
            }
            // Free for 3 checks in a row (~6 s): done with it.
            if (++_freeTicks >= 3) _ = RelockAsync();
        }

        private async Task RelockAsync()
        {
            if (_locking) return;
            _locking = true;
            _timer.Stop();
            try
            {
                string path = _path, pass = _password;
                await Task.Run(() => FileLocker.Lock(path, pass));
                Finish();
            }
            catch (FileLocker.OriginalNotDeletedException)
            {
                Finish();  // the locked copy exists; the leftover is reported nowhere else, keep quiet
            }
            catch (Exception ex)
            {
                // Still open somewhere: try again shortly.
                CrashLog.Note($"relock of {_name} failed, retrying: {ex.Message}");
                _locking = false;
                _freeTicks = 0;
                _timer.Start();
            }
        }

        private void OnSessionEnding(object? sender, SessionEndingCancelEventArgs e)
        {
            if (_locking) return;
            try { FileLocker.Lock(_path, _password); } catch { }
            Finish();
        }

        private void Finish()
        {
            _timer.Stop();
            _password = "";
            _tray.Visible = false;
            _tray.Dispose();
            if (Application.Current != null) Application.Current.SessionEnding -= OnSessionEnding;
            _active.Remove(this);
            if (ExitWhenDone && _active.Count == 0) Application.Current?.Shutdown();
        }

        // ---- "Is it still open?" ---------------------------------------------------------

        private bool IsInUse()
        {
            try
            {
                if (WindowShowsName()) return true;
                var files = _folder
                    ? Directory.EnumerateFiles(_path, "*", SearchOption.AllDirectories).Take(300).ToArray()
                    : new[] { _path };
                return files.Length > 0 && FileHasUsers(files);
            }
            catch { return true; }  // can't tell: don't lock under someone's feet
        }

        private bool WindowShowsName()
        {
            // Match the name without extension too ("report - Word").
            string full = _name;
            string bare = _folder ? _name : Path.GetFileNameWithoutExtension(_name);
            if (bare.Length < 3) bare = full;
            uint self = (uint)Environment.ProcessId;
            bool found = false;
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == self) return true;
                int len = GetWindowTextLength(hwnd);
                if (len == 0) return true;
                var sb = new StringBuilder(len + 1);
                GetWindowText(hwnd, sb, sb.Capacity);
                string title = sb.ToString();
                if (title.Contains(full, StringComparison.OrdinalIgnoreCase) ||
                    title.Contains(bare, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>Asks Windows' Restart Manager whether any program has these files open.</summary>
        private static bool FileHasUsers(string[] files)
        {
            if (RmStartSession(out uint session, 0, Guid.NewGuid().ToString("N")) != 0) return false;
            try
            {
                if (RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null) != 0) return false;
                uint needed = 0, count = 0, reasons = 0;
                int r = RmGetList(session, out needed, ref count, null, ref reasons);
                return (r == 0 || r == 234 /* ERROR_MORE_DATA */) && needed > 0;
            }
            finally
            {
                RmEndSession(session);
            }
        }

        // ---- Win32 ------------------------------------------------------------------------

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RM_UNIQUE_PROCESS { public int dwProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
            public int ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint session, int flags, string key);
        [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint session);
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint session, uint nFiles, string[] files, uint nApps,
                                                      RM_UNIQUE_PROCESS[]? apps, uint nServices, string[]? services);
        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(uint session, out uint needed, ref uint count,
                                            [In, Out] RM_PROCESS_INFO[]? info, ref uint rebootReasons);
    }
}
