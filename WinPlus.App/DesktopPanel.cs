using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinPlus
{
    /// <summary>
    /// The list of the desktop's files, in the middle of the screen, in the widgets' liquid
    /// glass and color. It can be dragged around; a click anywhere outside it hides it,
    /// unless it's pinned.
    /// </summary>
    internal sealed class DesktopPanel : Window
    {
        private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
        private static readonly Brush PlainBack = new SolidColorBrush(Color.FromArgb(0xE8, 0x1E, 0x1F, 0x22));
        private static readonly Brush PlainEdge = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
        private static readonly Brush GlassEdge = new LinearGradientBrush(
            Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 90);

        // Light text on a dark glass, dark text on a light one (like the widgets).
        private readonly SolidColorBrush _fg = new(Color.FromRgb(0xF2, 0xF3, 0xF5));
        private readonly SolidColorBrush _muted = new(Color.FromRgb(0xA0, 0xA3, 0xAA));
        private readonly SolidColorBrush _hover = new(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
        private readonly SolidColorBrush _pressed = new(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        private readonly SolidColorBrush _field = new(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        private readonly SolidColorBrush _accent;

        private readonly Window? _opener;
        private readonly Grid _host;
        private readonly Border _card;
        private readonly WrapPanel _tiles = new();
        private readonly TextBlock _empty, _count, _placeholder;
        private readonly TextBox _search;
        private readonly Border _pinButton;
        private readonly TextBlock _pinGlyph;
        private readonly OutsideClickWatcher _outside = new();
        private LiquidGlass? _glass;
        private readonly ScaleTransform _zoom = new(1, 1);
        private readonly Dictionary<string, FrameworkElement> _tileCache = new(StringComparer.OrdinalIgnoreCase);
        private bool _waitingForGlass;
        private readonly System.Windows.Threading.DispatcherTimer _revealFallback = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private const double MinTile = 104;
        private List<string> _items = new();
        private bool _pinned;

        /// <summary>The pin button was pressed: pinned (true) or not.</summary>
        public event Action<bool>? PinChanged;

        /// <param name="opener">The widget that opens it (a click on it toggles, so it doesn't count as outside).</param>
        public DesktopPanel(Color accent, Window? opener)
        {
            _accent = new SolidColorBrush(accent);
            _opener = opener;
            Title = Lang.T("سطح المكتب");
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;   // no size grip in the corner
            Width = 760;
            Height = 500;
            WindowStartupLocation = WindowStartupLocation.Manual;
            FontFamily = Widgets.Font();
            Foreground = _fg;

            // Header: title, count … search, pin, close
            var header = new DockPanel { Margin = new Thickness(18, 14, 12, 10), Background = Brushes.Transparent };
            var close = IconButton(Glyph(""), Lang.T("سكّر"));
            close.MouseLeftButtonUp += (_, e) => { e.Handled = true; HideIt(); };
            _pinGlyph = Glyph("", 14);
            _pinButton = IconButton(_pinGlyph, "");
            _pinButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowPinned(!_pinned); PinChanged?.Invoke(_pinned); };
            DockPanel.SetDock(close, Dock.Right);
            DockPanel.SetDock(_pinButton, Dock.Right);
            header.Children.Add(close);
            header.Children.Add(_pinButton);

            _search = new TextBox
            {
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = _fg, CaretBrush = _fg,
                FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0),
            };
            _placeholder = new TextBlock { Text = Lang.T("دوّر…"), Foreground = _muted, FontSize = 13, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
            _search.TextChanged += (_, _) => { _placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Fill(); };
            var searchRow = new DockPanel();
            var searchIcon = new TextBlock { Text = "", FontFamily = IconFont, FontSize = 12, Foreground = _muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(searchIcon, Dock.Left);
            searchRow.Children.Add(searchIcon);
            searchRow.Children.Add(new Grid { Children = { _placeholder, _search } });
            var searchBox = new Border
            {
                Width = 200, Height = 30, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 0, 10, 0),
                Background = _field, Margin = new Thickness(0, 0, 8, 0), Child = searchRow,
            };
            DockPanel.SetDock(searchBox, Dock.Right);
            header.Children.Add(searchBox);

            var titles = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(new TextBlock { Text = Lang.T("سطح المكتب"), FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = _fg, VerticalAlignment = VerticalAlignment.Center });
            _count = new TextBlock { FontSize = 12, Foreground = _muted, Margin = new Thickness(10, 3, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(_count);
            header.Children.Add(titles);

            // The files
            _tiles.Margin = new Thickness(10, 0, 10, 10);
            _empty = new TextBlock { Foreground = _muted, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            var scroll = new ScrollViewer
            {
                Content = _tiles, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
            };
            SmoothScroll.Attach(scroll);
            // The tiles share the whole width (no empty strip at the side).
            scroll.ScrollChanged += (_, e) => { if (e.ViewportWidthChange != 0) FitColumns(scroll.ViewportWidth); };
            scroll.SizeChanged += (_, _) => FitColumns(scroll.ViewportWidth);
            var body = new Grid();
            body.Children.Add(scroll);
            body.Children.Add(_empty);

            var root = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            root.Children.Add(body);

            // Like a widget's card: rounded, its edge, and the glass (or the plain color) behind.
            _card = new Border
            {
                CornerRadius = new CornerRadius(16), Background = PlainBack, BorderBrush = PlainEdge, BorderThickness = new Thickness(1),
                Margin = new Thickness(4), Child = root, FlowDirection = Lang.Flow,
                RenderTransform = _zoom, RenderTransformOrigin = new Point(0.5, 0.5),
            };
            _host = new Grid { FlowDirection = FlowDirection.LeftToRight, Background = Brushes.Transparent };
            _host.Children.Add(_card);
            Content = _host;

            // Drag it by any empty spot (the files and buttons handle their own clicks).
            _card.MouseLeftButtonDown += (_, e) =>
            {
                if (e.Handled || IsInside<TextBox>(e.OriginalSource as DependencyObject)) return;
                try { DragMove(); } catch (InvalidOperationException) { }
            };
            LocationChanged += (_, _) => _glass?.Follow();
            KeyDown += (_, e) => { if (e.Key == Key.Escape) HideIt(); };
            _outside.Clicked += OnClickSomewhere;
            Closed += (_, _) => { _outside.Stop(); StopGlass(); _revealFallback.Stop(); };
            _revealFallback.Tick += (_, _) => Reveal();
        }

        public void SetAccent(Color c)
        {
            _accent.Color = c;
            if (_pinned) _pinButton.Background = _accent;
        }

        public void ShowPinned(bool pinned)
        {
            _pinned = pinned;
            _pinGlyph.Text = pinned ? "" /* pinned */ : "" /* pin */;
            _pinGlyph.Foreground = pinned ? Brushes.White : _fg;
            _pinButton.Background = pinned ? _accent : Brushes.Transparent;
            _pinButton.ToolTip = pinned
                ? Lang.T("مثبّتة: ما تختفي لو ضغطت برّاها. اضغط عشان تلغي التثبيت")
                : Lang.T("ثبّتها: ما تختفي لو ضغطت برّاها");
        }

        /// <summary>Shows it in the middle of the screen the mouse is on (it opens there even if it
        /// was moved last time), and starts listening for clicks outside it. It appears only once
        /// its glass is drawn, so it never shows the plain color first and then jumps to the glass.</summary>
        public void ShowCentered()
        {
            _card.Opacity = 0;
            _search.Text = "";
            Show();
            var dpi = VisualTreeHelper.GetDpi(this);
            var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
            double x = area.X / dpi.DpiScaleX, y = area.Y / dpi.DpiScaleY, w = area.Width / dpi.DpiScaleX, h = area.Height / dpi.DpiScaleY;
            Width = Math.Min(760, w - 40);
            Height = Math.Min(500, h - 40);
            Left = x + (w - Width) / 2;
            Top = y + (h - Height) / 2;
            Activate();
            _search.Focus();
            _outside.Start();

            if (_glass != null)
            {
                _glass.Follow();   // kept from last time: only its spot changes, drawn from the picture it has
                Reveal();
                return;
            }
            UpdateLayout();
            _waitingForGlass = true;
            _revealFallback.Start();
            StartGlass();
            if (_glass == null) Reveal();   // no glass: the plain color is already there
        }

        /// <summary>Grows a little into place while it fades in.</summary>
        private void Reveal()
        {
            _waitingForGlass = false;
            _revealFallback.Stop();
            var d = AnimSettings.Open;   // the Animations page
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = ease });
            _zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, d) { EasingFunction = ease });
            _zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, d) { EasingFunction = ease });
        }

        public void HideIt()
        {
            if (!IsVisible) return;
            _outside.Stop();
            _revealFallback.Stop();
            _waitingForGlass = false;
            var d = AnimSettings.Close;
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var fade = new DoubleAnimation(0, d) { EasingFunction = ease };
            fade.Completed += (_, _) => { if (_card.Opacity == 0) Hide(); };
            _card.BeginAnimation(OpacityProperty, fade);
            _zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, d) { EasingFunction = ease });
            _zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, d) { EasingFunction = ease });
        }

        /// <summary>A mouse button went down somewhere (the window under it is given): outside the
        /// panel and its own menus, it hides. The widget that opens it toggles it by itself.</summary>
        private void OnClickSomewhere(IntPtr hwnd)
        {
            if (_pinned || !IsVisible) return;
            IntPtr top = GetAncestor(hwnd, 2 /* GA_ROOT */);
            var source = top == IntPtr.Zero ? null : System.Windows.Interop.HwndSource.FromHwnd(top);
            if (source != null)
            {
                if (source.RootVisual is Window w) { if (w == this || w == _opener) return; }
                else return;   // its own menus and tooltips (popups)
            }
            HideIt();
        }

        /// <summary>The desktop's items. Tiles are made once per file and kept, so opening it
        /// again (or a file being added) doesn't rebuild every tile and icon.</summary>
        public void Load(List<string> items)
        {
            if (items.SequenceEqual(_items, StringComparer.OrdinalIgnoreCase) && _tiles.Children.Count > 0 && _search.Text.Length == 0) return;
            _items = items;
            var keep = new HashSet<string>(items, StringComparer.OrdinalIgnoreCase);
            foreach (var gone in _tileCache.Keys.Where(k => !keep.Contains(k)).ToList()) _tileCache.Remove(gone);
            Fill();
        }

        private void Fill()
        {
            string q = _search.Text.Trim();
            var shown = q.Length == 0 ? _items : _items.Where(p => DesktopCorner.DisplayName(p).Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
            _tiles.Children.Clear();
            foreach (var path in shown)
            {
                if (!_tileCache.TryGetValue(path, out var tile)) _tileCache[path] = tile = Tile(path);
                _tiles.Children.Add(tile);
            }
            _count.Text = Lang.F("{0} عنصر", _items.Count);
            _empty.Text = _items.Count == 0 ? Lang.T("سطح المكتب فاضي") : Lang.T("ما فيه شي بهالاسم");
            _empty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>As many columns as fit, each as wide as the width shared out between them.</summary>
        private void FitColumns(double viewport)
        {
            double room = viewport - _tiles.Margin.Left - _tiles.Margin.Right;
            if (room < MinTile) return;
            int columns = Math.Max(1, (int)(room / MinTile));
            double width = Math.Floor(room / columns);
            if (double.IsNaN(_tiles.ItemWidth) || Math.Abs(_tiles.ItemWidth - width) > 0.5) _tiles.ItemWidth = width;
        }

        // ---- Liquid glass (the widgets' settings and color) ----------------------------------------

        private void StartGlass()
        {
            StopGlass();
            if (!GlassSettings.Widgets.On) { ShowPlain(); return; }
            try
            {
                Point at = _card.TranslatePoint(new Point(0, 0), _host);
                var glass = new LiquidGlass(this, _host, _card, new Thickness(at.X, at.Y, 0, 0), 16, GlassSettings.Widgets, desktop: true,
                                            tint: Widgets.BackColor());
                if (!glass.Available) { ShowPlain(); return; }
                _glass = glass;
                _card.BorderBrush = GlassEdge;
                glass.LightBehindChanged += light =>
                {
                    SetLightBehind(light);
                    if (_waitingForGlass) Reveal();   // its first picture is on: now it can appear
                };
                glass.Start();
            }
            catch (Exception ex) { ShowPlain(); CrashLog.Write("desktop panel glass", ex); }
        }

        private void StopGlass()
        {
            _glass?.Dispose();
            _glass = null;
        }

        /// <summary>No glass: dark gray, or the widgets' color (dark text on a light one).</summary>
        private void ShowPlain()
        {
            var back = Widgets.BackColor();
            _card.Background = back is { } b ? new SolidColorBrush(Color.FromArgb(0xF0, b.Color.R, b.Color.G, b.Color.B)) : PlainBack;
            _card.BorderBrush = PlainEdge;
            SetLightBehind(back is { } l && Widgets.Lightness(l.Color) > 0.6);
        }

        private void SetLightBehind(bool light)
        {
            var d = new Duration(TimeSpan.FromMilliseconds(200));
            void To(SolidColorBrush b, Color c) => b.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(c, d));
            To(_fg, light ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xF2, 0xF3, 0xF5));
            To(_muted, light ? Color.FromArgb(0xB0, 0x3C, 0x3C, 0x43) : Color.FromRgb(0xA0, 0xA3, 0xAA));
            To(_hover, light ? Color.FromArgb(0x16, 0x00, 0x00, 0x00) : Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
            To(_pressed, light ? Color.FromArgb(0x26, 0x00, 0x00, 0x00) : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            To(_field, light ? Color.FromArgb(0x18, 0x00, 0x00, 0x00) : Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        }

        // ---- One file ----------------------------------------------------------------------------

        private FrameworkElement Tile(string path)
        {
            var image = new Image { Width = 48, Height = 48, Source = ShortcutsWidget.Icon(path), Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var name = new TextBlock
            {
                Text = DesktopCorner.DisplayName(path), FontSize = 12, Foreground = _fg, TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 34, Margin = new Thickness(0, 6, 0, 0),
                FlowDirection = FlowDirection.LeftToRight,
            };
            var stack = new StackPanel();
            stack.Children.Add(image);
            stack.Children.Add(name);
            var tile = new Border
            {
                Padding = new Thickness(6, 10, 6, 8), Margin = new Thickness(3), CornerRadius = new CornerRadius(10),
                Background = Brushes.Transparent, Child = stack, Cursor = Cursors.Hand, ToolTip = Path.GetFileName(path),
            };
            tile.MouseEnter += (_, _) => tile.Background = _hover;
            tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;

            // A click opens it; a drag takes it out (to a folder, a chat, a program…).
            Point? down = null;
            tile.MouseLeftButtonDown += (_, e) => { down = e.GetPosition(tile); tile.Background = _pressed; tile.CaptureMouse(); e.Handled = true; };
            tile.MouseMove += (_, e) =>
            {
                if (down is not { } at || e.LeftButton != MouseButtonState.Pressed) return;
                var now = e.GetPosition(tile);
                if (Math.Abs(now.X - at.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(now.Y - at.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                down = null;
                tile.ReleaseMouseCapture();
                tile.Background = Brushes.Transparent;
                try { DragDrop.DoDragDrop(tile, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
                catch { }
            };
            tile.MouseLeftButtonUp += (_, e) =>
            {
                bool click = down != null;
                down = null;
                tile.ReleaseMouseCapture();
                tile.Background = tile.IsMouseOver ? _hover : Brushes.Transparent;
                e.Handled = true;
                if (click && tile.IsMouseOver) Open(path);
            };
            tile.ContextMenu = TileMenu(path, stack, name);
            return tile;
        }

        private ContextMenu TileMenu(string path, StackPanel stack, TextBlock name)
        {
            var app = Application.Current;
            var menu = new ContextMenu { FlowDirection = Lang.Flow, Style = (Style)app.FindResource("DarkContextMenu") };
            if (app.TryFindResource("DarkMenuItem") is Style item) menu.Resources[typeof(MenuItem)] = item;
            MenuItem Item(string text, Action act)
            {
                var m = new MenuItem { Header = Lang.T(text) };
                m.Click += (_, _) => act();
                return m;
            }
            menu.Items.Add(Item("افتح", () => Open(path)));
            menu.Items.Add(Item("اعرضه في مكانه", () => Reveal(path)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("قص", () => ToClipboard(path, cut: true)));
            menu.Items.Add(Item("انسخ", () => ToClipboard(path, cut: false)));
            menu.Items.Add(Item("انسخ المسار", () => { try { Clipboard.SetText(path); } catch { } }));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("غيّر الاسم", () => StartRename(path, stack, name)));
            menu.Items.Add(Item("احذف", () => Delete(path)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("الخصائص", () => Properties(path)));
            return menu;
        }

        /// <summary>Copy or cut, like Explorer: pasting it in a folder copies (or moves) the file.</summary>
        private static void ToClipboard(string path, bool cut)
        {
            try
            {
                var data = new DataObject();
                data.SetFileDropList(new System.Collections.Specialized.StringCollection { path });
                data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 /* move */ : 5 /* copy | link */)));
                Clipboard.SetDataObject(data, true);
            }
            catch { }
        }

        /// <summary>To the Recycle Bin (it can come back from there).</summary>
        private void Delete(string path)
        {
            try { ShellOps.RecycleFiles(new System.Windows.Interop.WindowInteropHelper(this).Handle, new[] { path }); }
            catch { }
        }

        private static void Properties(string path)
        {
            try { SHObjectProperties(IntPtr.Zero, 0x2 /* SHOP_FILEPATH */, path, null); } catch { }
        }

        /// <summary>The name turns into a box right on the tile: Enter (or clicking away) renames,
        /// Escape leaves it. Like Explorer, only the name is selected, not its extension.</summary>
        private void StartRename(string path, StackPanel stack, TextBlock name)
        {
            bool isDir = Directory.Exists(path);
            string shown = DesktopCorner.DisplayName(path);
            string hiddenExt = shown == Path.GetFileName(path) ? "" : Path.GetExtension(path);   // .lnk / .url stay hidden
            var box = new TextBox
            {
                Text = shown, FontSize = 12, Foreground = _fg, CaretBrush = _fg, Background = _field, SelectionBrush = _accent,
                BorderBrush = _accent, BorderThickness = new Thickness(1), Padding = new Thickness(2),
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxHeight = 52,
                Margin = name.Margin, FlowDirection = FlowDirection.LeftToRight,
            };
            int at = stack.Children.IndexOf(name);
            if (at < 0) return;
            name.Visibility = Visibility.Collapsed;
            stack.Children.Insert(at + 1, box);

            bool done = false;
            void Finish(bool keep)
            {
                if (done) return;
                done = true;
                stack.Children.Remove(box);
                name.Visibility = Visibility.Visible;
                string typed = box.Text.Trim();
                if (!keep || typed.Length == 0 || typed == shown) return;
                if (typed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    MessageBox.Show(Lang.T("الاسم فيه حروف ما تنفع:") + @" \ / : * ? "" < > |", "Wibn", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                string target = Path.Combine(Path.GetDirectoryName(path) ?? "", typed + hiddenExt);
                try
                {
                    if (isDir) Directory.Move(path, target); else File.Move(path, target);
                    name.Text = DesktopCorner.DisplayName(target);   // the watcher brings the new tile in a moment
                }
                catch (Exception ex)
                {
                    MessageBox.Show(Lang.T("ما قدرت أغيّر الاسم:") + Environment.NewLine + ex.Message, "Wibn", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; Finish(true); }
                else if (e.Key == Key.Escape) { e.Handled = true; Finish(false); }   // not the panel's Escape
            };
            box.LostKeyboardFocus += (_, _) => Finish(true);
            box.MouseLeftButtonDown += (_, e) => e.Handled = true;   // not a drag of the tile
            box.Loaded += (_, _) =>
            {
                box.Focus();
                int len = isDir || hiddenExt.Length > 0 || Path.GetExtension(shown).Length == 0 ? shown.Length : shown.Length - Path.GetExtension(shown).Length;
                box.Select(0, len);
            };
        }

        private void Open(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                    WorkingDirectory = File.Exists(path) ? Path.GetDirectoryName(path) ?? "" : path,
                });
                if (!_pinned) HideIt();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }

        private static void Reveal(string path)
        {
            try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
        }

        // ---- Pieces ---------------------------------------------------------------------------------

        private TextBlock Glyph(string glyph, double size = 12) => new()
        {
            Text = glyph, FontFamily = IconFont, FontSize = size, Foreground = _fg,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };

        private Border IconButton(UIElement content, string tip)
        {
            var b = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent,
                Child = content, Cursor = Cursors.Hand, Margin = new Thickness(4, 0, 0, 0),
            };
            if (tip.Length > 0) b.ToolTip = tip;
            b.MouseEnter += (_, _) => { if (b.Background == Brushes.Transparent) b.Background = _hover; };
            b.MouseLeave += (_, _) => { if (b.Background == _hover) b.Background = Brushes.Transparent; };
            b.MouseLeftButtonDown += (_, e) => e.Handled = true;   // not a drag of the window
            return b;
        }

        private static bool IsInside<T>(DependencyObject? d) where T : DependencyObject
        {
            for (; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d is T) return true;
            return false;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool SHObjectProperties(IntPtr hwnd, uint type, string name, string? page);
    }

    /// <summary>
    /// Hears every mouse button going down anywhere (a mouse hook on its own thread) and
    /// tells the UI thread which window it went down on.
    /// </summary>
    internal sealed class OutsideClickWatcher
    {
        public event Action<IntPtr>? Clicked;

        private Thread? _thread;
        private uint _threadId;
        private HookProc? _proc;   // kept alive (the hook calls it)

        public void Start()
        {
            if (_thread != null) return;
            var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                _proc = Proc;
                IntPtr hook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _proc, GetModuleHandle(null), 0);
                ready.Set();
                while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
                if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            }) { IsBackground = true, Name = "Wibn outside clicks" };
            _thread.Start();
            ready.Wait(1000);
        }

        public void Stop()
        {
            if (_thread == null) return;
            PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
            _thread = null;
        }

        private IntPtr Proc(int code, IntPtr wParam, IntPtr lParam)
        {
            int msg = (int)wParam;
            if (code >= 0 && msg is 0x0201 /* left */ or 0x0204 /* right */ or 0x0207 /* middle */ or 0x020B /* side */)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                IntPtr hwnd = WindowFromPoint(info.pt);
                Application.Current?.Dispatcher.BeginInvoke(() => Clicked?.Invoke(hwnd));
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extraInfo; }
        [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; public uint lPrivate; }

        [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    }
}
