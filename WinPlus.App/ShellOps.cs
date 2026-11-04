using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace WinPlus
{
    /// <summary>Recycle Bin, sizes and safe deletes, shared by the cleanup views.</summary>
    internal static class ShellOps
    {
        public static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string? pTo;
            public ushort fFlags;
            public int fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string? lpszProgressTitle;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

        private const uint FO_DELETE = 3;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_ALLOWUNDO = 0x0040;        // → Recycle Bin
        private const ushort FOF_WANTNUKEWARNING = 0x4000;  // warn if a file can't go to the bin
        private const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;

        /// <summary>Sends files to the Recycle Bin (Windows warns if one is too big for it).</summary>
        public static void RecycleFiles(IntPtr owner, IEnumerable<string> paths)
        {
            // Never send a Windows / system file to the bin, whatever the caller passed.
            string from = string.Join("\0", paths.Where(p => !SystemFiles.IsProtectedOnDisk(p)));
            if (from.Length == 0) return;
            var op = new SHFILEOPSTRUCT
            {
                hwnd = owner,
                wFunc = FO_DELETE,
                pFrom = from + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING,
            };
            SHFileOperation(ref op);
        }

        public static bool QueryRecycleBin(out long items, out long bytes)
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            bool ok = SHQueryRecycleBin(null, ref info) == 0;
            items = info.i64NumItems;
            bytes = info.i64Size;
            return ok;
        }

        public static void EmptyRecycleBin(IntPtr owner) =>
            SHEmptyRecycleBin(owner, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);

        /// <summary>Total size of files under a folder (missing / locked parts are skipped).</summary>
        public static long FolderSize(string dir, TimeSpan? olderThan, CancellationToken token)
        {
            if (!Directory.Exists(dir)) return 0;
            long total = 0;
            DateTime cutoff = olderThan.HasValue ? DateTime.UtcNow - olderThan.Value : DateTime.MaxValue;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            try
            {
                foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", options))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (file.LastWriteTimeUtc <= cutoff) total += file.Length;
                    }
                    catch { }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            return total;
        }

        /// <summary>
        /// Permanently deletes the files inside a folder (not the folder itself).
        /// Files in use are skipped. Returns the bytes freed.
        /// </summary>
        public static long CleanFolder(string dir, TimeSpan? olderThan, CancellationToken token)
        {
            if (!Directory.Exists(dir)) return 0;
            long freed = 0;
            DateTime cutoff = olderThan.HasValue ? DateTime.UtcNow - olderThan.Value : DateTime.MaxValue;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };

            List<FileInfo> files;
            try { files = new List<FileInfo>(new DirectoryInfo(dir).EnumerateFiles("*", options)); }
            catch { return 0; }

            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (file.LastWriteTimeUtc > cutoff) continue;
                    long size = file.Length;
                    if (file.IsReadOnly) file.IsReadOnly = false;
                    file.Delete();
                    freed += size;
                }
                catch { /* in use or no permission: leave it */ }
            }

            // Remove folders that are now empty (deepest first).
            try
            {
                var dirs = new List<string>(Directory.EnumerateDirectories(dir, "*", options));
                dirs.Sort((a, b) => b.Length.CompareTo(a.Length));
                foreach (var d in dirs)
                {
                    try { Directory.Delete(d, false); } catch { }
                }
            }
            catch { }
            return freed;
        }
    }
}
