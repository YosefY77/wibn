using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// Sorts the Downloads folder into sub-folders by file type.
    ///   * "Sort now" moves every loose file (and can be undone).
    ///   * Automatic mode watches the folder (FileSystemWatcher, no polling) and moves
    ///     each new file once its download has finished.
    /// Only files directly in Downloads are touched; folders, hidden files and
    /// files that are still downloading are left alone. Same-named files never
    /// overwrite each other ("name (1).ext").
    /// </summary>
    internal static class DownloadsOrganizer
    {
        public sealed record Category(string Folder, string Icon, string[] Extensions);

        public static readonly Category[] Categories =
        {
            new(Lang.T("صور"), "", new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".tif", ".tiff", ".svg", ".ico", ".avif", ".raw", ".cr2", ".nef" }),
            new(Lang.T("فيديو"), "", new[] { ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".flv", ".m4v", ".3gp", ".mpg", ".mpeg" }),
            new(Lang.T("صوتيات"), "", new[] { ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma", ".opus", ".mid" }),
            new(Lang.T("مستندات"), "", new[] { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".rtf", ".odt", ".ods", ".csv", ".md", ".epub" }),
            new(Lang.T("ملفات مضغوطة"), "\uE7B8", new[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso" }),
            new(Lang.T("برامج"), "", new[] { ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".bat", ".cmd", ".apk" }),
            new(Lang.T("تصاميم"), "", new[] { ".psd", ".ai", ".aep", ".prproj", ".xd", ".fig", ".blend", ".sketch", ".indd", ".eps", ".ttf", ".otf" }),
        };

        // Still being downloaded by a browser / tool: never move these.
        private static readonly string[] PartialExtensions =
            { ".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload", ".!ut", ".aria2" };

        private const string OurKey = @"Software\WinPlus\TaskbarScroll";

        public static string DownloadsFolder
        {
            get
            {
                try
                {
                    var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
                    if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out IntPtr p) == 0)
                    {
                        string path = Marshal.PtrToStringUni(p)!;
                        Marshal.FreeCoTaskMem(p);
                        return path;
                    }
                }
                catch { }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            }
        }

        // ---- Settings ---------------------------------------------------------------

        public static bool AutoEnabled
        {
            get => GetInt("DlAuto", 0) != 0;
            set { SetInt("DlAuto", value ? 1 : 0); if (value) Start(); else Stop(); }
        }

        /// <summary>Which categories are used (bit per category, all on by default).</summary>
        public static bool IsCategoryOn(int index) => (GetInt("DlCategories", -1) & (1 << index)) != 0;

        public static void SetCategory(int index, bool on)
        {
            int mask = GetInt("DlCategories", -1);
            mask = on ? mask | (1 << index) : mask & ~(1 << index);
            SetInt("DlCategories", mask);
        }

        private static Category? CategoryFor(string file)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            for (int i = 0; i < Categories.Length; i++)
            {
                if (IsCategoryOn(i) && Categories[i].Extensions.Contains(ext)) return Categories[i];
            }
            return null;
        }

        // ---- Sort now / undo ----------------------------------------------------------

        public sealed record Move(string From, string To);

        /// <summary>Moves every loose file of a folder (Downloads by default) into
        /// type folders inside it. Returns how many moved per folder.</summary>
        public static Dictionary<string, int> SortNow(string? folder = null)
        {
            var counts = new Dictionary<string, int>();
            var moves = new List<Move>();
            string root = folder ?? DownloadsFolder;
            if (IsProtectedFolder(root)) throw new UnauthorizedAccessException(Lang.T("هذا مجلد نظام وما يترتب."));
            foreach (var file in Directory.EnumerateFiles(root))
            {
                var move = TryMove(file);
                if (move == null) continue;
                moves.Add(move);
                string category = Path.GetFileName(Path.GetDirectoryName(move.To)!);
                counts[category] = counts.GetValueOrDefault(category) + 1;
            }
            if (moves.Count > 0) SaveUndo(moves);
            return counts;
        }

        /// <summary>A drive's root or Windows' own folders: never sorted.</summary>
        public static bool IsProtectedFolder(string path)
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            string? root = Path.GetPathRoot(full);
            if (root != null && string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase))
                return true;
            foreach (var sp in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
                                       Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.UserProfile,
                                       Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
                                       Environment.SpecialFolder.CommonApplicationData })
            {
                string p = Environment.GetFolderPath(sp);
                if (p.Length > 0 && full.StartsWith(Path.TrimEndingDirectorySeparator(p), StringComparison.OrdinalIgnoreCase) &&
                    (sp != Environment.SpecialFolder.UserProfile || full.Length == Path.TrimEndingDirectorySeparator(p).Length))
                    return true;
            }
            return false;
        }

        // ---- Right-click on a folder: "ترتيب الملفات" ------------------------------------

        private const string DirVerb = @"Software\Classes\Directory\shell\WinPlusSort";
        private const string BackVerb = @"Software\Classes\Directory\Background\shell\WinPlusSort";

        /// <summary>Adds "ترتيب الملفات (WinPlus)" to the right-click menu of folders
        /// (and of a folder's empty space). Called on every start, so it follows the exe.</summary>
        public static void RegisterShell()
        {
            string? exe = Environment.ProcessPath;
            if (exe == null) return;
            try
            {
                foreach (var (path, arg) in new[] { (DirVerb, "%1"), (BackVerb, "%V") })
                {
                    using var verb = Registry.CurrentUser.CreateSubKey(path);
                    verb.SetValue("", Lang.T("ترتيب الملفات حسب النوع (Wibn)"));
                    verb.SetValue("Icon", "%SystemRoot%\\System32\\shell32.dll,4", RegistryValueKind.ExpandString);
                    using var cmd = verb.CreateSubKey("command");
                    cmd.SetValue("", $"\"{exe}\" --sort \"{arg}\"");
                }
            }
            catch { }
        }

        public static void UnregisterShell()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(DirVerb, false);
                Registry.CurrentUser.DeleteSubKeyTree(BackVerb, false);
            }
            catch { }
        }

        public static bool CanUndo => File.Exists(UndoFile);

        /// <summary>Puts the files of the last "sort now" back. Returns how many.</summary>
        public static int Undo()
        {
            if (!File.Exists(UndoFile)) return 0;
            int n = 0;
            try
            {
                var moves = JsonSerializer.Deserialize<List<Move>>(File.ReadAllText(UndoFile)) ?? new();
                foreach (var m in moves)
                {
                    try
                    {
                        if (!File.Exists(m.To)) continue;  // moved or deleted since
                        File.Move(m.To, UniquePath(m.From));
                        n++;
                    }
                    catch { }
                }
                // Remove category folders that are empty again.
                foreach (var dir in moves.Select(m => Path.GetDirectoryName(m.To)!).Distinct())
                {
                    try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
                }
            }
            finally
            {
                try { File.Delete(UndoFile); } catch { }
            }
            return n;
        }

        private static string UndoFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPlus", "downloads-undo.json");

        private static void SaveUndo(List<Move> moves)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(UndoFile)!);
                File.WriteAllText(UndoFile, JsonSerializer.Serialize(moves));
            }
            catch { }
        }

        private static Move? TryMove(string file)
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) return null;
                string ext = info.Extension.ToLowerInvariant();
                if (PartialExtensions.Contains(ext) || info.Name.StartsWith("~$") || info.Name.Contains(".wpconv")) return null;
                var cat = CategoryFor(file);
                if (cat == null) return null;

                string dir = Path.Combine(info.DirectoryName!, cat.Folder);
                Directory.CreateDirectory(dir);
                string target = UniquePath(Path.Combine(dir, info.Name));
                File.Move(file, target);  // same drive: instant, and fails if it's still open
                return new Move(file, target);
            }
            catch
            {
                return null;  // in use / no access: leave it
            }
        }

        private static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path)!;
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 1; ; i++)
            {
                string p = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(p)) return p;
            }
        }

        // ---- Automatic -------------------------------------------------------------------

        private static FileSystemWatcher? _watcher;
        private static DispatcherTimer? _timer;
        // New files waiting for their download to finish: path → last size / when it changed.
        private static readonly Dictionary<string, (long size, DateTime since)> _pending = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new();

        public static event Action<string>? Moved;  // the page shows it

        /// <summary>Called at app start (and when switched on).</summary>
        public static void Start()
        {
            if (_watcher != null || !AutoEnabled) return;
            try
            {
                string root = DownloadsFolder;
                if (!Directory.Exists(root)) return;
                _watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                    InternalBufferSize = 16384,
                };
                _watcher.Created += (_, e) => Track(e.FullPath);
                _watcher.Renamed += (_, e) => Track(e.FullPath);  // "x.crdownload" → "x.zip" when done
                _watcher.Changed += (_, e) => Track(e.FullPath);
                _watcher.EnableRaisingEvents = true;

                // Checks waiting files once a second, only while there are any.
                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _timer.Tick += (_, _) => CheckPending();
            }
            catch
            {
                Stop();
            }
        }

        public static void Stop()
        {
            _watcher?.Dispose();
            _watcher = null;
            _timer?.Stop();
            _timer = null;
            lock (_lock) _pending.Clear();
        }

        // Files put here on purpose by another Wibn feature (a downloaded video):
        // not sorted away. Kept for a minute.
        private static readonly Dictionary<string, DateTime> _skip = new(StringComparer.OrdinalIgnoreCase);

        public static void Skip(string path)
        {
            lock (_lock)
            {
                foreach (var old in _skip.Where(kv => (DateTime.Now - kv.Value).TotalMinutes > 1).Select(kv => kv.Key).ToList())
                    _skip.Remove(old);
                _skip[path] = DateTime.Now;
            }
        }

        private static void Track(string path)
        {
            lock (_lock) if (_skip.ContainsKey(path)) return;
            if (!File.Exists(path) || path.Contains(".wpconv")) return;  // a conversion in progress
            if (PartialExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())) return;
            if (CategoryFor(path) == null) return;
            lock (_lock) _pending[path] = (-1, DateTime.Now);
            // Watcher events come on a worker thread; the timer lives on the UI thread.
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => _timer?.Start());
        }

        private static void CheckPending()
        {
            List<string> ready = new();
            lock (_lock)
            {
                foreach (var path in _pending.Keys.ToList())
                {
                    long size;
                    try { size = new FileInfo(path).Length; }
                    catch { _pending.Remove(path); continue; }  // gone
                    var (last, since) = _pending[path];
                    if (size != last) { _pending[path] = (size, DateTime.Now); continue; }
                    // Same size for 3 seconds: the download is done.
                    if ((DateTime.Now - since).TotalSeconds >= 3) { ready.Add(path); _pending.Remove(path); }
                }
                if (_pending.Count == 0) _timer?.Stop();
            }
            foreach (var path in ready)
            {
                var move = TryMove(path);
                if (move != null) Moved?.Invoke(move.To);
                else if (File.Exists(path)) lock (_lock) _pending[path] = (-1, DateTime.Now);  // still open: retry
            }
            if (ready.Count > 0) lock (_lock) if (_pending.Count > 0) _timer?.Start();
        }

        // ---- Helpers ------------------------------------------------------------------------

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

        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags,
                                                       IntPtr token, out IntPtr path);
    }
}
