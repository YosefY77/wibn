using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// Favorites (shown first in the side menu, under "خيارات مفضلة") and hidden ones: pages by their key
    /// ("taskbar"), options inside a page as "page|title". Kept in the registry with the other settings,
    /// so they go with "save to a file" too.
    /// </summary>
    internal static class SidePrefs
    {
        private const string KeyPath = @"Software\WinPlus\Sidebar";

        private static List<string>? _favorites, _hidden;

        public static IReadOnlyList<string> Favorites => _favorites ??= Read("Favorites");
        public static IReadOnlyList<string> Hidden => _hidden ??= Read("Hidden");

        public static bool IsFavorite(string id) => Favorites.Contains(id);
        public static bool IsHidden(string id) => Hidden.Contains(id);

        public static void SetFavorite(string id, bool on) => _favorites = Change("Favorites", Favorites, id, on);
        public static void SetHidden(string id, bool on) => _hidden = Change("Hidden", Hidden, id, on);
        public static void ShowAll() { _hidden = new List<string>(); Write("Hidden", _hidden); }

        // Side-menu groups that are folded, by their id ("clean", "tools", "fav"…).
        private static List<string>? _collapsed;
        public static bool IsCollapsed(string group) => (_collapsed ??= Read("Collapsed")).Contains(group);
        public static void SetCollapsed(string group, bool on) => _collapsed = Change("Collapsed", _collapsed ??= Read("Collapsed"), group, on);

        private static List<string> Change(string name, IReadOnlyList<string> now, string id, bool on)
        {
            var list = now.Where(x => x != id).ToList();
            if (on) list.Add(id);   // a new one goes to the end
            Write(name, list);
            return list;
        }

        private static List<string> Read(string name)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
                IEnumerable<string> items = key?.GetValue(name) switch
                {
                    string[] many => many,
                    string one => one.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    _ => Array.Empty<string>(),
                };
                return items.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
            }
            catch { return new List<string>(); }
        }

        private static void Write(string name, List<string> list)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
                key.SetValue(name, list.ToArray(), RegistryValueKind.MultiString);   // titles can hold commas
            }
            catch (Exception ex) { CrashLog.Write("side prefs", ex); }
        }
    }
}
