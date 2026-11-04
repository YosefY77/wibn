using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace WinPlus
{
    /// <summary>Recycle Bin page: how much is in it, and a button to empty it.</summary>
    public partial class CleanupPage : UserControl
    {
        public CleanupPage()
        {
            InitializeComponent();
            UpdateBinInfo();
            // Refresh every time the page is shown (files may have been deleted meanwhile).
            IsVisibleChanged += (_, _) => { if (IsVisible) UpdateBinInfo(); };
        }

        private void UpdateBinInfo()
        {
            if (ShellOps.QueryRecycleBin(out long items, out long bytes))
            {
                BinText.Text = items == 0 ? Lang.T("فاضية") : Lang.F("{0:N0} عنصر  —  {1}", items, ShellOps.FormatSize(bytes));
                EmptyBinButton.IsEnabled = items > 0;
            }
            else
            {
                BinText.Text = "";
            }
        }

        private void OnEmptyBin(object sender, RoutedEventArgs e)
        {
            var answer = MessageBox.Show(Window.GetWindow(this)!,
                Lang.T("تبي تفرغ سلة المهملات؟ الملفات بتنحذف نهائيًا وما ترجع."),
                Lang.T("إفراغ سلة المهملات"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No,
                Lang.MsgOptions);
            if (answer != MessageBoxResult.Yes) return;

            ShellOps.EmptyRecycleBin(new WindowInteropHelper(Window.GetWindow(this)!).Handle);
            UpdateBinInfo();
        }
    }
}
