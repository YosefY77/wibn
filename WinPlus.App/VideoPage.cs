using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace WinPlus
{
    /// <summary>
    /// Settings page of the video download bar: how to use it, its shortcut, where
    /// files go, video or audio, and the two tools it uses. Built in code.
    /// </summary>
    internal sealed class VideoPage : StackPanel
    {
        private readonly Button _keyButton;
        private readonly TextBlock _keyDesc;
        private readonly TextBlock _heroTitle;
        private readonly TextBlock _badgeText;
        private readonly TextBlock _folderText;
        private readonly Button _folderReset;
        private bool _capturing;

        private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
        private static Style St(string key) => (Style)Application.Current.FindResource(key);

        public VideoPage()
        {
            // ---- How to use it
            _badgeText = new TextBlock
            {
                FontSize = 15, FontWeight = FontWeights.SemiBold, FlowDirection = FlowDirection.LeftToRight,
                Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            var badge = new Border
            {
                MinWidth = 64, Height = 64, CornerRadius = new CornerRadius(32), Margin = new Thickness(0, 0, 16, 0),
                Padding = new Thickness(10, 0, 10, 0),
                Background = new SolidColorBrush(Color.FromArgb(0x1F, 0x0A, 0x84, 0xFF)), Child = _badgeText,
            };
            var hero = new DockPanel();
            DockPanel.SetDock(badge, Dock.Left);
            hero.Children.Add(badge);
            var heroText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _heroTitle = new TextBlock { FontSize = 17, FontWeight = FontWeights.SemiBold };
            heroText.Children.Add(_heroTitle);
            heroText.Children.Add(new TextBlock
            {
                Foreground = Res("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
                Text = Lang.T("من أي مكان، اضغط الاختصار ويطلع شريط بنص الشاشة. الصق رابط المقطع والشريط يختفي على طول، ") +
                       Lang.T("ولما يخلص التحميل ينفتح المجلد والملف محدد. Esc أو الضغط برا الشريط يسكّره."),
            });
            hero.Children.Add(heroText);
            Children.Add(new Border
            {
                Background = Res("RowBg"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(20), Margin = new Thickness(0, 0, 0, 12), Child = hero,
            });

            // ---- Settings (two columns)
            var grid = new UniformGrid { Columns = 2, Margin = new Thickness(-3, 0, -3, 0) };

            var onBox = new CheckBox { Style = St("Switch"), IsChecked = VideoDownloader.Enabled };
            onBox.Click += (_, _) => { VideoDownloader.Enabled = onBox.IsChecked == true; UpdateKey(); };
            grid.Children.Add(Row(onBox, "", Lang.T("الاختصار شغّال"), Lang.T("طفّه لو تبي الاختصار لبرنامج ثاني"), 3));

            _keyButton = new Button { Style = St("FlatButton"), Padding = new Thickness(14, 5, 14, 5), MinWidth = 96 };
            _keyButton.Click += (_, _) => StartCapture();
            _keyDesc = new TextBlock { Style = St("RowDesc") };
            grid.Children.Add(Row(_keyButton, "", Lang.T("الاختصار"), _keyDesc, 3));

            var kind = new ComboBox { Width = 170, VerticalAlignment = VerticalAlignment.Center };
            kind.Items.Add(Lang.T("فيديو (أعلى جودة)"));
            kind.Items.Add(Lang.T("صوت فقط (MP3)"));
            kind.SelectedIndex = VideoDownloader.AudioOnly ? 1 : 0;
            kind.SelectionChanged += (_, _) => VideoDownloader.AudioOnly = kind.SelectedIndex == 1;
            grid.Children.Add(Row(kind, "", Lang.T("وش يتحمّل"), Lang.T("روابط سبوتيفاي دايمًا صوت"), 3));

            grid.Children.Add(ToolRow(
                "yt-dlp + gallery-dl", Lang.T("أدوات التحميل (مفتوحة المصدر، تقريبًا 30 ميقا)"),
                () => VideoDownloader.IsInstalled, (p, c) => VideoDownloader.InstallAsync(p, c)));
            grid.Children.Add(ToolRow(
                "FFmpeg", Lang.T("لأعلى جودة وMP3 (تقريبًا 140 ميقا). بدونه تتحمّل أفضل نسخة جاهزة"),
                () => Converter.FindFfmpeg() != null, (p, c) => Converter.DownloadFfmpegAsync(p, c)));
            Children.Add(grid);

            // ---- Folder (full width: paths are long)
            _folderText = new TextBlock { Style = St("RowDesc"), FlowDirection = FlowDirection.LeftToRight, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Right };
            var change = new Button { Style = St("FlatButton"), Content = Lang.T("تغيير…"), Padding = new Thickness(14, 5, 14, 5) };
            change.Click += (_, _) => PickFolder();
            _folderReset = new Button { Style = St("FlatButton"), Content = Lang.T("التحميلات"), Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0) };
            _folderReset.Click += (_, _) => { VideoDownloader.SaveFolder = ""; UpdateFolder(); };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(_folderReset);
            buttons.Children.Add(change);
            Children.Add(Row(buttons, "", Lang.T("مكان الحفظ"), _folderText, 0));

            // ---- Sign-in (cookies.txt from the user's own browser)
            _cookiesText = new TextBlock { Style = St("RowDesc"), TextWrapping = TextWrapping.Wrap };
            var signIn = new Button { Style = St("AccentButton"), Content = Lang.T("سجّل دخول"), Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0) };
            signIn.Click += (_, _) =>
            {
                var w = new SignInWindow { Owner = Window.GetWindow(this) };
                w.Closed += (_, _) => UpdateCookies();
                w.Show();
            };
            var pickCookies = new Button { Style = St("FlatButton"), Content = Lang.T("ملف…"), Padding = new Thickness(14, 5, 14, 5), ToolTip = Lang.T("أو اختر ملف cookies.txt صدّرته بنفسك") };
            pickCookies.Click += (_, _) => PickCookies();
            _cookiesRemove = new Button { Style = St("FlatButton"), Content = Lang.T("إزالة"), Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0) };
            _cookiesRemove.Click += (_, _) => { SignInWindow.SignOut(); VideoDownloader.CookiesFile = null; UpdateCookies(); };
            var cookieButtons = new StackPanel { Orientation = Orientation.Horizontal };
            cookieButtons.Children.Add(_cookiesRemove);
            cookieButtons.Children.Add(signIn);
            cookieButtons.Children.Add(pickCookies);
            Children.Add(Row(cookieButtons, "", Lang.T("تسجيل الدخول"), _cookiesText, 0));

            // ---- Liquid glass
            Children.Add(GlassSettings.Section(GlassSettings.Video, VideoDownloader.ShowBar,
                Lang.T("الشريط يعرض اللي وراه منحني عند الأطراف مثل زجاج أبل، حتى لو وراه فيديو")));

            // ---- Sites
            Children.Add(new TextBlock { Text = Lang.T("المواقع"), Style = St("Section"), FontSize = 18, Margin = new Thickness(2, 24, 0, 8) });
            var sites = new UniformGrid { Columns = 2, Margin = new Thickness(-3, 0, -3, 0) };
            sites.Children.Add(Card(Lang.T("YouTube · Instagram · X (تويتر)"), Lang.T("المقاطع والصور والشورتس والريلز، وكل صور البوست لو فيه أكثر من صورة")));
            sites.Children.Add(Card("TikTok · Reddit · Facebook", Lang.T("وأي موقع ثاني تدعمه yt-dlp (أكثر من ألف موقع)")));
            sites.Children.Add(Card("Spotify", Lang.T("صوت سبوتيفاي مشفّر، فيدوّر على نفس الأغنية في يوتيوب ويحمّلها MP3. رابط أغنية وحدة بس")));
            sites.Children.Add(Card(Lang.T("ما يتحمّل"), Lang.T("المقاطع الخاصة (إلا لو حسابك يشوفها ومسجّل دخول في Wibn)، أو البث المباشر وهو شغّال")));
            Children.Add(sites);

            UpdateKey();
            UpdateFolder();
            UpdateCookies();
        }

        private TextBlock _cookiesText = null!;
        private Button _cookiesRemove = null!;

        private void UpdateCookies()
        {
            string? file = VideoDownloader.CookiesFile;
            _cookiesText.Text = file != null
                ? Lang.T("مسجّل. يستخدم تسجيل دخولك بس لما الموقع يطلبه (مثل كل صور بوست انستقرام، وريديت). لو وقف يشتغل اضغط \"سجّل دخول\" مرة ثانية.")
                : Lang.T("انستقرام وريديت أحيانًا ما يعطون إلا للمسجّل. اضغط \"سجّل دخول\"، سجّل في المواقع اللي تبيها، وخلاص.");
            _cookiesRemove.ToolTip = Lang.T("تسجيل خروج: يمسح تسجيل الدخول من Wibn");
            _cookiesRemove.Visibility = file != null ? Visibility.Visible : Visibility.Collapsed;
        }

        private void PickCookies()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = Lang.T("اختر ملف cookies.txt"),
                Filter = "cookies.txt|*.txt|*.*|*.*",
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            // A Netscape cookie file (what the tools read): has this header, or tab-separated rows.
            string head = "";
            try { head = string.Join("\n", System.IO.File.ReadLines(dlg.FileName).Take(20)); } catch { }
            if (!head.Contains("Netscape HTTP Cookie File") && !head.Contains('\t'))
            {
                MessageBox.Show(Lang.T("هذا مو ملف cookies.txt. صدّره بصيغة Netscape (الإضافة تسويها كذا)."), "Wibn",
                                MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK, Lang.MsgOptions);
                return;
            }
            VideoDownloader.CookiesFile = dlg.FileName;
            UpdateCookies();
        }

        // ---- Shortcut ------------------------------------------------------------------------

        private void UpdateKey()
        {
            string k = VideoDownloader.HotkeyText;
            _badgeText.Text = k;
            _heroTitle.Text = Lang.F("اضغط {0} والصق الرابط", k);
            _keyButton.Content = k;
            _keyDesc.Text = VideoDownloader.HotkeyFailed
                ? Lang.T("⚠ برنامج ثاني ماخذ هذا الاختصار، اختر غيره")
                : Lang.T("اضغط الزر ثم الاختصار اللي تبيه");
            _keyDesc.Foreground = VideoDownloader.HotkeyFailed ? new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x40)) : Res("Muted");
        }

        private void StartCapture()
        {
            var window = Window.GetWindow(this);
            if (window == null || _capturing) return;
            _capturing = true;
            VideoDownloader.Pause();   // or pressing the current one would open the bar
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
            VideoDownloader.Refresh();
            UpdateKey();
        }

        private void OnCaptureCancel(object? sender, EventArgs e) => EndCapture();

        private void OnCaptureKey(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { EndCapture(); return; }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
                return;   // wait for the actual key

            uint mods = 0;
            var m = Keyboard.Modifiers;
            if (m.HasFlag(ModifierKeys.Control)) mods |= HotkeyManager.MOD_CONTROL;
            if (m.HasFlag(ModifierKeys.Alt)) mods |= HotkeyManager.MOD_ALT;
            if (m.HasFlag(ModifierKeys.Shift)) mods |= HotkeyManager.MOD_SHIFT;
            if (m.HasFlag(ModifierKeys.Windows)) mods |= HotkeyManager.MOD_WIN;
            // A plain letter would get in the way of typing: needs Ctrl, Alt or Win.
            if ((mods & (HotkeyManager.MOD_CONTROL | HotkeyManager.MOD_ALT | HotkeyManager.MOD_WIN)) == 0)
            {
                _keyButton.Content = Lang.T("لازم معه Ctrl أو Alt");
                return;
            }
            VideoDownloader.HotkeyMods = mods;
            VideoDownloader.HotkeyKey = (uint)KeyInterop.VirtualKeyFromKey(key);
            EndCapture();
        }

        // ---- Folder ----------------------------------------------------------------------------

        private void UpdateFolder()
        {
            _folderText.Text = VideoDownloader.SaveFolder;
            _folderReset.Visibility = VideoDownloader.IsDefaultFolder ? Visibility.Collapsed : Visibility.Visible;
        }

        private void PickFolder()
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = Lang.T("وين تبي تنحفظ المقاطع؟"),
                InitialDirectory = VideoDownloader.SaveFolder,
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) == true)
            {
                VideoDownloader.SaveFolder = dlg.FolderName;
                UpdateFolder();
            }
        }

        // ---- Building blocks ----------------------------------------------------------------

        /// <summary>A tool's state with a download button while it's missing.</summary>
        private Border ToolRow(string name, string what, Func<bool> installed,
                               Func<IProgress<double>, CancellationToken, System.Threading.Tasks.Task> install)
        {
            var desc = new TextBlock { Style = St("RowDesc") };
            var bar = new ProgressBar
            {
                Height = 4, Maximum = 1, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed,
                Foreground = Res("Accent"), Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x44)), BorderThickness = new Thickness(0),
            };
            var button = new Button { Style = St("AccentButton"), Content = Lang.T("تحميل"), Padding = new Thickness(14, 5, 14, 5) };

            void Show()
            {
                bool ok = installed();
                desc.Text = ok ? Lang.T("✓ جاهز") : what;
                button.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            }
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                bar.Visibility = Visibility.Visible;
                desc.Text = Lang.T("جاري التحميل…");
                try
                {
                    await install(new Progress<double>(v => bar.Value = v), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    CrashLog.Write("download " + name, ex);
                    desc.Text = Lang.T("ما تحمّل. تأكد من النت وجرّب مرة ثانية.");
                    button.IsEnabled = true;
                    bar.Visibility = Visibility.Collapsed;
                    return;
                }
                bar.Visibility = Visibility.Collapsed;
                button.IsEnabled = true;
                Show();
            };
            Show();

            var text = new StackPanel();
            text.Children.Add(desc);
            text.Children.Add(bar);
            return Row(button, "", name, text, 3);
        }

        private static Border Row(UIElement control, string icon, string title, object desc, double side)
        {
            DockPanel.SetDock(control, Dock.Right);
            if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            text.Children.Add(new TextBlock { Text = title, Style = St("RowTitle") });
            text.Children.Add(desc as UIElement ?? new TextBlock { Text = desc as string ?? "", Style = St("RowDesc"), TextWrapping = TextWrapping.Wrap });
            var dock = new DockPanel();
            dock.Children.Add(control);
            dock.Children.Add(new TextBlock { Text = icon, Style = St("RowIcon") });
            dock.Children.Add(text);
            return new Border { Style = St("Row"), Margin = new Thickness(side, 0, side, 6), Child = dock };
        }

        private static Border Card(string title, string desc)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = title, Style = St("RowTitle"), FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = desc, Style = St("RowDesc"), TextWrapping = TextWrapping.Wrap });
            return new Border { Style = St("Row"), Margin = new Thickness(3, 0, 3, 6), Child = text };
        }
    }
}
