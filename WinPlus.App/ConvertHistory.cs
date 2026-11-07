using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// Converting replaces the file. The untouched original is moved (not copied:
    /// same drive, so it's instant) into a hidden folder at the drive's root,
    /// "$WinPlusOriginals". That way:
    ///   * converting a song you made from a video back to MP4 gives the video back;
    ///   * converting again always starts from the best-quality original;
    ///   * converting back to the original format just puts the original back.
    /// Originals are kept 30 days (and at most 20 GB); then they're deleted.
    /// The setting "keep originals" can be turned off: the original then goes to
    /// the Recycle Bin instead.
    /// </summary>
    internal static class ConvertHistory
    {
        public sealed class Entry
        {
            public string Current { get; set; } = "";  // the converted file you see
            public string Backup { get; set; } = "";   // the hidden original
            public string Ext { get; set; } = "";      // the original's extension
            public DateTime Time { get; set; }

            public Entry Clone() => new() { Current = Current, Backup = Backup, Ext = Ext, Time = Time };
        }

        private const string OurKey = @"Software\WinPlus\TaskbarScroll";
        private const string FolderName = "$WinPlusOriginals";
        private static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);
        private const long MaxBytes = 20L << 30;

        public static bool KeepOriginals
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(OurKey);
                return key?.GetValue("ConvertKeep") is not int v || v != 0;
            }
            set
            {
                using var key = Registry.CurrentUser.CreateSubKey(OurKey);
                key.SetValue("ConvertKeep", value ? 1 : 0, RegistryValueKind.DWord);
            }
        }

        private static string IndexFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPlus", "convert-history.json");

        /// <summary>The original this file was converted from, if we still have it.</summary>
        public static Entry? OriginalOf(string path) =>
            Load().FirstOrDefault(e => string.Equals(e.Current, path, StringComparison.OrdinalIgnoreCase) && File.Exists(e.Backup));

        /// <summary>Moves the original into the hidden backup and remembers it.
        /// Returns null (and leaves the file) when originals aren't kept: the caller
        /// sets it aside for undo, and it goes to the Recycle Bin later (FileUndo).</summary>
        public static Entry? Keep(string original, string converted)
        {
            if (!KeepOriginals) return null;
            string backup = Path.Combine(BackupFolderFor(original), $"{Guid.NewGuid():N}{Path.GetExtension(original)}");
            File.Move(original, backup);
            var list = Load();
            var entry = new Entry { Current = converted, Backup = backup, Ext = Path.GetExtension(original), Time = DateTime.Now };
            list.Add(entry);
            Save(list);
            Cleanup();
            return entry;
        }

        /// <summary>The converted file was converted again: the same original now belongs to the new one.</summary>
        public static void Update(Entry entry, string converted)
        {
            var list = Load();
            var e = list.FirstOrDefault(x => string.Equals(x.Backup, entry.Backup, StringComparison.OrdinalIgnoreCase));
            if (e == null) return;
            e.Current = converted;
            e.Time = DateTime.Now;
            Save(list);
        }

        public static void Remove(Entry entry) => RemoveBackup(entry.Backup);

        public static void RemoveBackup(string backup)
        {
            var list = Load();
            list.RemoveAll(x => string.Equals(x.Backup, backup, StringComparison.OrdinalIgnoreCase));
            Save(list);
        }

        /// <summary>Adds the entry back (undo), replacing one with the same backup.</summary>
        public static void Put(Entry entry)
        {
            var list = Load();
            list.RemoveAll(x => string.Equals(x.Backup, entry.Backup, StringComparison.OrdinalIgnoreCase));
            list.Add(entry);
            Save(list);
        }

        /// <summary>Drops old / missing originals (run after every conversion and at startup).</summary>
        public static void Cleanup()
        {
            try
            {
                var list = Load();
                var keep = new List<Entry>();
                long total = 0;
                foreach (var e in list.OrderByDescending(e => e.Time))
                {
                    if (!File.Exists(e.Backup)) continue;
                    long size = new FileInfo(e.Backup).Length;
                    bool tooOld = DateTime.Now - e.Time > KeepFor;
                    bool currentGone = !File.Exists(e.Current);  // converted file deleted/renamed: keep a bit only
                    if (tooOld || total + size > MaxBytes || (currentGone && DateTime.Now - e.Time > TimeSpan.FromDays(7)))
                    {
                        try { File.Delete(e.Backup); } catch { keep.Add(e); }
                        continue;
                    }
                    total += size;
                    keep.Add(e);
                }
                if (keep.Count != list.Count) Save(keep);
            }
            catch { }
        }

        /// <summary>"X:\$WinPlusOriginals" on the file's own drive (hidden), so moving is instant.</summary>
        internal static string BackupFolderFor(string file)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(file))!;
                string dir = Path.Combine(root, FolderName);
                var info = Directory.CreateDirectory(dir);
                info.Attributes |= FileAttributes.Hidden | FileAttributes.System;
                return dir;
            }
            catch
            {
                // No rights at the drive's root: our own folder (may need a copy if on another drive).
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                          "WinPlus", "originals");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static List<Entry> Load()
        {
            try
            {
                if (File.Exists(IndexFile))
                    return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(IndexFile)) ?? new();
            }
            catch { }
            return new();
        }

        private static void Save(List<Entry> list)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(IndexFile)!);
                File.WriteAllText(IndexFile, JsonSerializer.Serialize(list));
            }
            catch { }
        }
    }
}
