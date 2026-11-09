using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace WinPlus
{
    public partial class MainWindow : Window
    {
        private bool _loading = true;
        private bool _closeForReal;

        public event EventHandler? Hidden;

        public MainWindow()
        {
            InitializeComponent();

            EnabledBox.IsChecked = SettingsStore.Enabled;
            StepBox.SelectedIndex = Math.Clamp(SettingsStore.ScrollStep, 1, 5) - 1;
            ReverseBox.IsChecked = SettingsStore.Reverse;
            SmoothBox.IsChecked = SettingsStore.Smooth;
            StartupBox.IsChecked = SettingsStore.StartWithWindows;
            LanguageBox.SelectedIndex = Lang.English ? 1 : 0;
            _languageReady = true;
            ColorBox.IsChecked = SettingsStore.ColorEnabled;
            SetupColorPicker();
            int iconSize = SettingsStore.IconSize;
            IconSizeBox.IsChecked = iconSize > 0;
            IconSlider.Value = iconSize > 0 ? iconSize : 24;
            IconSizeText.Text = $"{(int)IconSlider.Value} px";
            ShowYearBox.IsChecked = SettingsStore.ShowYear;
            RunningFirstBox.IsChecked = SettingsStore.RunningFirst;
            FloatingBox.IsChecked = SettingsStore.Floating;
            MagnifyBox.IsChecked = SettingsStore.Magnify;
            IndicatorBox.IsChecked = SettingsStore.IndicatorColor;
            TaskbarGlassBox.IsChecked = SettingsStore.TaskbarStyle == 1;
            TaskbarGlassSlider.Value = Math.Clamp(SettingsStore.TaskbarOpacity, 0, 90);
            TaskbarGlassText.Text = $"{(int)TaskbarGlassSlider.Value}%";
            int pref = SettingsStore.SmallButtons;
            foreach (ComboBoxItem item in SmallButtonsBox.Items)
            {
                if ((string)item.Tag == pref.ToString()) SmallButtonsBox.SelectedItem = item;
            }
            UpdateAlignWarning();
            AutoHideBox.IsChecked = TaskbarPlacement.AutoHideEnabled;

            VersionText.Text = "Wibn " + UpdateChecker.CurrentText;
            BuildFeatureList();
            ApplyFeatureVisibility();

            _loading = false;
        }

        public void SetStatus(string text) => StatusText.Text = text;

        // ---- Features (Settings → المزايا) ----------------------------------------

        private void BuildFeatureList()
        {
            FeatureGrid.Children.Clear();
            foreach (var f in Features.All)
            {
                var box = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = Features.IsOn(f.Id) };
                DockPanel.SetDock(box, Dock.Right);
                var desc = new TextBlock { Text = f.Description, Style = (Style)FindResource("RowDesc") };
                var progress = new ProgressBar
                {
                    Height = 4, Maximum = 1, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed,
                    Foreground = (System.Windows.Media.Brush)FindResource("Accent"),
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x3D, 0x44)),
                    BorderThickness = new Thickness(0),
                };
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = f.Name, Style = (Style)FindResource("RowTitle") });
                text.Children.Add(desc);
                text.Children.Add(progress);
                var dock = new DockPanel();
                dock.Children.Add(box);
                dock.Children.Add(text);
                FeatureGrid.Children.Add(new Border { Style = (Style)FindResource("Row"), Margin = new Thickness(3, 0, 3, 6), Child = dock });

                box.Click += async (_, _) =>
                {
                    bool on = box.IsChecked == true;
                    Features.Set(f.Id, on);
                    ApplyFeatureVisibility();
                    if (Application.Current is App app) app.ApplyFeatures();
                    // Turned on and it needs its extra download: get it now.
                    if (on && !Features.IsDownloaded(f))
                    {
                        box.IsEnabled = false;
                        progress.Visibility = Visibility.Visible;
                        desc.Text = Lang.T("جاري التحميل…");
                        try
                        {
                            await Features.DownloadAsync(f, new Progress<double>(v => progress.Value = v));
                            desc.Text = f.Description;
                        }
                        catch (Exception ex)
                        {
                            CrashLog.Write("feature download", ex);
                            desc.Text = Lang.T("ما تحمّل. بيحاول مرة ثانية لما تفتح الميزة.");
                        }
                        finally
                        {
                            box.IsEnabled = true;
                            progress.Visibility = Visibility.Collapsed;
                        }
                    }
                };
            }
        }

        /// <summary>Hides the side-menu entries of features that are off.</summary>
        private void ApplyFeatureVisibility()
        {
            TextBlock? header = null;
            bool anyInGroup = false;
            void CloseGroup() { if (header != null) header.Visibility = anyInGroup ? Visibility.Visible : Visibility.Collapsed; }
            foreach (var child in SideList.Children)
            {
                if (child is TextBlock group)
                {
                    CloseGroup();
                    header = group;
                    anyInGroup = false;
                }
                else if (child is RadioButton { CommandParameter: string key } nav)
                {
                    bool on = Features.IsNavOn(key);
                    nav.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                    if (on) anyInGroup = true;
                    if (!on && nav.IsChecked == true) NavGeneral.IsChecked = true;
                }
            }
            CloseGroup();
        }

        // ---- Updates ------------------------------------------------------------------

        private UpdateChecker.Release? _release;

        internal void ShowUpdate(UpdateChecker.Release release)
        {
            _release = release;
            UpdateTitle.Text = Lang.F("نسخة جديدة: {0}", release.Tag);
            UpdateSub.Text = Lang.F("عندك {0}", UpdateChecker.CurrentText);
            UpdateText.Text = Lang.F("فيه نسخة جديدة: {0}", release.Tag);
            UpdateBar.Visibility = Visibility.Visible;
        }

        private async void OnUpdateNow(object sender, RoutedEventArgs e)
        {
            if (_release == null) return;
            UpdateNowButton.IsEnabled = false;
            UpdateSub.Text = Lang.T("جاري التحميل…");
            try
            {
                await UpdateChecker.InstallAsync(_release,
                    new Progress<double>(v => UpdateSub.Text = Lang.F("جاري التحميل… {0:P0}", v)), default);
                UpdateSub.Text = Lang.T("جاري التثبيت، البرنامج بيرجع يشتغل لحاله…");
                if (Application.Current is App app) app.ExitForUpdate();
            }
            catch (Exception ex)
            {
                CrashLog.Write("update", ex);
                UpdateSub.Text = Lang.T("ما تحمّل التحديث. جرّب بعدين.");
                UpdateNowButton.IsEnabled = true;
            }
        }

        private void OnIgnoreUpdate(object sender, RoutedEventArgs e)
        {
            if (_release != null) UpdateChecker.IgnoredVersion = _release.Tag;
            UpdateBar.Visibility = Visibility.Collapsed;
            UpdateText.Text = Lang.T("تجاهلت النسخة ") + _release?.Tag + Lang.T(". تقدر تحدّث من \"تحقق الحين\".");
        }

        private async void OnCheckUpdate(object sender, RoutedEventArgs e)
        {
            CheckUpdateButton.IsEnabled = false;
            UpdateText.Text = Lang.T("جاري التحقق…");
            var release = await UpdateChecker.CheckAsync(includeIgnored: true);
            CheckUpdateButton.IsEnabled = true;
            if (release != null) ShowUpdate(release);
            else UpdateText.Text = Lang.T("عندك آخر نسخة.");
        }

        public void CloseForReal()
        {
            _closeForReal = true;
            Close();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            UpdateAlignWarning();
        }

        // ---- Navigation (PowerToys-style sidebar) ------------------------------

        private BigFilesView? _bigView;
        private TempCleanView? _tempView;
        private DuplicatesView? _dupView;
        private CleanupPage? _binView;
        private HotkeysPage? _hotkeysPage;
        private DesktopPage? _desktopPage;
        private RamView? _ramView;
        private FileLockPage? _lockPage;
        private StartupPage? _startupPage;
        private DownloadsPage? _downloadsPage;
        private ConvertPage? _convertPage;
        private ToolPage? _resizePage, _compressPage, _archivePage;
        private TransferPage? _transferPage;
        private VideoPage? _videoPage;
        private CapturePage? _capturePage;
        private VolumePage? _volumePage;
        private WidgetsPage? _widgetsPage;

        private FrameworkElement[] AllPages => new FrameworkElement[]
        {
            PageTaskbar, PageDesktop, PageBig, PageTemp, PageDup, PageBin, PageHotkeys, PageRam, PageLock, PageStartup, PageDownloads, PageConvert, PageResize, PageCompress, PageArchive, PageVideo, PageTransfer, PageCapture, PageVolume, PageWidgets, PageGlass, PageGeneral,
        };

        private void OnNavChanged(object sender, RoutedEventArgs e)
        {
            if (PageGeneral == null) return;  // during startup
            if (sender is RadioButton { CommandParameter: string key }) ShowPage(key);
        }

        private void ShowPage(string key)
        {
            // Heavy pages are built only when first opened, so they cost nothing until then.
            FrameworkElement page = key switch
            {
                "big" => Build(PageBig, () => BigHost.Content = _bigView ??= new BigFilesView()),
                "temp" => Build(PageTemp, () => TempHost.Content = _tempView ??= new TempCleanView()),
                "dup" => Build(PageDup, () => DupHost.Content = _dupView ??= new DuplicatesView()),
                "bin" => Build(PageBin, () => BinHost.Content = _binView ??= new CleanupPage()),
                "hotkeys" => Build(PageHotkeys, () => HotkeysHost.Content = _hotkeysPage ??= new HotkeysPage()),
                "desktop" => Build(PageDesktop, () => DesktopHost.Content = _desktopPage ??= new DesktopPage()),
                "ram" => Build(PageRam, () => RamHost.Content = _ramView ??= new RamView()),
                "lock" => Build(PageLock, () => LockHost.Content = _lockPage ??= new FileLockPage()),
                "startup" => Build(PageStartup, () => StartupHost.Content = _startupPage ??= new StartupPage()),
                "downloads" => Build(PageDownloads, () => DownloadsHost.Content = _downloadsPage ??= new DownloadsPage()),
                "convert" => Build(PageConvert, () => ConvertHost.Content = _convertPage ??= new ConvertPage()),
                "resize" => Build(PageResize, () => ResizeHost.Content = _resizePage ??= new ToolPage(MediaTools.Mode.Resize)),
                "compress" => Build(PageCompress, () => CompressHost.Content = _compressPage ??= new ToolPage(MediaTools.Mode.Compress)),
                "archive" => Build(PageArchive, () => ArchiveHost.Content = _archivePage ??= new ToolPage(MediaTools.Mode.Archive)),
                "video" => Build(PageVideo, () => VideoHost.Content = _videoPage ??= new VideoPage()),
                "transfer" => Build(PageTransfer, () => TransferHost.Content = _transferPage ??= new TransferPage()),
                "capture" => Build(PageCapture, () => CaptureHost.Content = _capturePage ??= new CapturePage()),
                "volume" => Build(PageVolume, () => VolumeHost.Content = _volumePage ??= new VolumePage()),
                "widgets" => Build(PageWidgets, () => WidgetsHost.Content = _widgetsPage ??= new WidgetsPage()),
                "glass" => Build(PageGlass, () => GlassHost.Content ??= GlassSettings.Page()),
                "general" => PageGeneral,
                _ => PageTaskbar,
            };
            foreach (var p in AllPages)
            {
                p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private static FrameworkElement Build(FrameworkElement page, Action create)
        {
            create();
            return page;
        }

        // ---- Icon size / small buttons ------------------------------------------

        private void OnIconSizeChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            SettingsStore.IconSize = IconSizeBox.IsChecked == true ? (int)IconSlider.Value : 0;
        }

        private void OnIconSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (IconSizeText == null) return;
            IconSizeText.Text = $"{(int)IconSlider.Value} px";
            if (_loading) return;
            if (IconSizeBox.IsChecked != true)
            {
                IconSizeBox.IsChecked = true;  // moving the slider turns it on
            }
            else
            {
                SettingsStore.IconSize = (int)IconSlider.Value;
            }
        }

        // ---- Taskbar look ---------------------------------------------------------

        /// <summary>Glass taskbar: Windows' own blur under our color, see-through. Its own
        /// switch: the Liquid Glass page's main switch doesn't touch it.</summary>
        private void OnTaskbarGlassChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            SettingsStore.TaskbarStyle = TaskbarGlassBox.IsChecked == true ? 1 : 0;
            TaskbarInjector.SignalRefresh();
        }

        private readonly System.Windows.Threading.DispatcherTimer _glassTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };

        private void OnTaskbarGlassSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TaskbarGlassText == null) return;
            TaskbarGlassText.Text = $"{(int)TaskbarGlassSlider.Value}%";
            if (_loading) return;
            SettingsStore.TaskbarOpacity = (int)TaskbarGlassSlider.Value;
            if (TaskbarGlassBox.IsChecked != true) { TaskbarGlassBox.IsChecked = true; return; }  // moving it turns it on
            // The taskbar follows live, not on every single step.
            _glassTimer.Stop();
            _glassTimer.Tick -= OnGlassTimer;
            _glassTimer.Tick += OnGlassTimer;
            _glassTimer.Start();
        }

        private void OnGlassTimer(object? sender, EventArgs e)
        {
            _glassTimer.Stop();
            TaskbarInjector.SignalRefresh();
        }

        private void OnLookChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || FloatingBox == null) return;
            SettingsStore.Floating = FloatingBox.IsChecked == true;
            SettingsStore.Magnify = MagnifyBox.IsChecked == true;
            SettingsStore.IndicatorColor = IndicatorBox.IsChecked == true;
            if (SettingsStore.Magnify && EnabledBox.IsChecked != true)
            {
                EnabledBox.IsChecked = true;  // magnify works through the scroll feature
            }
            TaskbarInjector.SignalRefresh();
        }

        private void OnRunningFirstChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            SettingsStore.RunningFirst = RunningFirstBox.IsChecked == true;
            if (SettingsStore.RunningFirst && EnabledBox.IsChecked != true)
            {
                EnabledBox.IsChecked = true;  // it works through the scroll feature
            }
            TaskbarInjector.SignalRefresh();
        }

        private void OnShowYearChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            SettingsStore.ShowYear = ShowYearBox.IsChecked == true;
        }

        // ---- Auto-hide ----------------------------------------------

        private void OnAutoHideChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            TaskbarPlacement.AutoHideEnabled = AutoHideBox.IsChecked == true;
        }

        private void OnSmallButtonsChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (SmallButtonsBox.SelectedItem is ComboBoxItem { Tag: string tag })
            {
                SettingsStore.SmallButtons = int.Parse(tag);
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_closeForReal)
            {
                e.Cancel = true;
                Hide();
                // Free the scan results; the page is rebuilt next time.
                // Free the heavy pages; they are rebuilt next time.
                _bigView?.Release();
                _dupView?.Release();
                _bigView = null; _tempView = null; _dupView = null; _binView = null; _hotkeysPage = null; _desktopPage = null;
                DesktopHost.Content = null;
                _ramView?.Release();
                _ramView = null;
                RamHost.Content = null;
                _lockPage = null;
                LockHost.Content = null;
                _startupPage = null;
                StartupHost.Content = null;
                _downloadsPage = null;
                DownloadsHost.Content = null;
                _convertPage = null;
                ConvertHost.Content = null;
                _resizePage = null; _compressPage = null; _archivePage = null;
                ResizeHost.Content = null;
                CompressHost.Content = null;
                ArchiveHost.Content = null;
                _transferPage = null;
                TransferHost.Content = null;
                _videoPage = null;
                VideoHost.Content = null;
                _capturePage = null;
                CaptureHost.Content = null;
                BigHost.Content = null;
                TempHost.Content = null;
                DupHost.Content = null;
                BinHost.Content = null;
                HotkeysHost.Content = null;
                NavScroll.IsChecked = true;
                Hidden?.Invoke(this, EventArgs.Empty);
            }
            base.OnClosing(e);
        }

        private void UpdateAlignWarning()
        {
            // Scrolling now works with a centered taskbar too.
            AlignWarning.Visibility = Visibility.Collapsed;
        }

        private void OnSettingChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            SettingsStore.Enabled = EnabledBox.IsChecked == true;
            SettingsStore.ScrollStep = StepBox.SelectedIndex + 1;
            SettingsStore.Reverse = ReverseBox.IsChecked == true;
            SettingsStore.Smooth = SmoothBox.IsChecked == true;
            TaskbarInjector.SignalRefresh();
        }

        // ---- Taskbar color -------------------------------------------------

        // The taskbar follows the picker live; the timer keeps it from being
        // updated on every single mouse move.
        private readonly System.Windows.Threading.DispatcherTimer _colorTimer =
            new() { Interval = TimeSpan.FromMilliseconds(60) };
        private uint _pendingColor;

        private void SetupColorPicker()
        {
            Picker.Color = SettingsStore.Color;
            Picker.ColorChanged += argb =>
            {
                _pendingColor = argb;
                _colorTimer.Stop();
                _colorTimer.Start();
            };
            _colorTimer.Tick += (_, _) =>
            {
                _colorTimer.Stop();
                SettingsStore.Color = _pendingColor;
                if (ColorBox.IsChecked != true)
                {
                    ColorBox.IsChecked = true;  // picking a color turns the feature on
                }
                else
                {
                    TaskbarInjector.SignalRefresh();
                }
                if (Widgets.Back == Widgets.BackMode.Taskbar) Widgets.RefreshGlass();   // widgets in the taskbar's color follow it
            };
        }

        private void OnColorToggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            SettingsStore.ColorEnabled = ColorBox.IsChecked == true;
            TaskbarInjector.SignalRefresh();
            if (Widgets.Back == Widgets.BackMode.Taskbar) Widgets.RefreshGlass();
            _ = TaskbarInjector.EnsureStartMenuAsync();
        }

        private bool _languageReady;

        private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_languageReady) return;
            bool english = LanguageBox.SelectedIndex == 1;
            if (english == Lang.English) return;
            Lang.Save(english);
            var answer = MessageBox.Show(this,
                english ? "Wibn needs to restart to switch to English. Restart now?"
                        : "يحتاج Wibn يعيد التشغيل عشان يصير بالعربي. أعيد التشغيل الحين؟",
                "Wibn", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes,
                english ? MessageBoxOptions.None : MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            if (answer == MessageBoxResult.Yes) ((App)Application.Current).RestartApp();
        }

        private void OnStartupChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            SettingsStore.StartWithWindows = StartupBox.IsChecked == true;
        }

        private void OnAlignLeft(object sender, RoutedEventArgs e)
        {
            SettingsStore.AlignTaskbarLeft();
            // Explorer watches this key and re-aligns the taskbar by itself.
            UpdateAlignWarning();
            TaskbarInjector.SignalRefresh();
        }

        private async void OnReconnect(object sender, RoutedEventArgs e)
        {
            if (Application.Current is App app)
            {
                await app.ConnectAsync();
            }
        }

        public void ShowRestartButton(bool show)
        {
            RestartButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show) NavGeneral.IsChecked = true;  // the button lives on that page
        }

        private async void OnRestartExplorer(object sender, RoutedEventArgs e)
        {
            RestartButton.IsEnabled = false;
            SetStatus(Lang.T("جاري إعادة تشغيل التاسك بار…"));
            await TaskbarInjector.RestartExplorerAsync();
            RestartButton.IsEnabled = true;
            if (Application.Current is App app)
            {
                await app.ConnectAsync();
            }
        }

        private void OnOpenLog(object sender, RoutedEventArgs e)
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinPlus", "taskbar.log");
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            else
            {
                SetStatus(Lang.T("ما فيه سجل للحين — الـDLL ما اشتغل داخل التاسك بار."));
            }
        }
    }
}
