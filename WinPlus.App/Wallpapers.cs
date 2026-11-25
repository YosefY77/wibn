using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// Wallpapers: the user's own list of pictures and videos, a shortcut that opens them in the
    /// middle of the screen (<see cref="WallpaperSwitcher"/>), and changing the wallpaper with a
    /// transition.
    ///
    /// How a change looks: a window of ours is put inside the desktop, under the icons (the
    /// "WorkerW" trick live-wallpaper apps use), showing the old wallpaper; the new one comes in
    /// over it (a growing circle, a fade or a wipe). Meanwhile Windows gets the new picture behind
    /// it, so for a picture our window then goes and nothing is left running. A video stays in our
    /// window (muted, looping), and Windows gets one frame of it as its own wallpaper, so the
    /// desktop still looks right if Wibn closes.
    ///
    /// The window under the icons runs on its own thread, so the desktop never waits for Wibn's
    /// other windows. Settings in HKCU\Software\WinPlus\Wallpapers.
    /// </summary>
    internal static class Wallpapers
    {
        private const string OurKey = @"Software\WinPlus\Wallpapers";
        private const int HotkeyId = 0xB201;
        private const int WM_HOTKEY = 0x0312;

        /// <summary>Ctrl + Alt + W until the user picks another.</summary>
        public const uint DefaultMods = HotkeyManager.MOD_CONTROL | HotkeyManager.MOD_ALT, DefaultKey = 0x57;

        public static readonly string[] ImageTypes = { ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".webp", ".tif", ".tiff" };
        public static readonly string[] VideoTypes = { ".mp4", ".m4v", ".mov", ".wmv", ".webm", ".mkv", ".avi" };

        /// <summary>How the new wallpaper comes in (the numbers are stored: only add at the end).</summary>
        public enum Transition { Grow = 0, Fade = 1, Wipe = 2, None = 3, Wave = 4, Blinds = 5, Tiles = 6, Slide = 7, Diamond = 8, Random = 9 }

        /// <summary>The order and names they're offered in.</summary>
        public static readonly (Transition Fx, string Name)[] Transitions =
        {
            (Transition.Grow, "دائرة تكبر"), (Transition.Diamond, "معيّن يكبر"), (Transition.Fade, "تلاشي"),
            (Transition.Wipe, "مسح ناعم"), (Transition.Wave, "موجة"), (Transition.Slide, "انزلاق"),
            (Transition.Blinds, "ستارة"), (Transition.Tiles, "مربعات"), (Transition.Random, "عشوائي كل مرة"),
            (Transition.None, "بدون"),
        };

        public static bool IsVideo(string path) => VideoTypes.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

        public static bool IsSupported(string path)
        {
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return ImageTypes.Contains(ext) || VideoTypes.Contains(ext);
        }

        /// <summary>Something about the list, the current one or the shortcut changed (on the UI thread).</summary>
        public static event Action? Changed;

        private static void RaiseChanged()
        {
            var app = Application.Current;
            if (app == null) return;
            if (app.Dispatcher.CheckAccess()) Changed?.Invoke();
            else app.Dispatcher.BeginInvoke(() => Changed?.Invoke());
        }

        // ---- Settings ---------------------------------------------------------------------------

        private static int GetInt(string name, int fallback)
        {
            try { using var key = Registry.CurrentUser.OpenSubKey(OurKey); return key?.GetValue(name) is int v ? v : fallback; }
            catch { return fallback; }
        }

        private static void SetInt(string name, int value)
        {
            try { using var key = Registry.CurrentUser.CreateSubKey(OurKey); key.SetValue(name, value, RegistryValueKind.DWord); } catch { }
        }

        private static string GetString(string name)
        {
            try { using var key = Registry.CurrentUser.OpenSubKey(OurKey); return key?.GetValue(name) as string ?? ""; }
            catch { return ""; }
        }

        private static void SetString(string name, string value)
        {
            try { using var key = Registry.CurrentUser.CreateSubKey(OurKey); key.SetValue(name, value); } catch { }
        }

        /// <summary>The wallpapers, in the user's order (full paths).</summary>
        public static List<string> List
        {
            get
            {
                try { return JsonSerializer.Deserialize<List<string>>(GetString("List")) ?? new(); }
                catch { return new(); }
            }
            private set => SetString("List", JsonSerializer.Serialize(value));
        }

        /// <summary>The one set last from here ("" = none).</summary>
        public static string Current
        {
            get => GetString("Current");
            private set => SetString("Current", value);
        }

        public static Transition Effect
        {
            get => (Transition)Math.Clamp(GetInt("Effect", 0), 0, 9);
            set => SetInt("Effect", (int)value);
        }

        /// <summary>How many frames a second a video wallpaper shows (30 by default: smooth and light).</summary>
        public static int FrameRate
        {
            get { int v = GetInt("Fps", 30); return v is 15 or 30 or 60 ? v : 30; }
            set { SetInt("Fps", value); Layer.SetFps(value); }
        }

        /// <summary>A video wallpaper becomes a still picture (and frees its memory) while a window is maximized
        /// or something is full screen (on by default).</summary>
        public static bool PauseFullscreen
        {
            get => GetInt("PauseFullscreen", 1) != 0;
            set => SetInt("PauseFullscreen", value ? 1 : 0);
        }

        /// <summary>Adds files (and every wallpaper inside folders); returns how many were new.</summary>
        public static int Add(IEnumerable<string> paths)
        {
            var list = List;
            var seen = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
            int added = 0;
            foreach (var p in paths)
            {
                IEnumerable<string> files;
                try
                {
                    files = Directory.Exists(p)
                        ? Directory.EnumerateFiles(p).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                        : new[] { p };
                }
                catch { continue; }
                foreach (var f in files)
                {
                    if (!IsSupported(f) || !File.Exists(f) || !seen.Add(f)) continue;
                    list.Add(f);
                    added++;
                }
            }
            if (added > 0) { List = list; RaiseChanged(); }
            return added;
        }

        public static void Remove(string path)
        {
            var list = List;
            if (list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) == 0) return;
            List = list;
            RaiseChanged();
        }

        /// <summary>Moves a wallpaper one place back (-1) or forward (+1) in the list.</summary>
        public static void Move(string path, int by)
        {
            var list = List;
            int i = list.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            int j = i + by;
            if (i < 0 || j < 0 || j >= list.Count) return;
            (list[i], list[j]) = (list[j], list[i]);
            List = list;
            RaiseChanged();
        }

        // ---- The shortcut -------------------------------------------------------------------------

        public static uint HotkeyMods => (uint)GetInt("Mods", (int)DefaultMods);
        public static uint HotkeyKey => (uint)GetInt("Key", (int)DefaultKey);
        public static bool HasHotkey => HotkeyKey != 0;
        public static string HotkeyText => HasHotkey ? HotkeyManager.KeyText(HotkeyMods, HotkeyKey) : "";

        /// <summary>Did Windows refuse the shortcut (another program has it)?</summary>
        public static bool HotkeyFailed { get; private set; }

        public static void SetHotkey(uint mods, uint vk)
        {
            SetInt("Mods", (int)mods);
            SetInt("Key", (int)vk);
            Refresh();
        }

        private static HwndSource? _window;
        private static bool _registered;

        public static void Init(HwndSource window)
        {
            if (_window != null) return;
            _window = window;
            window.AddHook(WndProc);
            SystemEvents.DisplaySettingsChanged += (_, _) => OnDisplayChanged();
        }

        /// <summary>Registers the shortcut and starts / stops the video wallpaper to match the settings.</summary>
        public static void Refresh()
        {
            if (_window == null) return;
            Pause();
            bool on = Features.IsOn("wallpapers");
            HotkeyFailed = false;
            if (on && HasHotkey)
            {
                _registered = RegisterHotKey(_window.Handle, HotkeyId, HotkeyMods | 0x4000 /* MOD_NOREPEAT */, HotkeyKey);
                HotkeyFailed = !_registered;
            }

            // A video wallpaper chosen before: back on with Wibn (and off with the feature).
            string current = Current;
            if (on && current.Length > 0 && IsVideo(current) && File.Exists(current))
            {
                if (!VideoPlaying && !_applying) Apply(current, null, Transition.Fade);
            }
            else if (VideoPlaying && !_applying)
            {
                StopVideo();
            }
            RaiseChanged();
        }

        /// <summary>Lets go of the shortcut for a moment (while a new one is being picked).</summary>
        public static void Pause()
        {
            if (_window == null || !_registered) return;
            UnregisterHotKey(_window.Handle, HotkeyId);
            _registered = false;
        }

        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                handled = true;
                ShowSwitcher();
            }
            return IntPtr.Zero;
        }

        /// <summary>The wallpapers in the middle of the screen (or closes them if they're open).</summary>
        public static void ShowSwitcher()
        {
            try
            {
                if (WallpaperSwitcher.Open is { } open) { open.Dismiss(); return; }
                var list = List.Where(File.Exists).ToList();
                if (list.Count == 0)
                {
                    (Application.Current as App)?.ShowPage("wallpapers");
                    return;
                }
                new WallpaperSwitcher(list, Current).ShowOnCursorScreen();
            }
            catch (Exception ex) { CrashLog.Write("wallpaper switcher", ex); }
        }

        // ---- Changing the wallpaper -----------------------------------------------------------------

        private static int _applyId;
        private static bool _applying;

        /// <summary>Is a video wallpaper on the desktop now?</summary>
        public static volatile bool VideoPlaying;

        /// <summary>
        /// Makes this the wallpaper. <paramref name="origin"/>: where the circle grows from (screen
        /// pixels), e.g. where its card was; the middle of each screen when null.
        /// </summary>
        public static async void Apply(string path, System.Drawing.Point? origin = null, Transition? effect = null)
        {
            if (!File.Exists(path)) return;
            int id = ++_applyId;
            _applying = true;
            try
            {
                bool video = IsVideo(path);
                var fx = effect ?? Effect;
                Current = path;
                RaiseChanged();

                // 1. Our window under the icons, showing what's there now (only if it's needed).
                bool layered = video || fx != Transition.None || VideoPlaying;
                if (layered) layered = await Layer.EnsureAsync();
                if (id != _applyId) return;

                // Windows' own wallpaper (a video gives Windows one of its frames).
                Task<bool> SetWindows() => Task.Run(() =>
                {
                    string? still = video ? VideoFrame(path) : StillFor(path);
                    return still != null && SetWindowsWallpaper(still);
                });

                if (!layered)
                {
                    await SetWindows();
                    // Right after sign-in the desktop (Progman and its WorkerW) isn't ready yet: try again in a
                    // moment, quietly, instead of leaving the video as a still picture.
                    if (video && RetryLater(path)) return;
                    if (video) Notify(Lang.T("ما قدرت أحط الخلفية المتحركة تحت أيقونات سطح المكتب"));
                    return;
                }

                // 2. The new one comes in. As soon as our windows show the old picture, Windows gets the new
                //    one behind them: Windows then plays its own fade (about a second) in a window it puts over
                //    ours, so ours is put back on top again and again until that's over, and it's never seen.
                Task<bool>? setting = null;
                void Start()
                {
                    if (setting != null) return;
                    setting = SetWindows();
                    _ = Layer.GuardAsync(setting);
                }
                bool shown = await Layer.ShowAsync(path, video, fx, origin, Start);
                if (id != _applyId) return;
                if (shown || !video) Start();
                if (setting != null) await setting;
                if (id != _applyId) return;

                if (video && !shown)
                {
                    if (RetryLater(path)) { await Layer.StopAsync(); VideoPlaying = false; return; }
                    Notify(Lang.T("ما اشتغل المقطع. جرّب صيغة MP4"));
                    await Layer.StopAsync();
                    VideoPlaying = false;
                }
                else if (!video)
                {
                    // Windows shows the same picture now (its own fade is over): our window can go.
                    await Task.Delay(1500);
                    if (id != _applyId) return;
                    await Layer.StopAsync();
                    VideoPlaying = false;
                }
                else
                {
                    VideoPlaying = true;
                    _retries = 0;
                    StartWatch();
                }
                // The glass of the widgets and taskbars: a new picture of the desktop.
                _ = Task.Delay(3500).ContinueWith(_ => LiquidGlass.RefreshDesktop());
            }
            catch (Exception ex)
            {
                CrashLog.Write("wallpaper", ex);
            }
            finally
            {
                if (id == _applyId)
                {
                    _applying = false;
                    if (!VideoPlaying) StopWatch();
                }
                RaiseChanged();
            }
        }

        private static int _retries;

        /// <summary>
        /// The video couldn't go on the desktop in Wibn's first minutes (Windows is still starting): tries again in
        /// 3 seconds, up to 20 times. False = that's not the case (or it's tried enough): tell the user.
        /// </summary>
        private static bool RetryLater(string path)
        {
            DateTime started;
            try { started = Process.GetCurrentProcess().StartTime.ToUniversalTime(); } catch { return false; }
            if ((DateTime.UtcNow - started).TotalMinutes > 3 || _retries >= 20) return false;
            _retries++;
            int id = _applyId;
            _ = Task.Delay(3000).ContinueWith(_ => Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                // Something else was picked (or the video was turned off) meanwhile: leave it.
                if (id != _applyId || _applying || VideoPlaying || Current != path || !Features.IsOn("wallpapers")) return;
                Apply(path, null, Transition.Fade);
            }));
            return true;
        }

        /// <summary>Takes the video off the desktop (Windows keeps the frame it was given).</summary>
        public static async void StopVideo()
        {
            ++_applyId;
            StopWatch();
            await Layer.StopAsync();
            VideoPlaying = false;
            _ = Task.Delay(1500).ContinueWith(_ => LiquidGlass.RefreshDesktop());
            RaiseChanged();
        }

        /// <summary>When Wibn closes.</summary>
        public static void Shutdown()
        {
            StopWatch();
            Layer.StopNow();
            VideoPlaying = false;
        }

        private static void Notify(string text)
        {
            App.Notify(Lang.T("الخلفيات"), text);
        }

        // ---- Keeping a video wallpaper well ------------------------------------------------------------

        private static DispatcherTimer? _watch;
        private static bool _paused;
        private static int _lostCount;

        private static void StartWatch()
        {
            if (_watch != null) return;
            _watch = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            _watch.Tick += (_, _) => OnWatch();
            _watch.Start();
        }

        private static void StopWatch()
        {
            _watch?.Stop();
            _watch = null;
            _paused = false;
            _lostCount = 0;
        }

        private static void OnWatch()
        {
            if (!VideoPlaying || _applying) return;

            // Explorer restarted (or the desktop was rebuilt): our window went with it.
            if (!Layer.Healthy())
            {
                if (++_lostCount >= 2)
                {
                    _lostCount = 0;
                    string current = Current;
                    VideoPlaying = false;
                    if (File.Exists(current)) Apply(current, null, Transition.Fade);
                }
                return;
            }
            _lostCount = 0;
            Layer.KeepOrder();   // Explorer puts its wallpaper window back on top now and then

            // A window is maximized on the main screen or something is full screen (with the setting on), or
            // nobody can see it anyway (every screen covered): the video closes and its last frame stays as a
            // still picture, so its memory goes back to Windows. It carries on from the same moment afterwards.
            // (A window maximized on another screen doesn't stop it: the main screen still shows it moving.)
            Covered(out bool main, out bool all);
            bool still = all || (PauseFullscreen && (main || ForegroundFullscreen()));
            if (still != _paused)
            {
                _paused = still;
                if (still) Layer.Suspend(); else Layer.Resume();
            }
        }

        private static async void OnDisplayChanged()
        {
            if (!VideoPlaying) return;
            await Task.Delay(1500);
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                string current = Current;
                if (VideoPlaying && !_applying && File.Exists(current))
                {
                    _ = Layer.StopAsync().ContinueWith(_ => Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        VideoPlaying = false;
                        Apply(current, null, Transition.Fade);
                    }));
                }
            });
        }

        /// <summary>
        /// A see-through layer over the screen (NVIDIA's overlay, Discord's, Steam's, Game Bar…), not a window
        /// you look at: click-through, layered, or a tool window that never takes the focus.
        /// </summary>
        private static bool IsOverlay(IntPtr hwnd)
        {
            int ex = GetWindowLong(hwnd, -20 /* GWL_EXSTYLE */);
            const int TRANSPARENT = 0x20, TOOLWINDOW = 0x80, LAYERED = 0x80000, NOACTIVATE = 0x08000000;
            return (ex & TRANSPARENT) != 0 || (ex & LAYERED) != 0 || (ex & (TOOLWINDOW | NOACTIVATE)) != 0;
        }

        /// <summary>Every screen has a maximized or full-screen window on it: the wallpaper can't be seen anywhere.</summary>
        /// <summary>
        /// <paramref name="main"/>: a window is maximized on the main screen or fills it. <paramref name="all"/>:
        /// every screen has one (the wallpaper can't be seen anywhere).
        /// </summary>
        private static void Covered(out bool main, out bool all)
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            var covered = new bool[screens.Length];
            int left = screens.Length;
            uint me = (uint)Environment.ProcessId;
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
                if (DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) >= 0 && cloaked != 0) return true;
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == me) return true;
                var cls = new StringBuilder(64);
                GetClassName(hwnd, cls, cls.Capacity);
                if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
                if (IsOverlay(hwnd)) return true;
                if (!GetWindowRect(hwnd, out var r)) return true;
                bool zoomed = IsZoomed(hwnd);
                for (int i = 0; i < screens.Length; i++)
                {
                    if (covered[i]) continue;
                    var b = screens[i].Bounds;
                    bool fills = r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
                    bool maxHere = zoomed && System.Windows.Forms.Screen.FromHandle(hwnd).Bounds == b;
                    if (fills || maxHere) { covered[i] = true; left--; }
                }
                return left > 0;
            }, IntPtr.Zero);
            int primary = Array.FindIndex(screens, sc => sc.Primary);
            main = primary >= 0 && covered[primary];
            all = left == 0;
        }

        /// <summary>A game or a video covers its whole screen.</summary>
        private static bool ForegroundFullscreen()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            GetWindowThreadProcessId(fg, out uint pid);
            if (pid == (uint)Environment.ProcessId) return false;   // ours (the wallpaper list)
            var cls = new StringBuilder(64);
            GetClassName(fg, cls, cls.Capacity);
            if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
            if (IsOverlay(fg)) return false;
            if (IsZoomed(fg)) return false;   // a maximized window: the other screens still show the wallpaper
            if (!GetWindowRect(fg, out var r)) return false;
            var b = System.Windows.Forms.Screen.FromHandle(fg).Bounds;
            return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
        }

        // ---- Windows' own wallpaper ----------------------------------------------------------------------

        private static bool SetWindowsWallpaper(string file)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop"))
                {
                    key.SetValue("WallpaperStyle", "10");   // Fill, like our window
                    key.SetValue("TileWallpaper", "0");
                }
                return SystemParametersInfo(0x0014 /* SPI_SETDESKWALLPAPER */, 0, file, 0x01 | 0x02 /* UPDATEINIFILE | SENDCHANGE */);
            }
            catch (Exception ex)
            {
                CrashLog.Write("set wallpaper", ex);
                return false;
            }
        }

        /// <summary>The picture Windows shows now (its file), or null.</summary>
        internal static string? WindowsWallpaper()
        {
            try
            {
                var sb = new StringBuilder(520);
                if (SystemParametersInfo(0x0073 /* SPI_GETDESKWALLPAPER */, (uint)sb.Capacity, sb, 0) && File.Exists(sb.ToString()))
                    return sb.ToString();
                string transcoded = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Themes\TranscodedWallpaper");
                return File.Exists(transcoded) ? transcoded : null;
            }
            catch { return null; }
        }

        /// <summary>A file Windows can take as its wallpaper: the picture itself, or a JPEG copy (WebP…).</summary>
        private static string? StillFor(string path)
        {
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".jpg" or ".jpeg" or ".jfif" or ".png" or ".bmp" or ".tif" or ".tiff") return path;
            string out_ = CacheFile(path, "still");
            if (File.Exists(out_)) return out_;
            try
            {
                var frame = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
                SaveJpeg(frame, out_);
                return out_;
            }
            catch (Exception ex) { CrashLog.Write("wallpaper still", ex); return null; }
        }

        /// <summary>A big frame of a video (for Windows' own wallpaper), or null.</summary>
        private static string? VideoFrame(string path)
        {
            string out_ = CacheFile(path, "frame");
            if (File.Exists(out_)) return out_;
            return Sta.Run(() => ShellPicture(path, 1920, 1080, out_)).GetAwaiter().GetResult() ? out_ : null;
        }

        // ---- Thumbnails ------------------------------------------------------------------------------

        private static string CacheDir
        {
            get
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPlus", "wallpapers");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>A cache file for this wallpaper (a new name when the file changes).</summary>
        private static string CacheFile(string path, string kind)
        {
            long size = 0, time = 0;
            try { var fi = new FileInfo(path); size = fi.Length; time = fi.LastWriteTimeUtc.Ticks; } catch { }
            string id = path.ToLowerInvariant() + "|" + size + "|" + time;
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(id)))[..16];
            return System.IO.Path.Combine(CacheDir, kind + "_" + hash + ".jpg");
        }

        /// <summary>A small picture of the wallpaper (a frame for a video), made once and kept on disk.</summary>
        public static Task<ImageSource?> ThumbAsync(string path) => Sta.Run<ImageSource?>(() =>
        {
            try
            {
                string file = CacheFile(path, "thumb");
                if (!File.Exists(file))
                {
                    if (!ShellPicture(path, 768, 432, file) && !IsVideo(path))
                    {
                        var img = Load(path, 768);
                        if (img is BitmapSource bs) SaveJpeg(bs, file);
                    }
                }
                return File.Exists(file) ? Load(file, 0) : null;
            }
            catch (Exception ex) { CrashLog.Write("wallpaper thumb", ex); return null; }
        });

        /// <summary>A picture file, decoded at about this width (0 = as it is), ready for any thread.</summary>
        internal static ImageSource? Load(string file, int width)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(file);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache | BitmapCreateOptions.IgnoreColorProfile;
                if (width > 0) bmp.DecodePixelWidth = width;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private static void SaveJpeg(BitmapSource source, string file)
        {
            var enc = new JpegBitmapEncoder { QualityLevel = 92 };
            enc.Frames.Add(BitmapFrame.Create(source));
            string tmp = file + ".tmp";
            using (var fs = File.Create(tmp)) enc.Save(fs);
            File.Move(tmp, file, true);
        }

        /// <summary>Windows' own thumbnail of a file (pictures and videos), saved as a JPEG.</summary>
        private static bool ShellPicture(string path, int w, int h, string outFile)
        {
            IShellItemImageFactory? factory = null;
            IntPtr hbmp = IntPtr.Zero;
            try
            {
                SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory);
                int hr = factory.GetImage(new SIZE { cx = w, cy = h }, 0x1 /* BIGGERSIZEOK */ | 0x8 /* THUMBNAILONLY */, out hbmp);
                if (hr < 0 || hbmp == IntPtr.Zero) return false;
                using var bmp = System.Drawing.Image.FromHbitmap(hbmp);
                string tmp = outFile + ".tmp";
                var codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
                using var prms = new System.Drawing.Imaging.EncoderParameters(1);
                prms.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
                bmp.Save(tmp, codec, prms);
                File.Move(tmp, outFile, true);
                return true;
            }
            catch { return false; }
            finally
            {
                if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
                if (factory != null) Marshal.ReleaseComObject(factory);
            }
        }

        /// <summary>One background STA thread for the shell's thumbnails (some need STA), one at a time.</summary>
        private static class Sta
        {
            private static readonly BlockingCollection<Action> Queue = new();
            private static Thread? _thread;
            private static readonly object Gate = new();

            public static Task<T> Run<T>(Func<T> work)
            {
                lock (Gate)
                {
                    if (_thread == null)
                    {
                        _thread = new Thread(() => { foreach (var a in Queue.GetConsumingEnumerable()) a(); })
                        { IsBackground = true, Name = "Wibn thumbnails", Priority = ThreadPriority.BelowNormal };
                        _thread.SetApartmentState(ApartmentState.STA);
                        _thread.Start();
                    }
                }
                var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                Queue.Add(() =>
                {
                    try { tcs.SetResult(work()); }
                    catch (Exception ex) { tcs.SetException(ex); }
                });
                return tcs.Task;
            }
        }

        // ---- Our window under the desktop icons ----------------------------------------------------------

        /// <summary>
        /// Our windows under the icons (one per screen) and the thread that owns them. That thread only
        /// answers Windows, so the desktop, whose input is tied to it, never waits: the transitions
        /// (<see cref="WallpaperTransition"/>) and the video (<see cref="VideoWallpaper"/>) draw into
        /// them from threads of their own.
        /// </summary>
        private static class Layer
        {
            private static Dispatcher? _dispatcher;
            private static readonly List<WallpaperHost> Screens = new();   // one per screen; only touched on _dispatcher
            private static VideoWallpaper? _player;
            private static readonly object Gate = new();

            private static Dispatcher Start()
            {
                lock (Gate)
                {
                    if (_dispatcher != null) return _dispatcher;
                    using var ready = new ManualResetEventSlim();
                    Dispatcher? d = null;
                    var t = new Thread(() =>
                    {
                        d = Dispatcher.CurrentDispatcher;
                        ready.Set();
                        Dispatcher.Run();
                    })
                    { IsBackground = true, Name = "Wibn wallpaper" };
                    t.SetApartmentState(ApartmentState.STA);
                    t.Start();
                    ready.Wait();
                    _dispatcher = d!;
                    return _dispatcher;
                }
            }

            private static Task<T> On<T>(Func<Task<T>> work)
            {
                var d = Start();
                return d.InvokeAsync(work).Task.Unwrap();
            }

            /// <summary>Our windows are under the icons (made if needed). False if the desktop can't take them.</summary>
            public static Task<bool> EnsureAsync() => On(async () =>
            {
                if (Screens.Count > 0 && Screens.All(h => h.Alive)) return true;
                CloseAll();

                var desktop = FindDesktopLayer();
                if (desktop.Parent == IntPtr.Zero) return false;
                double scale = SystemScale();
                foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                {
                    // Invisible until the transition has drawn what's there now into it (no black flash).
                    var win = new WallpaperHost(screen.Bounds, scale, desktop.Layered, wpf: false);
                    if (!win.Attach(desktop))
                    {
                        win.Close();
                        CloseAll();
                        return false;
                    }
                    Screens.Add(win);
                }
                _shown = Screens.ToArray();
                await Task.Yield();
                return Screens.Count > 0;
            });

            /// <summary>The new wallpaper comes in on every screen. False if it couldn't (a video that won't play).</summary>
            /// <param name="shown">Runs once the old picture is in our windows and they show (any thread).</param>
            public static Task<bool> ShowAsync(string path, bool video, Transition fx, System.Drawing.Point? origin, Action shown) => On(async () =>
            {
                if (Screens.Count == 0) return false;
                var all = System.Windows.Forms.Screen.AllScreens;
                int maxW = all.Max(sc => sc.Bounds.Width), maxH = all.Max(sc => sc.Bounds.Height);

                // What's on the desktop now: the video's frame (it stops; its last frame stays), or Windows' picture.
                BitmapSource? from = _player?.Snapshot();
                _player?.Dispose();
                _player = null;
                _resumeAt = -1;
                if (from == null && WindowsWallpaper() is { } old)
                    from = await Task.Run(() => Load(old, maxW) as BitmapSource);

                VideoWallpaper? player = null;
                BitmapSource? to;
                if (video)
                {
                    // Its first frame does the transition; the video carries on from it right after.
                    player = new VideoWallpaper(path, maxW, maxH, FrameRate);
                    to = await player.FirstFrame;
                    if (to == null) { player.Dispose(); return false; }
                }
                else
                {
                    to = await Task.Run(() => Load(path, maxW) as BitmapSource);
                    if (to == null) return false;   // Windows shows it by itself, without the transition
                }

                var screens = Screens.Select(w => (w.Handle, w.Bounds)).ToList();
                var wins = Screens.ToList();
                await Task.Factory.StartNew(() => WallpaperTransition.Run(screens, from, to, fx, origin,
                    () => { foreach (var w in wins) w.Reveal(); shown(); }), TaskCreationOptions.LongRunning);

                if (player != null)
                {
                    player.Play(Screens.Select(w => (w.Handle, w.Bounds.Width, w.Bounds.Height)).ToList());
                    await Task.WhenAny(player.FirstShown, Task.Delay(1500));
                    _player = player;
                    _videoPath = path;
                }
                return true;
            });

            /// <summary>
            /// While Windows changes its wallpaper (and plays its fade for about a second after), puts our
            /// windows back over Explorer's every 10 ms. From its own thread: the order is set right away.
            /// </summary>
            public static Task GuardAsync(Task setting) => Task.Factory.StartNew(() =>
            {
                var wins = _shown;
                var clock = Stopwatch.StartNew();
                long doneAt = -1;
                while (clock.ElapsedMilliseconds < 8000)
                {
                    foreach (var w in wins) w.KeepOrder();
                    if (setting.IsCompleted && doneAt < 0) doneAt = clock.ElapsedMilliseconds;
                    if (doneAt >= 0 && clock.ElapsedMilliseconds - doneAt > 1600) break;
                    Thread.Sleep(10);
                }
            }, TaskCreationOptions.LongRunning);

            /// <summary>Our windows now (for other threads).</summary>
            private static volatile WallpaperHost[] _shown = Array.Empty<WallpaperHost>();

            /// <summary>Our windows right under the icons again (above Explorer's wallpaper window). Any thread.</summary>
            public static void KeepOrder()
            {
                var d = _dispatcher;
                d?.BeginInvoke(() =>
                {
                    for (int i = Screens.Count - 1; i >= 0; i--) Screens[i].KeepOrder();
                });
            }

            private static string? _videoPath;
            private static long _resumeAt = -1;   // >= 0 while the video is closed and its last frame stays

            /// <summary>Closes the video (its memory goes back); its last frame stays in our windows.</summary>
            public static void Suspend()
            {
                _dispatcher?.BeginInvoke(() =>
                {
                    if (_player == null) return;
                    _resumeAt = _player.Position;
                    _player.Dispose();
                    _player = null;
                    // Its frames and the decoder are native memory, given back by Dispose. No forced collection or
                    // working-set trim here: those made every maximize / restore stall Wibn (and the PC) for a moment.
                    MemoryTrim.Soon();
                });
            }

            /// <summary>Opens the video again and carries on from where it stopped.</summary>
            public static void Resume()
            {
                var d = _dispatcher;
                d?.InvokeAsync(async () =>
                {
                    if (_resumeAt < 0 || _player != null || _videoPath == null || Screens.Count == 0) return;
                    long at = _resumeAt;
                    _resumeAt = -1;
                    var all = System.Windows.Forms.Screen.AllScreens;
                    var player = new VideoWallpaper(_videoPath, all.Max(sc => sc.Bounds.Width), all.Max(sc => sc.Bounds.Height), FrameRate, at);
                    if (await player.FirstFrame == null) { player.Dispose(); return; }
                    player.Play(Screens.Select(w => (w.Handle, w.Bounds.Width, w.Bounds.Height)).ToList());
                    _player = player;
                });
            }

            public static void SetFps(int fps)
            {
                var p = _player;
                if (p != null) p.Fps = fps;
            }

            /// <summary>Are the video's windows still under the icons? (Explorer restarting takes them away.)</summary>
            public static bool Healthy()
            {
                var d = _dispatcher;
                if (d == null) return false;
                try { return d.Invoke(() => (_player != null || _resumeAt >= 0) && Screens.Count > 0 && Screens.All(h => h.Alive), DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(1)); }
                catch { return true; }   // busy: don't rebuild for that
            }

            public static Task StopAsync()
            {
                Dispatcher? d;
                lock (Gate) { d = _dispatcher; _dispatcher = null; }
                if (d == null) return Task.CompletedTask;
                return d.InvokeAsync(() =>
                {
                    CloseAll();
                    d.InvokeShutdown();
                }).Task;
            }

            public static void StopNow()
            {
                Dispatcher? d;
                lock (Gate) { d = _dispatcher; _dispatcher = null; }
                if (d == null) return;
                try { d.Invoke(() => { CloseAll(); d.InvokeShutdown(); }, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(3)); }
                catch { }
            }

            private static void StopVideo()
            {
                _player?.Dispose();
                _player = null;
                _resumeAt = -1;
                foreach (var w in Screens) w.Close();
                Screens.Clear();
            }

            private static void CloseAll()
            {
                _shown = Array.Empty<WallpaperHost>();
                StopVideo();
            }
        }

        /// <summary>Where our window goes: inside <see cref="Parent"/>, right under <see cref="Above"/> (or anywhere
        /// when zero), and <see cref="Layered"/> when the parent has no drawing surface of its own.</summary>
        internal readonly record struct DesktopSpot(IntPtr Parent, IntPtr Above, bool Layered);

        /// <summary>
        /// The spot under the desktop icons: Progman gets message 0x052C, which puts a WorkerW
        /// between the icons and the wallpaper. Before Windows 11 24H2 that WorkerW is a top-level
        /// window right after the one holding the icons, and ours goes inside it. From 24H2 the icons
        /// (SHELLDLL_DefView) and the WorkerW are both children of Progman, which draws with no
        /// surface of its own: ours goes between them, as a layered window (so it gets a surface).
        /// </summary>
        private static DesktopSpot FindDesktopLayer()
        {
            IntPtr progman = FindWindow("Progman", null);
            if (progman == IntPtr.Zero) return default;
            SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(1), 0x0002 /* ABORTIFHUNG */, 1000, out _);

            // Windows 11 24H2 and later: the icons and the WorkerW are both inside Progman.
            IntPtr icons = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (icons != IntPtr.Zero && FindWindowEx(progman, IntPtr.Zero, "WorkerW", null) != IntPtr.Zero)
                return new DesktopSpot(progman, icons, true);

            IntPtr found = IntPtr.Zero;
            EnumWindows((top, _) =>
            {
                if (FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                {
                    IntPtr next = FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
                    if (next != IntPtr.Zero) { found = next; return false; }
                }
                return true;
            }, IntPtr.Zero);
            return new DesktopSpot(found, IntPtr.Zero, false);
        }

        private static double SystemScale()
        {
            try { return GetDpiForSystem() / 96.0; } catch { return 1; }
        }

        // ---- Win32 ----------------------------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SystemParametersInfo(uint action, uint param, string value, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SystemParametersInfo(uint action, uint param, StringBuilder value, uint flags);
    }

    /// <summary>
    /// Our window under the desktop icons, on one screen. A plain Win32 window ("holder") made by us,
    /// with WPF drawing inside it as a child (HwndSource).
    ///
    /// Windows 11 24H2 and later draw the desktop with no surface of its own: only a layered window
    /// shows there, and WPF drops WS_EX_LAYERED on its own windows, so the holder is the layered one.
    /// Inside it WPF draws in software: its GPU drawing doesn't reach a layered parent (tested: the
    /// video shows only this way).
    /// </summary>
    internal sealed class WallpaperHost
    {
        public readonly System.Drawing.Rectangle Bounds;   // the screen, in (system DPI) pixels
        public readonly Grid Root;
        private readonly bool _layered;
        private IntPtr _holder, _parent;
        private HwndSource? _source;
        private bool _underIcons;

        private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private static readonly WndProc Proc = DefWindowProc;   // kept alive for Windows
        private static bool _registered;
        private const string ClassName = "WibnWallpaperHolder";

        private readonly bool _wpf;

        /// <summary>The window to draw into (the video).</summary>
        public IntPtr Handle => _holder;

        /// <param name="wpf">WPF draws in it (a transition); otherwise it's drawn into directly (a video).</param>
        public WallpaperHost(System.Drawing.Rectangle bounds, double scale, bool layered, bool wpf)
        {
            Bounds = bounds;
            _layered = layered;
            _wpf = wpf;
            Root = new Grid
            {
                ClipToBounds = true, Background = Brushes.Black,
                Width = bounds.Width / scale, Height = bounds.Height / scale,
            };
        }

        /// <summary>Is it still a window inside the desktop? (Explorer restarting takes it away.)</summary>
        public bool Alive => _holder != IntPtr.Zero && IsWindow(_holder) && GetParent(_holder) is var p && p != IntPtr.Zero && IsWindow(p);

        /// <summary>Puts it inside the desktop's window, over this screen.</summary>
        internal bool Attach(Wallpapers.DesktopSpot spot)
        {
            IntPtr module = GetModuleHandle(null);
            if (!_registered)
            {
                var wc = new WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Proc, hInstance = module,
                    lpszClassName = ClassName,   // no background: nothing gets painted over the video
                };
                if (RegisterClassEx(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410 /* already there */) return false;
                _registered = true;
            }

            Wallpapers.GetWindowRect(spot.Parent, out var d);
            int ex = 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */ | (_layered ? 0x00080000 /* LAYERED */ : 0);
            const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000;
            _holder = CreateWindowEx(ex, ClassName, "Wibn wallpaper", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
                Bounds.X - d.Left, Bounds.Y - d.Top, Bounds.Width, Bounds.Height, spot.Parent, IntPtr.Zero, module, IntPtr.Zero);
            if (_holder == IntPtr.Zero) return false;
            if (_layered && !SetLayeredWindowAttributes(_holder, 0, (byte)(_wpf ? 255 : 0), 0x2 /* LWA_ALPHA */))
            {
                Close();
                return false;
            }
            _parent = spot.Parent;
            _underIcons = spot.Above != IntPtr.Zero;
            KeepOrder();
            if (!_wpf) return true;   // drawn into directly (Reveal once the first picture is in)

            _source = new HwndSource(new HwndSourceParameters("Wibn wallpaper")
            {
                ParentWindow = _holder, WindowStyle = WS_CHILD | WS_VISIBLE,
                PositionX = 0, PositionY = 0, Width = Bounds.Width, Height = Bounds.Height,
            });
            if (_layered && _source.CompositionTarget is HwndTarget target) target.RenderMode = RenderMode.SoftwareOnly;
            _source.RootVisual = Root;
            return true;
        }

        /// <summary>Right under the desktop icons, over Explorer's own wallpaper window (24H2 and later).</summary>
        public void KeepOrder()
        {
            if (!_underIcons || !Alive) return;
            IntPtr icons = FindWindowEx(_parent, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (icons == IntPtr.Zero) return;
            SetWindowPos(_holder, icons, 0, 0, 0, 0, 0x0001 /* NOSIZE */ | 0x0002 /* NOMOVE */ | 0x0010 /* NOACTIVATE */);
        }

        /// <summary>Shows it (it starts see-through, so nothing shows before its first picture).</summary>
        public void Reveal()
        {
            if (_layered && _holder != IntPtr.Zero) SetLayeredWindowAttributes(_holder, 0, 255, 0x2 /* LWA_ALPHA */);
        }

        public void Close()
        {
            try { _source?.Dispose(); } catch { }
            _source = null;
            if (_holder != IntPtr.Zero && IsWindow(_holder)) DestroyWindow(_holder);
            _holder = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public uint cbSize, style;
            public WndProc lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            public string? lpszMenuName, lpszClassName;
            public IntPtr hIconSm;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint key, byte alpha, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
        [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int obj);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    }
}
