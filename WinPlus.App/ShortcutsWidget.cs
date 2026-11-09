using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinPlus
{
    /// <summary>
    /// The shortcuts widget: the programs, folders and files you like, each as its icon and
    /// name; a click opens it. Add them by dropping them on it (from Explorer, the desktop or
    /// the Start menu's folder) or from its right-click menu; right-click one to open where it
    /// is or take it off. The list is kept in the widget's options, one path per line.
    /// </summary>
    internal sealed class ShortcutsWidget : WidgetContent
    {
        private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

        private readonly SolidColorBrush _ink = new(Color.FromRgb(0xF2, 0xF3, 0xF5));
        private readonly SolidColorBrush _inkMuted = new(Color.FromRgb(0xA0, 0xA3, 0xAA));
        private readonly SolidColorBrush _accent = new(Color.FromRgb(0x4C, 0x8D, 0xFF));
        // Always 3 × 3, at any widget size (they grow with it); more than 9 and they all shrink to fit. Never a scroll.
        private readonly System.Windows.Controls.Primitives.UniformGrid _tiles = new();
        private readonly TextBlock _empty;
        private readonly Border _dropHint;

        public ShortcutsWidget()
        {
            _empty = new TextBlock
            {
                Text = Lang.T("اسحب برامج أو مجلدات هنا، أو كليك يمين ← أضف"),
                FontSize = 12, Foreground = _inkMuted, TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6),
            };
            _dropHint = new Border
            {
                BorderBrush = _accent, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(10),
                Margin = new Thickness(-4), Visibility = Visibility.Collapsed, IsHitTestVisible = false,
            };
            var grid = new Grid();
            grid.Children.Add(_tiles);
            grid.Children.Add(_empty);
            grid.Children.Add(_dropHint);
            Child = grid;
            Background = Brushes.Transparent;   // drops land anywhere in it

            AllowDrop = true;
            DragEnter += (_, e) => { e.Effects = Files(e) != null ? DragDropEffects.Link : DragDropEffects.None; _dropHint.Visibility = e.Effects == DragDropEffects.None ? Visibility.Collapsed : Visibility.Visible; e.Handled = true; };
            DragOver += (_, e) => { e.Effects = Files(e) != null ? DragDropEffects.Link : DragDropEffects.None; e.Handled = true; };
            DragLeave += (_, _) => _dropHint.Visibility = Visibility.Collapsed;
            Drop += (_, e) =>
            {
                _dropHint.Visibility = Visibility.Collapsed;
                if (Files(e) is { } files) AddPaths(files);
                e.Handled = true;
            };
        }

        private static string[]? Files(DragEventArgs e) =>
            e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0 ? f : null;

        public override void Start() => Rebuild();

        public override void SetAccent(Color color) => _accent.Color = color;

        public override void SetLightBehind(bool light)
        {
            _ink.Color = light ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xF2, 0xF3, 0xF5);
            _inkMuted.Color = light ? Color.FromArgb(0xB0, 0x3C, 0x3C, 0x43) : Color.FromRgb(0xA0, 0xA3, 0xAA);
        }

        // ---- The list ---------------------------------------------------------------------------

        private List<string> Items =>
            (Info?.Options.TryGetValue("items", out var s) == true ? s : "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        private void SaveItems(List<string> items)
        {
            if (Info == null) return;
            Info.Options["items"] = string.Join("\n", items);
            Widgets.Save();
            Rebuild();
        }

        private void AddPaths(IEnumerable<string> paths)
        {
            var items = Items;
            foreach (var p in paths)
                if (!items.Contains(p, StringComparer.OrdinalIgnoreCase)) items.Add(p);
            SaveItems(items);
        }

        private void Remove(string path)
        {
            var items = Items;
            items.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            DropIcon(path);
            Info?.Options.Remove("icon:" + path);
            Info?.Options.Remove("color:" + path);
            SaveItems(items);
        }

        /// <summary>Moves one a step toward the start (-1) or the end (+1).</summary>
        private void Shift(string path, int by)
        {
            var items = Items;
            int i = items.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            int j = i + by;
            if (i < 0 || j < 0 || j >= items.Count) return;
            (items[i], items[j]) = (items[j], items[i]);
            SaveItems(items);
        }

        private void Rebuild()
        {
            _tiles.Children.Clear();
            var items = Items;
            // Always 3 × 3 (they grow with the widget); a 10th shortcut and on adds rows, and they all shrink to fit.
            _tiles.Columns = 3;
            _tiles.Rows = Math.Max(3, (items.Count + 2) / 3);
            foreach (var path in items) _tiles.Children.Add(Tile(path, items.Count));
            _empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- One shortcut -------------------------------------------------------------------------

        private Button Tile(string path, int count)
        {
            bool there = File.Exists(path) || Directory.Exists(path);
            // Drawn at one size and scaled to its cell, so the icon and the name grow and shrink together.
            var panel = new StackPanel { Width = 74 };
            var icon = IconElement(path);
            icon.Width = icon.Height = 40;
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.Margin = new Thickness(0, 0, 0, 4);
            panel.Children.Add(icon);
            string name = NameOf(path);
            panel.Children.Add(new TextBlock
            {
                Text = name, FontSize = 12, Foreground = _ink,
                TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 74,
                // Each name in its own language's direction ("C:" not ":C", English cut at its end).
                FlowDirection = StartsRtl(name) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            });

            var b = new Button
            {
                Style = (Style)Application.Current.FindResource("FlatButton"),
                Content = new Viewbox { Child = panel, Stretch = Stretch.Uniform },
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(2), Margin = new Thickness(1),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = path, Opacity = there ? 1 : 0.4, Cursor = System.Windows.Input.Cursors.Hand,
            };
            b.Click += (_, _) => Open(path);
            b.ContextMenu = TileMenu(path, count);
            return b;
        }

        // ---- Its own look: a picture, or a folder's color --------------------------------------------

        private string? CustomIcon(string path) =>
            Info?.Options.TryGetValue("icon:" + path, out var p) == true && File.Exists(p) ? p : null;

        /// <summary>The color all its folders have; null = Windows' own yellow folder.</summary>
        private Color? FolderColor() =>
            Info?.Options.TryGetValue("folderColor", out var hex) == true && TryColor(hex, out var c) ? c : null;

        private static bool TryColor(string hex, out Color c)
        {
            try { c = (Color)ColorConverter.ConvertFromString(hex); return true; }
            catch { c = default; return false; }
        }

        private FrameworkElement IconElement(string path)
        {
            if (CustomIcon(path) is { } file)
            {
                try
                {
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.UriSource = new Uri(file);
                    img.DecodePixelWidth = 96;
                    img.CacheOption = BitmapCacheOption.OnLoad;   // the file isn't kept open
                    img.EndInit();
                    img.Freeze();
                    return new Image { Source = img, Stretch = Stretch.Uniform };
                }
                catch { }
            }
            if (FolderColor() is { } color && Directory.Exists(path) && new DirectoryInfo(path).Parent != null) return Folder(color);   // drives keep their icon
            return new Image { Source = Icon(path), Stretch = Stretch.Uniform };
        }

        /// <summary>A folder like Windows 11's, in any color: the back a shade darker, the front with a soft light from the top.</summary>
        private static FrameworkElement Folder(Color c)
        {
            // The back a little darker for depth; on a very light color (white) only a touch, so it stays white, not gray.
            double light = (c.R * 3 + c.G * 6 + c.B) / 10.0 / 255;
            double depth = light > 0.85 ? 0.94 : light > 0.65 ? 0.86 : 0.78;
            Color Shade(double f) => Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));
            Color Tint(double f) => Color.FromRgb((byte)(c.R + (255 - c.R) * f), (byte)(c.G + (255 - c.G) * f), (byte)(c.B + (255 - c.B) * f));
            var canvas = new Canvas { Width = 24, Height = 24 };
            canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 2,6 A 2,2 0 0 1 4,4 H 9 L 11,6 H 20 A 2,2 0 0 1 22,8 V 18 A 2,2 0 0 1 20,20 H 4 A 2,2 0 0 1 2,18 Z"),
                Fill = new SolidColorBrush(Shade(depth)),
            });
            canvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 2,10 A 2,2 0 0 1 4,8 H 20 A 2,2 0 0 1 22,10 V 18 A 2,2 0 0 1 20,20 H 4 A 2,2 0 0 1 2,18 Z"),
                Fill = new LinearGradientBrush(Tint(0.18), c, 90),
            });
            return new Viewbox { Child = canvas, Stretch = Stretch.Uniform };
        }

        private static readonly (string Name, string Hex)[] FolderColors =
        {
            ("أصفر", "#FFC83D"), ("أزرق", "#4C8DFF"), ("سماوي", "#40C8E0"), ("أخضر", "#34C759"),
            ("برتقالي", "#FF9500"), ("أحمر", "#FF453A"), ("وردي", "#FF5FA2"), ("بنفسجي", "#AF52DE"),
            ("أبيض", "#FFFFFF"), ("رمادي", "#8E8E93"), ("أسود", "#2C2C2E"),
        };

        private void SetLook(string key, string? value)
        {
            if (Info == null) return;
            if (value == null) Info.Options.Remove(key); else Info.Options[key] = value;
            Widgets.Save();
            Rebuild();
        }

        private void PickIcon(string path)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = Lang.T("اختر أيقونة"),
                Filter = Lang.T("الصور") + " (*.png, *.ico, *.jpg, *.webp, *.gif, *.bmp)|*.png;*.ico;*.jpg;*.jpeg;*.webp;*.gif;*.bmp",
            };
            if (dlg.ShowDialog() != true || Info == null) return;
            try
            {
                // A copy (like the picture widget), so moving the original doesn't matter; gone with the widget.
                Directory.CreateDirectory(PictureWidget.Folder);
                string target = Path.Combine(PictureWidget.Folder, $"{Info.Id}-icon-{DateTime.Now.Ticks}{Path.GetExtension(dlg.FileName).ToLowerInvariant()}");
                File.Copy(dlg.FileName, target);
                DropIcon(path);
                SetLook("icon:" + path, target);
            }
            catch (Exception ex) { CrashLog.Write("shortcut icon", ex); }
        }

        /// <summary>Deletes the copied picture of one shortcut (when it's changed, reset or removed).</summary>
        private void DropIcon(string path)
        {
            if (Info?.Options.TryGetValue("icon:" + path, out var old) == true)
                try { File.Delete(old); } catch { }
        }

        private void PickFolderColor()
        {
            var now = FolderColor() ?? Color.FromRgb(0xFF, 0xC8, 0x3D);
            using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true, Color = System.Drawing.Color.FromArgb(now.R, now.G, now.B) };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                SetLook("folderColor", $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
        }

        /// <summary>The color of every folder in the widget (one with its own picture keeps it).</summary>
        private MenuItem FolderColorMenu()
        {
            var colors = new MenuItem { Header = Lang.T("لون المجلدات") };
            var original = new MenuItem { Header = Lang.T("لون ويندوز"), Tag = "" };
            original.Click += (_, _) => SetLook("folderColor", null);
            colors.Items.Add(original);
            colors.Items.Add(new Separator());
            foreach (var (name, hex) in FolderColors)
            {
                TryColor(hex, out var c);
                var choice = new MenuItem
                {
                    Header = Lang.T(name), Tag = hex,
                    Icon = new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(c), Stroke = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1 },
                };
                string h = hex;
                choice.Click += (_, _) => SetLook("folderColor", h);
                colors.Items.Add(choice);
            }
            colors.Items.Add(new Separator());
            var other = new MenuItem { Header = Lang.T("لون ثاني…") };
            other.Click += (_, _) => PickFolderColor();
            colors.Items.Add(other);
            colors.SubmenuOpened += (_, e) =>
            {
                if (e.OriginalSource != colors) return;
                string now = Info?.Options.TryGetValue("folderColor", out var v) == true ? v : "";
                bool listed = now.Length == 0 || FolderColors.Any(f => string.Equals(f.Hex, now, StringComparison.OrdinalIgnoreCase));
                foreach (var i in colors.Items.OfType<MenuItem>())
                    i.IsChecked = i.Tag is string t ? string.Equals(t, now, StringComparison.OrdinalIgnoreCase) : !listed;
            };
            return colors;
        }

        private ContextMenu TileMenu(string path, int count)
        {
            var app = Application.Current;
            var menu = new ContextMenu { FlowDirection = Lang.Flow, Style = (Style)app.FindResource("DarkContextMenu") };
            menu.Resources[typeof(MenuItem)] = app.FindResource("DarkMenuItem");
            menu.Resources[MenuItem.SeparatorStyleKey] = app.FindResource("DarkMenuSeparator");

            MenuItem Item(string text, Action act)
            {
                var m = new MenuItem { Header = Lang.T(text) };
                m.Click += (_, _) => act();
                return m;
            }
            menu.Items.Add(Item("افتح", () => Open(path)));
            menu.Items.Add(Item("افتح مكانه", () => Reveal(path)));
            menu.Items.Add(new Separator());
            int at = Items.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            var before = Item("قدّمه", () => Shift(path, -1));
            var after = Item("أخّره", () => Shift(path, 1));
            before.IsEnabled = at > 0;
            after.IsEnabled = at >= 0 && at < count - 1;
            menu.Items.Add(before);
            menu.Items.Add(after);
            menu.Items.Add(new Separator());

            // Its look: a picture of your own (any shortcut), a color (folders), or back to Windows' icon.
            menu.Items.Add(Item("غيّر الأيقونة…", () => PickIcon(path)));
            if (Directory.Exists(path)) menu.Items.Add(FolderColorMenu());
            bool custom = Info != null && Info.Options.ContainsKey("icon:" + path);
            var reset = Item("رجّع الأيقونة الأصلية", () =>
            {
                DropIcon(path);
                SetLook("icon:" + path, null);
            });
            reset.IsEnabled = custom;
            menu.Items.Add(reset);

            menu.Items.Add(new Separator());
            menu.Items.Add(Item("شيله من الاختصارات", () => Remove(path)));
            return menu;
        }

        /// <summary>Is the first letter Arabic / Hebrew (written right to left)?</summary>
        private static bool StartsRtl(string s)
        {
            foreach (char ch in s)
            {
                if (ch >= 0x0590 && ch <= 0x08FF || ch >= 0xFB1D && ch <= 0xFEFC) return true;
                if (char.IsLetter(ch)) return false;
            }
            return false;
        }

        private static string NameOf(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    var d = new DirectoryInfo(path);
                    return d.Parent == null ? path.TrimEnd('\\') : d.Name;   // "C:" for a drive
                }
                if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                {
                    var info = FileVersionInfo.GetVersionInfo(path);
                    string desc = (info.FileDescription ?? "").Trim();
                    if (desc.Length is > 0 and <= 30) return desc;
                }
                return Path.GetFileNameWithoutExtension(path) is { Length: > 0 } n ? n : path;
            }
            catch { return Path.GetFileName(path); }
        }

        private void Open(string path)
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    MessageBox.Show(Lang.T("ما لقيته، يمكن انحذف أو انتقل:") + "\n" + path, "Wibn", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Process.Start(new ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                    WorkingDirectory = File.Exists(path) ? Path.GetDirectoryName(path) ?? "" : path,
                });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Cancelled (e.g. "No" on the administrator prompt): nothing to say.
            }
        }

        private static void Reveal(string path)
        {
            try
            {
                if (File.Exists(path) || Directory.Exists(path))
                    Process.Start("explorer.exe", $"/select,\"{path}\"");
                else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
                    Process.Start("explorer.exe", $"\"{dir}\"");
            }
            catch { }
        }

        // ---- Its menu -----------------------------------------------------------------------------

        public override IEnumerable<Control> MenuItems()
        {
            var add = new MenuItem { Header = Lang.T("أضف") };
            var program = new MenuItem { Header = Lang.T("برنامج أو ملف…") };
            program.Click += (_, _) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = Lang.T("اختر برنامج أو ملف"), Multiselect = true, DereferenceLinks = false,
                    Filter = Lang.T("البرامج") + " (*.exe, *.lnk)|*.exe;*.lnk;*.url;*.bat;*.cmd|" + Lang.T("كل الملفات") + "|*.*",
                };
                if (dlg.ShowDialog() == true) AddPaths(dlg.FileNames);
            };
            var folder = new MenuItem { Header = Lang.T("مجلد…") };
            folder.Click += (_, _) =>
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Lang.T("اختر مجلد"), Multiselect = true };
                if (dlg.ShowDialog() == true) AddPaths(dlg.FolderNames);
            };
            add.Items.Add(program);
            add.Items.Add(folder);
            yield return add;
            yield return FolderColorMenu();
        }

        // ---- Icons (as big and sharp as Explorer's) -----------------------------------------------

        private static ImageSource? Icon(string path)
        {
            if (IconCache.TryGetValue(path, out var cached)) return cached;
            ImageSource? image = null;
            try { image = ShellIcon(path, 64); } catch { }
            if (image == null)
            {
                try
                {
                    using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                    if (icon != null)
                    {
                        var s = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        s.Freeze();
                        image = s;
                    }
                }
                catch { }
            }
            if (File.Exists(path) || Directory.Exists(path)) IconCache[path] = image;   // a missing one may come back
            return image;
        }

        /// <summary>The shell's own icon (folders, shortcuts, programs, any file), with its see-through edges.</summary>
        private static BitmapSource? ShellIcon(string path, int size)
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
            try
            {
                if (factory.GetImage(new SIZE { cx = size, cy = size }, 0x1 /* BIGGERSIZEOK */ | 0x4 /* ICONONLY */, out IntPtr hbm) != 0 || hbm == IntPtr.Zero)
                    return null;
                try
                {
                    var bm = new BITMAP();
                    if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), ref bm) == 0) return null;
                    int w = bm.bmWidth, h = bm.bmHeight;
                    var bmi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h /* top-down */, biPlanes = 1, biBitCount = 32 };
                    var pixels = new byte[w * h * 4];
                    IntPtr dc = GetDC(IntPtr.Zero);
                    try { if (GetDIBits(dc, hbm, 0, (uint)h, pixels, ref bmi, 0) == 0) return null; }
                    finally { ReleaseDC(IntPtr.Zero, dc); }
                    // An old icon with no see-through info at all comes with every pixel at 0: show it whole.
                    bool anyAlpha = false;
                    for (int i = 3; i < pixels.Length && !anyAlpha; i += 4) anyAlpha = pixels[i] != 0;
                    if (!anyAlpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                    var source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, w * 4);
                    source.Freeze();
                    return source;
                }
                finally { DeleteObject(hbm); }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx, ref Guid riid, out IShellItemImageFactory item);
        [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, ref BITMAP bm);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr hbm, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    }
}
