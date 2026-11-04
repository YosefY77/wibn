using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace WinPlus
{
    /// <summary>One row in the list of big files.</summary>
    public sealed class BigFile : INotifyPropertyChanged
    {
        private bool _isChecked;

        public BigFile(string path, long size, bool isProtected)
        {
            Path = path;
            Size = size;
            IsProtected = isProtected;
        }

        public string Path { get; }
        /// <summary>A Windows / system file: shown, but can't be selected or deleted.</summary>
        public bool IsProtected { get; }
        public bool CanDelete => !IsProtected;
        public string Tip => IsProtected ? Lang.T("ملف نظام أو ويندوز، ما ينحذف عشان ما يخرب الجهاز.") + "\n" + Path : Path;
        public long Size { get; }
        public string Name => System.IO.Path.GetFileName(Path);
        public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
        public string SizeText => ShellOps.FormatSize(Size);

        public bool IsChecked
        {
            get => _isChecked;
            set { _isChecked = value && !IsProtected; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// Finds the biggest files on a drive, sends selected ones to the Recycle
    /// Bin, and empties the Recycle Bin.
    ///
    /// Memory stays small: the scan keeps only the current top 200 in a heap,
    /// and doesn't even build a path string for files smaller than the
    /// smallest one in it. Nothing is cached after the window closes.
    /// </summary>
    public partial class BigFilesView : UserControl
    {
        private const int TopCount = 200;

        private readonly ObservableCollection<BigFile> _files = new();
        private CancellationTokenSource? _scanCts;

        public BigFilesView()
        {
            InitializeComponent();
            FileList.ItemsSource = _files;

            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                DriveBox.Items.Add(new ComboBoxItem
                {
                    Content = Lang.F("{0}  ({1} فاضي)", drive.Name.TrimEnd('\\'), FormatSize(drive.AvailableFreeSpace)),
                    Tag = drive.RootDirectory.FullName,
                });
            }
            if (DriveBox.Items.Count > 0) DriveBox.SelectedIndex = 0;
        }

        private static string FormatSize(long bytes) => ShellOps.FormatSize(bytes);

        /// <summary>Raised after files were sent to the Recycle Bin.</summary>
        public event Action? FilesDeleted;

        /// <summary>Drops the results, e.g. when the window is hidden.</summary>
        public void Release()
        {
            _scanCts?.Cancel();
            _files.Clear();
            EmptyHint.Visibility = Visibility.Visible;
            UpdateSelection();
        }

        // ---- Scan ------------------------------------------------------------

        private async void OnScan(object sender, RoutedEventArgs e)
        {
            if (DriveBox.SelectedItem is not ComboBoxItem { Tag: string root }) return;

            _scanCts?.Cancel();
            _scanCts = new CancellationTokenSource();
            var token = _scanCts.Token;

            _files.Clear();
            EmptyHint.Visibility = Visibility.Collapsed;
            ScanButton.IsEnabled = false;
            CancelButton.Visibility = Visibility.Visible;
            UpdateSelection();

            var progress = new ScanProgress();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (_, _) =>
                ScanStatus.Text = Lang.F("جاري الفحص… {0:N0} ملف  —  {1}", progress.Files, progress.CurrentFolder);
            timer.Start();

            var watch = Stopwatch.StartNew();
            List<BigFile> results;
            try
            {
                results = await Task.Run(() => Scan(root, progress, token), token);
            }
            catch (OperationCanceledException)
            {
                results = new List<BigFile>();
            }
            finally
            {
                timer.Stop();
                ScanButton.IsEnabled = true;
                CancelButton.Visibility = Visibility.Collapsed;
            }

            foreach (var file in results) _files.Add(file);
            EmptyHint.Visibility = _files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ScanStatus.Text = token.IsCancellationRequested
                ? Lang.T("توقف الفحص.")
                : Lang.F("خلص الفحص: {0:N0} ملف في {1:0} ثانية.", progress.Files, watch.Elapsed.TotalSeconds);
            UpdateSelection();
        }

        private void OnCancel(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

        private sealed class ScanProgress
        {
            public long Files;
            public volatile string CurrentFolder = "";
        }

        private static List<BigFile> Scan(string root, ScanProgress progress, CancellationToken token)
        {
            // Windows / system files are listed too (locked), only folders nobody can read are skipped.
            string[] skipFolders =
            {
                root + "$Recycle.Bin\\",
                root + "System Volume Information\\",
            };

            // Min-heap of the biggest files so far (smallest on top).
            var heap = new PriorityQueue<(string Path, bool Protected), long>(TopCount + 1);
            long threshold = 0;  // files smaller than this can't make the list

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline,
            };

            var files = new FileSystemEnumerable<(string Path, long Size, FileAttributes Attributes)>(
                root,
                (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.Length, entry.Attributes),
                options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                {
                    if (entry.IsDirectory) return false;
                    long count = ++progress.Files;
                    if ((count & 4095) == 0)
                    {
                        token.ThrowIfCancellationRequested();
                        progress.CurrentFolder = entry.Directory.ToString();
                    }
                    return entry.Length > threshold;
                },
                ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                {
                    string path = entry.ToFullPath() + "\\";
                    foreach (string skip in skipFolders)
                    {
                        if (path.StartsWith(skip, StringComparison.OrdinalIgnoreCase)) return false;
                    }
                    return true;
                },
            };

            foreach (var (path, size, attributes) in files)
            {
                heap.Enqueue((path, SystemFiles.IsProtected(path, attributes)), size);
                if (heap.Count > TopCount)
                {
                    heap.Dequeue();
                }
                if (heap.Count == TopCount)
                {
                    heap.TryPeek(out _, out threshold);
                }
            }
            token.ThrowIfCancellationRequested();

            var result = new List<BigFile>(heap.Count);
            while (heap.TryDequeue(out var item, out long size))
            {
                result.Add(new BigFile(item.Path, size, item.Protected));
            }
            result.Reverse();  // biggest first
            return result;
        }

        // ---- Selection / delete ---------------------------------------------

        private void OnCheckClicked(object sender, RoutedEventArgs e) => UpdateSelection();

        private void UpdateSelection()
        {
            var selected = _files.Where(f => f.IsChecked).ToList();
            DeleteButton.IsEnabled = selected.Count > 0;
            SelectionText.Text = selected.Count > 0
                ? Lang.F("محدد {0} ملف  ({1})", selected.Count, FormatSize(selected.Sum(f => f.Size)))
                : "";
        }

        private void OnOpenLocation(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (FileList.SelectedItem is BigFile file && File.Exists(file.Path))
            {
                Process.Start("explorer.exe", $"/select,\"{file.Path}\"");
            }
        }

        private void OnDelete(object sender, RoutedEventArgs e)
        {
            var selected = _files.Where(f => f.IsChecked && !f.IsProtected).ToList();
            if (selected.Count == 0) return;

            var answer = MessageBox.Show(Window.GetWindow(this)!,
                Lang.F("تبي ترسل {0} ملف ({1}) لسلة المهملات؟", selected.Count, FormatSize(selected.Sum(f => f.Size))),
                Lang.T("حذف الملفات"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No,
                Lang.MsgOptions);
            if (answer != MessageBoxResult.Yes) return;

            ShellOps.RecycleFiles(OwnerHandle, selected.Select(f => f.Path));

            // Drop the rows whose file is gone.
            foreach (var file in selected.Where(f => !File.Exists(f.Path)).ToList())
            {
                _files.Remove(file);
            }
            UpdateSelection();
            FilesDeleted?.Invoke();
        }

        private IntPtr OwnerHandle =>
            new WindowInteropHelper(Window.GetWindow(this)!).Handle;
    }
}
