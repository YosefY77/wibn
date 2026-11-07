using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace WinPlus
{
    /// <summary>
    /// Settings page of screen capture: a shortcut for each of the four kinds (none
    /// at first), the video length, the "last minute" switch and where files go.
    /// Built in code.
    /// </summary>
    internal sealed class CapturePage : StackPanel
    {
        private readonly Button[] _keyButtons = new Button[4];
        private readonly Button[] _clearButtons = new Button[4];
        private readonly TextBlock[] _keyDescs = new TextBlock[4];
        private readonly string[] _normalDescs = new string[4];
        private readonly CheckBox _replayBox;
        private readonly TextBlock _replayDesc;
        private readonly TextBlock _folderText;
        private readonly Button _folderReset;
        private ScreenCapture.Kind? _capturing;

        private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
        private static Style St(string key) => (Style)Application.Current.FindResource(key);

        public CapturePage()
        {
            // ---- How to use it
            var badge = new Border
            {
                Width = 64, Height = 64, CornerRadius = new CornerRadius(32), Margin = new Thickness(0, 0, 16, 0),
                Background = new SolidColorBrush(Color.FromArgb(0x1F, 0x0A, 0x84, 0xFF)),
                Child = new TextBlock
                {
                    Text = "", FontSize = 26, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            var hero = new DockPanel();
            DockPanel.SetDock(badge, Dock.Left);
            hero.Children.Add(badge);
            var heroText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            heroText.Children.Add(new TextBlock { Text = Lang.T("اختر اختصار لكل نوع تبيه"), FontSize = 17, FontWeight = FontWeights.SemiBold });
            heroText.Children.Add(new TextBlock
            {
                Foreground = Res("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
                Text = Lang.T("اضغط على زر \"اختيار\" ثم اضغط الأزرار اللي تبيها. الاختصار يشتغل من أي مكان، ") +
                       Lang.T("وبعد كل تصوير يطلع لك إشعار صغير في الزاوية، اضغط عليه عشان تفتح الملف."),
            });
            hero.Children.Add(heroText);
            Children.Add(new Border
            {
                Background = Res("RowBg"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(20), Margin = new Thickness(0, 0, 0, 12), Child = hero,
            });

            // ---- The four shortcuts
            Children.Add(Section(Lang.T("الاختصارات"), 8));
            Children.Add(KeyRow(ScreenCapture.Kind.Region, "", Lang.T("تصوير مكان تحدده"),
                Lang.T("الشاشة توقف، وتسحب على المكان اللي تبيه أو تضغط على نافذة. بعدها تختار: حفظ، حفظ باسم، أو نسخ")));
            Children.Add(KeyRow(ScreenCapture.Kind.Full, "", Lang.T("تصوير الشاشة كاملة"),
                Lang.T("يصوّر الشاشة اللي فيها الماوس")));
            Children.Add(KeyRow(ScreenCapture.Kind.Record, "", Lang.T("تسجيل فيديو"),
                Lang.T("يسجّل الشاشة اللي فيها الماوس. اضغط الاختصار مرة ثانية عشان توقف قبل ما يخلص الوقت")));
            Children.Add(KeyRow(ScreenCapture.Kind.Replay, "", Lang.T("حفظ آخر دقيقة"),
                Lang.T("يحفظ اللي صار على الشاشة قبل لا تضغط. يحتاج التسجيل المستمر يكون شغّال (تحت)")));

            // ---- Video
            Children.Add(Section(Lang.T("الفيديو"), 24));
            var grid = new UniformGrid { Columns = 2, Margin = new Thickness(-3, 0, -3, 0) };

            var length = Minutes(ScreenCapture.RecordChoices, ScreenCapture.RecordMinutes);
            length.SelectionChanged += (_, _) => ScreenCapture.RecordMinutes = (int)((ComboBoxItem)length.SelectedItem).Tag;
            grid.Children.Add(VideoPage.Row(length, "", Lang.T("مدة التسجيل"), Lang.T("يوقف ويحفظ لحاله لما يخلص الوقت"), 3));

            _replayBox = new CheckBox { Style = St("Switch"), IsChecked = ScreenCapture.ReplayOn };
            _replayBox.Click += (_, _) => OnReplaySwitch();
            _replayDesc = new TextBlock { Style = St("RowDesc"), TextWrapping = TextWrapping.Wrap };
            grid.Children.Add(VideoPage.Row(_replayBox, "", Lang.T("التسجيل المستمر"), _replayDesc, 3));

            var keep = Minutes(ScreenCapture.ReplayChoices, ScreenCapture.ReplayMinutes);
            keep.SelectionChanged += (_, _) => ScreenCapture.ReplayMinutes = (int)((ComboBoxItem)keep.SelectedItem).Tag;
            grid.Children.Add(VideoPage.Row(keep, "", Lang.T("كم يحفظ من آخر"), Lang.T("الاختصار يحفظ هالمدة اللي قبله"), 3));

            var sound = new CheckBox { Style = St("Switch"), IsChecked = ScreenCapture.RecordSound };
            sound.Click += (_, _) => ScreenCapture.RecordSound = sound.IsChecked == true;
            grid.Children.Add(VideoPage.Row(sound, "", Lang.T("صوت الجهاز"), Lang.T("يسجّل الصوت اللي يطلع من السماعات"), 3));

            var fps = new ComboBox { Width = 120, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight };
            fps.Items.Add("30 FPS");
            fps.Items.Add("60 FPS");
            fps.SelectedIndex = ScreenCapture.Fps == 60 ? 1 : 0;
            fps.SelectionChanged += (_, _) => ScreenCapture.Fps = fps.SelectedIndex == 1 ? 60 : 30;
            grid.Children.Add(VideoPage.Row(fps, "", Lang.T("السلاسة"), Lang.T("60 أنعم للألعاب، بس الملف أكبر"), 3));

            grid.Children.Add(VideoPage.ToolRow(
                "FFmpeg", Lang.T("للفيديو (تقريبًا 140 ميقا). الصور ما تحتاجه"),
                () => Converter.FindFfmpeg() != null,
                async (p, c) => { await Converter.DownloadFfmpegAsync(p, c); UpdateReplay(); }));
            Children.Add(grid);

            // ---- Saving
            Children.Add(Section(Lang.T("الحفظ"), 24));
            var copy = new CheckBox { Style = St("Switch"), IsChecked = ScreenCapture.CopyPicture };
            copy.Click += (_, _) => ScreenCapture.CopyPicture = copy.IsChecked == true;
            Children.Add(VideoPage.Row(copy, "", Lang.T("نسخ صورة الشاشة كاملة"), Lang.T("تنسخ بعد ما تنحفظ عشان تلصقها على طول (Ctrl+V). تحديد المكان تختار له من الأزرار"), 0));

            _folderText = new TextBlock { Style = St("RowDesc"), TextWrapping = TextWrapping.Wrap };
            var change = new Button { Style = St("FlatButton"), Content = Lang.T("تغيير…"), Padding = new Thickness(14, 5, 14, 5) };
            change.Click += (_, _) => PickFolder();
            _folderReset = new Button { Style = St("FlatButton"), Content = Lang.T("الافتراضي"), Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0) };
            _folderReset.Click += (_, _) => { ScreenCapture.CustomFolder = ""; UpdateFolder(); };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(_folderReset);
            buttons.Children.Add(change);
            Children.Add(VideoPage.Row(buttons, "", Lang.T("مكان الحفظ"), _folderText, 0));

            UpdateKeys();
            UpdateReplay();
            UpdateFolder();

            ScreenCapture.Changed += OnChanged;
            Unloaded += (_, _) => ScreenCapture.Changed -= OnChanged;
            Loaded += (_, _) => { ScreenCapture.Changed -= OnChanged; ScreenCapture.Changed += OnChanged; };
        }

        private void OnChanged()
        {
            if (_capturing != null) return;
            UpdateKeys();
            UpdateReplay();
        }

        private static TextBlock Section(string text, double top) =>
            new() { Text = text, Style = St("Section"), FontSize = 18, Margin = new Thickness(2, top, 0, 8) };

        private static ComboBox Minutes(int[] choices, int current)
        {
            var box = new ComboBox { Width = 120, VerticalAlignment = VerticalAlignment.Center };
            foreach (int m in choices)
            {
                string text = m == 1 ? Lang.T("دقيقة") : m == 2 ? Lang.T("دقيقتين") : Lang.F("{0} دقائق", m);
                if (m == 60) text = Lang.T("ساعة");
                box.Items.Add(new ComboBoxItem { Content = text, Tag = m });
            }
            box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == current) ?? box.Items[0];
            return box;
        }

        // ---- Shortcuts ------------------------------------------------------------------------

        private Border KeyRow(ScreenCapture.Kind kind, string icon, string title, string desc)
        {
            int i = (int)kind;
            _normalDescs[i] = desc;
            _keyDescs[i] = new TextBlock { Style = St("RowDesc"), TextWrapping = TextWrapping.Wrap };
            _keyButtons[i] = new Button { Style = St("FlatButton"), Padding = new Thickness(14, 5, 14, 5), MinWidth = 110 };
            _keyButtons[i].Click += (_, _) => StartCapture(kind);
            _clearButtons[i] = new Button
            {
                Style = St("FlatButton"), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 0),
                Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11 },
                ToolTip = Lang.T("إزالة الاختصار"),
            };
            _clearButtons[i].Click += (_, _) => ScreenCapture.SetHotkey(kind, 0, 0);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(_clearButtons[i]);
            buttons.Children.Add(_keyButtons[i]);
            return VideoPage.Row(buttons, icon, title, _keyDescs[i], 0);
        }

        private void UpdateKeys()
        {
            foreach (ScreenCapture.Kind k in Enum.GetValues(typeof(ScreenCapture.Kind)))
            {
                int i = (int)k;
                bool has = ScreenCapture.HasHotkey(k);
                _keyButtons[i].Content = has ? ScreenCapture.HotkeyText(k) : Lang.T("اختيار");
                _clearButtons[i].Visibility = has ? Visibility.Visible : Visibility.Collapsed;
                bool failed = ScreenCapture.HotkeyFailed(k);
                _keyDescs[i].Text = failed ? Lang.T("⚠ برنامج ثاني ماخذ هذا الاختصار، اختر غيره") : _normalDescs[i];
                _keyDescs[i].Foreground = failed ? new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x40)) : Res("Muted");
            }
        }

        private void StartCapture(ScreenCapture.Kind kind)
        {
            var window = Window.GetWindow(this);
            if (window == null) return;
            if (_capturing != null) EndCapture();
            _capturing = kind;
            ScreenCapture.Pause();   // or pressing the current one would take a picture
            _keyButtons[(int)kind].Content = Lang.T("اضغط الاختصار…");
            window.PreviewKeyDown += OnCaptureKey;
            window.PreviewKeyUp += OnCaptureKeyUp;
            window.Deactivated += OnCaptureCancel;
        }

        private void EndCapture()
        {
            var window = Window.GetWindow(this);
            if (window != null)
            {
                window.PreviewKeyDown -= OnCaptureKey;
                window.PreviewKeyUp -= OnCaptureKeyUp;
                window.Deactivated -= OnCaptureCancel;
            }
            _capturing = null;
            ScreenCapture.Refresh();
            UpdateKeys();
        }

        private void OnCaptureCancel(object? sender, EventArgs e) => EndCapture();

        // Print Screen only sends "key up" to apps.
        private void OnCaptureKeyUp(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Snapshot) OnCaptureKey(sender, e);
        }

        private void OnCaptureKey(object sender, KeyEventArgs e)
        {
            if (_capturing is not ScreenCapture.Kind kind) return;
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

            // A plain letter would get in the way of typing: needs Ctrl, Alt or Win
            // (F1-F24 and Print Screen are fine alone).
            bool alone = key == Key.Snapshot || (key >= Key.F1 && key <= Key.F24);
            if (!alone && (mods & (HotkeyManager.MOD_CONTROL | HotkeyManager.MOD_ALT | HotkeyManager.MOD_WIN)) == 0)
            {
                _keyButtons[(int)kind].Content = Lang.T("لازم معه Ctrl أو Alt");
                return;
            }
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);

            // The same keys on another kind: that one loses them.
            foreach (ScreenCapture.Kind other in Enum.GetValues(typeof(ScreenCapture.Kind)))
            {
                if (other != kind && ScreenCapture.HotkeyMods(other) == mods && ScreenCapture.HotkeyKey(other) == vk)
                    ScreenCapture.SetHotkey(other, 0, 0);
            }
            _capturing = null;
            ScreenCapture.SetHotkey(kind, mods, vk);
            EndCapture();
        }

        // ---- Last minute --------------------------------------------------------------------------

        private void OnReplaySwitch()
        {
            bool on = _replayBox.IsChecked == true;
            if (on && !ScreenCapture.HasFfmpeg)
            {
                _replayBox.IsChecked = false;
                _replayDesc.Text = Lang.T("يحتاج FFmpeg أول. حمّله من تحت");
                _replayDesc.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x40));
                return;
            }
            ScreenCapture.ReplayOn = on;
            UpdateReplay();
        }

        private void UpdateReplay()
        {
            _replayBox.IsChecked = ScreenCapture.ReplayOn;
            _replayDesc.Foreground = Res("Muted");
            _replayDesc.Text = ScreenCapture.ReplayOn
                ? Lang.T("شغّال: يسجّل الشاشة الرئيسية بالخلفية عشان تقدر تحفظ آخر دقيقة. ياخذ شوي من الجهاز")
                : Lang.T("لازم يكون شغّال عشان \"حفظ آخر دقيقة\". يسجّل بالخلفية ويمسح القديم أول بأول");
        }

        // ---- Folder ---------------------------------------------------------------------------------

        private void UpdateFolder()
        {
            bool custom = ScreenCapture.CustomFolder.Length > 0;
            _folderText.Text = custom
                ? ScreenCapture.CustomFolder
                : Lang.F("الصور: {0}\nالفيديو: {1}", ScreenCapture.PictureFolder, ScreenCapture.VideoFolder);
            _folderReset.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        }

        private void PickFolder()
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = Lang.T("وين تبي تنحفظ الصور والمقاطع؟"),
                InitialDirectory = ScreenCapture.PictureFolder,
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) == true)
            {
                ScreenCapture.CustomFolder = dlg.FolderName;
                UpdateFolder();
            }
        }
    }
}
