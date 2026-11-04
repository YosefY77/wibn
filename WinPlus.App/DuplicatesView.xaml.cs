using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace WinPlus
{
    /// <summary>A row in the duplicates list: either a group header or a file.</summary>
    public sealed class DupRow : INotifyPropertyChanged
    {
        private bool _isChecked;

        public bool IsHeader { get; init; }
        public bool IsFile => !IsHeader;
        public string HeaderText { get; init; } = "";
        public int Group { get; init; }
        public string Path { get; init; } = "";
        public long Size { get; init; }
        public DateTime Modified { get; init; }
        /// <summary>A Windows / system file: shown, but can't be selected or deleted.</summary>
        public bool IsProtected { get; init; }
        public bool CanDelete => IsFile && !IsProtected;
        public string Tip => IsProtected ? Lang.T("ملف نظام أو ويندوز، ما ينحذف عشان ما يخرب الجهاز.") + "\n" + Path : Path;
        public string Name => System.IO.Path.GetFileName(Path);
        public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
        public string DateText => IsHeader ? "" : Modified.ToString("yyyy/MM/dd");

        public bool IsChecked
        {
            get => _isChecked;
            set { _isChecked = value && !IsProtected; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// Finds files with identical content: group by size, then by a hash of the
    /// first and last 64 KB, then by a full hash. Only files that share a size
    /// are ever read, so it's fast and light.
    /// </summary>
    public partial class DuplicatesView : UserControl
    {
        private const int MaxGroups = 500;

        private readonly record struct DupFile(string Path, long Size, DateTime Modified, bool Protected);

        /// <summary>What deleting the extra copies frees (system copies stay, so they don't count).</summary>
        private static long Savable(IReadOnlyCollection<DupFile> group)
        {
            int deletable = group.Count(f => !f.Protected);
            return group.First().Size * Math.Min(group.Count - 1, deletable);
        }

        private readonly ObservableCollection<DupRow> _rows = new();
        private CancellationTokenSource? _cts;
        private string? _folder;

        public event Action? FilesDeleted;

        public DuplicatesView()
        {
            InitializeComponent();
            DupList.ItemsSource = _rows;
            Unloaded += (_, _) => _cts?.Cancel();
        }

        private IntPtr OwnerHandle => new WindowInteropHelper(Window.GetWindow(this)!).Handle;

        private void OnChooseFolder(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Lang.T("اختر المجلد اللي تبي تدور فيه") };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            {
                _folder = dialog.FolderName;
                FolderText.Text = _folder;
                ScanButton.IsEnabled = true;
            }
        }

        private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

        private sealed class Progress
        {
            public long Files;
            public volatile string Stage = "";
        }

        private async void OnScan(object sender, RoutedEventArgs e)
        {
            if (_folder == null) return;
            long minSize = MinSizeBox.SelectedItem is ComboBoxItem { Tag: string t } ? long.Parse(t) : 1048576;

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _rows.Clear();
            EmptyHint.Visibility = Visibility.Collapsed;
            ScanButton.IsEnabled = false;
            ChooseButton.IsEnabled = false;
            CancelButton.Visibility = Visibility.Visible;
            UpdateSelection();

            var progress = new Progress();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (_, _) => ScanStatus.Text = progress.Stage;
            timer.Start();

            var watch = Stopwatch.StartNew();
            List<List<DupFile>> groups;
            try
            {
                string folder = _folder;
                groups = await Task.Run(() => FindDuplicates(folder, minSize, progress, token), token);
            }
            catch (OperationCanceledException)
            {
                groups = new();
            }
            finally
            {
                timer.Stop();
                ScanButton.IsEnabled = true;
                ChooseButton.IsEnabled = true;
                CancelButton.Visibility = Visibility.Collapsed;
            }

            ShowGroups(groups);
            long wasted = groups.Sum(Savable);
            ScanStatus.Text = token.IsCancellationRequested
                ? Lang.T("توقف الفحص.")
                : groups.Count == 0
                    ? Lang.F("ما فيه ملفات مكررة ({0:N0} ملف في {1:0} ثانية).", progress.Files, watch.Elapsed.TotalSeconds)
                    : Lang.F("{0} ملف مكرر، تقدر توفر {1}  ({2:N0} ملف في {3:0} ثانية).", groups.Count, ShellOps.FormatSize(wasted), progress.Files, watch.Elapsed.TotalSeconds);
        }

        private static List<List<DupFile>> FindDuplicates(
            string root, long minSize, Progress progress, CancellationToken token)
        {
            // 1) Group by size. Windows / system files are included (shown locked).
            var bySize = new Dictionary<long, List<(string Path, bool Protected)>>();
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline,
            };
            var files = new FileSystemEnumerable<(string, long, FileAttributes)>(root,
                (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.Length, entry.Attributes), options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                {
                    if (entry.IsDirectory) return false;
                    long n = ++progress.Files;
                    if ((n & 4095) == 0)
                    {
                        token.ThrowIfCancellationRequested();
                        progress.Stage = Lang.F("جاري جمع الملفات… {0:N0} ملف", n);
                    }
                    return entry.Length >= minSize;
                },
                ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                {
                    string path = entry.ToFullPath() + "\\";
                    return !path.Contains("\\$Recycle.Bin\\", StringComparison.OrdinalIgnoreCase) &&
                           !path.Contains("\\System Volume Information\\", StringComparison.OrdinalIgnoreCase);
                },
            };
            foreach (var (path, size, attributes) in files)
            {
                if (!bySize.TryGetValue(size, out var list)) bySize[size] = list = new(1);
                list.Add((path, SystemFiles.IsProtected(path, attributes)));
            }

            // Only sizes with at least one file the user may delete are worth reading.
            var candidates = bySize.Where(kv => kv.Value.Count > 1 && kv.Value.Any(f => !f.Protected)).ToList();
            bySize = null!;  // free the rest early

            // 2) Same size → same first/last 64 KB → same full content.
            var result = new List<List<DupFile>>();
            int done = 0;
            foreach (var (size, entries) in candidates)
            {
                var paths = entries.Select(f => f.Path).ToList();
                var protectedPaths = entries.Where(f => f.Protected).Select(f => f.Path)
                                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                token.ThrowIfCancellationRequested();
                progress.Stage = Lang.F("جاري مقارنة المحتوى… {0:N0} من {1:N0}", ++done, candidates.Count);

                foreach (var quick in GroupByHash(paths, p => QuickHash(p, size)))
                {
                    var exact = size <= 128 * 1024 ? new List<List<string>> { quick }
                                                   : GroupByHash(quick, p => FullHash(p, token));
                    foreach (var group in exact)
                    {
                        if (group.All(protectedPaths.Contains)) continue;  // nothing the user could delete
                        result.Add(group.Select(p => new DupFile(p, size, SafeModified(p), protectedPaths.Contains(p)))
                                        .OrderBy(x => x.Modified).ToList());
                    }
                }
            }

            return result
                .OrderByDescending(Savable)
                .Take(MaxGroups)
                .ToList();
        }

        private static List<List<string>> GroupByHash(List<string> paths, Func<string, string?> hash)
        {
            return paths
                .Select(p => (Path: p, Hash: hash(p)))
                .Where(x => x.Hash != null)
                .GroupBy(x => x.Hash!)
                .Where(g => g.Count() > 1)
                .Select(g => g.Select(x => x.Path).ToList())
                .ToList();
        }

        private static string? QuickHash(string path, long size)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                const int chunk = 64 * 1024;
                var buffer = new byte[Math.Min(size, 2L * chunk)];
                int read = stream.Read(buffer, 0, (int)Math.Min(size, chunk));
                if (size > chunk)
                {
                    stream.Seek(Math.Max(chunk, size - chunk), SeekOrigin.Begin);
                    read += stream.Read(buffer, read, buffer.Length - read);
                }
                return Convert.ToHexString(MD5.HashData(buffer.AsSpan(0, read)));
            }
            catch { return null; }
        }

        private static string? FullHash(string path, CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1 << 20);
                return Convert.ToHexString(MD5.HashData(stream));
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        private static DateTime SafeModified(string path)
        {
            try { return File.GetLastWriteTime(path); } catch { return DateTime.MinValue; }
        }

        // ---- List -------------------------------------------------------------

        private void ShowGroups(List<List<DupFile>> groups)
        {
            _rows.Clear();
            int id = 0;
            foreach (var group in groups)
            {
                id++;
                long size = group[0].Size;
                _rows.Add(new DupRow
                {
                    IsHeader = true,
                    Group = id,
                    HeaderText = Lang.F("{0} نسخ  ×  {1}   (يوفر {2})", group.Count, ShellOps.FormatSize(size), ShellOps.FormatSize(Savable(group))),
                });
                foreach (var f in group)
                {
                    _rows.Add(new DupRow { Group = id, Path = f.Path, Size = f.Size, Modified = f.Modified, IsProtected = f.Protected });
                }
            }
            EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AutoSelectButton.IsEnabled = _rows.Count > 0;
            UpdateSelection();
        }

        private void OnCheckClicked(object sender, RoutedEventArgs e) => UpdateSelection();

        private void UpdateSelection()
        {
            var selected = _rows.Where(r => r.IsFile && r.IsChecked).ToList();
            DeleteButton.IsEnabled = selected.Count > 0;
            SelectionText.Text = selected.Count > 0
                ? Lang.F("محدد {0} ملف  ({1})", selected.Count, ShellOps.FormatSize(selected.Sum(r => r.Size)))
                : "";
        }

        /// <summary>Keeps the oldest copy of each file and selects the rest (system copies are never selected).</summary>
        private void OnAutoSelect(object sender, RoutedEventArgs e)
        {
            foreach (var group in _rows.Where(r => r.CanDelete).GroupBy(r => r.Group))
            {
                bool first = true;
                foreach (var row in group.OrderBy(r => r.Modified))
                {
                    row.IsChecked = !first;
                    first = false;
                }
            }
            UpdateSelection();
        }

        private void OnOpenLocation(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (DupList.SelectedItem is DupRow { IsFile: true } row && File.Exists(row.Path))
            {
                Process.Start("explorer.exe", $"/select,\"{row.Path}\"");
            }
        }

        private void OnDelete(object sender, RoutedEventArgs e)
        {
            var selected = _rows.Where(r => r.CanDelete && r.IsChecked).ToList();
            if (selected.Count == 0) return;

            // Never delete every copy of a file by accident (a locked system copy always stays).
            var wholeGroups = _rows.Where(r => r.IsFile).GroupBy(r => r.Group)
                                   .Where(g => g.All(r => r.IsChecked && !r.IsProtected)).Count();
            string extra = wholeGroups > 0
                ? Lang.F("\n\nانتبه: في {0} مجموعة حددت كل النسخ، يعني الملف بيروح كله.", wholeGroups)
                : "";

            var answer = MessageBox.Show(Window.GetWindow(this)!,
                Lang.F("تبي ترسل {0} ملف ({1}) لسلة المهملات؟{2}", selected.Count, ShellOps.FormatSize(selected.Sum(r => r.Size)), extra),
                Lang.T("حذف الملفات المكررة"), MessageBoxButton.YesNo,
                wholeGroups > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No,
                Lang.MsgOptions);
            if (answer != MessageBoxResult.Yes) return;

            ShellOps.RecycleFiles(OwnerHandle, selected.Select(r => r.Path));

            // Rebuild the list without the files that are gone.
            var remaining = _rows.Where(r => r.IsFile && File.Exists(r.Path))
                                 .GroupBy(r => r.Group)
                                 .Where(g => g.Count() > 1 && g.Any(r => !r.IsProtected))
                                 .Select(g => g.Select(r => new DupFile(r.Path, r.Size, r.Modified, r.IsProtected)).ToList())
                                 .ToList();
            ShowGroups(remaining);
            FilesDeleted?.Invoke();
        }

        public void Release()
        {
            _cts?.Cancel();
            _rows.Clear();
        }
    }
}
