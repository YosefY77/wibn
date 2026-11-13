using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WinPlus
{
    /// <summary>
    /// The picture widget: a photo or a GIF (it plays) of your own, edge to edge on the card.
    /// The file is copied to %LocalAppData%\WinPlus\WidgetImages, so moving or deleting the
    /// original doesn't matter. Fill (cropped to the card) or whole picture, from its menu.
    /// GIF frames are made once, scaled down to what the card needs, so playing costs nothing.
    /// </summary>
    internal sealed class PictureWidget : WidgetContent
    {
        public static string Folder => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPlus", "WidgetImages");

        private const string Filter = "*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.tif;*.tiff;*.ico";
        private const long MaxBytes = 60L * 1024 * 1024;

        private readonly SolidColorBrush _ink = new(Color.FromRgb(0xF2, 0xF3, 0xF5));
        private readonly SolidColorBrush _inkMuted = new(Color.FromRgb(0xA0, 0xA3, 0xAA));
        private readonly ImageBrush _brush = new() { Stretch = Stretch.UniformToFill };
        private readonly Border _picture;
        private readonly StackPanel _empty;
        private readonly TextBlock _emptyText;
        private readonly DispatcherTimer _player = new(DispatcherPriority.Render);
        private BitmapSource[] _frames = Array.Empty<BitmapSource>();
        private int[] _delays = Array.Empty<int>();
        private int _frame, _loadId;
        private string _loaded = "";
        private bool _running;

        public PictureWidget()
        {
            // Over the card's padding, so the picture reaches its rounded edges.
            _picture = new Border
            {
                CornerRadius = new CornerRadius(13), Background = _brush,
                Margin = new Thickness(-10, -8, -10, -8), Visibility = Visibility.Collapsed,
            };

            _emptyText = new TextBlock
            {
                Text = Lang.T("اختر صورة أو GIF"), FontSize = 13, Foreground = _ink,
                HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            };
            var pick = new Button
            {
                Style = (Style)Application.Current.FindResource("AccentButton"), Content = Lang.T("اختر…"),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0),
            };
            pick.Click += (_, _) => Pick();   // the button takes the press, so it's a click, not a drag of the widget
            _empty = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _empty.Children.Add(new TextBlock
            {
                Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 30,
                Foreground = _inkMuted, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8),
            });
            _empty.Children.Add(_emptyText);
            _empty.Children.Add(pick);

            var grid = new Grid();
            grid.Children.Add(_picture);
            grid.Children.Add(_empty);
            Child = grid;

            _player.Tick += (_, _) => NextFrame();
        }

        // ---- Life ---------------------------------------------------------------------------------

        public override void Start()
        {
            _running = true;
            ApplyFit();
            Load();
            if (_frames.Length > 1) _player.Start();
        }

        /// <summary>Under a maximized / full-screen window: the GIF stops until it shows again.</summary>
        public override void Covered(bool covered)
        {
            if (!_running) return;
            if (covered) _player.Stop();
            else if (_frames.Length > 1) _player.Start();
        }

        public override void Stop()
        {
            _running = false;
            _player.Stop();
        }

        public override void SetCells(int w, int h) => Load();   // a bigger card may need sharper frames

        public override void SetLightBehind(bool light)
        {
            _ink.Color = light ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xF2, 0xF3, 0xF5);
            _inkMuted.Color = light ? Color.FromArgb(0xB0, 0x3C, 0x3C, 0x43) : Color.FromRgb(0xA0, 0xA3, 0xAA);
        }

        // ---- The menu -----------------------------------------------------------------------------

        public override IEnumerable<Control> MenuItems()
        {
            var change = new MenuItem { Header = Lang.T("غيّر الصورة…") };
            change.Click += (_, _) => Pick();
            yield return change;

            var fill = new MenuItem { Header = Lang.T("املأ الويدجت"), IsCheckable = true };
            var whole = new MenuItem { Header = Lang.T("الصورة كاملة"), IsCheckable = true };
            fill.Click += (_, _) => SetFit(false);
            whole.Click += (_, _) => SetFit(true);
            fill.Loaded += (_, _) => { fill.IsChecked = !Whole; whole.IsChecked = Whole; };
            yield return fill;
            yield return whole;
        }

        private bool Whole => Info?.Options.TryGetValue("fit", out var f) == true && f == "whole";

        private void SetFit(bool whole)
        {
            if (Info == null) return;
            Info.Options["fit"] = whole ? "whole" : "fill";
            Widgets.Save();
            ApplyFit();
        }

        private void ApplyFit() => _brush.Stretch = Whole ? Stretch.Uniform : Stretch.UniformToFill;

        // ---- Picking ------------------------------------------------------------------------------

        private void Pick()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = Lang.T("اختر صورة أو GIF"),
                Filter = Lang.T("الصور") + $" ({Filter.Replace(";", ", ")})|{Filter}",
            };
            if (dlg.ShowDialog() != true || Info == null) return;
            try
            {
                var file = new FileInfo(dlg.FileName);
                if (file.Length > MaxBytes)
                {
                    ShowEmpty(Lang.T("الملف كبير مرة (أكثر من 60 ميقا)"));
                    return;
                }
                Directory.CreateDirectory(Folder);
                // A new name each time, so the old picture (maybe still being read) isn't in the way.
                string target = System.IO.Path.Combine(Folder, $"{Info.Id}-{DateTime.Now.Ticks}{file.Extension.ToLowerInvariant()}");
                File.Copy(file.FullName, target);
                string? old = Info.Options.TryGetValue("image", out var o) ? o : null;
                Info.Options["image"] = target;
                Widgets.Save();
                if (old != null && old != target) TryDelete(old);
                _loaded = "";
                Load();
            }
            catch (Exception ex)
            {
                CrashLog.Write("picture widget pick", ex);
                ShowEmpty(Lang.T("ما قدرت أفتح الصورة"));
            }
        }

        /// <summary>The widget was removed: its copied pictures go too.</summary>
        public static void DeleteFiles(string id)
        {
            try
            {
                if (!Directory.Exists(Folder)) return;
                foreach (var f in Directory.GetFiles(Folder, id + "*")) TryDelete(f);
            }
            catch { }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }

        // ---- Loading ------------------------------------------------------------------------------

        /// <summary>How many pixels the card needs on its long side (and a little more for sharpness).</summary>
        private int Needed()
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            double side = Math.Max(Info?.W ?? 3, Info?.H ?? 3) * 110 * dpi.DpiScaleX;
            return (int)Math.Clamp(side, 200, 1400);
        }

        private void Load()
        {
            string path = Info?.Options.TryGetValue("image", out var p) == true ? p : "";
            if (path.Length == 0 || !File.Exists(path)) { ShowEmpty(Lang.T("اختر صورة أو GIF")); return; }
            int need = Needed();
            string key = path + "|" + need;
            if (key == _loaded) return;
            _loaded = key;
            int id = ++_loadId;
            Task.Run(() => Decode(path, need)).ContinueWith(t =>
            {
                if (id != _loadId) return;   // a newer one was asked for
                if (t.Exception != null || t.Result.Frames.Length == 0)
                {
                    if (t.Exception != null) CrashLog.Write("picture widget load", t.Exception);
                    _loaded = "";
                    ShowEmpty(Lang.T("ما قدرت أفتح الصورة"));
                    return;
                }
                Show(t.Result.Frames, t.Result.Delays);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void ShowEmpty(string text)
        {
            _player.Stop();
            _frames = Array.Empty<BitmapSource>();
            _brush.ImageSource = null;
            _picture.Visibility = Visibility.Collapsed;
            _emptyText.Text = text;
            _empty.Visibility = Visibility.Visible;
        }

        private void Show(BitmapSource[] frames, int[] delays)
        {
            _player.Stop();
            _frames = frames;
            _delays = delays;
            _frame = 0;
            _brush.ImageSource = frames[0];
            _picture.Visibility = Visibility.Visible;
            _empty.Visibility = Visibility.Collapsed;
            if (frames.Length > 1 && _running)
            {
                _player.Interval = TimeSpan.FromMilliseconds(delays[0]);
                _player.Start();
            }
        }

        private void NextFrame()
        {
            if (_frames.Length < 2) { _player.Stop(); return; }
            _frame = (_frame + 1) % _frames.Length;
            _brush.ImageSource = _frames[_frame];
            _player.Interval = TimeSpan.FromMilliseconds(_delays[_frame]);
        }

        // ---- Decoding (off the UI thread) ---------------------------------------------------------

        private static (BitmapSource[] Frames, int[] Delays) Decode(string path, int need)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            {
                var gif = DecodeGif(bytes, need);
                if (gif.Frames.Length > 0) return gif;
            }

            // A still picture: Windows' decoders (WebP and HEIC too when installed), already scaled down.
            var img = new BitmapImage();
            img.BeginInit();
            img.StreamSource = new MemoryStream(bytes);
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            img.EndInit();
            BitmapSource result = img;
            if (Math.Max(img.PixelWidth, img.PixelHeight) > need)
            {
                double s = need / (double)Math.Max(img.PixelWidth, img.PixelHeight);
                result = new TransformedBitmap(img, new ScaleTransform(s, s));
            }
            // The photo's own turn (phones save it sideways with an "up is here" note).
            result = Oriented(bytes, result);
            result.Freeze();
            return (new[] { result }, new[] { 0 });
        }

        /// <summary>Every frame of a GIF, put together the way browsers show it (GDI+ does the
        /// layering), scaled so all of them fit in about 200 MB. Times as the GIF says, with
        /// "as fast as possible" (under 20 ms) treated as 100 ms like browsers do.</summary>
        private static (BitmapSource[] Frames, int[] Delays) DecodeGif(byte[] bytes, int need)
        {
            using var ms = new MemoryStream(bytes);
            using var img = System.Drawing.Image.FromStream(ms);
            var dim = new FrameDimension(img.FrameDimensionsList[0]);
            int count = Math.Min(img.GetFrameCount(dim), 1000);

            int[] delays = new int[count];
            try
            {
                var raw = img.GetPropertyItem(0x5100)?.Value;   // frame delays, in 1/100 s
                for (int i = 0; i < count; i++)
                {
                    int d = raw != null && raw.Length >= (i + 1) * 4 ? BitConverter.ToInt32(raw, i * 4) * 10 : 100;
                    delays[i] = d < 20 ? 100 : d;
                }
            }
            catch { for (int i = 0; i < count; i++) delays[i] = 100; }

            double scale = Math.Min(1, need / (double)Math.Max(img.Width, img.Height));
            const double budget = 200.0 * 1024 * 1024;
            double bytesAll = count * img.Width * img.Height * 4.0 * scale * scale;
            if (bytesAll > budget) scale *= Math.Sqrt(budget / bytesAll);
            int w = Math.Max(1, (int)Math.Round(img.Width * scale)), h = Math.Max(1, (int)Math.Round(img.Height * scale));

            var frames = new BitmapSource[count];
            using var canvas = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using var g = System.Drawing.Graphics.FromImage(canvas);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            for (int i = 0; i < count; i++)
            {
                img.SelectActiveFrame(dim, i);
                g.Clear(System.Drawing.Color.Transparent);
                g.DrawImage(img, 0, 0, w, h);
                var data = canvas.LockBits(new System.Drawing.Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, canvas.PixelFormat);
                try
                {
                    var frame = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, data.Scan0, data.Stride * h, data.Stride);
                    frame.Freeze();
                    frames[i] = frame;
                }
                finally { canvas.UnlockBits(data); }
            }
            return (frames, delays);
        }

        /// <summary>Turns a photo the way its EXIF orientation says (1 = as it is).</summary>
        private static BitmapSource Oriented(byte[] bytes, BitmapSource source)
        {
            try
            {
                var frame = BitmapFrame.Create(new MemoryStream(bytes), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                if (frame.Metadata is not BitmapMetadata meta || !meta.ContainsQuery("System.Photo.Orientation")) return source;
                int o = Convert.ToInt32(meta.GetQuery("System.Photo.Orientation"));
                Transform? t = o switch
                {
                    3 => new RotateTransform(180),
                    6 => new RotateTransform(90),
                    8 => new RotateTransform(270),
                    2 => new ScaleTransform(-1, 1),
                    4 => new ScaleTransform(1, -1),
                    _ => null,
                };
                return t == null ? source : new TransformedBitmap(source, t);
            }
            catch { return source; }
        }
    }
}
