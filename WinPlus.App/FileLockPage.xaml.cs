using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinPlus
{
    public partial class FileLockPage : UserControl
    {
        private bool _loading = true;

        public FileLockPage()
        {
            InitializeComponent();
            ShellBox.IsChecked = FileLockShell.Enabled;
            RelockBox.IsChecked = Relocker.Enabled;
            _loading = false;
        }

        private void Handle(string path)
        {
            bool locking = !FileLocker.IsLocked(path);
            string? result = LockDialog.Run(path, Window.GetWindow(this), openAfterUnlock: true);
            if (result == null) return;
            ResultText.Text = locking
                ? Lang.F("انقفل: {0}", Path.GetFileName(result))
                : Relocker.Enabled
                    ? Lang.F("انفتح: {0}. يرجع يتقفل لحاله لما تسكّره.", Path.GetFileName(result))
                    : Lang.F("انفتح ورجع عادي: {0} (اقفله مرة ثانية لما تخلص)", Path.GetFileName(result));
            ResultText.Visibility = Visibility.Visible;
        }

        private void OnPickLock(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = Lang.T("اختر الملف اللي تبي تقفله") };
            if (dlg.ShowDialog(Window.GetWindow(this)) == true) Handle(dlg.FileName);
        }

        private void OnPickFolder(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Lang.T("اختر المجلد اللي تبي تقفله") };
            if (dlg.ShowDialog(Window.GetWindow(this)) == true) Handle(dlg.FolderName);
        }

        private void OnPickUnlock(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = Lang.T("اختر الملف المقفول"),
                Filter = Lang.T("ملفات مقفولة (*.wplock)|*.wplock"),
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) == true) Handle(dlg.FileName);
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            DropZone.BorderBrush = (Brush)FindResource("Accent");
        }

        private void OnDragLeave(object sender, DragEventArgs e) =>
            DropZone.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x4D, 0x55));

        private void OnDrop(object sender, DragEventArgs e)
        {
            OnDragLeave(sender, e);
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
            foreach (var f in files)
            {
                if (File.Exists(f) || Directory.Exists(f)) Handle(f);
            }
        }

        private void OnRelockChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            Relocker.Enabled = RelockBox.IsChecked == true;
        }

        private void OnShellChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            FileLockShell.Enabled = ShellBox.IsChecked == true;
        }
    }
}
