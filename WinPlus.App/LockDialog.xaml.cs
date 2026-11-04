using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace WinPlus
{
    /// <summary>Asks for the password and locks / unlocks one file.</summary>
    public partial class LockDialog : Window
    {
        private readonly string _path;
        private readonly bool _lock;
        private CancellationTokenSource? _cts;
        private string? _usedPassword;  // for locking it again after it's closed

        /// <summary>Where the result went (null if cancelled / failed).</summary>
        public string? ResultPath { get; private set; }

        public LockDialog(string path)
        {
            InitializeComponent();
            _path = path;
            _lock = !FileLocker.IsLocked(path);
            bool folder = Directory.Exists(path);
            FileText.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            TitleText.Text = !_lock ? Lang.T("فتح المقفول") : folder ? Lang.T("قفل المجلد بكلمة سر") : Lang.T("قفل الملف بكلمة سر");
            OkButton.Content = _lock ? Lang.T("قفل") : Lang.T("فتح");
            ConfirmPanel.Visibility = _lock ? Visibility.Visible : Visibility.Collapsed;
            Loaded += (_, _) => Pass1.Focus();
        }

        /// <summary>Shows the dialog; after unlocking, opens the file with its normal program.</summary>
        public static string? Run(string path, Window? owner, bool openAfterUnlock)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                MessageBox.Show(Lang.T("الملف أو المجلد مو موجود."), "Wibn", MessageBoxButton.OK, MessageBoxImage.Warning,
                                MessageBoxResult.OK, Lang.MsgOptions);
                return null;
            }
            var dlg = new LockDialog(path);
            if (owner != null && owner.IsVisible) { dlg.Owner = owner; dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            dlg.ShowDialog();
            if (dlg.ResultPath != null && openAfterUnlock && !dlg._lock)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dlg.ResultPath) { UseShellExecute = true });
                }
                catch { }
                // Lock it again with the same password once it's closed.
                if (Relocker.Enabled && dlg._usedPassword != null) Relocker.Start(dlg.ResultPath, dlg._usedPassword);
            }
            dlg._usedPassword = null;
            return dlg.ResultPath;
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) OnOk(sender, e);
        }

        private async void OnOk(object sender, RoutedEventArgs e)
        {
            if (!OkButton.IsEnabled) return;
            string pass = Pass1.Password;
            if (pass.Length == 0) { ShowError(Lang.T("اكتب كلمة السر.")); return; }
            if (_lock)
            {
                if (pass.Length < 4) { ShowError(Lang.T("كلمة السر لازم تكون 4 حروف أو أكثر.")); return; }
                if (pass != Pass2.Password) { ShowError(Lang.T("كلمتين السر مو نفس بعض.")); return; }
            }

            ErrorText.Visibility = Visibility.Collapsed;
            OkButton.IsEnabled = false;
            Pass1.IsEnabled = Pass2.IsEnabled = false;
            Progress.Value = 0;
            Progress.Visibility = Visibility.Visible;
            _cts = new CancellationTokenSource();
            var progress = new Progress<double>(v => Progress.Value = v);
            try
            {
                string path = _path;
                bool doLock = _lock;
                var token = _cts.Token;
                ResultPath = await Task.Run(() => doLock
                    ? FileLocker.Lock(path, pass, progress, token)
                    : FileLocker.Unlock(path, pass, progress, token));
                if (!doLock) _usedPassword = pass;
                Close();
            }
            catch (FileLocker.OriginalNotDeletedException ex)
            {
                ResultPath = ex.LockedPath;
                MessageBox.Show(this, Lang.T("انقفل، بس ما قدرت أحذف الأصلي كامل لأن شي فيه مفتوح. سكّر البرامج واحذفه بنفسك."),
                                "Wibn", MessageBoxButton.OK, MessageBoxImage.Warning,
                                MessageBoxResult.OK, Lang.MsgOptions);
                Close();
            }
            catch (FileLocker.WrongPasswordException)
            {
                ShowError(Lang.T("كلمة السر غلط."));
                Pass1.Clear();
            }
            catch (OperationCanceledException)
            {
                Close();
            }
            catch (InvalidDataException)
            {
                ShowError(Lang.T("الملف خربان أو مو ملف مقفول من Wibn."));
            }
            catch (IOException)
            {
                ShowError(Lang.T("فيه ملف مفتوح في برنامج ثاني. سكّره وجرّب مرة ثانية."));
            }
            catch (UnauthorizedAccessException)
            {
                ShowError(Directory.Exists(_path) && FileLocker.IsProtectedFolder(_path)
                    ? Lang.T("هذا مجلد مهم للنظام وما ينقفل.")
                    : Lang.T("ما عندي صلاحية على هالملف أو مجلده."));
            }
            catch (Exception ex)
            {
                ShowError(Lang.T("صار خطأ: ") + ex.Message);
            }
            finally
            {
                if (IsLoaded)
                {
                    OkButton.IsEnabled = true;
                    Pass1.IsEnabled = Pass2.IsEnabled = true;
                    Progress.Visibility = Visibility.Collapsed;
                    if (ResultPath == null) Pass1.Focus();
                }
            }
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            if (_cts != null && !OkButton.IsEnabled) { _cts.Cancel(); return; }
            Close();
        }

        private void ShowError(string text)
        {
            ErrorText.Text = text;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
