using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinPlus
{
    public partial class StartupPage : UserControl
    {
        public sealed class Row : INotifyPropertyChanged
        {
            public StartupApps.Entry Entry { get; init; } = null!;
            public string Title => Entry.Title;
            public string Detail { get; init; } = "";
            public ImageSource? Icon { get; set; }
            public Visibility AdminVisibility => Entry.AllUsers ? Visibility.Visible : Visibility.Collapsed;
            public bool Enabled => Entry.Enabled;
            public event PropertyChangedEventHandler? PropertyChanged;
            public void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
        }

        private List<Row> _rows = new();
        private string _signature = "";
        private bool _loading;
        // Checks every second (only while the page is on screen) whether anything changed:
        // here, in Task Manager, or an app adding/removing itself. Cheap: registry reads only.
        private readonly System.Windows.Threading.DispatcherTimer _watch =
            new() { Interval = TimeSpan.FromSeconds(1) };

        public StartupPage()
        {
            InitializeComponent();
            Loaded += async (_, _) => { await LoadAsync(); _watch.Start(); };
            Unloaded += (_, _) => _watch.Stop();
            IsVisibleChanged += (_, _) => { if (IsVisible && IsLoaded) _watch.Start(); else _watch.Stop(); };
            _watch.Tick += async (_, _) => await CheckForChangesAsync();
        }

        private async Task CheckForChangesAsync()
        {
            if (_loading) return;
            string now = await Task.Run(StartupApps.Signature);
            if (now != _signature && !_loading) await LoadAsync(quiet: true);
        }

        private async Task LoadAsync(bool quiet = false)
        {
            if (_loading) return;
            _loading = true;
            try
            {
                if (!quiet) SummaryText.Text = Lang.T("جاري القراءة…");
                // Reading the registry, manifests and file versions: keep the UI free.
                var (entries, sig) = await Task.Run(() =>
                {
                    var list = StartupApps.Load();
                    return (list, StartupApps.Signature());
                });
                _signature = sig;
                _rows = entries.Select(e => new Row
                {
                    Entry = e,
                    Detail = (e.Publisher.Length > 0 ? e.Publisher + "  ·  " : "") +
                             (e.ApprovedKey == "Packaged" ? Lang.T("تطبيق من المتجر") : e.Command),
                    Icon = IconFor(e.LogoPath) ?? IconFor(e.ExePath),
                }).ToList();
                AppList.ItemsSource = _rows;
                UpdateSummary();
            }
            finally { _loading = false; }
        }

        private void UpdateSummary()
        {
            int on = _rows.Count(r => r.Entry.Enabled);
            SummaryText.Text = Lang.F("{0} برنامج، الشغّال منها {1}", _rows.Count, on);
        }

        private static ImageSource? IconFor(string? path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return null;
                if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return PngFor(path);
                if (!File.Exists(path)) return null;
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon == null) return null;
                var img = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                                                              BitmapSizeOptions.FromEmptyOptions());
                img.Freeze();
                return img;
            }
            catch { return null; }
        }

        /// <summary>Store app logos are often saved as "Logo.scale-200.png" instead of "Logo.png".</summary>
        private static ImageSource? PngFor(string path)
        {
            string? file = File.Exists(path) ? path : null;
            if (file == null)
            {
                string dir = Path.GetDirectoryName(path) ?? "";
                string stem = Path.GetFileNameWithoutExtension(path);
                if (!Directory.Exists(dir)) return null;
                file = Directory.EnumerateFiles(dir, stem + ".*png")
                                .OrderByDescending(f => f.Contains("scale-200") ? 2 : f.Contains("scale-100") ? 1 : 0)
                                .FirstOrDefault();
            }
            if (file == null) return null;
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = 56;
            img.UriSource = new Uri(file);
            img.EndInit();
            img.Freeze();
            return img;
        }

        /// <summary>Part of Windows: lives in the Windows folder or is signed as Windows itself.</summary>
        private static bool IsWindowsPart(StartupApps.Entry e)
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
            if (e.ExePath != null && win.Length > 1 &&
                e.ExePath.StartsWith(win, StringComparison.OrdinalIgnoreCase)) return true;
            if (e.Command.Contains("%windir%", StringComparison.OrdinalIgnoreCase) ||
                e.Command.Contains("%SystemRoot%", StringComparison.OrdinalIgnoreCase)) return true;
            return e.Publisher.Equals("Microsoft Windows", StringComparison.OrdinalIgnoreCase) ||
                   e.Name.Equals("SecurityHealth", StringComparison.OrdinalIgnoreCase);
        }

        private async void OnRefresh(object sender, RoutedEventArgs e)
        {
            StartupApps.ForgetPackages();
            await LoadAsync();
        }

        private void OnToggle(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox { Tag: Row row } box) return;
            bool want = box.IsChecked == true;
            if (!want && IsWindowsPart(row.Entry))
            {
                var answer = MessageBox.Show(Window.GetWindow(this)!,
                    Lang.F("⚠ \"{0}\" جزء من ويندوز.\n\nلو وقّفته عن التشغيل مع الجهاز ممكن يوقف شي في ويندوز ", row.Entry.Title) +
                    Lang.T("(مثل أيقونة الحماية أو الصوت أو كرت الشاشة) لين ترجّع تشغّله.\n\nمتأكد تبي توقفه؟"),
                    Lang.T("تحذير: من النظام"), MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    MessageBoxResult.No, Lang.MsgOptions);
                if (answer != MessageBoxResult.Yes)
                {
                    box.IsChecked = true;
                    return;
                }
            }
            bool ok;
            try { ok = StartupApps.SetEnabled(row.Entry, want); }
            catch { ok = false; }
            if (!ok)
            {
                MessageBox.Show(Window.GetWindow(this)!,
                    row.Entry.Locked
                        ? Lang.T("ما تغيّر. هذا البرنامج مقفول من إعدادات الجهاز (سياسة).")
                        : Lang.T("ما تغيّر. هذا البرنامج لكل المستخدمين ويحتاج موافقة المسؤول."),
                    "Wibn", MessageBoxButton.OK, MessageBoxImage.Information,
                    MessageBoxResult.OK, Lang.MsgOptions);
            }
            row.Changed();
            box.IsChecked = row.Entry.Enabled;  // show the real state
            UpdateSummary();
            // Our own change shouldn't trigger a full reload a second later.
            _ = Task.Run(StartupApps.Signature).ContinueWith(t => Dispatcher.Invoke(() => _signature = t.Result));
        }
    }
}
