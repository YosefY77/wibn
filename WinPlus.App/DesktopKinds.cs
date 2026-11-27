using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// The kinds the desktop panel groups its items in (programs, games, folders, files), and the order the user
    /// put them in (dragged on the panel's "ترتيب" sheet). A shortcut counts as what it opens: a Steam game's
    /// shortcut is a game, a shortcut to a folder is a folder.
    /// </summary>
    internal static class DesktopKinds
    {
        public sealed record Kind(string Id, string Name, string Icon);

        public static readonly Kind[] All =
        {
            new("programs", "برامج", ""),
            new("games", "ألعاب", ""),
            new("folders", "مجلدات", ""),
            new("files", "ملفات", ""),
        };

        private const string Key = @"Software\WinPlus\Widgets";

        /// <summary>The kinds in the user's order (any missing one at the end, in its usual place).</summary>
        private static List<Kind>? _order;   // read once, kept

        public static List<Kind> Order
        {
            get
            {
                if (_order != null) return new List<Kind>(_order);
                string[]? saved = null;
                try { using var k = Registry.CurrentUser.OpenSubKey(Key); saved = k?.GetValue("DesktopKindOrder") as string[]; } catch { }
                var list = new List<Kind>();
                foreach (var id in saved ?? Array.Empty<string>())
                    if (All.FirstOrDefault(x => x.Id == id) is { } kind && !list.Contains(kind)) list.Add(kind);
                foreach (var kind in All) if (!list.Contains(kind)) list.Add(kind);
                _order = list;
                return new List<Kind>(list);
            }
            set
            {
                _order = new List<Kind>(value);
                try
                {
                    using var k = Registry.CurrentUser.CreateSubKey(Key);
                    k.SetValue("DesktopKindOrder", value.Select(x => x.Id).ToArray(), RegistryValueKind.MultiString);
                }
                catch { }
                Changed?.Invoke();
            }
        }

        public static event Action? Changed;

        public static int Rank(string path)
        {
            string id = Of(path);
            return Order.FindIndex(k => k.Id == id);
        }

        // ---- What an item is ---------------------------------------------------------------------------

        private static readonly Dictionary<string, (DateTime At, string Kind)> Cache = new(StringComparer.OrdinalIgnoreCase);

        private static readonly string[] GameSchemes =
            { "steam://", "com.epicgames.launcher://", "uplay://", "origin://", "origin2://", "battlenet://", "eadm://", "goggalaxy://", "riotclient://", "minecraft://" };

        private static readonly string[] GameArgs =
            { "steam://rungameid", "steam://run/", "--launch-product=", "com.epicgames.launcher://apps", "-epicapp", "--exec=\"launch", "uplay://launch", "/command=runGame", "--game-id" };

        // ---- Put in a kind by hand (right-click → تصنيف كـ…) --------------------------------------------

        private static Dictionary<string, string>? _byHand;   // path → kind, read once, kept

        private static Dictionary<string, string> ByHand()
        {
            if (_byHand != null) return _byHand;
            _byHand = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(Key);
                if (k?.GetValue("DesktopKindByHand") is string[] lines)
                    foreach (var line in lines)
                    {
                        int bar = line.LastIndexOf('|');
                        if (bar > 0 && All.Any(x => x.Id == line[(bar + 1)..])) _byHand[line[..bar]] = line[(bar + 1)..];
                    }
            }
            catch { }
            return _byHand;
        }

        /// <summary>
        /// The user's own kind for this item. Picking what it would be anyway just forgets the choice, so a fix that
        /// isn't needed any more doesn't stay saved.
        /// </summary>
        public static void SetKind(string path, string id)
        {
            var map = ByHand();
            if (id == Detected(path)) map.Remove(path); else map[path] = id;
            // (Items no longer on the desktop are forgotten.)
            foreach (var gone in map.Keys.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList()) map.Remove(gone);
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(Key);
                k.SetValue("DesktopKindByHand", map.Select(e => e.Key + "|" + e.Value).ToArray(), RegistryValueKind.MultiString);
            }
            catch { }
            Changed?.Invoke();
        }

        /// <summary>"programs", "games", "folders" or "files": the user's own choice if there's one, else what it looks like.</summary>
        public static string Of(string path) => ByHand().TryGetValue(path, out var mine) ? mine : Detected(path);

        /// <summary>What it looks like (read once per file, again only when it changes).</summary>
        private static string Detected(string path)
        {
            DateTime at;
            try { at = Directory.Exists(path) ? DateTime.MinValue : File.GetLastWriteTimeUtc(path); } catch { at = DateTime.MinValue; }
            if (Cache.TryGetValue(path, out var c) && c.At == at) return c.Kind;
            string kind;
            try { kind = Read(path); } catch { kind = "files"; }
            if (Cache.Count > 2000) Cache.Clear();
            Cache[path] = (at, kind);
            return kind;
        }

        private static string Read(string path)
        {
            if (Directory.Exists(path)) return "folders";
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".lnk":
                {
                    var (target, args) = Shortcut(path);
                    if (GameArgs.Any(a => args.Contains(a, StringComparison.OrdinalIgnoreCase))) return "games";
                    if (target.Length == 0) return "programs";   // a Store app or an installed one's shortcut
                    if (Directory.Exists(target)) return "folders";
                    if (GameBoost.LooksLikeGame(target)) return "games";
                    string t = Path.GetExtension(target).ToLowerInvariant();
                    return t is ".exe" or ".bat" or ".cmd" or ".msc" or ".appref-ms" or ".com" or ".ps1" ? "programs" : "files";
                }
                case ".url":
                {
                    string url = UrlOf(path);
                    if (GameSchemes.Any(s => url.StartsWith(s, StringComparison.OrdinalIgnoreCase))) return "games";
                    return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) || url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? "files" : "programs";
                }
                case ".exe":
                    return GameBoost.LooksLikeGame(path) ? "games" : "programs";
                case ".appref-ms" or ".msc" or ".bat" or ".cmd":
                    return "programs";
                default:
                    return "files";
            }
        }

        private static string UrlOf(string path)
        {
            foreach (var line in File.ReadLines(path).Take(40))
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) return line[4..].Trim();
            return "";
        }

        /// <summary>A shortcut's target and its arguments (both empty if it can't be read).</summary>
        private static (string Target, string Args) Shortcut(string path)
        {
            object? link = null;
            try
            {
                link = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!);
                ((IPersistFile)link!).Load(path, 0 /* STGM_READ */);
                var sl = (IShellLinkW)link;
                var target = new StringBuilder(1024);
                sl.GetPath(target, target.Capacity, IntPtr.Zero, 0x4 /* SLGP_RAWPATH */);
                var args = new StringBuilder(2048);
                sl.GetArguments(args, args.Capacity);
                return (Environment.ExpandEnvironmentVariables(target.ToString()), args.ToString());
            }
            catch { return ("", ""); }
            finally { if (link != null) Marshal.ReleaseComObject(link); }
        }

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, uint flags);
            void GetIDList(out IntPtr pidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        }

        [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersistFile
        {
            void GetClassID(out Guid clsid);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        }
    }
}
