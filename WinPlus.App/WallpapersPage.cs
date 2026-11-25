using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinPlus
{
    /// <summary>The "Wallpapers" page: the shortcut, the transition, and the list (add, set, remove).</summary>
    internal sealed class WallpapersPage : StackPanel
    {
        private static T Res<T>(string key) => (T)Application.Current.FindResource(key);
        private static Brush Muted => Res<Brush>("Muted");

        private readonly Button _keyButton, _clearButton;
        private readonly TextBlock _keyDesc = new() { TextWrapping = TextWrapping.Wrap };
        private readonly string _keyNormal;
        private bool _capturing;

        private readonly WrapPanel _grid = new() { Margin = new Thickness(-5, 4, -5, 0) };
        private readonly TextBlock _empty;
        private readonly Button _stopVideo;

        public WallpapersPage()
        {
            AllowDrop = true;
            Drop += OnDrop;
            DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };

            // ---- The shortcut ----
            Children.Add(Section(Lang.T("الاختصار")));
            _keyNormal = Lang.T("يفتح خلفياتك بنص الشاشة. تنقّل بالأسهم واضغط Enter عشان تصير خلفيتك، أو Esc للإغلاق");
            _keyDesc.Style = Res<Style>("RowDesc");
            _keyButton = new Button { Style = Res<Style>("FlatButton"), Padding = new Thickness(14, 5, 14, 5), MinWidth = 110 };
            _keyButton.Click += (_, _) => StartCapture();
            _clearButton = new Button
            {
                Style = Res<Style>("FlatButton"), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 0),
                Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11 },
                ToolTip = Lang.T("إزالة الاختصار"),
            };
            _clearButton.Click += (_, _) => Wallpapers.SetHotkey(0, 0);
            var keyButtons = new StackPanel { Orientation = Orientation.Horizontal };
            keyButtons.Children.Add(_clearButton);
            keyButtons.Children.Add(_keyButton);
            Children.Add(Row(keyButtons, "", Lang.T("قائمة الخلفيات"), _keyDesc));

            var tryIt = new Button { Style = Res<Style>("FlatButton"), Content = Lang.T("افتحها الحين"), Padding = new Thickness(14, 5, 14, 5) };
            tryIt.Click += (_, _) => Wallpapers.ShowSwitcher();
            Children.Add(Row(tryIt, "", Lang.T("جرّب القائمة"), Lang.T("نفس اللي يطلع لك بالاختصار")));

            // ---- The look ----
            Children.Add(Section(Lang.T("الحركة")));
            var effects = new ComboBox { Width = 190, VerticalAlignment = VerticalAlignment.Center };
            foreach (var (fx, name) in Wallpapers.Transitions)
            {
                var item = new ComboBoxItem { Content = Lang.T(name), Tag = fx };
                effects.Items.Add(item);
                if (fx == Wallpapers.Effect) effects.SelectedItem = item;
            }
            effects.SelectionChanged += (_, _) =>
            {
                if (effects.SelectedItem is ComboBoxItem { Tag: Wallpapers.Transition fx }) Wallpapers.Effect = fx;
            };
            Children.Add(Row(effects, "\uE945", Lang.T("طريقة ظهور الخلفية"), Lang.T("الحركة اللي تدخل فيها الخلفية الجديدة على سطح المكتب")));

            Children.Add(Choice(Lang.T("سلاسة الخلفية المتحركة"),
                new[] { Lang.T("توفير (15)"), Lang.T("عادي (30)"), Lang.T("ناعم (60)") },
                Wallpapers.FrameRate switch { 15 => 0, 60 => 2, _ => 1 },
                v => Wallpapers.FrameRate = v switch { 0 => 15, 2 => 60, _ => 30 }, "wp_fps", "\uE916"));

            var pause = new CheckBox { Style = Res<Style>("Switch"), IsChecked = Wallpapers.PauseFullscreen };
            pause.Click += (_, _) => Wallpapers.PauseFullscreen = pause.IsChecked == true;
            Children.Add(Row(pause, "\uE769", Lang.T("خلفية ثابتة لما تكبّر نافذة أو بملء الشاشة"),
                Lang.T("لما تكبّر نافذة على الشاشة الرئيسية أو تشغّل شي بملء الشاشة، الفيديو يتسكّر وآخر إطار منه يبقى صورة ثابتة، فيرجع استهلاك الرام والمعالج مثل الخلفية العادية. ولما تصغّر النافذة يكمل من نفس اللحظة. نافذة مكبّرة على شاشة ثانية ما توقفه")));

            _stopVideo = new Button { Style = Res<Style>("FlatButton"), Content = Lang.T("إيقاف"), Padding = new Thickness(14, 5, 14, 5) };
            _stopVideo.Click += (_, _) => Wallpapers.StopVideo();
            var stopRow = Row(_stopVideo, "", Lang.T("الخلفية المتحركة شغّالة"),
                Lang.T("صوتها مكتوم ودايم تعيد. إيقافها يخلي صورة منها خلفية ويندوز"));
            Children.Add(stopRow);

            // ---- The list ----
            var head = new DockPanel { Margin = new Thickness(0, 22, 0, 6) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var addFiles = new Button { Style = Res<Style>("AccentButton"), Content = Lang.T("إضافة خلفيات"), Margin = new Thickness(0, 0, 8, 0) };
            addFiles.Click += (_, _) => OnAddFiles();
            var addFolder = new Button { Style = Res<Style>("FlatButton"), Content = Lang.T("إضافة مجلد"), Margin = new Thickness(0) };
            addFolder.Click += (_, _) => OnAddFolder();
            buttons.Children.Add(addFiles);
            buttons.Children.Add(addFolder);
            DockPanel.SetDock(buttons, Dock.Right);
            head.Children.Add(buttons);
            head.Children.Add(new TextBlock { Text = Lang.T("خلفياتك"), Style = Res<Style>("Section"), Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            Children.Add(head);
            Children.Add(new TextBlock
            {
                Text = Lang.T("صور (JPG، PNG، WebP…) ومقاطع (MP4، WebM، MOV…). اضغط على خلفية عشان تصير خلفيتك، أو اسحب ملفات ومجلدات لهنا عشان تضيفها."),
                Style = Res<Style>("RowDesc"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 8),
            });

            _empty = new TextBlock
            {
                Text = Lang.T("ما فيه خلفيات للحين. أضف صور أو مقاطع من الأزرار فوق، أو اسحبها لهنا."),
                Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 30, 0, 30), TextWrapping = TextWrapping.Wrap,
            };
            Children.Add(_empty);
            Children.Add(_grid);

            Wallpapers.Changed += OnChanged;
            Unloaded += (_, _) => { if (_capturing) EndCapture(); };
            UpdateKey();
            Fill();
            stopRow.Visibility = Wallpapers.VideoPlaying ? Visibility.Visible : Visibility.Collapsed;
            _stopRow = stopRow;
        }

        private readonly Border _stopRow;
        private int _shownHash;

        private void OnChanged()
        {
            UpdateKey();
            _stopRow.Visibility = Wallpapers.VideoPlaying ? Visibility.Visible : Visibility.Collapsed;
            Fill();
        }

        // ---- The list -------------------------------------------------------------------------------

        private void Fill()
        {
            var list = Wallpapers.List;
            string current = Wallpapers.Current;
            int hash = string.Join("|", list).GetHashCode() ^ current.GetHashCode();
            if (hash == _shownHash && _grid.Children.Count == list.Count) return;
            _shownHash = hash;

            _grid.Children.Clear();
            _empty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var path in list) _grid.Children.Add(Tile(path, string.Equals(path, current, StringComparison.OrdinalIgnoreCase)));
        }

        private Border Tile(string path, bool isCurrent)
        {
            bool exists = File.Exists(path);
            const double W = 208, H = 117;
            var pic = new Border { CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x22)), Height = H };
            if (exists) LoadInto(pic, path);

            var over = new Grid();
            // Badges
            var badges = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(6) };
            if (Wallpapers.IsVideo(path)) badges.Children.Add(Badge(""));
            if (isCurrent) badges.Children.Add(Badge("", Res<Brush>("Accent")));
            if (!exists) badges.Children.Add(Badge("", new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80))));
            over.Children.Add(badges);

            // Hover buttons: move back / forward, remove.
            var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(6), Opacity = 0 };
            tools.Children.Add(ToolButton(Lang.English ? "" : "", Lang.T("قبل"), () => Wallpapers.Move(path, -1)));
            tools.Children.Add(ToolButton(Lang.English ? "" : "", Lang.T("بعد"), () => Wallpapers.Move(path, 1)));
            tools.Children.Add(ToolButton("", Lang.T("إزالة من القائمة (الملف يبقى)"), () => Wallpapers.Remove(path)));
            over.Children.Add(tools);
            pic.Child = over;

            var name = new TextBlock
            {
                Text = Path.GetFileNameWithoutExtension(path), FontSize = 12, Margin = new Thickness(2, 6, 2, 0),
                TextTrimming = TextTrimming.CharacterEllipsis, Foreground = isCurrent ? Res<Brush>("Accent") : Res<Brush>("Fg"),
                ToolTip = path,
            };
            var stack = new StackPanel();
            stack.Children.Add(pic);
            stack.Children.Add(name);

            var tile = new Border
            {
                Width = W + 12, Padding = new Thickness(6), Margin = new Thickness(5), CornerRadius = new CornerRadius(10),
                Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = stack,
                BorderThickness = new Thickness(2),
                BorderBrush = isCurrent ? Res<Brush>("Accent") : Brushes.Transparent,
                ToolTip = exists ? null : Lang.T("الملف مو موجود"),
            };
            tile.MouseEnter += (_, _) =>
            {
                tile.Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
                tools.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
            };
            tile.MouseLeave += (_, _) =>
            {
                tile.Background = Brushes.Transparent;
                tools.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)));
            };
            tile.MouseLeftButtonUp += (_, e) =>
            {
                if (e.Handled || !exists) return;
                var b = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? default;
                Wallpapers.Apply(path, new System.Drawing.Point(b.Left + b.Width / 2, b.Top + b.Height / 2));
            };
            return tile;
        }

        private static async void LoadInto(Border target, string path)
        {
            var img = await Wallpapers.ThumbAsync(path);
            if (img == null) return;
            var brush = new ImageBrush(img) { Stretch = Stretch.UniformToFill, Opacity = 0 };
            target.Background = brush;
            brush.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        }

        private static Border Badge(string glyph, Brush? back = null) => new()
        {
            Background = back ?? new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x11, 0x14)),
            CornerRadius = new CornerRadius(10), Width = 20, Height = 20, Margin = new Thickness(0, 0, 4, 0),
            Child = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10,
                Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };

        private static Border ToolButton(string glyph, string tip, Action click)
        {
            var b = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x10, 0x11, 0x14)),
                CornerRadius = new CornerRadius(6), Width = 24, Height = 24, Margin = new Thickness(3, 0, 0, 0), ToolTip = tip, Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10,
                    Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
            b.MouseLeftButtonDown += (_, e) => e.Handled = true;
            return b;
        }

        // ---- Adding -------------------------------------------------------------------------------------

        private static string Filter()
        {
            string images = string.Join(";", Wallpapers.ImageTypes.Select(e => "*" + e));
            string videos = string.Join(";", Wallpapers.VideoTypes.Select(e => "*" + e));
            return Lang.T("الخلفيات") + "|" + images + ";" + videos + "|" + Lang.T("الصور") + "|" + images + "|" + Lang.T("المقاطع") + "|" + videos;
        }

        private void OnAddFiles()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Lang.T("اختر الخلفيات"),
                Filter = Filter(),
                Multiselect = true,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) Wallpapers.Add(dialog.FileNames);
        }

        private void OnAddFolder()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = Lang.T("اختر مجلد فيه خلفيات"),
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true && Wallpapers.Add(new[] { dialog.FolderName }) == 0)
                MessageBox.Show(Window.GetWindow(this)!, Lang.T("ما لقيت فيه صور أو مقاطع جديدة"), "Wibn",
                    MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK, Lang.MsgOptions);
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) Wallpapers.Add(files);
            e.Handled = true;
        }

        // ---- The shortcut -------------------------------------------------------------------------------

        private void UpdateKey()
        {
            bool has = Wallpapers.HasHotkey;
            if (!_capturing) _keyButton.Content = has ? Wallpapers.HotkeyText : Lang.T("اختيار");
            _clearButton.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            bool failed = Wallpapers.HotkeyFailed;
            _keyDesc.Text = failed ? Lang.T("⚠ برنامج ثاني ماخذ هذا الاختصار، اختر غيره") : _keyNormal;
            _keyDesc.Foreground = failed ? new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x40)) : Muted;
        }

        private void StartCapture()
        {
            var window = Window.GetWindow(this);
            if (window == null || _capturing) return;
            _capturing = true;
            Wallpapers.Pause();   // or pressing the current one would open the list
            _keyButton.Content = Lang.T("اضغط الاختصار…");
            window.PreviewKeyDown += OnCaptureKey;
            window.Deactivated += OnCaptureCancel;
        }

        private void EndCapture()
        {
            var window = Window.GetWindow(this);
            if (window != null)
            {
                window.PreviewKeyDown -= OnCaptureKey;
                window.Deactivated -= OnCaptureCancel;
            }
            _capturing = false;
            Wallpapers.Refresh();
            UpdateKey();
        }

        private void OnCaptureCancel(object? sender, EventArgs e) => EndCapture();

        private void OnCaptureKey(object sender, KeyEventArgs e)
        {
            if (!_capturing) return;
            e.Handled = true;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { EndCapture(); return; }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
                    or Key.LWin or Key.RWin or Key.None or Key.Tab)
                return;   // wait for the actual key

            uint mods = 0;
            var m = Keyboard.Modifiers;
            if (m.HasFlag(ModifierKeys.Control)) mods |= HotkeyManager.MOD_CONTROL;
            if (m.HasFlag(ModifierKeys.Alt)) mods |= HotkeyManager.MOD_ALT;
            if (m.HasFlag(ModifierKeys.Shift)) mods |= HotkeyManager.MOD_SHIFT;
            if (m.HasFlag(ModifierKeys.Windows) || Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
                mods |= HotkeyManager.MOD_WIN;

            bool alone = key >= Key.F1 && key <= Key.F24;
            if (!alone && (mods & (HotkeyManager.MOD_CONTROL | HotkeyManager.MOD_ALT | HotkeyManager.MOD_WIN)) == 0)
            {
                _keyButton.Content = Lang.T("لازم معه Ctrl أو Alt");
                return;
            }
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            _capturing = false;
            var window = Window.GetWindow(this);
            if (window != null)
            {
                window.PreviewKeyDown -= OnCaptureKey;
                window.Deactivated -= OnCaptureCancel;
            }
            Wallpapers.SetHotkey(mods, vk);
            UpdateKey();
        }

        // ---- Building blocks -----------------------------------------------------------------------------

        private static TextBlock Section(string text) =>
            new() { Text = text, Style = Res<Style>("Section") };

        private static UIElement Choice(string title, string[] names, int selected, Action<int> set, string group, string icon)
        {
            var bar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < names.Length; i++)
            {
                int v = i;
                var r = new RadioButton { Style = Res<Style>("NavButton"), Content = names[i], GroupName = group, IsChecked = i == selected };
                r.Checked += (_, _) => set(v);
                bar.Children.Add(r);
            }
            var bg = new Border { Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x22)), CornerRadius = new CornerRadius(8), Padding = new Thickness(3), Child = bar };
            DockPanel.SetDock(bg, Dock.Right);
            var dock = new DockPanel();
            dock.Children.Add(bg);
            dock.Children.Add(new TextBlock { Text = icon, Style = Res<Style>("RowIcon") });
            dock.Children.Add(new TextBlock { Text = title, Style = Res<Style>("RowTitle"), VerticalAlignment = VerticalAlignment.Center });
            return new Border { Style = Res<Style>("Row"), Child = dock, Margin = new Thickness(0, 0, 0, 6) };
        }

        private static Border Row(UIElement control, string icon, string title, object desc)
        {
            DockPanel.SetDock(control, Dock.Right);
            if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = title, Style = Res<Style>("RowTitle") });
            text.Children.Add(desc as UIElement ?? new TextBlock { Text = desc as string ?? "", Style = Res<Style>("RowDesc"), TextWrapping = TextWrapping.Wrap });
            var dock = new DockPanel();
            dock.Children.Add(control);
            dock.Children.Add(new TextBlock { Text = icon, Style = Res<Style>("RowIcon") });
            dock.Children.Add(text);
            return new Border { Style = Res<Style>("Row"), Child = dock, Margin = new Thickness(0, 0, 0, 6) };
        }
    }
}
