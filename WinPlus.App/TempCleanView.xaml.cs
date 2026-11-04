using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>A group of cache / temp folders that can be cleaned together.</summary>
    public sealed class TempCategory : INotifyPropertyChanged
    {
        private bool _isChecked;
        private long _size = -1;

        public TempCategory(string title, string description, IEnumerable<string> dirs,
                            bool isChecked, TimeSpan? olderThan = null)
        {
            Title = title;
            Description = description;
            Dirs = dirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _isChecked = isChecked;
            OlderThan = olderThan;
        }

        public string Title { get; }
        public string Description { get; }
        public List<string> Dirs { get; }
        public TimeSpan? OlderThan { get; }

        public bool IsChecked
        {
            get => _isChecked;
            set { _isChecked = value; Changed(nameof(IsChecked)); }
        }

        /// <summary>-1 while it's being calculated.</summary>
        public long Size
        {
            get => _size;
            set { _size = value; Changed(nameof(Size)); Changed(nameof(SizeText)); }
        }

        public string SizeText => _size < 0 ? "…" : ShellOps.FormatSize(_size);

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Cleans temp files and app caches that are safe to delete.</summary>
    public partial class TempCleanView : UserControl
    {
        private readonly ObservableCollection<TempCategory> _categories = new();
        private CancellationTokenSource? _cts;

        public TempCleanView()
        {
            InitializeComponent();
            CategoryList.ItemsSource = _categories;
            foreach (var c in BuildCategories().Where(c => c.Dirs.Count > 0)) _categories.Add(c);
            Loaded += (_, _) => _ = CalculateAsync();
            Unloaded += (_, _) => _cts?.Cancel();
        }

        private static IEnumerable<TempCategory> BuildCategories()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var day = TimeSpan.FromHours(24);

            yield return new TempCategory(Lang.T("ملفات ويندوز المؤقتة"),
                Lang.T("مجلد Temp حقك. الملفات اللي عمرها أقل من يوم تنترك عشان ما يخرب تثبيت شغال."),
                new[] { Path.GetTempPath() }, true, day);

            yield return new TempCategory(Lang.T("ملفات النظام المؤقتة"),
                Lang.T(@"C:\Windows\Temp. بعضها يحتاج صلاحية مسؤول وينترك."),
                new[] { Path.Combine(windows, "Temp") }, true, day);

            yield return new TempCategory(Lang.T("كاش المتصفحات"),
                Lang.T("Chrome و Edge و Brave و Firefox. ما يمس الباسوردات ولا السجل ولا تسجيل الدخول."),
                BrowserCacheDirs(local), true);

            yield return new TempCategory(Lang.T("كاش ديسكورد"),
                Lang.T("الصور والملفات اللي حفظها ديسكورد مؤقتًا."),
                new[] { "Cache", "Code Cache", "GPUCache" }.Select(d => Path.Combine(roaming, "discord", d)), true);

            yield return new TempCategory(Lang.T("كاش ستيم"),
                Lang.T("كاش المتجر والصفحات داخل ستيم. ما يمس الألعاب."),
                SteamCacheDirs(local), true);

            yield return new TempCategory(Lang.T("كاش وسائط أدوبي"),
                Lang.T("ملفات المعاينة اللي يسويها After Effects و Premiere. ترجع تنبني لما تفتح المشروع."),
                new[]
                {
                    Path.Combine(roaming, @"Adobe\Common\Media Cache Files"),
                    Path.Combine(roaming, @"Adobe\Common\Media Cache"),
                    Path.Combine(roaming, @"Adobe\Common\Peak Files"),
                }, true);

            yield return new TempCategory(Lang.T("تقارير الأخطاء وملفات الكراش"),
                Lang.T("تقارير يسويها ويندوز لما يطيح برنامج."),
                new[]
                {
                    Path.Combine(local, "CrashDumps"),
                    Path.Combine(local, @"Microsoft\Windows\WER\ReportArchive"),
                    Path.Combine(local, @"Microsoft\Windows\WER\ReportQueue"),
                }, true);

            yield return new TempCategory(Lang.T("كاش كرت الشاشة (Shaders)"),
                Lang.T("ينبني من جديد، بس أول تشغيل لكل لعبة بعدها ممكن يكون فيه تقطيع بسيط."),
                new[]
                {
                    Path.Combine(local, "D3DSCache"),
                    Path.Combine(local, @"NVIDIA\DXCache"),
                    Path.Combine(local, @"NVIDIA\GLCache"),
                    Path.Combine(local, @"AMD\DxCache"),
                    Path.Combine(local, @"AMD\DxcCache"),
                }, false);

            yield return new TempCategory(Lang.T("ملفات تحديثات ويندوز المحملة"),
                Lang.T("تحديثات انتهى تثبيتها. يحتاج تشغيل البرنامج كمسؤول."),
                new[] { Path.Combine(windows, @"SoftwareDistribution\Download") }, false);
        }

        private static IEnumerable<string> BrowserCacheDirs(string local)
        {
            string[] chromiumRoots =
            {
                @"Google\Chrome\User Data",
                @"Microsoft\Edge\User Data",
                @"BraveSoftware\Brave-Browser\User Data",
            };
            foreach (var rootName in chromiumRoots)
            {
                string root = Path.Combine(local, rootName);
                if (!Directory.Exists(root)) continue;
                yield return Path.Combine(root, "ShaderCache");
                yield return Path.Combine(root, "GrShaderCache");
                string[] profiles;
                try { profiles = Directory.GetDirectories(root); } catch { continue; }
                foreach (var profile in profiles)
                {
                    yield return Path.Combine(profile, "Cache");
                    yield return Path.Combine(profile, "Code Cache");
                    yield return Path.Combine(profile, "GPUCache");
                }
            }

            string firefox = Path.Combine(local, @"Mozilla\Firefox\Profiles");
            if (Directory.Exists(firefox))
            {
                string[] profiles;
                try { profiles = Directory.GetDirectories(firefox); } catch { yield break; }
                foreach (var profile in profiles)
                {
                    yield return Path.Combine(profile, "cache2");
                }
            }
        }

        private static IEnumerable<string> SteamCacheDirs(string local)
        {
            yield return Path.Combine(local, @"Steam\htmlcache");
            string? steam = null;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                steam = key?.GetValue("SteamPath") as string;
            }
            catch { }
            if (!string.IsNullOrEmpty(steam))
            {
                steam = steam.Replace('/', '\\');
                yield return Path.Combine(steam, @"appcache\httpcache");
            }
        }

        // ---- Calculate / clean ------------------------------------------------

        private async Task CalculateAsync()
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            SetBusy(true, Lang.T("جاري حساب الأحجام…"));
            try
            {
                foreach (var c in _categories) c.Size = -1;
                foreach (var c in _categories)
                {
                    long size = await Task.Run(
                        () => c.Dirs.Sum(d => ShellOps.FolderSize(d, c.OlderThan, token)), token);
                    c.Size = size;
                }
                UpdateTotal();
            }
            catch (OperationCanceledException) { }
            finally
            {
                SetBusy(false, null);
            }
        }

        private void OnRefresh(object sender, RoutedEventArgs e) => _ = CalculateAsync();

        private void OnCheckClicked(object sender, RoutedEventArgs e) => UpdateTotal();

        private void UpdateTotal()
        {
            long total = _categories.Where(c => c.IsChecked && c.Size > 0).Sum(c => c.Size);
            StatusText.Text = Lang.F("المحدد: {0}", ShellOps.FormatSize(total));
        }

        private void SetBusy(bool busy, string? text)
        {
            CleanButton.IsEnabled = !busy;
            RefreshButton.IsEnabled = !busy;
            if (text != null) StatusText.Text = text;
        }

        private async void OnClean(object sender, RoutedEventArgs e)
        {
            var selected = _categories.Where(c => c.IsChecked).ToList();
            if (selected.Count == 0) return;

            var answer = MessageBox.Show(Window.GetWindow(this)!,
                Lang.T("تبي تحذف الملفات المؤقتة المحددة؟ هالملفات تنحذف نهائيًا (ما تروح لسلة المهملات)."),
                Lang.T("تنظيف"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No,
                Lang.MsgOptions);
            if (answer != MessageBoxResult.Yes) return;

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            SetBusy(true, Lang.T("جاري التنظيف…"));
            long freed = 0;
            try
            {
                foreach (var c in selected)
                {
                    freed += await Task.Run(
                        () => c.Dirs.Sum(d => ShellOps.CleanFolder(d, c.OlderThan, token)), token);
                    c.Size = await Task.Run(
                        () => c.Dirs.Sum(d => ShellOps.FolderSize(d, c.OlderThan, token)), token);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                SetBusy(false, null);
            }
            StatusText.Text = Lang.F("تم تحرير {0}. اللي بقى مستخدم حاليًا من برامج شغالة.", ShellOps.FormatSize(freed));
        }
    }
}
