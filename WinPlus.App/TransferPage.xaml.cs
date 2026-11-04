using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinPlus
{
    public partial class TransferPage : UserControl
    {
        // Sharing keeps going when this page / the window is closed.
        private static P2PTransfer.Sending? _sending;
        private static string _sendName = "";
        private CancellationTokenSource? _receiveCts;

        private static string ReceiveFolder => DownloadsOrganizer.DownloadsFolder;

        public TransferPage()
        {
            InitializeComponent();
            FolderText.Text = "← " + ReceiveFolder;
            UpdateSetup();
            ShowSending();
        }

        private void UpdateSetup() =>
            SetupRow.Visibility = P2PTransfer.IsInstalled ? Visibility.Collapsed : Visibility.Visible;

        private async Task<bool> EnsureInstalledAsync()
        {
            if (P2PTransfer.IsInstalled) return true;
            SetupButton.IsEnabled = false;
            SetupProgress.Visibility = Visibility.Visible;
            SetupText.Text = Lang.T("جاري التحميل…");
            try
            {
                await P2PTransfer.InstallAsync(new Progress<double>(v => SetupProgress.Value = v), CancellationToken.None);
                UpdateSetup();
                return true;
            }
            catch (Exception ex)
            {
                CrashLog.Write("sendme install", ex);
                SetupText.Text = Lang.T("ما تحمّل. تأكد من النت وجرّب مرة ثانية.");
                return false;
            }
            finally
            {
                SetupButton.IsEnabled = true;
                SetupProgress.Visibility = Visibility.Collapsed;
            }
        }

        private async void OnSetup(object sender, RoutedEventArgs e) => await EnsureInstalledAsync();

        // ---- Send ----------------------------------------------------------------------------

        private async Task StartSend(string path)
        {
            if (!await EnsureInstalledAsync()) return;
            _sending?.Stop();
            _sending = null;
            _sendName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            SendPanel.Visibility = Visibility.Visible;
            SendName.Text = _sendName;
            CodeBox.Text = "";
            SendStatus.Text = Lang.T("جاري التجهيز… (الملفات الكبيرة تاخذ شوي لأنه يحسب بصمتها)");
            try
            {
                _sending = await Task.Run(() => P2PTransfer.SendAsync(path, CancellationToken.None));
                ShowSending();
                try { Clipboard.SetText(_sending.Code); SendStatus.Text += Lang.T(" · انتسخ الكود"); } catch { }
            }
            catch (Exception ex)
            {
                CrashLog.Write("sendme send", ex);
                SendStatus.Text = Lang.T("ما قدرت أبدأ المشاركة. التفاصيل في السجل.");
            }
        }

        private void ShowSending()
        {
            if (_sending == null || _sending.Process.HasExited)
            {
                if (_sending == null) SendPanel.Visibility = Visibility.Collapsed;
                return;
            }
            SendPanel.Visibility = Visibility.Visible;
            SendName.Text = _sendName;
            CodeBox.Text = _sending.Code;
            SendStatus.Text = Lang.T("جاهز. خل Wibn شغّال لين يخلص خويك.");
        }

        private async void OnPickFile(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = Lang.T("اختر الملف اللي تبي ترسله") };
            if (dlg.ShowDialog(Window.GetWindow(this)) == true) await StartSend(dlg.FileName);
        }

        private async void OnPickFolder(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Lang.T("اختر المجلد اللي تبي ترسله") };
            if (dlg.ShowDialog(Window.GetWindow(this)) == true) await StartSend(dlg.FolderName);
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            DropZone.BorderBrush = (Brush)FindResource("Accent");
        }

        private void OnDragLeave(object sender, DragEventArgs e) =>
            DropZone.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x4D, 0x55));

        private async void OnDrop(object sender, DragEventArgs e)
        {
            OnDragLeave(sender, e);
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) await StartSend(files[0]);
        }

        private void OnCopy(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(CodeBox.Text); SendStatus.Text = Lang.T("انتسخ الكود. أرسله لخويك."); } catch { }
        }

        private void OnStopSend(object sender, RoutedEventArgs e)
        {
            _sending?.Stop();
            _sending = null;
            SendPanel.Visibility = Visibility.Collapsed;
        }

        // ---- Receive -------------------------------------------------------------------------

        private async void OnReceive(object sender, RoutedEventArgs e)
        {
            var code = P2PTransfer.ParseCode(ReceiveBox.Text);
            if (code == null)
            {
                ShowReceive(Lang.T("هذا مو كود صحيح. انسخه كامل من خويك."), 0);
                return;
            }
            if (!await EnsureInstalledAsync()) return;

            var (ticket, size) = code.Value;
            _receiveCts = new CancellationTokenSource();
            ReceiveButton.IsEnabled = false;
            CancelReceiveButton.Visibility = Visibility.Visible;
            ReceiveProgress.IsIndeterminate = size == 0;
            ShowReceive(Lang.T("جاري الاتصال بجهاز خويك…"), 0);
            var progress = new Progress<P2PTransfer.TransferProgress>(p =>
            {
                if (p.Bytes == 0) return;
                string speed = p.BytesPerSecond > 0 ? Lang.F(" · {0}/ث", ShellOps.FormatSize((long)p.BytesPerSecond)) : "";
                string of = p.Total > 0 ? Lang.F(" من {0}", ShellOps.FormatSize(p.Total)) : "";
                ShowReceive($"{ShellOps.FormatSize(p.Bytes)}{of}{speed}", p.Total > 0 ? (double)p.Bytes / p.Total : 0);
            });
            try
            {
                var received = await P2PTransfer.ReceiveAsync(ticket, size, ReceiveFolder, progress, _receiveCts.Token);
                ReceiveProgress.IsIndeterminate = false;
                ShowReceive(received.Length > 0 ? Lang.F("وصل: {0}", string.Join(Lang.T("، "), received.Select(Path.GetFileName))) : Lang.T("وصل."), 1);
                // Show it in Explorer.
                string? first = received.FirstOrDefault();
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe",
                        first != null ? $"/select,\"{first}\"" : $"\"{ReceiveFolder}\"") { UseShellExecute = true });
                }
                catch { }
                ReceiveBox.Text = "";
            }
            catch (OperationCanceledException)
            {
                ReceiveProgress.IsIndeterminate = false;
                ShowReceive(Lang.T("انلغى. لو استلمت نفس الكود مرة ثانية يكمّل من مكان ما وقف."), 0);
            }
            catch (Exception ex)
            {
                CrashLog.Write("sendme receive", ex);
                ReceiveProgress.IsIndeterminate = false;
                ShowReceive(Lang.T("ما وصل. تأكد إن Wibn شغّال عند خويك وإن الكود كامل."), 0);
            }
            finally
            {
                ReceiveButton.IsEnabled = true;
                CancelReceiveButton.Visibility = Visibility.Collapsed;
                _receiveCts = null;
            }
        }

        private void OnCancelReceive(object sender, RoutedEventArgs e) => _receiveCts?.Cancel();

        private void ShowReceive(string text, double value)
        {
            ReceivePanel.Visibility = Visibility.Visible;
            ReceiveStatus.Text = text;
            if (!ReceiveProgress.IsIndeterminate) ReceiveProgress.Value = value;
        }
    }
}
