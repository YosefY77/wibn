using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinPlus
{
    /// <summary>
    /// The picture on the Start button of Wibn's taskbar: Windows' logo, Wibn's logo, one of
    /// the built-in icons (in the chosen color) or a picture the user picked.
    /// </summary>
    internal static class StartLogo
    {
        /// <summary>The built-in icons (Segoe Fluent Icons); CustomTaskbar.StartIcon 2 = the first.</summary>
        public static readonly string[] Glyphs =
        {
            "", // home
            "", // star
            "", // heart
            "", // globe
            "", // game controller
            "", // lightning
            "", // sun
            "", // moon
            "", // tiles
            "", // three lines
            "", // person
            "", // music
            "", // palette
            "", // screen
        };

        private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

        /// <summary>Where the user's picture is kept (a copy, so moving or deleting the original doesn't matter).</summary>
        public static string ImagePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPlus", "start-icon.png");

        public static bool HasImage => File.Exists(ImagePath);

        /// <summary>The brush of the logo / icons: Windows' blue, or the chosen color with a lighter top.</summary>
        public static Brush Fill(int color)
        {
            if (color < 0)
                return new LinearGradientBrush(Color.FromRgb(0x5E, 0xD0, 0xFF), Color.FromRgb(0x00, 0x78, 0xD4), 45);
            var c = Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color);
            static byte Up(byte v) => (byte)(v + (255 - v) * 0.35);
            return new LinearGradientBrush(Color.FromRgb(Up(c.R), Up(c.G), Up(c.B)), c, 45);
        }

        /// <summary>The logo for this icon, size × size (device-independent pixels).</summary>
        public static FrameworkElement Build(int icon, int color, double size)
        {
            ImageSource? picture = icon switch
            {
                -1 => LoadImage(),
                1 => WibnLogo(),
                _ => null,
            };
            if (picture != null)
            {
                var img = new Image { Source = picture, Width = size, Height = size, Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                return img;
            }

            var fill = Fill(color);
            if (icon >= 2 && icon - 2 < Glyphs.Length)
            {
                return new TextBlock
                {
                    Text = Glyphs[icon - 2], FontFamily = IconFont, FontSize = size * 0.92, Foreground = fill,
                    Width = size, Height = size, TextAlignment = TextAlignment.Center,
                    LineHeight = size, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    FlowDirection = FlowDirection.LeftToRight,
                };
            }

            // The four squares of the Windows logo.
            double q = size * 0.47, gap = size * 0.06;
            var logo = new Canvas { Width = size, Height = size, FlowDirection = FlowDirection.LeftToRight };
            for (int i = 0; i < 4; i++)
            {
                var sq = new Border { Width = q, Height = q, Background = fill, CornerRadius = new CornerRadius(q * 0.12) };
                Canvas.SetLeft(sq, i % 2 * (q + gap));
                Canvas.SetTop(sq, i / 2 * (q + gap));
                logo.Children.Add(sq);
            }
            return logo;
        }

        private static ImageSource? WibnLogo()
        {
            try { return new BitmapImage(new Uri("pack://application:,,,/logo.png")); }
            catch { return null; }
        }

        private static ImageSource? LoadImage()
        {
            try
            {
                if (!HasImage) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(ImagePath);
                bmp.CacheOption = BitmapCacheOption.OnLoad;              // the file isn't kept open
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache; // a new picture shows right away
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex) { CrashLog.Write("start icon", ex); return null; }
        }

        /// <summary>Copies the picked picture (made small, 128 px at most) as the Start icon.</summary>
        public static bool SaveImage(string file, out string error)
        {
            error = "";
            try
            {
                BitmapSource src;
                using (var s = File.OpenRead(file))
                {
                    var decoder = BitmapDecoder.Create(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    // An .ico has several sizes: take the biggest.
                    src = decoder.Frames[0];
                    foreach (var f in decoder.Frames) if (f.PixelWidth > src.PixelWidth) src = f;
                }
                double scale = Math.Min(1, 128.0 / Math.Max(src.PixelWidth, src.PixelHeight));
                if (scale < 1) src = new TransformedBitmap(src, new ScaleTransform(scale, scale));

                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(src));
                Directory.CreateDirectory(Path.GetDirectoryName(ImagePath)!);
                string temp = ImagePath + ".tmp";
                using (var o = File.Create(temp)) enc.Save(o);
                File.Move(temp, ImagePath, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                CrashLog.Write("start icon save", ex);
                error = ex.Message;
                return false;
            }
        }
    }
}
