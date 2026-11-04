using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// Color of the selection rectangle you drag on the desktop / in folders.
    /// Windows draws its fill from the "HotTrackingColor" system color and its
    /// border from "Hilight" (HKCU\Control Panel\Colors). SetSysColors changes
    /// them right away; the registry keeps them after a restart.
    /// </summary>
    internal static class SelectionColor
    {
        private const int COLOR_HIGHLIGHT = 13, COLOR_HIGHLIGHTTEXT = 14, COLOR_HOTLIGHT = 26;
        private const string ColorsKey = @"Control Panel\Colors";
        private const string OurKey = @"Software\WinPlus\TaskbarScroll";

        public static bool Enabled
        {
            get => GetInt("SelColorEnabled", 0) != 0;
            set
            {
                if (value == Enabled) return;
                if (value)
                {
                    SaveOriginals();
                    SetInt("SelColorEnabled", 1);
                    Apply();
                }
                else
                {
                    SetInt("SelColorEnabled", 0);
                    RestoreOriginals();
                }
            }
        }

        /// <summary>0xFFRRGGBB</summary>
        public static uint Color
        {
            get => (uint)GetInt("SelColor", unchecked((int)0xFF0078D4));
            set
            {
                SetInt("SelColor", unchecked((int)value));
                if (Enabled) Apply();
            }
        }

        /// <summary>Also color the border (otherwise only the fill).</summary>
        public static bool Border
        {
            get => GetInt("SelColorBorder", 1) != 0;
            set
            {
                SetInt("SelColorBorder", value ? 1 : 0);
                if (!Enabled) return;
                if (value) Apply();
                else
                {
                    Set(COLOR_HIGHLIGHT, "Hilight", OriginalOr("SelOrigHilight", "0 120 215"));
                    Set(COLOR_HIGHLIGHTTEXT, "HilightText", OriginalOr("SelOrigHilightText", "255 255 255"));
                }
            }
        }

        /// <summary>Back to Windows' own colors (and the option off).</summary>
        public static void ResetToWindowsDefault()
        {
            SetInt("SelColorEnabled", 0);
            SetInt("SelColor", unchecked((int)0xFF0066CC));
            Set(COLOR_HOTLIGHT, "HotTrackingColor", "0 102 204");
            Set(COLOR_HIGHLIGHT, "Hilight", "0 120 215");
            Set(COLOR_HIGHLIGHTTEXT, "HilightText", "255 255 255");
            using var ours = Registry.CurrentUser.CreateSubKey(OurKey);
            ours.DeleteValue("SelOrigHot", false);
            ours.DeleteValue("SelOrigHilight", false);
            ours.DeleteValue("SelOrigHilightText", false);
        }

        private static void Apply()
        {
            uint c = Color;
            string rgb = $"{(c >> 16) & 0xFF} {(c >> 8) & 0xFF} {c & 0xFF}";
            Set(COLOR_HOTLIGHT, "HotTrackingColor", rgb);
            if (Border)
            {
                Set(COLOR_HIGHLIGHT, "Hilight", rgb);
                // The same color is the background of selected text in some
                // programs, so the text goes black or white to stay readable.
                Set(COLOR_HIGHLIGHTTEXT, "HilightText", IsLight(c) ? "0 0 0" : "255 255 255");
            }
        }

        private static bool IsLight(uint c)
        {
            double r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
            return 0.299 * r + 0.587 * g + 0.114 * b > 150;
        }

        private static void SaveOriginals()
        {
            using var colors = Registry.CurrentUser.OpenSubKey(ColorsKey);
            using var ours = Registry.CurrentUser.CreateSubKey(OurKey);
            if (ours.GetValue("SelOrigHot") == null)
                ours.SetValue("SelOrigHot", colors?.GetValue("HotTrackingColor") as string ?? "0 102 204");
            if (ours.GetValue("SelOrigHilightText") == null)
                ours.SetValue("SelOrigHilightText", colors?.GetValue("HilightText") as string ?? "255 255 255");
            if (ours.GetValue("SelOrigHilight") == null)
                ours.SetValue("SelOrigHilight", colors?.GetValue("Hilight") as string ?? "0 120 215");
        }

        private static void RestoreOriginals()
        {
            Set(COLOR_HOTLIGHT, "HotTrackingColor", OriginalOr("SelOrigHot", "0 102 204"));
            Set(COLOR_HIGHLIGHT, "Hilight", OriginalOr("SelOrigHilight", "0 120 215"));
            Set(COLOR_HIGHLIGHTTEXT, "HilightText", OriginalOr("SelOrigHilightText", "255 255 255"));
            using var ours = Registry.CurrentUser.CreateSubKey(OurKey);
            ours.DeleteValue("SelOrigHot", false);
            ours.DeleteValue("SelOrigHilight", false);
            ours.DeleteValue("SelOrigHilightText", false);
        }

        private static string OriginalOr(string name, string fallback)
        {
            using var ours = Registry.CurrentUser.OpenSubKey(OurKey);
            return ours?.GetValue(name) as string ?? fallback;
        }

        private static void Set(int index, string regName, string rgb)
        {
            var parts = rgb.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || !byte.TryParse(parts[0], out byte r) ||
                !byte.TryParse(parts[1], out byte g) || !byte.TryParse(parts[2], out byte b)) return;
            using (var colors = Registry.CurrentUser.CreateSubKey(ColorsKey))
            {
                colors.SetValue(regName, $"{r} {g} {b}");
            }
            // COLORREF is 0x00BBGGRR. Windows tells every window to repaint.
            SetSysColors(1, new[] { index }, new[] { (uint)(r | (g << 8) | (b << 16)) });
        }

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

        [DllImport("user32.dll")]
        private static extern bool SetSysColors(int count, int[] elements, uint[] colors);
    }
}
