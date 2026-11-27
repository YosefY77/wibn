using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinPlus
{
    /// <summary>
    /// Right-click on a side-menu page or on an option inside a page: add it to the favorites
    /// (a shortcut shown first in the side menu, under "خيارات مفضلة"; it stays in its own place too) or hide it. Hidden ones come back from
    /// the same right-click menu, under "الخيارات المخفية".
    /// A page is kept by its key ("taskbar"); an option inside a page as "page|its title".
    /// </summary>
    public partial class MainWindow
    {
        private List<UIElement> _navOrder = new();
        private TextBlock? _favHeader;
        private readonly Dictionary<string, RadioButton> _rowFavItems = new();
        private readonly HashSet<Border> _rowsHiddenByUs = new();
        private Dictionary<FrameworkElement, string>? _pageKeys;
        private static Style? _rowStyle, _rowTitleStyle;
        private static bool _rowHookInstalled;

        private Dictionary<FrameworkElement, string> PageKeys => _pageKeys ??= new()
        {
            [PageTaskbar] = "taskbar", [PageDesktop] = "desktop", [PageBig] = "big", [PageTemp] = "temp", [PageDup] = "dup",
            [PageBin] = "bin", [PageHotkeys] = "hotkeys", [PageRam] = "ram", [PageLock] = "lock", [PageStartup] = "startup",
            [PageDownloads] = "downloads", [PageConvert] = "convert", [PageResize] = "resize", [PageCompress] = "compress",
            [PageArchive] = "archive", [PageVideo] = "video", [PageTransfer] = "transfer", [PageCapture] = "capture",
            [PageVolume] = "volume", [PageSpecs] = "specs", [PageGame] = "game", [PageWidgets] = "widgets",
            [PageGlass] = "glass", [PageAnim] = "anim", [PageWallpapers] = "wallpapers", [PageGeneral] = "general",
        };

        private static string RowPage(string id) => id.Split('|')[0];
        private static string RowTitle(string id) { int i = id.IndexOf('|'); return i < 0 ? id : id[(i + 1)..]; }
        private static bool IsRowId(string id) => id.Contains('|');

        private void SetupSideMenu()
        {
            _rowStyle = Application.Current.TryFindResource("Row") as Style;
            _rowTitleStyle = Application.Current.TryFindResource("RowTitle") as Style;
            _navOrder = SideList.Children.Cast<UIElement>().ToList();
            _favHeader = new TextBlock { Style = (Style)FindResource("SideGroup"), Text = Lang.T("خيارات مفضلة"), Tag = "fav" };
            foreach (var group in _navOrder.OfType<TextBlock>().Append(_favHeader)) MakeFoldable(group);

            foreach (var nav in _navOrder.OfType<RadioButton>())
                nav.MouseRightButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    if (((RadioButton)s).CommandParameter is string k) ShowFavMenu(k);
                };
            // A group title or the empty space: just the hidden ones, to show them again.
            SideScroll.MouseRightButtonUp += (_, e) => { if (e.Handled) return; e.Handled = true; ShowFavMenu(null); };
            PagesGrid.MouseRightButtonUp += OnPageRightClick;

            // Options are built when their page first opens, and some pages rebuild them: hide them as they come.
            if (!_rowHookInstalled)
            {
                _rowHookInstalled = true;
                EventManager.RegisterClassHandler(typeof(Border), LoadedEvent, new RoutedEventHandler(OnBorderLoaded));
            }
            ArrangeSideList();
        }

        private static void OnBorderLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Border b || _rowStyle == null || !ReferenceEquals(b.Style, _rowStyle)) return;
            if (Window.GetWindow(b) is MainWindow w) w.ApplyRow(b);
        }

        // ---- Groups that fold (click the title or its arrow) ------------------------------------

        private readonly Dictionary<TextBlock, System.Windows.Documents.Run> _groupArrows = new();

        /// <summary>The group's title with an arrow after it; a click folds or opens the group.</summary>
        private void MakeFoldable(TextBlock group)
        {
            if (group.Tag is not string id) return;
            string title = group.Text;
            var arrow = new System.Windows.Documents.Run
            {
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 9,
                BaselineAlignment = BaselineAlignment.Center,
            };
            group.Inlines.Clear();
            group.Inlines.Add(new System.Windows.Documents.Run(title));
            group.Inlines.Add(new System.Windows.Documents.Run("  "));
            group.Inlines.Add(arrow);
            group.Background = Brushes.Transparent;   // the whole line takes the click
            group.Cursor = Cursors.Hand;
            group.ToolTip = null;
            group.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                SidePrefs.SetCollapsed(id, !SidePrefs.IsCollapsed(id));
                ApplyFeatureVisibility();
            };
            _groupArrows[group] = arrow;
            UpdateGroupArrow(group, SidePrefs.IsCollapsed(id));
        }

        /// <summary>Down when open; toward the text's end when folded.</summary>
        private void UpdateGroupArrow(TextBlock group, bool folded)
        {
            if (!_groupArrows.TryGetValue(group, out var arrow)) return;
            arrow.Text = !folded ? "" : Lang.English ? "" : "";
        }

        /// <summary>Favorites first under their own title, then every page in its own place.</summary>
        private void ArrangeSideList()
        {
            // A favorite is a shortcut: the page or option stays in its own place too.
            var favs = new List<RadioButton>();
            foreach (var k in SidePrefs.Favorites)
                if (IsRowId(k) || NavOf(k) != null) favs.Add(FavItem(k));

            SideList.Children.Clear();
            if (favs.Count > 0 && _favHeader != null)
            {
                SideList.Children.Add(_favHeader);
                foreach (var r in favs) SideList.Children.Add(r);
            }
            foreach (var el in _navOrder) SideList.Children.Add(el);
            ApplyHidden();
        }

        /// <summary>The side-menu shortcut of a favorite (a page, or an option inside one): opens it.</summary>
        private RadioButton FavItem(string id)
        {
            if (_rowFavItems.TryGetValue(id, out var item)) return item;
            string page = RowPage(id);
            bool row = IsRowId(id);
            item = new RadioButton
            {
                Style = (Style)FindResource("SideItem"),
                Tag = PageGlyph(page),
                Content = row ? RowTitle(id) : PageName(page),
                DataContext = id,
                ToolTip = row ? PageName(page) : null,
            };
            item.Click += (_, _) => { if (row) OpenRow(id); else OpenPage(id); };
            item.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowFavMenu(id); };
            _rowFavItems[id] = item;
            return item;
        }

        private void OpenRow(string id)
        {
            string key = RowPage(id);
            OpenPage(key);
            var page = PageKeys.FirstOrDefault(p => p.Value == key).Key;
            if (page == null) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                var row = RowsIn(page).FirstOrDefault(b => RowId(b) == id);
                if (row == null) return;
                row.BringIntoView();
                Flash(row);
            }));
        }

        /// <summary>A short accent border, so the eye finds the option on its page.</summary>
        // A row lit up now: its own border (to put back) and its timer. A second flash just starts the time again,
        // so the border it puts back is always the row's own (not the light).
        private readonly System.Collections.Generic.Dictionary<Border, (object Local, DispatcherTimer Timer)> _flashing = new();

        private void Flash(Border row)
        {
            if (_flashing.TryGetValue(row, out var on)) { on.Timer.Stop(); on.Timer.Start(); return; }
            var local = row.ReadLocalValue(Border.BorderBrushProperty);
            row.BorderBrush = (Brush)FindResource("Accent");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _flashing.Remove(row);
                if (local == DependencyProperty.UnsetValue) row.ClearValue(Border.BorderBrushProperty);
                else row.BorderBrush = local as Brush;
            };
            _flashing[row] = (local, timer);
            timer.Start();
        }

        // ---- Options inside the pages -------------------------------------------------------------

        private void OnPageRightClick(object sender, MouseButtonEventArgs e)
        {
            if (e.Handled) return;
            Border? row = null;
            for (var d = e.OriginalSource as DependencyObject; d != null && d != PagesGrid; d = ParentOf(d))
            {
                // Text boxes and things with their own right-click menu keep it.
                if (d is TextBoxBase or PasswordBox) return;
                if (d is FrameworkElement { ContextMenu: not null }) return;
                if (d is Border b && _rowStyle != null && ReferenceEquals(b.Style, _rowStyle)) { row = b; break; }
            }
            string? id = row != null ? RowId(row) : null;
            e.Handled = true;
            ShowFavMenu(id);
        }

        private static DependencyObject? ParentOf(DependencyObject d) =>
            (d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : null) ?? LogicalTreeHelper.GetParent(d);

        /// <summary>"page|title" of an option row; null if it has no title or it's on the general page.</summary>
        private string? RowId(Border row)
        {
            string? page = null;
            for (DependencyObject? d = row; d != null; d = ParentOf(d))
                if (d is FrameworkElement fe && PageKeys.TryGetValue(fe, out var k)) { page = k; break; }
            if (page == null || page == "general") return null;
            string? title = TitleOf(row);
            return string.IsNullOrWhiteSpace(title) ? null : page + "|" + title.Trim();
        }

        private static string? TitleOf(DependencyObject root)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                if (child is TextBlock t && _rowTitleStyle != null && ReferenceEquals(t.Style, _rowTitleStyle)) return t.Text;
                if (child is Border b && _rowStyle != null && ReferenceEquals(b.Style, _rowStyle)) continue;   // another option inside
                if (TitleOf(child) is { } found) return found;
            }
            return null;
        }

        private static IEnumerable<Border> RowsIn(DependencyObject root)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                if (child is Border b && _rowStyle != null && ReferenceEquals(b.Style, _rowStyle)) yield return b;
                foreach (var inner in RowsIn(child)) yield return inner;
            }
        }

        private void ApplyRow(Border row)
        {
            if (RowId(row) is not { } id) return;
            if (SidePrefs.IsHidden(id))
            {
                if (row.Visibility != Visibility.Collapsed && _rowsHiddenByUs.Add(row)) row.Visibility = Visibility.Collapsed;
            }
            else if (_rowsHiddenByUs.Remove(row)) row.Visibility = Visibility.Visible;   // only the ones we hid
        }

        /// <summary>Side menu and options inside the pages, after a change.</summary>
        private void ApplyHidden()
        {
            ApplyFeatureVisibility();
            foreach (var page in PageKeys.Keys)
                foreach (var row in RowsIn(page).ToList()) ApplyRow(row);
            foreach (var row in _rowsHiddenByUs.ToList()) ApplyRow(row);   // ones no longer in a page's tree
        }

        // ---- The menu -----------------------------------------------------------------------------

        private RadioButton? NavOf(string key) =>
            _navOrder.OfType<RadioButton>().FirstOrDefault(r => r.CommandParameter is string k && k == key);

        private string PageName(string key) => NavOf(key)?.Content?.ToString() ?? key;
        private string PageGlyph(string key) => NavOf(key)?.Tag as string ?? "";

        /// <param name="id">The page or option right-clicked; null shows just the hidden ones.</param>
        private void ShowFavMenu(string? id)
        {
            var entries = new List<GlassMenu.Entry>();
            if (id != null)
            {
                bool fav = SidePrefs.IsFavorite(id);
                entries.Add(new GlassMenu.Entry(fav ? Lang.T("إزالة من المفضلة") : Lang.T("إضافة للمفضلة"), fav ? "" : "",
                    () => { SidePrefs.SetFavorite(id, !fav); ArrangeSideList(); }));
                entries.Add(new GlassMenu.Entry(Lang.T("إخفاء"), "",
                    () => { SidePrefs.SetHidden(id, true); ApplyHidden(); }));
            }

            var hidden = new List<GlassMenu.Entry>();
            foreach (var h in SidePrefs.Hidden)
            {
                string page = RowPage(h);
                if (!Features.IsNavOn(page) || NavOf(page) == null) continue;
                string text = IsRowId(h) ? RowTitle(h) + "  ·  " + PageName(page) : PageName(page);
                var key = h;
                hidden.Add(new GlassMenu.Entry(text, PageGlyph(page), () => { SidePrefs.SetHidden(key, false); ApplyHidden(); }));
            }
            if (hidden.Count > 1)
            {
                hidden.Add(GlassMenu.Entry.Line);
                hidden.Add(new GlassMenu.Entry(Lang.T("إظهار الكل"), "", () => { SidePrefs.ShowAll(); ApplyHidden(); }));
            }
            if (hidden.Count > 0)
            {
                if (entries.Count > 0) entries.Add(GlassMenu.Entry.Line);
                entries.Add(new GlassMenu.Entry(Lang.T("الخيارات المخفية"), "") { Children = hidden });
            }
            if (entries.Count == 0) return;

            try
            {
                GlassMenu.CloseAll();
                var p = System.Windows.Forms.Cursor.Position;
                new GlassMenu(entries).ShowAt(p.X, p.Y);
            }
            catch (Exception ex) { CrashLog.Write("favorites menu", ex); }
        }
    }
}
