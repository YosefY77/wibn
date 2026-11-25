using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// Wibn's settings in one small file (.wibn, JSON), and back: every setting under HKCU\Software\WinPlus and a few
    /// small files (appointments, the Start button's picture…).
    ///
    /// Never in it: anything private (the phone's pairing code, the cookies for downloads) and what belongs to this PC
    /// only while Wibn runs (window handles, work areas, Windows' own pointers and colors kept to put back, the locked
    /// files' list).
    ///
    /// A file brought in is applied at Wibn's next start, before anything reads a setting (while it runs, closing
    /// writes some settings again, e.g. where the bars are). The settings it had before are kept in
    /// settings-backup.wibn first.
    /// </summary>
    internal static class SettingsTransfer
    {
        private const string Root = @"Software\WinPlus";
        private const string Format = "wibn-settings";
        private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPlus");
        private static string PendingPath => Path.Combine(Folder, "settings-pending.wibn");
        public static string BackupPath => Path.Combine(Folder, "settings-backup.wibn");

        /// <summary>Small files that are settings too (name in the WinPlus folder).</summary>
        private static readonly string[] Files = { "appointments.json", "widgets.json", "start-icon.png" };

        /// <summary>Never exported (key\value, or just a value name anywhere).</summary>
        private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
        {
            @"Phone\Code",                                     // private: the phone's pairing code
            @"Video\CookiesFile", @"Video\UpdatedAt", @"Video\UpdatedDay",
            @"CustomTaskbar\Active", @"CustomTaskbar\AppBars", @"CustomTaskbar\WorkAreas", @"CustomTaskbar\SeenWindowsPins",
            @"TaskbarScroll\AutoHideUserState", @"TaskbarScroll\CursorOrigArrow", @"TaskbarScroll\CursorOrigHand",
            @"TaskbarScroll\SelOrigHilight", @"TaskbarScroll\SelOrigHilightText", @"TaskbarScroll\SelOrigHot",
            @"TaskbarScroll\WhatsNewSeen", @"TaskbarScroll\LockedFiles", @"TaskbarScroll\AppActive",
        };
        private static readonly string[] SkipNames = { "AppBar", "WasShared" };

        private static bool Skipped(string key, string name)
        {
            if (Skip.Contains(key.Length == 0 ? name : key + "\\" + name)) return true;
            if (SkipNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
            string n = name.ToLowerInvariant();
            return n.Contains("cookie") || n.Contains("token") || n.Contains("password");
        }

        // ---- Out ---------------------------------------------------------------------------------------

        /// <summary>Writes every setting into this file.</summary>
        public static void Export(string path) => File.WriteAllText(path, Build().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        private static JsonObject Build()
        {
            var reg = new JsonObject();
            using (var root = Registry.CurrentUser.OpenSubKey(Root))
                if (root != null) ReadKey(root, "", reg);
            var files = new JsonObject();
            foreach (var f in Files)
            {
                string p = Path.Combine(Folder, f);
                if (!File.Exists(p) || new FileInfo(p).Length > 2_000_000) continue;
                files[f] = Convert.ToBase64String(File.ReadAllBytes(p));
            }
            return new JsonObject
            {
                ["format"] = Format,
                ["version"] = UpdateChecker.CurrentText,
                ["saved"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                ["registry"] = reg,
                ["files"] = files,
            };
        }

        private static void ReadKey(RegistryKey key, string path, JsonObject into)
        {
            var values = new JsonObject();
            foreach (var name in key.GetValueNames())
            {
                if (Skipped(path, name)) continue;
                object? v = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                var kind = key.GetValueKind(name);
                JsonNode? node = kind switch
                {
                    RegistryValueKind.DWord => new JsonObject { ["dword"] = Convert.ToInt32(v) },
                    RegistryValueKind.QWord => new JsonObject { ["qword"] = Convert.ToInt64(v) },
                    RegistryValueKind.String => new JsonObject { ["string"] = v as string ?? "" },
                    RegistryValueKind.ExpandString => new JsonObject { ["expand"] = v as string ?? "" },
                    RegistryValueKind.MultiString => new JsonObject { ["multi"] = new JsonArray(((string[]?)v ?? Array.Empty<string>()).Select(s => (JsonNode?)s).ToArray()) },
                    RegistryValueKind.Binary => new JsonObject { ["binary"] = Convert.ToBase64String((byte[]?)v ?? Array.Empty<byte>()) },
                    _ => null,
                };
                if (node != null) values[name] = node;
            }
            into[path] = values;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var child = key.OpenSubKey(sub);
                if (child != null) ReadKey(child, path.Length == 0 ? sub : path + "\\" + sub, into);
            }
        }

        // ---- In ----------------------------------------------------------------------------------------

        /// <summary>Checks a file (is it Wibn's settings?) and keeps it to apply at the next start. Null = fine, else why not.</summary>
        public static string? Prepare(string path)
        {
            try
            {
                var doc = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                if (doc == null || (string?)doc["format"] != Format || doc["registry"] is not JsonObject)
                    return Lang.T("هذا الملف مو ملف إعدادات Wibn");
                Directory.CreateDirectory(Folder);
                File.Copy(path, PendingPath, true);
                return null;
            }
            catch (Exception ex)
            {
                CrashLog.Write("settings import", ex);
                return Lang.T("ما قدرت أقرأ الملف");
            }
        }

        /// <summary>At start, before anything reads a setting: a file brought in is applied (once).</summary>
        public static void ApplyPending()
        {
            if (!File.Exists(PendingPath)) return;
            try
            {
                var doc = JsonNode.Parse(File.ReadAllText(PendingPath)) as JsonObject;
                if (doc?["registry"] is JsonObject reg && (string?)doc["format"] == Format)
                {
                    try { Export(BackupPath); } catch (Exception ex) { CrashLog.Write("settings backup", ex); }   // what it had before
                    foreach (var (path, node) in reg)
                    {
                        if (node is not JsonObject values) continue;
                        using var key = Registry.CurrentUser.CreateSubKey(path.Length == 0 ? Root : Root + "\\" + path);
                        foreach (var (name, v) in values)
                        {
                            if (v is not JsonObject one || Skipped(path, name)) continue;
                            Write(key, name, one);
                        }
                    }
                    if (doc["files"] is JsonObject files)
                        foreach (var (name, v) in files)
                        {
                            if (!Files.Contains(name) || v is null) continue;   // only our own small files, by name
                            File.WriteAllBytes(Path.Combine(Folder, name), Convert.FromBase64String((string)v!));
                        }
                    CrashLog.Note("settings imported");
                }
            }
            catch (Exception ex) { CrashLog.Write("settings import", ex); }
            finally { try { File.Delete(PendingPath); } catch { } }
        }

        private static void Write(RegistryKey key, string name, JsonObject one)
        {
            try
            {
                if (one["dword"] is JsonNode d) key.SetValue(name, (int)d, RegistryValueKind.DWord);
                else if (one["qword"] is JsonNode q) key.SetValue(name, (long)q, RegistryValueKind.QWord);
                else if (one["string"] is JsonNode s) key.SetValue(name, (string)s!, RegistryValueKind.String);
                else if (one["expand"] is JsonNode x) key.SetValue(name, (string)x!, RegistryValueKind.ExpandString);
                else if (one["multi"] is JsonArray m) key.SetValue(name, m.Select(i => (string?)i ?? "").ToArray(), RegistryValueKind.MultiString);
                else if (one["binary"] is JsonNode b) key.SetValue(name, Convert.FromBase64String((string)b!), RegistryValueKind.Binary);
            }
            catch (Exception ex) { CrashLog.Write("settings import " + name, ex); }
        }
    }
}
