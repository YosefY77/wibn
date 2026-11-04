using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// Custom mouse pointer: a colored arrow or your own picture, at any size.
    ///
    /// We draw the pointer ourselves, save it as a .cur file in
    /// %LOCALAPPDATA%\WinPlus\cursors, point HKCU\Control Panel\Cursors\Arrow (and
    /// optionally Hand) at it and tell Windows to reload (SPI_SETCURSORS). The size is
    /// Windows' own pointer size (CursorBaseSize), so every pointer follows it.
    /// The values we replace are saved first, and "reset" puts them back.
    /// </summary>
    internal static class CursorTool
    {
        private const string OurKey = @"Software\WinPlus\TaskbarScroll";
        private const string CursorsKey = @"Control Panel\Cursors";
        public const int MinSize = 32, MaxSize = 256;

        public static string Folder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPlus", "cursors");

        // ---- Settings ----------------------------------------------------------

        public static bool Enabled { get => GetInt("CursorEnabled", 0) != 0; set => SetInt("CursorEnabled", value ? 1 : 0); }
        /// <summary>0 = colored arrow, 1 = picture.</summary>
        public static int Mode { get => GetInt("CursorMode", 0); set => SetInt("CursorMode", value); }
        public static uint Color { get => (uint)GetInt("CursorColor", unchecked((int)0xFF4C8DFF)); set => SetInt("CursorColor", unchecked((int)value)); }
        /// <summary>0 = the picture's top-left corner clicks, 1 = its center.</summary>
        public static int Hotspot { get => GetInt("CursorHotspot", 0); set => SetInt("CursorHotspot", value); }
        public static bool AlsoHand { get => GetInt("CursorHand", 1) != 0; set => SetInt("CursorHand", value ? 1 : 0); }

        /// <summary>Our copy of the picture the user chose (null if none yet).</summary>
        public static string? ImagePath
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(OurKey);
                var p = key?.GetValue("CursorImage") as string;
                return p != null && File.Exists(p) ? p : null;
            }
        }

        /// <summary>Copies the chosen picture into our folder (so it still works if the original moves).</summary>
        public static void SetImage(string source)
        {
            Directory.CreateDirectory(Folder);
            string ext = Path.GetExtension(source).ToLowerInvariant();
            string target = Path.Combine(Folder, $"source-{DateTime.Now.Ticks}{ext}");
            File.Copy(source, target);
            string? old = ImagePath;
            using (var key = Registry.CurrentUser.CreateSubKey(OurKey))
                key.SetValue("CursorImage", target);
            if (old != null) TryDelete(old);
        }

        /// <summary>Windows' pointer size in pixels (32 = normal).</summary>
        public static int Size
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(CursorsKey);
                return key?.GetValue("CursorBaseSize") is int v ? Math.Clamp(v, MinSize, MaxSize) : 32;
            }
            set
            {
                int size = Math.Clamp((value + 8) / 16 * 16, MinSize, MaxSize);
                // The same call Windows' Settings uses for "Mouse pointer size".
                SystemParametersInfo(SPI_SETCURSORBASESIZE, 0, (IntPtr)size, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
                using (var key = Registry.CurrentUser.CreateSubKey(CursorsKey))
                    key.SetValue("CursorBaseSize", size, RegistryValueKind.DWord);
                // Ours is drawn for a specific size, so redraw it.
                if (Enabled) Apply();
                else Reload();
            }
        }

        // ---- Apply / reset -------------------------------------------------------

        /// <summary>Draws the pointer from the settings and makes Windows use it.</summary>
        public static void Apply()
        {
            BackupOriginals();
            Directory.CreateDirectory(Folder);

            string file;
            string? image = ImagePath;
            if (Mode == 1 && image != null && (image.EndsWith(".cur", StringComparison.OrdinalIgnoreCase) ||
                                               image.EndsWith(".ani", StringComparison.OrdinalIgnoreCase)))
            {
                file = image;  // already a pointer file: use as is
            }
            else
            {
                var bmp = Render(Size, out int hx, out int hy);
                file = Path.Combine(Folder, $"pointer-{DateTime.Now.Ticks}.cur");
                File.WriteAllBytes(file, ToCur(bmp, hx, hy));
            }

            using (var key = Registry.CurrentUser.CreateSubKey(CursorsKey))
            {
                key.SetValue("Arrow", file, RegistryValueKind.ExpandString);
                if (AlsoHand) key.SetValue("Hand", file, RegistryValueKind.ExpandString);
                else RestoreValue(key, "Hand");
            }
            Reload();
            CleanOldFiles(file);
        }

        /// <summary>Puts back the pointers Windows had before.</summary>
        public static void Reset()
        {
            using (var key = Registry.CurrentUser.CreateSubKey(CursorsKey))
            {
                RestoreValue(key, "Arrow");
                RestoreValue(key, "Hand");
            }
            using (var ours = Registry.CurrentUser.CreateSubKey(OurKey))
            {
                ours.DeleteValue("CursorOrigArrow", false);
                ours.DeleteValue("CursorOrigHand", false);
            }
            Reload();
            CleanOldFiles(ImagePath);
        }

        private static void BackupOriginals()
        {
            using var cursors = Registry.CurrentUser.OpenSubKey(CursorsKey);
            using var ours = Registry.CurrentUser.CreateSubKey(OurKey);
            foreach (var name in new[] { "Arrow", "Hand" })
            {
                if (ours.GetValue("CursorOrig" + name) != null) continue;
                // Not one of ours already (e.g. after a crash).
                string value = cursors?.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                if (value.StartsWith(Folder, StringComparison.OrdinalIgnoreCase)) value = "";
                ours.SetValue("CursorOrig" + name, value);
            }
        }

        private static void RestoreValue(RegistryKey cursors, string name)
        {
            using var ours = Registry.CurrentUser.OpenSubKey(OurKey);
            string value = ours?.GetValue("CursorOrig" + name) as string ?? "";
            // "" = Windows' built-in pointer.
            cursors.SetValue(name, value, RegistryValueKind.ExpandString);
        }

        private static void Reload() =>
            SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);

        private static void CleanOldFiles(string? keep)
        {
            try
            {
                string? source = ImagePath;
                foreach (var f in Directory.EnumerateFiles(Folder))
                {
                    if (string.Equals(f, keep, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(f, source, StringComparison.OrdinalIgnoreCase)) continue;
                    TryDelete(f);  // a file Windows still has open just stays for next time
                }
            }
            catch { }
        }

        // ---- Drawing --------------------------------------------------------------

        /// <summary>The pointer as it will look (also used for the preview).</summary>
        public static BitmapSource Render(int size, out int hotX, out int hotY)
        {
            var visual = new DrawingVisual();
            hotX = hotY = 0;
            using (var dc = visual.RenderOpen())
            {
                string? image = ImagePath;
                if (Mode == 1 && image != null && TryLoad(image) is BitmapSource pic)
                {
                    // Fit inside the square, keeping its shape.
                    double scale = Math.Min((double)size / pic.PixelWidth, (double)size / pic.PixelHeight);
                    double w = pic.PixelWidth * scale, h = pic.PixelHeight * scale;
                    double x = Hotspot == 1 ? (size - w) / 2 : 0, y = Hotspot == 1 ? (size - h) / 2 : 0;
                    dc.DrawImage(pic, new Rect(x, y, w, h));
                    if (Hotspot == 1) { hotX = size / 2; hotY = size / 2; }
                }
                else
                {
                    DrawArrow(dc, size);
                    hotX = (int)Math.Round(size * 0.04);
                    hotY = (int)Math.Round(size * 0.04);
                }
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }

        // The classic Windows arrow, in a 32 x 32 grid.
        private static readonly Point[] ArrowPoints =
        {
            new(1, 1), new(1, 24), new(6.5, 19), new(10.5, 28), new(14, 26.5), new(10, 18), new(17.5, 18),
        };

        private static void DrawArrow(DrawingContext dc, int size)
        {
            uint c = Color;
            var fill = System.Windows.Media.Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c);
            // Outline in black or white, whichever stands out from the color.
            double light = 0.299 * fill.R + 0.587 * fill.G + 0.114 * fill.B;
            var edge = light > 150 ? Colors.Black : Colors.White;

            double k = size / 32.0;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(Scale(ArrowPoints[0], k), true, true);
                for (int i = 1; i < ArrowPoints.Length; i++) g.LineTo(Scale(ArrowPoints[i], k), true, true);
            }
            geo.Freeze();
            var pen = new Pen(new SolidColorBrush(edge), Math.Max(1, 1.4 * k)) { LineJoin = PenLineJoin.Round };
            dc.DrawGeometry(new SolidColorBrush(fill), pen, geo);
        }

        private static Point Scale(Point p, double k) => new(p.X * k, p.Y * k);

        private static BitmapSource? TryLoad(string path)
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;  // don't keep the file open
                bi.UriSource = new Uri(path);
                bi.DecodePixelWidth = MaxSize;              // big photos: don't load full size
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        /// <summary>A .cur file with one 32-bit image (classic DIB format, works everywhere).</summary>
        private static byte[] ToCur(BitmapSource bmp, int hotX, int hotY)
        {
            var src = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
            byte[] pixels = new byte[stride * h];
            src.CopyPixels(pixels, stride, 0);

            int maskStride = ((w + 31) / 32) * 4;
            int imageSize = 40 + pixels.Length + maskStride * h;
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            // ICONDIR
            bw.Write((ushort)0); bw.Write((ushort)2); bw.Write((ushort)1);
            // ICONDIRENTRY (for cursors: planes/bitcount = hotspot)
            bw.Write((byte)(w >= 256 ? 0 : w)); bw.Write((byte)(h >= 256 ? 0 : h));
            bw.Write((byte)0); bw.Write((byte)0);
            bw.Write((ushort)hotX); bw.Write((ushort)hotY);
            bw.Write(imageSize); bw.Write(22);
            // BITMAPINFOHEADER (height counts image + mask)
            bw.Write(40); bw.Write(w); bw.Write(h * 2); bw.Write((ushort)1); bw.Write((ushort)32);
            bw.Write(0); bw.Write(pixels.Length + maskStride * h); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
            // Pixels, bottom row first
            for (int y = h - 1; y >= 0; y--) bw.Write(pixels, y * stride, stride);
            // AND mask: all zero (the alpha channel decides)
            bw.Write(new byte[maskStride * h]);
            bw.Flush();
            return ms.ToArray();
        }

        // ---- Helpers -----------------------------------------------------------------

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

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }

        private const uint SPI_SETCURSORS = 0x0057;
        private const uint SPI_SETCURSORBASESIZE = 0x2029;
        private const uint SPIF_UPDATEINIFILE = 0x01, SPIF_SENDCHANGE = 0x02;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint flags);
    }
}
