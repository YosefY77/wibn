using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Interop;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// Screen capture, four kinds, each on its own shortcut that the user picks
    /// (none are set at first, and the feature itself starts off):
    ///   Region: freeze the screen, drag over a part of it (or click a window);
    ///   Full:   the screen the mouse is on;
    ///   Record: a video, 1 minute by default (the shortcut again stops it early);
    ///   Replay: saves the last minute(s). Only this one runs in the background, and
    ///           only while the user has its switch on: FFmpeg keeps recording into a
    ///           ring of short files and the shortcut joins the newest ones.
    /// Pictures are PNG (and copied); videos are MP4 with the PC's sound. Videos need FFmpeg.
    /// </summary>
    internal static class ScreenCapture
    {
        public enum Kind { Region = 0, Full = 1, Record = 2, Replay = 3 }

        private const string OurKey = @"Software\WinPlus\Capture";
        private const int HotkeyBase = 0xB101;
        private const int WM_HOTKEY = 0x0312;

        public static readonly int[] RecordChoices = { 1, 2, 3, 5, 10, 15, 30, 60 };
        public static readonly int[] ReplayChoices = { 1, 2, 3, 5, 10 };

        // ---- Settings ---------------------------------------------------------------------

        private static int GetInt(string name, int fallback)
        {
            using var key = Registry.CurrentUser.OpenSubKey(OurKey);
            return key?.GetValue(name) is int v ? v : fallback;
        }

        private static void SetInt(string name, int value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(OurKey);
            key.SetValue(name, value, RegistryValueKind.DWord);
        }

        public static uint HotkeyMods(Kind k) => (uint)GetInt(k + "Mods", 0);
        public static uint HotkeyKey(Kind k) => (uint)GetInt(k + "Key", 0);
        public static bool HasHotkey(Kind k) => HotkeyKey(k) != 0;
        public static string HotkeyText(Kind k) => HasHotkey(k) ? HotkeyManager.KeyText(HotkeyMods(k), HotkeyKey(k)) : "";

        public static void SetHotkey(Kind k, uint mods, uint vk)
        {
            SetInt(k + "Mods", (int)mods);
            SetInt(k + "Key", (int)vk);
            Refresh();
        }

        /// <summary>Video length in minutes (1 by default).</summary>
        public static int RecordMinutes
        {
            get => Math.Clamp(GetInt("RecordMinutes", 1), 1, 60);
            set => SetInt("RecordMinutes", Math.Clamp(value, 1, 60));
        }

        /// <summary>How much the replay keeps, in minutes (1 by default).</summary>
        public static int ReplayMinutes
        {
            get => Math.Clamp(GetInt("ReplayMinutes", 1), 1, 10);
            set { SetInt("ReplayMinutes", Math.Clamp(value, 1, 10)); Replay.Restart(); }
        }

        /// <summary>The background recording for "the last minute" (off by default).</summary>
        public static bool ReplayOn
        {
            get => GetInt("ReplayOn", 0) != 0;
            set { SetInt("ReplayOn", value ? 1 : 0); Refresh(); }
        }

        public static bool RecordSound
        {
            get => GetInt("Sound", 1) != 0;
            set { SetInt("Sound", value ? 1 : 0); Replay.Restart(); }
        }

        public static bool CopyPicture
        {
            get => GetInt("Copy", 1) != 0;
            set => SetInt("Copy", value ? 1 : 0);
        }

        public static int Fps
        {
            get => GetInt("Fps", 30) == 60 ? 60 : 30;
            set { SetInt("Fps", value == 60 ? 60 : 30); Replay.Restart(); }
        }

        /// <summary>A folder the user chose for everything, or "" for the Windows ones.</summary>
        public static string CustomFolder
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(OurKey);
                return key?.GetValue("Folder") is string f && f.Length > 0 && Directory.Exists(f) ? f : "";
            }
            set
            {
                using var key = Registry.CurrentUser.CreateSubKey(OurKey);
                if (string.IsNullOrEmpty(value)) key.DeleteValue("Folder", false);
                else key.SetValue("Folder", value);
            }
        }

        /// <summary>Pictures\Screenshots, like Windows' own Print Screen.</summary>
        public static string PictureFolder => CustomFolder.Length > 0 ? CustomFolder : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

        /// <summary>Videos\Screen Recordings, like the Snipping Tool.</summary>
        public static string VideoFolder => CustomFolder.Length > 0 ? CustomFolder : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Screen Recordings");

        public static bool HasFfmpeg => Converter.FindFfmpeg() != null;

        // ---- Shortcuts ----------------------------------------------------------------------

        private static HwndSource? _window;
        private static readonly bool[] Registered = new bool[4];
        private static readonly bool[] Failed = new bool[4];

        /// <summary>Did Windows refuse this shortcut (another program has it)?</summary>
        public static bool HotkeyFailed(Kind k) => Failed[(int)k];

        public static event Action? Changed;

        public static void Init(HwndSource window)
        {
            if (_window != null) return;
            _window = window;
            window.AddHook(WndProc);
        }

        /// <summary>Registers the shortcuts and starts / stops the replay to match the settings.</summary>
        public static void Refresh()
        {
            if (_window == null) return;
            Pause();
            bool on = Features.IsOn("capture");
            for (int i = 0; i < 4; i++)
            {
                Failed[i] = false;
                var k = (Kind)i;
                if (!on || !HasHotkey(k)) continue;
                Registered[i] = RegisterHotKey(_window.Handle, HotkeyBase + i, HotkeyMods(k) | 0x4000 /* MOD_NOREPEAT */, HotkeyKey(k));
                Failed[i] = !Registered[i];
            }
            if (on && ReplayOn && HasFfmpeg) Replay.Start();
            else Replay.Stop();
            if (!on) Recorder.Stop();
            Changed?.Invoke();
        }

        /// <summary>Lets go of the shortcuts for a moment (while a new one is being picked).</summary>
        public static void Pause()
        {
            if (_window == null) return;
            for (int i = 0; i < 4; i++)
            {
                if (Registered[i]) UnregisterHotKey(_window.Handle, HotkeyBase + i);
                Registered[i] = false;
            }
        }

        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int i = wParam.ToInt32() - HotkeyBase;
                if (i >= 0 && i < 4)
                {
                    handled = true;
                    Run((Kind)i);
                }
            }
            return IntPtr.Zero;
        }

        public static void Run(Kind kind)
        {
            try
            {
                switch (kind)
                {
                    case Kind.Region: TakeRegion(); break;
                    case Kind.Full: TakeFull(); break;
                    case Kind.Record:
                        if (Recorder.IsRecording) Recorder.Stop();
                        else if (!HasFfmpeg) CaptureToast.Show(Lang.T("تسجيل الفيديو يحتاج FFmpeg"), Lang.T("حمّله من Wibn ← تصوير الشاشة"));
                        else _ = Recorder.StartAsync();
                        break;
                    case Kind.Replay:
                        if (!HasFfmpeg) CaptureToast.Show(Lang.T("حفظ آخر دقيقة يحتاج FFmpeg"), Lang.T("حمّله من Wibn ← تصوير الشاشة"));
                        else if (!ReplayOn || !Replay.IsRunning)
                            CaptureToast.Show(Lang.T("التسجيل المستمر طافي"), Lang.T("شغّله من Wibn ← تصوير الشاشة عشان يحفظ آخر دقيقة"));
                        else _ = Replay.SaveAsync();
                        break;
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write("capture " + kind, ex);
            }
        }

        public static void StopAll()
        {
            Recorder.Stop();
            Replay.Stop();
        }

        // ---- Pictures ---------------------------------------------------------------------------

        private static bool _picking;

        private static void TakeFull()
        {
            Rectangle screen;
            Bitmap shot;
            using (new DpiScope())
            {
                screen = MonitorUnderCursor().Bounds;
                shot = Grab(screen);
            }
            using (shot) SavePicture(shot, CopyPicture);
        }

        private static void TakeRegion()
        {
            if (_picking) return;
            _picking = true;
            try
            {
                Bitmap part;
                RegionPicker.Choice choice;
                using (new DpiScope())
                {
                    var all = VirtualScreen();
                    using var frozen = Grab(all);
                    var windows = RegionPicker.ListWindows(all);
                    (Rectangle Area, RegionPicker.Choice Choice)? pick;
                    using (var picker = new RegionPicker(frozen, all, windows, MonitorUnderCursor().Bounds))
                    {
                        pick = picker.Pick();
                    }
                    if (pick is not { } p || p.Area.Width < 2 || p.Area.Height < 2) return;
                    var r = p.Area;
                    r.Offset(-all.X, -all.Y);
                    part = frozen.Clone(r, PixelFormat.Format32bppArgb);
                    choice = p.Choice;
                }
                using (part)
                {
                    switch (choice)
                    {
                        case RegionPicker.Choice.Copy: CopyPictureOnly(part); break;
                        case RegionPicker.Choice.SaveAs: SavePictureAs(part); break;
                        default: SavePicture(part, copy: false); break;
                    }
                }
            }
            finally
            {
                _picking = false;
            }
        }

        private static Bitmap Grab(Rectangle area)
        {
            var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(area.X, area.Y, 0, 0, area.Size, CopyPixelOperation.SourceCopy);
            return bmp;
        }

        /// <summary>Into the screenshots folder (and copied too if <paramref name="copy"/>).</summary>
        private static void SavePicture(Bitmap bmp, bool copy)
        {
            string folder = PictureFolder;
            Directory.CreateDirectory(folder);
            string path = UniquePath(folder, "Screenshot", ".png");
            bmp.Save(path, ImageFormat.Png);
            if (copy) copy = Copy(bmp);
            CaptureToast.ShowPicture(path, copy);
        }

        private static bool Copy(Bitmap bmp)
        {
            try { System.Windows.Forms.Clipboard.SetImage(bmp); return true; }
            catch (Exception ex) { CrashLog.Write("capture clipboard", ex); return false; }
        }

        private static void CopyPictureOnly(Bitmap bmp)
        {
            if (Copy(bmp)) CaptureToast.Show(Lang.T("انسخت الصورة"), Lang.T("تقدر تلصقها بأي مكان (Ctrl+V)"));
        }

        /// <summary>Asks where and in which format (PNG, JPG, BMP), then saves.</summary>
        private static void SavePictureAs(Bitmap bmp)
        {
            string folder = PictureFolder;
            Directory.CreateDirectory(folder);
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = Lang.T("حفظ الصورة باسم"),
                InitialDirectory = folder,
                FileName = System.IO.Path.GetFileNameWithoutExtension(UniquePath(folder, "Screenshot", ".png")),
                Filter = "PNG|*.png|JPG|*.jpg;*.jpeg|BMP|*.bmp",
                DefaultExt = ".png",
                AddExtension = true,
            };
            if (dlg.ShowDialog() != true) return;
            string path = dlg.FileName;
            var format = System.IO.Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => ImageFormat.Jpeg,
                ".bmp" => ImageFormat.Bmp,
                _ => ImageFormat.Png,
            };
            try
            {
                if (format == ImageFormat.Png) bmp.Save(path, format);
                else
                {
                    // No see-through in JPG / BMP: on a plain copy.
                    using var flat = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format24bppRgb);
                    using (var g = Graphics.FromImage(flat)) g.DrawImageUnscaled(bmp, 0, 0);
                    flat.Save(path, format);
                }
                CaptureToast.ShowPicture(path, false);
            }
            catch (Exception ex)
            {
                CrashLog.Write("capture save as", ex);
                CaptureToast.Show(Lang.T("ما انحفظت الصورة"), ex.Message);
            }
        }

        internal static string UniquePath(string folder, string prefix, string ext)
        {
            string name = $"{prefix} {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
            string path = Path.Combine(folder, name + ext);
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{name} ({i}){ext}");
            return path;
        }

        // ---- Screens (in real pixels) -------------------------------------------------------------

        internal readonly record struct Monitor(Rectangle Bounds, string Device);

        /// <summary>Real-pixel coordinates for the Win32 calls on this thread while it's alive
        /// (the app itself is only system-DPI aware).</summary>
        internal readonly struct DpiScope : IDisposable
        {
            private readonly IntPtr _old;
            public DpiScope() { _old = SetThreadDpiAwarenessContext(new IntPtr(-4) /* PER_MONITOR_AWARE_V2 */); }
            public void Dispose() { if (_old != IntPtr.Zero) SetThreadDpiAwarenessContext(_old); }
        }

        internal static Rectangle VirtualScreen() => new(
            GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));

        internal static Monitor MonitorUnderCursor()
        {
            GetCursorPos(out var pt);
            return MonitorInfo(MonitorFromPoint(pt, 2 /* NEAREST */));
        }

        internal static Monitor PrimaryMonitor() => MonitorInfo(MonitorFromPoint(default, 1 /* PRIMARY */));

        private static Monitor MonitorInfo(IntPtr monitor)
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            GetMonitorInfo(monitor, ref info);
            var r = info.rcMonitor;
            return new Monitor(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), info.szDevice);
        }

        // ---- Win32 ----------------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor, rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] internal static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
        [DllImport("user32.dll")] internal static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    }
}
