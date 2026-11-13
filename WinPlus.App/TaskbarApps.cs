using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinPlus
{
    /// <summary>
    /// What Wibn's taskbar shows: the open windows (the same ones Windows' taskbar would
    /// show), grouped by app, and the pinned apps. Also their icons (from the shell, so
    /// Store apps get their real icon too) and switching to / minimizing / closing a window.
    /// </summary>
    internal static class TaskbarApps
    {
        /// <summary>One open window.</summary>
        public sealed class Win
        {
            public IntPtr Hwnd;
            public string Title = "";
            public string Exe = "";       // full path, lower case ("" if it couldn't be read)
            public string? Aumid;         // the app id the window says it belongs to
            public bool Uwp;              // a Store app (hosted by ApplicationFrameHost)
            public bool Packaged;         // an installed-from-a-package app (its id comes from its process)
            public string Key => Aumid ?? Exe;
            /// <summary>Opened (and its icon found) by its app id, not by its program.</summary>
            public bool ById => (Uwp || Packaged) && Aumid != null;
        }

        /// <summary>One button: a pinned app and/or the windows of one app.</summary>
        public sealed class Group
        {
            public string Key = "";
            public Pin? Pin;
            public readonly List<Win> Windows = new();
            public string Name = "";
            public string IconSource = "";   // a path or shell:AppsFolder\id for the icon
            public string Launch = "";       // what opens a new one
        }

        /// <summary>A pinned app: a shortcut (.lnk), a program (.exe) or a Store app (shell:AppsFolder\id).</summary>
        public sealed class Pin
        {
            public string Path = "";
            public string? Target;     // the program it opens, lower case
            public string? Aumid;
            public string Name = "";
        }

        // ---- Pins -------------------------------------------------------------------------------

        private const string Key = @"Software\WinPlus\CustomTaskbar";
        private static List<Pin>? _pins;

        public static List<Pin> Pins
        {
            get
            {
                if (_pins != null) return _pins;
                string[]? saved = null;
                bool packagedDone = false;
                try
                {
                    using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key);
                    saved = k?.GetValue("Pins") as string[];
                    packagedDone = k?.GetValue("PinsPackaged") is int done && done == 1;
                }
                catch { }
                saved ??= ImportWindowsPins();
                if (!packagedDone)
                {
                    // Store / packaged apps pinned to Windows' taskbar (they aren't shortcuts): once.
                    var withPackaged = saved.ToList();
                    AddPackagedPins(withPackaged);
                    saved = withPackaged.ToArray();
                    try { using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key); k.SetValue("PinsPackaged", 1); } catch { }
                }
                _pins = saved.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).Select(MakePin).ToList();
                if (saved.Length > 0) SavePins();
                return _pins;
            }
        }

        public static void SavePins()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key);
                k.SetValue("Pins", (_pins ?? new()).Select(p => p.Path).ToArray(), Microsoft.Win32.RegistryValueKind.MultiString);
            }
            catch { }
        }

        /// <summary>The first time: the apps already pinned to Windows' taskbar.</summary>
        private static string[] ImportWindowsPins()
        {
            try
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
                if (!Directory.Exists(dir)) return Array.Empty<string>();
                var files = Directory.GetFiles(dir, "*.lnk").ToList();
                // File Explorer first, like on Windows' own taskbar.
                files.Sort((a, b) =>
                {
                    bool ea = IsExplorerLink(a), eb = IsExplorerLink(b);
                    return ea != eb ? (ea ? -1 : 1) : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
                });
                return files.ToArray();
            }
            catch { return Array.Empty<string>(); }
        }

        private static bool IsExplorerLink(string lnk)
        {
            var (target, aumid) = LinkInfo(lnk);
            return (target ?? "").EndsWith(@"\explorer.exe", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(aumid, "Microsoft.Windows.Explorer", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Windows keeps Store / packaged apps pinned to its taskbar (Claude, Terminal...) in the registry,
        /// not as shortcuts: their ids, each with the shortcut pinned just before it (null if first).
        /// </summary>
        private static List<(string Aumid, string? After)> WindowsPackagedPins()
        {
            var list = new List<(string, string?)>();
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband");
                if (k?.GetValue("Favorites") is not byte[] blob) return list;
                var token = new System.Text.RegularExpressions.Regex(
                    @"[A-Za-z0-9][A-Za-z0-9.\-]*_[a-z0-9]{13}![A-Za-z0-9.\-]+|[^\x00-\x1f\\/:*?""<>|]+\.lnk");
                // The text can start on an odd byte, so read it both ways.
                for (int shift = 0; shift < 2; shift++)
                {
                    // Each reading on its own: a name read the other way round is just noise.
                    string text = System.Text.Encoding.Unicode.GetString(blob, shift, blob.Length - shift);
                    string? last = null;
                    foreach (System.Text.RegularExpressions.Match m in token.Matches(text))
                    {
                        string t = m.Value;
                        if (t.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) last = t;
                        else if (!list.Any(x => string.Equals(x.Item1, t, StringComparison.OrdinalIgnoreCase))) list.Add((t, last));
                    }
                }
            }
            catch { }
            return list;
        }

        /// <summary>Adds Windows' pinned packaged apps that aren't pinned here yet (once), next to their old neighbors.</summary>
        private static void AddPackagedPins(List<string> pins)
        {
            foreach (var (aumid, after) in WindowsPackagedPins())
            {
                string path = @"shell:AppsFolder\" + aumid;
                if (pins.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) continue;
                if (DisplayName(path) == null) continue;   // not installed any more
                int at = 0;
                if (after != null)
                {
                    // The name may carry stray letters in front (it's read out of binary data).
                    int i = pins.FindIndex(p => p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) &&
                                               after.EndsWith(System.IO.Path.GetFileName(p), StringComparison.OrdinalIgnoreCase));
                    if (i >= 0) at = i + 1;
                }
                pins.Insert(at, path);
            }
        }

        public static Pin MakePin(string path)
        {
            var pin = new Pin { Path = path };
            if (path.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase))
            {
                pin.Aumid = path.Substring(@"shell:AppsFolder\".Length);
                pin.Name = DisplayName(path) ?? pin.Aumid;
                pin.Target = AumidPath(pin.Aumid);   // a normal program listed in Start by its path
            }
            else if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var (target, aumid) = LinkInfo(path);
                pin.Target = PlainPath(target) ?? AumidPath(aumid);
                if (string.Equals(aumid, "Microsoft.Windows.Explorer", StringComparison.OrdinalIgnoreCase))
                    pin.Target = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe").ToLowerInvariant();
                pin.Aumid = aumid;
                pin.Name = System.IO.Path.GetFileNameWithoutExtension(path);
            }
            else
            {
                pin.Target = path.ToLowerInvariant();
                pin.Name = FileDescription(path) ?? System.IO.Path.GetFileNameWithoutExtension(path);
            }
            return pin;
        }

        public static bool IsPinned(Group g) => g.Pin != null;

        public static void PinGroup(Group g)
        {
            if (g.Pin != null) return;
            var w = g.Windows.FirstOrDefault();
            if (w == null) return;
            string path = w.ById ? @"shell:AppsFolder\" + w.Aumid : w.Exe;
            if (string.IsNullOrEmpty(path)) return;
            var pin = MakePin(path);
            if (!w.ById) pin.Aumid ??= w.Aumid;
            Pins.Add(pin);
            SavePins();
        }

        public static void AddPin(string path)
        {
            if (Pins.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase))) return;
            Pins.Add(MakePin(path));
            SavePins();
        }

        public static void Unpin(Pin pin)
        {
            Pins.Remove(pin);
            SavePins();
        }

        public static void MovePin(Pin pin, int index)
        {
            var list = Pins;
            if (!list.Remove(pin)) return;
            list.Insert(Math.Clamp(index, 0, list.Count), pin);
            SavePins();
        }

        // ---- Windows ----------------------------------------------------------------------------

        private static readonly Dictionary<IntPtr, Win> _known = new();
        private static readonly int _myPid = Environment.ProcessId;

        /// <summary>Our own bar and preview windows: never shown as apps.</summary>
        public static readonly HashSet<IntPtr> Ours = new();

        /// <summary>The open windows, in the order they first appeared.</summary>
        public static List<Win> OpenWindows()
        {
            var seen = new List<IntPtr>();
            EnumWindows((h, _) => { if (Shows(h)) seen.Add(h); return true; }, IntPtr.Zero);

            var alive = new HashSet<IntPtr>(seen);
            foreach (var dead in _known.Keys.Where(h => !alive.Contains(h)).ToList()) _known.Remove(dead);

            var result = new List<Win>();
            // Keep the old order; new windows go at the end.
            foreach (var w in _known.Values) w.Title = Title(w.Hwnd);
            foreach (var h in seen)
            {
                if (_known.ContainsKey(h)) continue;
                _known[h] = Describe(h);
            }
            result.AddRange(_known.Values);
            return result;
        }

        /// <summary>The buttons: pins first (in their order), then the other running apps.</summary>
        public static List<Group> Groups()
        {
            var groups = new List<Group>();
            var byKey = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
            foreach (var pin in Pins)
            {
                var g = new Group
                {
                    Key = "pin:" + pin.Path, Pin = pin, Name = pin.Name,
                    IconSource = pin.Path, Launch = pin.Path,
                };
                groups.Add(g);
            }
            foreach (var w in OpenWindows())
            {
                // Its own pin first (same app id), then the pin of the same program.
                var pinned = groups.FirstOrDefault(g => g.Pin != null && SameId(g.Pin, w))
                          ?? groups.FirstOrDefault(g => g.Pin != null && Matches(g.Pin, w));
                if (pinned != null) { pinned.Windows.Add(w); continue; }
                if (!byKey.TryGetValue(w.Key, out var g2))
                {
                    g2 = new Group
                    {
                        Key = w.Key,
                        Name = w.ById ? (DisplayName(@"shell:AppsFolder\" + w.Aumid) ?? w.Title)
                                     : (FileDescription(w.Exe) ?? System.IO.Path.GetFileNameWithoutExtension(w.Exe)),
                        IconSource = w.ById ? @"shell:AppsFolder\" + w.Aumid : w.Exe,
                        Launch = w.ById ? @"shell:AppsFolder\" + w.Aumid : w.Exe,
                    };
                    byKey[w.Key] = g2;
                    groups.Add(g2);
                }
                g2.Windows.Add(w);
            }
            return groups;
        }

        private static bool SameId(Pin pin, Win w) =>
            pin.Aumid != null && w.Aumid != null && string.Equals(pin.Aumid, w.Aumid, StringComparison.OrdinalIgnoreCase);

        /// <summary>Is this window the pinned app's (so it goes on the pin, not on a second button)?</summary>
        private static bool Matches(Pin pin, Win w)
        {
            if (SameId(pin, w)) return true;
            if (pin.Aumid != null && w.Aumid != null)
            {
                // Different app ids: only the same app with a longer id (e.g. "Chrome" and
                // "Chrome.UserData.Profile1"), never a web app made with it ("…_crx_…").
                if (!w.Aumid.StartsWith(pin.Aumid + ".", StringComparison.OrdinalIgnoreCase) ||
                    w.Aumid.Contains("_crx_", StringComparison.OrdinalIgnoreCase)) return false;
            }
            if (w.Exe.Length == 0) return false;
            if (pin.Target != null)
            {
                if (pin.Target == w.Exe) return true;
                // A launcher next to the program (Discord's Update.exe opens app-1.2\Discord.exe).
                string dir = Path.GetDirectoryName(pin.Target) ?? "";
                if (OwnFolder(dir) && w.Exe.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase) &&
                    pin.Target.EndsWith("update.exe")) return true;
                if (pin.Target.EndsWith(".exe")) return false;
            }
            // No program to compare (an installer's shortcut): the same name.
            string pinName = pin.Name.Trim();
            return pinName.Length > 2 && (
                string.Equals(FileDescription(w.Exe), pinName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileNameWithoutExtension(w.Exe), pinName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>A program's own folder, not a shared one (Windows, Program Files, AppData…).</summary>
        private static bool OwnFolder(string dir)
        {
            if (dir.Length < 4) return false;
            string[] shared =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            };
            foreach (var s in shared)
            {
                if (s.Length == 0) continue;
                if (string.Equals(dir, s, StringComparison.OrdinalIgnoreCase)) return false;
                if (s == shared[0] && dir.StartsWith(s + "\\", StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        /// <summary>A shortcut's target as a full path, lower case ("{known folder}\..." resolved; a shell name like "File Explorer" gives null).</summary>
        private static string? PlainPath(string? target)
        {
            if (string.IsNullOrEmpty(target)) return null;
            if (target.StartsWith("{")) return AumidPath(target);
            return System.IO.Path.IsPathRooted(target) ? target.ToLowerInvariant() : null;
        }

        /// <summary>
        /// Normal programs in Start have their path as app id, sometimes with a known folder in front
        /// ("{6D809377-…}\Google\Chrome\Application\chrome.exe"): the program's full path, lower case.
        /// </summary>
        private static string? AumidPath(string? aumid)
        {
            if (string.IsNullOrEmpty(aumid) || !aumid.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                if (aumid.StartsWith("{") && aumid.IndexOf('}') is int end and > 0 && Guid.TryParse(aumid.Substring(0, end + 1), out var id))
                {
                    if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out IntPtr p) != 0) return null;
                    try { return Path.Combine(Marshal.PtrToStringUni(p)!, aumid.Substring(end + 1).TrimStart('\\')).ToLowerInvariant(); }
                    finally { Marshal.FreeCoTaskMem(p); }
                }
                return Path.IsPathRooted(aumid) ? aumid.ToLowerInvariant() : null;
            }
            catch { return null; }
        }

        /// <summary>A packaged app's id, from its process (null for normal programs).</summary>
        private static string? ProcessAumid(uint pid)
        {
            IntPtr p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (p == IntPtr.Zero) return null;
            try
            {
                int len = 512;
                var sb = new StringBuilder(len);
                return GetApplicationUserModelId(p, ref len, sb) == 0 && sb.Length > 0 ? sb.ToString() : null;
            }
            catch { return null; }
            finally { CloseHandle(p); }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(IntPtr process, ref int length, StringBuilder id);

        [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

        /// <summary>Would Windows' taskbar show this window?</summary>
        private static bool Shows(IntPtr h)
        {
            if (!IsWindowVisible(h) || Ours.Contains(h)) return false;
            long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            IntPtr owner = GetWindow(h, GW_OWNER);
            bool app = (ex & WS_EX_APPWINDOW) != 0;
            if (!app)
            {
                if ((ex & WS_EX_TOOLWINDOW) != 0 || (ex & WS_EX_NOACTIVATE) != 0) return false;
                if (owner != IntPtr.Zero) return false;
            }
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return false;
            string cls = ClassOf(h);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow") return false;
            if (GetWindowTextLength(h) == 0) return false;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == _myPid && !IsOurMainWindow(h)) return false;
            return true;
        }

        private static bool IsOurMainWindow(IntPtr h) =>
            Application.Current?.MainWindow is { } m && new System.Windows.Interop.WindowInteropHelper(m).Handle == h;

        private static Win Describe(IntPtr h)
        {
            var w = new Win { Hwnd = h, Title = Title(h), Aumid = WindowAumid(h) };
            GetWindowThreadProcessId(h, out uint pid);
            w.Exe = ProcessPath(pid).ToLowerInvariant();
            if (ClassOf(h) == "ApplicationFrameWindow" || w.Exe.EndsWith(@"\applicationframehost.exe"))
            {
                w.Uwp = true;
                // The real app runs in a child window's process.
                IntPtr child = FindWindowEx(h, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
                if (child != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(child, out uint cpid);
                    string real = ProcessPath(cpid).ToLowerInvariant();
                    if (real.Length > 0) w.Exe = real;
                }
            }
            if (w.Aumid == null && ProcessAumid(pid) is { } id) { w.Aumid = id; w.Packaged = true; }
            if (!w.Uwp) w.Exe = MainProgram(pid, w.Exe);
            return w;
        }

        /// <summary>
        /// Some apps draw their window from a helper program (Steam's window is steamwebhelper.exe,
        /// started by steam.exe): the app is the program that started it, if that sits in a folder
        /// above the helper's. Its name, icon and what opens it come from there.
        /// </summary>
        private static string MainProgram(uint pid, string exe)
        {
            if (exe.Length == 0 || !Path.GetFileNameWithoutExtension(exe).Contains("helper")) return exe;
            try
            {
                uint parent = ParentPid(pid);
                if (parent == 0) return exe;
                string main = ProcessPath(parent).ToLowerInvariant();
                string dir = Path.GetDirectoryName(main) ?? "";
                if (main.Length == 0 || main == exe || !OwnFolder(dir) || !exe.StartsWith(dir + "\\")) return exe;
                return main;
            }
            catch { return exe; }
        }

        private static uint ParentPid(uint pid)
        {
            IntPtr snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return 0;
            try
            {
                var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                for (bool ok = Process32First(snap, ref e); ok; ok = Process32Next(snap, ref e))
                    if (e.th32ProcessID == pid) return e.th32ParentProcessID;
                return 0;
            }
            finally { CloseHandle(snap); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 e);

        public static string Title(IntPtr h)
        {
            int n = GetWindowTextLength(h);
            if (n <= 0) return "";
            var sb = new StringBuilder(n + 1);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string ClassOf(IntPtr h)
        {
            var sb = new StringBuilder(64);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static readonly Dictionary<uint, string> _paths = new();

        public static string ProcessPathOf(uint pid) => ProcessPath(pid);

        private static string ProcessPath(uint pid)
        {
            string? p;
            lock (_paths) if (_paths.TryGetValue(pid, out p)) return p;   // also used by the tray thread
            p = "";
            IntPtr hp = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hp != IntPtr.Zero)
            {
                var sb = new StringBuilder(1024);
                int len = sb.Capacity;
                if (QueryFullProcessImageName(hp, 0, sb, ref len)) p = sb.ToString();
                CloseHandle(hp);
            }
            lock (_paths)
            {
                if (_paths.Count > 500) _paths.Clear();
                _paths[pid] = p;
            }
            return p;
        }

        // ---- Doing things -----------------------------------------------------------------------

        public static IntPtr Foreground() => GetForegroundWindow();

        public static bool IsMinimized(IntPtr h) => IsIconic(h);

        /// <summary>Brings a window to the front (restoring it if it's minimized).</summary>
        public static void Activate(IntPtr h)
        {
            if (IsIconic(h)) ShowWindowAsync(h, SW_RESTORE);
            if (SetForegroundWindow(h) && GetForegroundWindow() == h) return;
            // Windows only lets the app that got the last input change the front window;
            // an empty key press (no real key) counts as ours without doing anything.
            keybd_event(0, 0, 0, UIntPtr.Zero);
            keybd_event(0, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            SetForegroundWindow(h);
        }

        public static void Minimize(IntPtr h) => PostMessage(h, WM_SYSCOMMAND, (IntPtr)SC_MINIMIZE, IntPtr.Zero);

        public static void Close(IntPtr h) => PostMessage(h, WM_SYSCOMMAND, (IntPtr)SC_CLOSE, IntPtr.Zero);

        public static void Launch(string what)
        {
            if (string.IsNullOrEmpty(what)) return;
            try
            {
                if (what.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + what + "\"") { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo(what) { UseShellExecute = true });
            }
            catch (Exception ex) { CrashLog.Write("taskbar launch", ex); }
        }

        // ---- Icons ------------------------------------------------------------------------------

        private static readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The icon of a program, shortcut or Store app (cached).</summary>
        public static ImageSource? Icon(string source, IntPtr windowFallback = default)
        {
            if (string.IsNullOrEmpty(source)) return WindowIcon(windowFallback);
            if (_icons.TryGetValue(source, out var cached)) return cached ?? WindowIcon(windowFallback);
            ImageSource? img = null;
            try { img = ShellImage(source, 64); } catch { }
            if (_icons.Count >= 200) _icons.Clear();   // a limit: icons still in use are taken again when needed
            _icons[source] = img;
            return img ?? WindowIcon(windowFallback);
        }

        private static ImageSource? WindowIcon(IntPtr h)
        {
            if (h == IntPtr.Zero) return null;
            try
            {
                SendMessageTimeout(h, WM_GETICON, (IntPtr)ICON_BIG, IntPtr.Zero, SMTO_ABORTIFHUNG, 100, out IntPtr icon);
                if (icon == IntPtr.Zero) icon = GetClassLongPtr(h, GCLP_HICON);
                if (icon == IntPtr.Zero) return null;
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
        }

        private static ImageSource? ShellImage(string path, int size)
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IShellItemImageFactory factory) != 0 || factory == null) return null;
            try
            {
                if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY, out IntPtr hbmp) != 0 || hbmp == IntPtr.Zero)
                    return null;
                try { return FromHBitmap(hbmp); }
                finally { DeleteObject(hbmp); }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }

        /// <summary>A shell bitmap with its see-through parts kept (WPF's own way loses them).</summary>
        private static BitmapSource? FromHBitmap(IntPtr hbmp)
        {
            var bm = new BITMAP();
            if (GetObject(hbmp, Marshal.SizeOf<BITMAP>(), ref bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0) return null;
            int w = bm.bmWidth, h = bm.bmHeight;
            var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var pixels = new byte[w * h * 4];
            IntPtr dc = GetDC(IntPtr.Zero);
            try { if (GetDIBits(dc, hbmp, 0, (uint)h, pixels, ref bi, 0) == 0) return null; }
            finally { ReleaseDC(IntPtr.Zero, dc); }
            bool anyAlpha = false;
            for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { anyAlpha = true; break; }
            if (!anyAlpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, w * 4);
            src.Freeze();
            return src;
        }

        // ---- Names ------------------------------------------------------------------------------

        private static readonly Dictionary<string, string?> _names = new(StringComparer.OrdinalIgnoreCase);

        private static string? FileDescription(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return null;
            if (_names.TryGetValue(exe, out var n)) return n;
            try
            {
                var v = FileVersionInfo.GetVersionInfo(exe);
                n = string.IsNullOrWhiteSpace(v.FileDescription) ? null : v.FileDescription.Trim();
            }
            catch { n = null; }
            _names[exe] = n;
            return n;
        }

        private static string? DisplayName(string parsingName)
        {
            if (_names.TryGetValue(parsingName, out var n)) return n;
            n = null;
            try
            {
                var iid = typeof(IShellItem).GUID;
                if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out IShellItem item) == 0 && item != null)
                {
                    if (item.GetDisplayName(SIGDN_NORMALDISPLAY, out IntPtr p) == 0 && p != IntPtr.Zero)
                    {
                        n = Marshal.PtrToStringUni(p);
                        Marshal.FreeCoTaskMem(p);
                    }
                    Marshal.ReleaseComObject(item);
                }
            }
            catch { }
            _names[parsingName] = n;
            return n;
        }

        // ---- Property stores (a window's app id, a shortcut's target) ----------------------------

        private static readonly PROPERTYKEY PKEY_AppUserModel_ID = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
        private static readonly PROPERTYKEY PKEY_Link_TargetParsingPath = new(new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), 2);

        private static string? WindowAumid(IntPtr h)
        {
            try
            {
                var iid = typeof(IPropertyStore).GUID;
                if (SHGetPropertyStoreForWindow(h, ref iid, out IPropertyStore store) != 0 || store == null) return null;
                try { return ReadString(store, PKEY_AppUserModel_ID); }
                finally { Marshal.ReleaseComObject(store); }
            }
            catch { return null; }
        }

        private static (string? Target, string? Aumid) LinkInfo(string lnk)
        {
            try
            {
                var iid = typeof(IPropertyStore).GUID;
                if (SHGetPropertyStoreFromParsingName(lnk, IntPtr.Zero, 0, ref iid, out IPropertyStore store) != 0 || store == null) return (null, null);
                try { return (ReadString(store, PKEY_Link_TargetParsingPath), ReadString(store, PKEY_AppUserModel_ID)); }
                finally { Marshal.ReleaseComObject(store); }
            }
            catch { return (null, null); }
        }

        private static string? ReadString(IPropertyStore store, PROPERTYKEY key)
        {
            var k = key;
            var pv = new PROPVARIANT();
            if (store.GetValue(ref k, out pv) != 0) return null;
            try
            {
                if (pv.vt == 31 /* VT_LPWSTR */ && pv.ptr != IntPtr.Zero)
                {
                    var s = Marshal.PtrToStringUni(pv.ptr);
                    return string.IsNullOrWhiteSpace(s) ? null : s;
                }
                return null;
            }
            finally { PropVariantClear(ref pv); }
        }

        // ---- Native --------------------------------------------------------------------------------

        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000;
        private const uint GW_OWNER = 4;
        private const int DWMWA_CLOAKED = 14;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int SW_RESTORE = 9;
        private const uint WM_SYSCOMMAND = 0x0112, WM_GETICON = 0x007F;
        private const int SC_MINIMIZE = 0xF020, SC_CLOSE = 0xF060;
        private const int ICON_BIG = 1, GCLP_HICON = -14;
        private const uint SMTO_ABORTIFHUNG = 2;
        private const uint KEYEVENTF_KEYUP = 2;
        private const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;
        private const int SIGDN_NORMALDISPLAY = 0;

        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr l);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] private static extern IntPtr GetClassLongPtr(IntPtr h, int index);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr h, int cmd);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr hp, int flags, StringBuilder sb, ref int size);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, ref BITMAP bm);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr bc, ref Guid riid, out IShellItemImageFactory item);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr bc, ref Guid riid, out IShellItem item);
        [DllImport("shell32.dll", PreserveSig = true)]
        private static extern int SHGetPropertyStoreForWindow(IntPtr h, ref Guid riid, out IPropertyStore store);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bc, int flags, ref Guid riid, out IPropertyStore store);
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT pv);

        [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PROPERTYKEY
        {
            public Guid fmtid; public int pid;
            public PROPERTYKEY(Guid g, int p) { fmtid = g; pid = p; }
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PROPVARIANT
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr ptr;
        }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr bc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetParent(out IShellItem parent);
            [PreserveSig] int GetDisplayName(int sigdn, out IntPtr name);
            [PreserveSig] int GetAttributes(uint mask, out uint attrs);
            [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
            [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
            [PreserveSig] int Commit();
        }
    }
}
