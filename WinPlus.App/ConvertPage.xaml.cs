using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace WinPlus
{
    public partial class ConvertPage : UserControl
    {
        private bool _loading = true;
        private CancellationTokenSource? _cts;

        public ConvertPage()
        {
            InitializeComponent();
            EnabledBox.IsChecked = DragSpaceHook.Enabled;
            KeepBox.IsChecked = ConvertHistory.KeepOriginals;
            UpdateFfmpeg();
            Root.Children.Add(GlassSettings.Section(GlassSettings.Menu(MediaTools.Mode.Convert), null,
                Lang.T("الدائرة تعرض اللي تحتها منحني عند حافتها مثل زجاج أبل")));
            _loading = false;
            Unloaded += (_, _) => _cts?.Cancel();
        }

        private void UpdateFfmpeg()
        {
            string? exe = Converter.FindFfmpeg();
            FfmpegText.Text = exe != null
                ? Lang.T("موجود. المقاطع والصوت وWEBP وAVIF جاهزة")
                : Lang.T("مطلوب للمقاطع والصوت وWEBP وAVIF (مجاني، تقريبًا 140 ميقا). الصور الثانية ما تحتاجه");
            FfmpegButton.Visibility = exe != null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnEnabledChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            DragSpaceHook.Enabled = EnabledBox.IsChecked == true;
        }

        private void OnKeepChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            ConvertHistory.KeepOriginals = KeepBox.IsChecked == true;
        }

        private async void OnDownloadFfmpeg(object sender, RoutedEventArgs e)
        {
            FfmpegButton.IsEnabled = false;
            FfmpegProgress.Visibility = Visibility.Visible;
            FfmpegText.Text = Lang.T("جاري التحميل…");
            _cts = new CancellationTokenSource();
            try
            {
                await Converter.DownloadFfmpegAsync(new Progress<double>(v => FfmpegProgress.Value = v), _cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                CrashLog.Write("FFmpeg download", ex);
                FfmpegText.Text = Lang.T("ما تحمّل. تأكد من النت وجرّب مرة ثانية");
                FfmpegButton.IsEnabled = true;
                FfmpegProgress.Visibility = Visibility.Collapsed;
                return;
            }
            FfmpegProgress.Visibility = Visibility.Collapsed;
            FfmpegButton.IsEnabled = true;
            UpdateFfmpeg();
        }
    }
}
