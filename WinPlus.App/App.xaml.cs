using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace WinPlus
{
    public partial class App : Application
    {
        private Mutex? _singleInstance;
        private EventWaitHandle? _showEvent;
        private const string ShowEventName = "WinPlus.App.ShowWindow";
        private Forms.NotifyIcon? _tray;
        private MainWindow? _window;
        private HwndSource? _messageWindow;
        private int _taskbarCreatedMsg;
        private readonly System.Collections.Generic.List<DateTime> _taskbarRestarts = new();
        private bool _safeMode;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string lpString);

        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Lang.Init(this);  // Arabic or English, before any window

            // Helper mode: WinPlus.exe --inject <pid> <dll> <endpoint>
            // Loading into another process runs in a throwaway child process,
            // so if Windows' loader crashes, the app itself keeps running.
            if (e.Args.Length == 4 && e.Args[0] == "--inject")
            {
                Shutdown(TaskbarInjector.RunInjectHelper(e.Args));
                return;
            }

            // Guard of Wibn's taskbar: brings Windows' taskbar back if Wibn dies or freezes.
            if (e.Args.Length == 2 && e.Args[0] == "--taskbar-guard")
            {
                Shutdown(CustomTaskbar.RunGuard(e.Args));
                return;
            }

            // Elevated helper: turn an all-users startup program on/off, then exit.
            if (e.Args.Length == 4 && e.Args[0] == "--startup-approve")
            {
                Shutdown(StartupApps.RunElevatedHelper(e.Args));
                return;
            }

            // Elevated helper of game booster mode: WinPlus.exe --game-boost install|uninstall|on|off <folder>
            if (e.Args.Length == 3 && e.Args[0] == "--game-boost")
            {
                Shutdown(GameBoost.RunElevatedHelper(e.Args));
                return;
            }

            // Right-click on a folder → "sort files": sort, say what happened, exit.
            if (e.Args.Length == 2 && e.Args[0] == "--sort")
            {
                string text;
                try
                {
                    var counts = DownloadsOrganizer.SortNow(e.Args[1]);
                    int total = counts.Values.Sum();
                    text = total == 0
                        ? Lang.T("ما فيه ملفات تحتاج ترتيب في هالمجلد.")
                        : Lang.F("انتقل {0} ملف:\n", total) + string.Join("\n", counts.Select(kv => $"{kv.Key}: {kv.Value}")) +
                          Lang.T("\n\nتقدر تتراجع من Wibn ← ترتيب التحميلات ← تراجع.");
                }
                catch (Exception ex)
                {
                    text = Lang.T("ما قدرت أرتب المجلد: ") + ex.Message;
                }
                MessageBox.Show(text, "Wibn", MessageBoxButton.OK, MessageBoxImage.Information,
                                MessageBoxResult.OK, Lang.MsgOptions);
                Shutdown();
                return;
            }

            // Right-click "lock" / double-click on a locked file: just the password
            // window, then exit (works whether or not the main app is running).
            if (e.Args.Length == 2 && (e.Args[0] == "--lock" || e.Args[0] == "--unlock"))
            {
                try { LockDialog.Run(e.Args[1], null, openAfterUnlock: e.Args[0] == "--unlock"); }
                catch (Exception ex) { CrashLog.Write("Lock", ex); }
                // Opened: stay (tiny, no window) until it's closed and locked again.
                if (Relocker.AnyActive) { Relocker.ExitWhenDone = true; return; }
                Shutdown();
                return;
            }

            // Write any crash to the log instead of dying silently.
            DispatcherUnhandledException += (_, ex) =>
            {
                CrashLog.Write("UI", ex.Exception);
                ex.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
                CrashLog.Write("Domain", ex.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, ex) =>
            {
                CrashLog.Write("Task", ex.Exception);
                ex.SetObserved();
            };

            _singleInstance = new Mutex(true, "WinPlus.App.SingleInstance", out bool first);
            if (!first)
            {
                // Already running (often hidden next to the clock): opened by hand → show that one.
                if (!e.Args.Contains("--tray"))
                {
                    try { if (EventWaitHandle.TryOpenExisting(ShowEventName, out var show)) using (show) show.Set(); }
                    catch (Exception ex) { CrashLog.Write("show running", ex); }
                }
                Shutdown();
                return;
            }
            // Another copy opened by hand asks this one to show its window.
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            ThreadPool.RegisterWaitForSingleObject(_showEvent,
                (_, _) => Dispatcher.BeginInvoke(new Action(ShowWindow)), null, Timeout.Infinite, executeOnlyOnce: false);

            CrashLog.Note($"started ({(e.Args.Contains("--tray") ? "with Windows" : "by hand")})");
            SettingsStore.AppActive = Features.IsOn("taskbar");
            SettingsStore.EnsureStartup();

            _window = new MainWindow();
            _window.Hidden += (_, _) => TrimMemory();

            var menu = new Forms.ContextMenuStrip { RightToLeft = Lang.English ? Forms.RightToLeft.No : Forms.RightToLeft.Yes };
            menu.Items.Add(Lang.T("فتح الإعدادات"), null, (_, _) => ShowWindow());
            menu.Items.Add(Lang.T("خروج"), null, (_, _) => ExitApp());

            _tray = new Forms.NotifyIcon
            {
                Icon = LoadTrayIcon(),
                Text = "Wibn",
                Visible = true,
                ContextMenuStrip = menu,
            };
            _tray.DoubleClick += (_, _) => ShowWindow();

            CreateMessageWindow();
            TaskbarPlacement.ApplyAutoHideMode();
            ApplyFeatures(starting: true);  // only what the user chose runs
            _ = Task.Run(ConvertHistory.Cleanup);  // drop originals older than 30 days
            _ = Task.Run(FileUndo.FinishAll);      // undo steps left from last time can't be undone now

            // The Start menu process can start later or restart on its own;
            // check now and then whether it needs the DLL (only when the
            // custom color is on).
            var startMenuTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(15),
            };
            startMenuTimer.Tick += (_, _) =>
            {
                if (!_safeMode && Features.IsOn("taskbar")) _ = TaskbarInjector.EnsureStartMenuAsync();
            };
            startMenuTimer.Start();

            if (!e.Args.Contains("--tray"))
            {
                ShowWindow();
            }

            if (Features.IsOn("taskbar")) _ = ConnectAsync();

            // Extras of the chosen features (FFmpeg, sendme) download in the background.
            _ = Task.Delay(TimeSpan.FromSeconds(15)).ContinueWith(_ => Features.DownloadMissingAsync());

            // New version? Check a little after start, then every 12 hours.
            var updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            updateTimer.Tick += async (_, _) =>
            {
                updateTimer.Interval = TimeSpan.FromHours(12);
                var release = await UpdateChecker.CheckAsync();
                if (release == null) return;
                _window?.ShowUpdate(release);
                _tray?.ShowBalloonTip(8000, Lang.T("Wibn: نسخة جديدة ") + release.Tag,
                    Lang.T("افتح Wibn عشان تحدّث أو تتجاهلها."), Forms.ToolTipIcon.Info);
            };
            updateTimer.Start();
            if (_tray != null) _tray.BalloonTipClicked += (_, _) => ShowWindow();
        }

        /// <summary>Starts / stops the background parts to match the chosen features.</summary>
        public void ApplyFeatures(bool starting = false)
        {
            bool taskbar = Features.IsOn("taskbar");
            if (!starting)
            {
                SettingsStore.AppActive = taskbar;
                TaskbarInjector.SignalRefresh();
                if (taskbar) _ = ConnectAsync();
            }
            TaskbarPlacement.ApplyAutoHideMode();
            CustomTaskbar.Apply();   // Wibn's own taskbar, if chosen
            VideoDownloader.Refresh();
            ScreenCapture.Refresh();  // its shortcuts, and the "last minute" recording if on
            // Glass taskbar (TaskbarStyle 1) is drawn by the DLL inside the taskbar now.
            if (SettingsStore.TaskbarStyle > 1) SettingsStore.TaskbarStyle = 0;   // the old "see-through" look is gone

            if (Features.IsOn("lock") && FileLockShell.Enabled) FileLockShell.Register();  // keeps it pointing at this exe
            else FileLockShell.Unregister();

            if (Features.IsOn("downloads"))
            {
                DownloadsOrganizer.Start();  // only if automatic sorting is on
                DownloadsOrganizer.RegisterShell();
            }
            else
            {
                DownloadsOrganizer.Stop();
                DownloadsOrganizer.UnregisterShell();
            }

            if (Features.IsOn("widgets")) Widgets.Start(); else Widgets.Stop();
            Appointments.StartReminders();   // the calendar's notifications (once)
            if (Features.IsOn("phone")) PhoneClipboard.Start(); else PhoneClipboard.Stop();
            if (starting) GameBoost.RecoverAtStart(); else GameBoost.Refresh();   // game booster: watches for games

            DragSpaceHook.Refresh();  // Space / R / C while dragging a file → convert / resize / compress
        }

        /// <summary>The installer of a new version was started: get out of its way.</summary>
        public void ExitForUpdate() => ExitApp();

        /// <summary>Loads the DLL into explorer (or refreshes it if already there).</summary>
        public async Task ConnectAsync()
        {
            _window?.SetStatus(Lang.T("جاري الاتصال بالتاسك بار…"));
            SettingsStore.RetireRemovedTaskbarOptions();
            string status = await TaskbarInjector.EnsureLoadedAsync();
            _window?.SetStatus(status);
            _window?.ShowRestartButton(TaskbarInjector.NeedsExplorerRestart);
        }

        /// <summary>A notification from the tray icon (any thread).</summary>
        internal static void Notify(string title, string text)
        {
            Current?.Dispatcher.InvokeAsync(() =>
                (Current as App)?._tray?.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.None));
        }

        /// <summary>The tray icon's tooltip, e.g. a download's progress (null = normal; any thread).</summary>
        internal static void SetTrayText(string? text)
        {
            Current?.Dispatcher.InvokeAsync(() =>
            {
                if ((Current as App)?._tray is { } tray)
                    tray.Text = text == null ? "Wibn" : (text.Length > 63 ? text[..63] : text);
            });
        }

        /// <summary>The app's logo at the tray's size (falls back to the Windows one).</summary>
        private static System.Drawing.Icon LoadTrayIcon()
        {
            try
            {
                var res = GetResourceStream(new Uri("pack://application:,,,/app.ico"));
                if (res != null)
                    using (res.Stream)
                        return new System.Drawing.Icon(res.Stream, Forms.SystemInformation.SmallIconSize);
            }
            catch { }
            return System.Drawing.SystemIcons.Application;
        }

        // An invisible top-level window that receives "TaskbarCreated", which
        // Windows broadcasts after explorer restarts. Then we load the DLL again.
        private void CreateMessageWindow()
        {
            _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
            var p = new HwndSourceParameters("WinPlusMessageWindow") { WindowStyle = 0 };
            _messageWindow = new HwndSource(p);
            _messageWindow.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == _taskbarCreatedMsg && (DateTime.Now - TrayHost.LastBroadcast).TotalSeconds < 5)
                {
                    return IntPtr.Zero;   // Wibn's taskbar asked apps for their icons; explorer didn't restart
                }
                if (msg == _taskbarCreatedMsg)
                {
                    // Crash-loop guard: if the taskbar keeps restarting, stop
                    // loading the DLL and tell the user instead.
                    var now = DateTime.Now;
                    _taskbarRestarts.RemoveAll(t => (now - t).TotalSeconds > 120);
                    _taskbarRestarts.Add(now);
                    if (_taskbarRestarts.Count >= 3)
                    {
                        _safeMode = true;
                        _window?.SetStatus(Lang.T("التاسك بار أعاد التشغيل أكثر من مرة، فوقفت الميزة احتياطًا. أرسل ملف السجل."));
                        return IntPtr.Zero;
                    }
                    if (_safeMode || !Features.IsOn("taskbar")) return IntPtr.Zero;

                    Dispatcher.InvokeAsync(async () =>
                    {
                        await Task.Delay(3000);
                        await ConnectAsync();
                    });
                }
                return IntPtr.Zero;
            });

            // Custom keyboard shortcuts are registered on the same window.
            HotkeyManager.ShowAppRequested += ShowWindow;
            if (Features.IsOn("hotkeys")) HotkeyManager.Init(_messageWindow);
            VideoDownloader.Init(_messageWindow);  // its shortcut (Ctrl+H), see ApplyFeatures
            ScreenCapture.Init(_messageWindow);    // the screen capture shortcuts
        }

        /// <summary>Opens Wibn on a page (by its side-menu key, e.g. "glass").</summary>
        internal void ShowPage(string key)
        {
            ShowWindow();
            _window?.OpenPage(key);
        }

        /// <summary>Opens Wibn on the Taskbar page (from the taskbar's right-click menu).</summary>
        internal void ShowTaskbarSettings()
        {
            ShowWindow();
            _window?.OpenTaskbarPage();
        }

        private void ShowWindow()
        {
            if (_window == null) return;
            _window.Show();
            if (_window.WindowState == WindowState.Minimized)
                _window.WindowState = WindowState.Normal;
            _window.Activate();
        }

        /// <summary>Closes and starts again (e.g. after changing the language).</summary>
        public void RestartApp()
        {
            string? exe = Environment.ProcessPath;
            ExitApp();  // lets go of the single-instance lock first
            if (exe != null)
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true }); }
                catch (Exception ex) { CrashLog.Write("restart", ex); }
            }
        }

        private void ExitApp()
        {
            // The DLL stays inside explorer but switches itself off.
            SettingsStore.AppActive = false;
            TaskbarInjector.SignalRefresh();
            CustomTaskbar.Stop();   // Windows' taskbar back
            TaskbarPlacement.RestoreTaskbar();
            Relocker.LockAllNow();
            DragSpaceHook.Stop();
            FileUndo.FinishAll();
            P2PTransfer.StopAll();
            ScreenCapture.StopAll();
            GameBoost.Shutdown();   // services and apps back as they were

            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            _messageWindow?.Dispose();
            _window?.CloseForReal();
            _singleInstance?.ReleaseMutex();
            Shutdown();
        }

        private static void TrimMemory()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle,
                                     (IntPtr)(-1), (IntPtr)(-1));
        }
    }
}
