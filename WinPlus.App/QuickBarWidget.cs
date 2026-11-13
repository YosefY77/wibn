using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace WinPlus
{
    /// <summary>
    /// The quick actions bar: a thin bar, across or down, of one-click buttons. A button is a
    /// program, folder or file (dropped on it or picked), one of Wibn's actions (a screenshot,
    /// muting the mic, muting yourself in Discord, locking the PC…) or any keyboard shortcut.
    /// Saved in the widget's options, one per line: a path, "a:&lt;action&gt;" or "k:&lt;mods&gt;:&lt;key&gt;".
    /// </summary>
    internal sealed class QuickBarWidget : WidgetContent
    {
        private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

        /// <summary>Wibn's own actions: id, icon, name, and the group it's in on the "Add" menu.</summary>
        private static readonly (string Id, string Glyph, string Name, string Group)[] Actions =
        {
            ("shot_region", "", "لقطة لجزء من الشاشة", "تصوير الشاشة"),
            ("shot_full", "", "لقطة للشاشة كاملة", "تصوير الشاشة"),
            ("record", "", "سجّل الشاشة / وقّف", "تصوير الشاشة"),
            ("replay", "", "احفظ آخر دقيقة", "تصوير الشاشة"),
            ("mic", "", "اكتم المايك / رجّعه", "الصوت"),
            ("mute", "", "اكتم الصوت / رجّعه", "الصوت"),
            ("play_pause", "", "تشغيل / إيقاف", "الصوت"),
            ("prev_track", "", "المقطع السابق", "الصوت"),
            ("next_track", "", "المقطع التالي", "الصوت"),
            ("discord_mute", "", "ديسكورد: اكتم المايك", "ديسكورد"),
            ("discord_deafen", "", "ديسكورد: اكتم الصوت", "ديسكورد"),
            ("show_desktop", "", "اعرض سطح المكتب", "الجهاز"),
            ("lock", "", "قفل الجهاز", "الجهاز"),
            ("monitor_off", "", "إطفاء الشاشة", "الجهاز"),
            ("sleep", "", "سكون", "الجهاز"),
            ("task_manager", "", "مدير المهام", "الجهاز"),
            ("settings", "", "إعدادات ويندوز", "الجهاز"),
            ("calculator", "", "الآلة الحاسبة", "الجهاز"),
            ("empty_bin", "", "فضّي سلة المهملات", "الجهاز"),
            ("show_winplus", "", "فتح Wibn", "الجهاز"),
        };

        private static readonly string[] Groups = { "تصوير الشاشة", "الصوت", "ديسكورد", "الجهاز" };

        // Discord's own shortcuts for "Toggle Mute" / "Toggle Deafen" (changeable from the button's menu).
        private const uint DefaultMods = HotkeyManager.MOD_CONTROL | HotkeyManager.MOD_SHIFT;

        private readonly SolidColorBrush _ink = new(Color.FromRgb(0xF2, 0xF3, 0xF5));
        private readonly SolidColorBrush _inkMuted = new(Color.FromRgb(0xA0, 0xA3, 0xAA));
        private readonly SolidColorBrush _accent = new(Color.FromRgb(0x4C, 0x8D, 0xFF));
        private static readonly SolidColorBrush Alert = new(Color.FromRgb(0xFF, 0x45, 0x3A));
        private readonly System.Windows.Controls.Primitives.UniformGrid _tiles = new();
        private readonly TextBlock _empty;
        private readonly Border _dropHint;
        private readonly DispatcherTimer _stateTimer;
        private readonly List<(string Id, TextBlock Glyph)> _live = new();
        private MMDeviceEnumerator? _audio;

        public QuickBarWidget()
        {
            _empty = new TextBlock
            {
                Text = Lang.T("كليك يمين ← أضف"),
                FontSize = 12, Foreground = _inkMuted, TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            };
            _dropHint = new Border
            {
                BorderBrush = _accent, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(10),
                Margin = new Thickness(-3), Visibility = Visibility.Collapsed, IsHitTestVisible = false,
            };
            var grid = new Grid();
            grid.Children.Add(_tiles);
            grid.Children.Add(_empty);
            grid.Children.Add(_dropHint);
            Child = grid;
            Background = Brushes.Transparent;
            SizeChanged += (_, _) => Arrange();

            AllowDrop = true;
            DragEnter += (_, e) => { e.Effects = Files(e) != null ? DragDropEffects.Link : DragDropEffects.None; _dropHint.Visibility = e.Effects == DragDropEffects.None ? Visibility.Collapsed : Visibility.Visible; e.Handled = true; };
            DragOver += (_, e) => { e.Effects = Files(e) != null ? DragDropEffects.Link : DragDropEffects.None; e.Handled = true; };
            DragLeave += (_, _) => _dropHint.Visibility = Visibility.Collapsed;
            Drop += (_, e) =>
            {
                _dropHint.Visibility = Visibility.Collapsed;
                if (Files(e) is { } files) AddItems(files);
                e.Handled = true;
            };

            // The mic's and the recording's state, shown on their buttons (only while there's one of them).
            _stateTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _stateTimer.Tick += (_, _) => RefreshState();
        }

        private static string[]? Files(DragEventArgs e) =>
            e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0 ? f : null;

        public override void Start() => Rebuild();

        public override void Stop()
        {
            _stateTimer.Stop();
            _audio?.Dispose();
            _audio = null;
        }

        public override void SetAccent(Color color) => _accent.Color = color;

        public override void SetLightBehind(bool light)
        {
            _ink.Color = light ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xF2, 0xF3, 0xF5);
            _inkMuted.Color = light ? Color.FromArgb(0xB0, 0x3C, 0x3C, 0x43) : Color.FromRgb(0xA0, 0xA3, 0xAA);
        }

        // ---- Across or down -----------------------------------------------------------------------

        private bool Vertical => Info?.Options.TryGetValue("vertical", out var v) == true && v == "1";

        public override (int MinW, int MinH, int MaxW, int MaxH) Limits =>
            Vertical ? (1, 2, 2, Widgets.AnyMax) : (2, 1, Widgets.AnyMax, 2);

        public override (int W, int H)[]? SizeChoices
        {
            get
            {
                var sizes = Enumerable.Range(2, Widgets.AnyMax - 1).Select(n => (n, 1))
                    .Concat(new[] { 4, 6, 8 }.Select(n => (n, 2))).ToArray();
                return Vertical ? sizes.Select(s => (s.Item2, s.Item1)).ToArray() : sizes;
            }
        }

        public override Thickness? CardPadding => new Thickness(5);

        private void SetVertical(bool vertical)
        {
            if (Info == null || vertical == Vertical) return;
            if (vertical) Info.Options["vertical"] = "1"; else Info.Options.Remove("vertical");
            Widgets.Save();
            var lim = Limits;
            RequestCells?.Invoke(Math.Clamp(Info.H, lim.MinW, lim.MaxW), Math.Clamp(Info.W, lim.MinH, lim.MaxH));   // turned around
            Arrange();
        }

        private bool ShowNames => Info?.Options.TryGetValue("names", out var n) == true && n == "1";

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

        private void AddItems(IEnumerable<string> entries)
        {
            var items = Items;
            foreach (var p in entries)
                if (!items.Contains(p, StringComparer.OrdinalIgnoreCase)) items.Add(p);
            SaveItems(items);
        }

        private void Remove(string entry)
        {
            var items = Items;
            items.RemoveAll(p => string.Equals(p, entry, StringComparison.OrdinalIgnoreCase));
            SaveItems(items);
        }

        private void Shift(string entry, int by)
        {
            var items = Items;
            int i = items.FindIndex(p => string.Equals(p, entry, StringComparison.OrdinalIgnoreCase));
            int j = i + by;
            if (i < 0 || j < 0 || j >= items.Count) return;
            (items[i], items[j]) = (items[j], items[i]);
            SaveItems(items);
        }

        private void Rebuild()
        {
            _tiles.Children.Clear();
            _live.Clear();
            var items = Items;
            foreach (var entry in items) _tiles.Children.Add(Tile(entry, items.Count));
            _empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Arrange();
            RefreshState();
            if (_live.Count > 0) _stateTimer.Start(); else _stateTimer.Stop();
        }

        /// <summary>The buttons as big as fit: square, filling the bar's length, shrinking when there are many.</summary>
        private void Arrange()
        {
            double w = ActualWidth, h = ActualHeight;
            int n = Math.Max(1, _tiles.Children.Count);
            if (w < 4 || h < 4) return;
            double s = Math.Min(w, h);
            while (s > 8 && Math.Floor(w / s) * Math.Floor(h / s) < n) s -= 1;
            int cols = Math.Max(1, (int)Math.Floor(w / s)), rows = Math.Max(1, (int)Math.Floor(h / s));
            if (Vertical) { rows = Math.Max(rows, (n + cols - 1) / cols); }
            else { cols = Math.Max(cols, (n + rows - 1) / rows); }
            _tiles.Columns = cols;
            _tiles.Rows = rows;
            // Kept square and at the start of the bar, not stretched over all of it.
            _tiles.Width = cols * s;
            _tiles.Height = rows * s;
            _tiles.HorizontalAlignment = HorizontalAlignment.Left;
            _tiles.VerticalAlignment = VerticalAlignment.Top;
            if (Vertical) _tiles.HorizontalAlignment = HorizontalAlignment.Center; else _tiles.VerticalAlignment = VerticalAlignment.Center;
            _tiles.FlowDirection = FlowDirection.LeftToRight;
            if (Lang.Flow == FlowDirection.RightToLeft && !Vertical) _tiles.HorizontalAlignment = HorizontalAlignment.Right;
        }

        // ---- One button ---------------------------------------------------------------------------

        private static (string Id, string Glyph, string Name, string Group)? ActionOf(string entry) =>
            entry.StartsWith("a:") && Actions.FirstOrDefault(a => a.Id == entry[2..]) is { Id: not null } a ? a : null;

        private static bool TryKeys(string entry, out uint mods, out uint vk)
        {
            mods = vk = 0;
            if (!entry.StartsWith("k:")) return false;
            var parts = entry[2..].Split(':');
            return parts.Length == 2 && uint.TryParse(parts[0], out mods) && uint.TryParse(parts[1], out vk) && vk != 0;
        }

        private string NameOf(string entry)
        {
            if (ActionOf(entry) is { } a) return Lang.T(a.Name);
            if (TryKeys(entry, out var mods, out var vk)) return HotkeyManager.KeyText(mods, vk);
            return ShortcutsWidget.NameOf(entry);
        }

        private Button Tile(string entry, int count)
        {
            var panel = new StackPanel { Width = 60 };
            FrameworkElement icon;
            bool there = true;
            if (ActionOf(entry) is { } a)
            {
                var glyph = new TextBlock
                {
                    Text = a.Glyph, FontFamily = new FontFamily(IconFont), FontSize = 30, Foreground = _ink,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                if (a.Id is "mic" or "record") _live.Add((a.Id, glyph));
                icon = new Grid { Children = { glyph } };
            }
            else if (TryKeys(entry, out _, out _))
            {
                icon = new TextBlock
                {
                    Text = "", FontFamily = new FontFamily(IconFont), FontSize = 30, Foreground = _ink,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
            }
            else
            {
                there = File.Exists(entry) || Directory.Exists(entry);
                icon = new Image { Source = ShortcutsWidget.Icon(entry), Stretch = Stretch.Uniform };
            }
            icon.Width = icon.Height = 36;
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            panel.Children.Add(icon);
            string name = NameOf(entry);
            if (ShowNames)
                panel.Children.Add(new TextBlock
                {
                    Text = name, FontSize = 11, Foreground = _ink, Margin = new Thickness(0, 3, 0, 0),
                    TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 60, FlowDirection = Lang.Flow,
                });

            var b = new Button
            {
                Style = (Style)Application.Current.FindResource("FlatButton"),
                Content = new Viewbox { Child = panel, Stretch = Stretch.Uniform },
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(3), Margin = new Thickness(1),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = name, Opacity = there ? 1 : 0.4, Cursor = Cursors.Hand,
            };
            b.Click += (_, _) => Run(entry);
            b.ContextMenu = TileMenu(entry, count);
            return b;
        }

        private ContextMenu TileMenu(string entry, int count)
        {
            var menu = DarkMenu();
            MenuItem Item(string text, Action act)
            {
                var m = new MenuItem { Header = Lang.T(text) };
                m.Click += (_, _) => act();
                return m;
            }
            menu.Items.Add(Item("شغّله", () => Run(entry)));
            bool isPath = ActionOf(entry) == null && !TryKeys(entry, out _, out _);
            if (isPath) menu.Items.Add(Item("افتح مكانه", () => Reveal(entry)));
            if (entry is "a:discord_mute" or "a:discord_deafen")
            {
                string id = entry[2..];
                menu.Items.Add(Item("غيّر اختصار ديسكورد…", () =>
                {
                    var (m, k) = DiscordKeys(id);
                    if (KeyRecorder.Ask(Lang.T("اضغط نفس الاختصار اللي في ديسكورد ← الإعدادات ← Keybinds"), m, k) is { } keys && Info != null)
                    {
                        Info.Options["keys:" + id] = $"{keys.Mods}:{keys.Vk}";
                        Widgets.Save();
                    }
                }));
            }
            menu.Items.Add(new Separator());
            int at = Items.FindIndex(p => string.Equals(p, entry, StringComparison.OrdinalIgnoreCase));
            var before = Item("قدّمه", () => Shift(entry, -1));
            var after = Item("أخّره", () => Shift(entry, 1));
            before.IsEnabled = at > 0;
            after.IsEnabled = at >= 0 && at < count - 1;
            menu.Items.Add(before);
            menu.Items.Add(after);
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("شيله من الشريط", () => Remove(entry)));
            return menu;
        }

        private static ContextMenu DarkMenu()
        {
            var app = Application.Current;
            var menu = new ContextMenu { FlowDirection = Lang.Flow, Style = (Style)app.FindResource("DarkContextMenu") };
            menu.Resources[typeof(MenuItem)] = app.FindResource("DarkMenuItem");
            menu.Resources[MenuItem.SeparatorStyleKey] = app.FindResource("DarkMenuSeparator");
            return menu;
        }

        // ---- What a button does ---------------------------------------------------------------------

        private void Run(string entry)
        {
            try
            {
                if (ActionOf(entry) is { } a) RunAction(a.Id);
                else if (TryKeys(entry, out var mods, out var vk)) SendKeys(mods, vk);
                else Open(entry);
            }
            catch (Exception ex) { CrashLog.Write("quick bar " + entry, ex); }
            Dispatcher.BeginInvoke(DispatcherPriority.Background, RefreshState);
        }

        private void RunAction(string id)
        {
            switch (id)
            {
                case "shot_region": ScreenCapture.Run(ScreenCapture.Kind.Region); break;
                case "shot_full": ScreenCapture.Run(ScreenCapture.Kind.Full); break;
                case "record": ScreenCapture.Run(ScreenCapture.Kind.Record); break;
                case "replay": ScreenCapture.Run(ScreenCapture.Kind.Replay); break;
                case "mic": ToggleMic(); break;
                case "mute" or "play_pause" or "prev_track" or "next_track" or "lock" or "monitor_off" or "show_winplus":
                    HotkeyManager.Run(new HotkeyItem { Action = id });
                    break;
                case "discord_mute" or "discord_deafen":
                    var (m, k) = DiscordKeys(id);
                    SendKeys(m, k);
                    break;
                case "show_desktop": SendKeys(HotkeyManager.MOD_WIN, 0x44 /* D */); break;
                case "sleep": SetSuspendState(false, false, false); break;
                case "task_manager": Start("taskmgr.exe"); break;
                case "settings": Start("ms-settings:"); break;
                case "calculator": Start("calc.exe"); break;
                case "empty_bin": SHEmptyRecycleBin(IntPtr.Zero, null, 0); break;   // Windows asks first
            }
        }

        private (uint Mods, uint Vk) DiscordKeys(string id)
        {
            if (Info?.Options.TryGetValue("keys:" + id, out var s) == true && TryKeys("k:" + s, out var m, out var k)) return (m, k);
            return (DefaultMods, id == "discord_deafen" ? 0x44u /* D */ : 0x4Du /* M */);
        }

        private static void Start(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

        private static void Open(string path)
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
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }

        private static void Reveal(string path)
        {
            try
            {
                if (File.Exists(path) || Directory.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
                else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir)) Process.Start("explorer.exe", $"\"{dir}\"");
            }
            catch { }
        }

        /// <summary>Presses a shortcut, as if from the keyboard (the window you're in, or a program listening for it everywhere, gets it).</summary>
        internal static void SendKeys(uint mods, uint vk)
        {
            var down = new List<byte>();
            if ((mods & HotkeyManager.MOD_CONTROL) != 0) down.Add(0x11);
            if ((mods & HotkeyManager.MOD_ALT) != 0) down.Add(0x12);
            if ((mods & HotkeyManager.MOD_SHIFT) != 0) down.Add(0x10);
            if ((mods & HotkeyManager.MOD_WIN) != 0) down.Add(0x5B);
            foreach (var m in down) keybd_event(m, 0, 0, UIntPtr.Zero);
            uint ext = IsExtended(vk) ? 0x1u : 0;
            keybd_event((byte)vk, 0, ext, UIntPtr.Zero);
            keybd_event((byte)vk, 0, ext | 0x2 /* KEYUP */, UIntPtr.Zero);
            for (int i = down.Count - 1; i >= 0; i--) keybd_event(down[i], 0, 0x2, UIntPtr.Zero);
        }

        private static bool IsExtended(uint vk) => vk is >= 0x21 and <= 0x2E;   // Page Up … Delete, arrows, Insert

        // ---- The microphone (every program's: Discord, games, calls) ---------------------------------

        private MMDevice? Mic()
        {
            try
            {
                _audio ??= new MMDeviceEnumerator();
                return _audio.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            }
            catch { return null; }
        }

        private void ToggleMic()
        {
            using var mic = Mic();
            if (mic == null)
            {
                CaptureToast.Show(Lang.T("ما فيه مايك"), Lang.T("ما لقيت مايك شغّال في الجهاز"));
                return;
            }
            bool mute = !mic.AudioEndpointVolume.Mute;
            mic.AudioEndpointVolume.Mute = mute;
            CaptureToast.Show(mute ? Lang.T("المايك مكتوم") : Lang.T("المايك شغّال"), Lang.T("في كل البرامج"));
        }

        private void RefreshState()
        {
            foreach (var (id, glyph) in _live)
            {
                bool on = false;
                if (id == "mic")
                {
                    using var mic = Mic();
                    try { on = mic?.AudioEndpointVolume.Mute == true; } catch { }
                    glyph.Text = on ? "" : "";
                }
                else if (id == "record") on = Recorder.IsRecording;
                glyph.Foreground = on ? Alert : _ink;   // red: muted / recording
            }
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
                if (dlg.ShowDialog() == true) AddItems(dlg.FileNames);
            };
            var folder = new MenuItem { Header = Lang.T("مجلد…") };
            folder.Click += (_, _) =>
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Lang.T("اختر مجلد"), Multiselect = true };
                if (dlg.ShowDialog() == true) AddItems(dlg.FolderNames);
            };
            add.Items.Add(program);
            add.Items.Add(folder);
            add.Items.Add(new Separator());
            foreach (var group in Groups)
            {
                var sub = new MenuItem { Header = Lang.T(group) };
                foreach (var a in Actions.Where(a => a.Group == group))
                {
                    var item = new MenuItem
                    {
                        Header = Lang.T(a.Name),
                        Icon = new TextBlock { Text = a.Glyph, FontFamily = new FontFamily(IconFont), FontSize = 14 },
                    };
                    string id = a.Id;
                    item.Click += (_, _) => AddItems(new[] { "a:" + id });
                    sub.Items.Add(item);
                }
                add.Items.Add(sub);
            }
            add.Items.Add(new Separator());
            var keys = new MenuItem { Header = Lang.T("اختصار كيبورد…") };
            keys.Click += (_, _) =>
            {
                if (KeyRecorder.Ask(Lang.T("اضغط الاختصار اللي تبي الزر يضغطه"), 0, 0) is { } k)
                    AddItems(new[] { $"k:{k.Mods}:{k.Vk}" });
            };
            add.Items.Add(keys);
            yield return add;

            var direction = new MenuItem { Header = Lang.T("الاتجاه") };
            var across = new MenuItem { Header = Lang.T("أفقي"), IsCheckable = true };
            var down = new MenuItem { Header = Lang.T("عمودي"), IsCheckable = true };
            across.Click += (_, _) => SetVertical(false);
            down.Click += (_, _) => SetVertical(true);
            direction.Items.Add(across);
            direction.Items.Add(down);
            direction.SubmenuOpened += (_, _) => { across.IsChecked = !Vertical; down.IsChecked = Vertical; };
            yield return direction;

            var names = new MenuItem { Header = Lang.T("اعرض الأسماء"), IsCheckable = true, IsChecked = ShowNames };
            names.Click += (_, _) =>
            {
                if (Info == null) return;
                if (names.IsChecked) Info.Options["names"] = "1"; else Info.Options.Remove("names");
                Widgets.Save();
                Rebuild();
            };
            yield return names;
        }

        [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("powrprof.dll")] private static extern bool SetSuspendState(bool hibernate, bool force, bool wakeupEventsDisabled);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);
    }

    /// <summary>A small window that waits for you to press a shortcut and gives it back.</summary>
    internal sealed class KeyRecorder : Window
    {
        private (uint Mods, uint Vk)? _result;
        private readonly TextBlock _shown;

        private KeyRecorder(string prompt, uint mods, uint vk)
        {
            Title = "Wibn";
            WindowStyle = WindowStyle.ToolWindow;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            FlowDirection = Lang.Flow;
            Background = (Brush)Application.Current.FindResource("Bg");
            Foreground = (Brush)Application.Current.FindResource("Fg");
            _shown = new TextBlock
            {
                Text = vk != 0 ? HotkeyManager.KeyText(mods, vk) : "…", FontSize = 22, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 10), FlowDirection = FlowDirection.LeftToRight,
            };
            Content = new StackPanel
            {
                Margin = new Thickness(24, 18, 24, 18), MinWidth = 300,
                Children =
                {
                    new TextBlock { Text = prompt, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 320, TextAlignment = TextAlignment.Center },
                    _shown,
                    new TextBlock { Text = Lang.T("Esc للإلغاء"), FontSize = 11, Foreground = (Brush)Application.Current.FindResource("Muted"), HorizontalAlignment = HorizontalAlignment.Center },
                },
            };
            PreviewKeyDown += OnKey;
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None) { Close(); return; }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
                return;   // wait for the key that goes with them
            uint mods = 0;
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods |= HotkeyManager.MOD_CONTROL;
            if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) mods |= HotkeyManager.MOD_ALT;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mods |= HotkeyManager.MOD_SHIFT;
            if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= HotkeyManager.MOD_WIN;
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0) return;
            _result = (mods, vk);
            _shown.Text = HotkeyManager.KeyText(mods, vk);
            // A moment to see it, then done.
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            t.Tick += (_, _) => { t.Stop(); Close(); };
            t.Start();
        }

        public static (uint Mods, uint Vk)? Ask(string prompt, uint mods, uint vk)
        {
            var w = new KeyRecorder(prompt, mods, vk);
            w.ShowDialog();
            return w._result;
        }
    }
}
