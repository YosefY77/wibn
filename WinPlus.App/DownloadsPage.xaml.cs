using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinPlus
{
    public partial class DownloadsPage : UserControl
    {
        private bool _loading = true;

        public DownloadsPage()
        {
            InitializeComponent();
            AutoBox.IsChecked = DownloadsOrganizer.AutoEnabled;
            FolderText.Text = DownloadsOrganizer.DownloadsFolder;
            UndoButton.IsEnabled = DownloadsOrganizer.CanUndo;
            BuildCategories();
            _loading = false;

            // Show what the automatic mode just moved, while the page is open.
            Action<string> onMoved = path => Dispatcher.InvokeAsync(() =>
                ShowResult(Lang.F("انتقل: {0} ← {1}", Path.GetFileName(path), Path.GetFileName(Path.GetDirectoryName(path)))));
            Loaded += (_, _) => DownloadsOrganizer.Moved += onMoved;
            Unloaded += (_, _) => DownloadsOrganizer.Moved -= onMoved;
        }

        private void BuildCategories()
        {
            var rowStyle = (Style)FindResource("Row");
            for (int i = 0; i < DownloadsOrganizer.Categories.Length; i++)
            {
                var cat = DownloadsOrganizer.Categories[i];
                int index = i;
                var box = new CheckBox
                {
                    Style = (Style)FindResource("Switch"),
                    IsChecked = DownloadsOrganizer.IsCategoryOn(i),
                };
                box.Click += (_, _) => DownloadsOrganizer.SetCategory(index, box.IsChecked == true);
                DockPanel.SetDock(box, Dock.Right);

                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = cat.Folder, Style = (Style)FindResource("RowTitle") });
                text.Children.Add(new TextBlock
                {
                    Text = string.Join(" ", cat.Extensions.Select(e => e.TrimStart('.'))),
                    Style = (Style)FindResource("RowDesc"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                    FlowDirection = FlowDirection.LeftToRight,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    ToolTip = string.Join("  ", cat.Extensions),
                });

                var dock = new DockPanel();
                dock.Children.Add(box);
                dock.Children.Add(new TextBlock { Text = cat.Icon, Style = (Style)FindResource("RowIcon") });
                dock.Children.Add(text);
                CategoryGrid.Children.Add(new Border { Style = rowStyle, Margin = new Thickness(3, 0, 3, 6), Child = dock });
            }
        }

        private void OnAutoChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            DownloadsOrganizer.AutoEnabled = AutoBox.IsChecked == true;
        }

        private void OnSortNow(object sender, RoutedEventArgs e) => Sort(null);

        private void OnSortOther(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Lang.T("اختر المجلد اللي تبي ترتّبه") };
            if (dlg.ShowDialog(Window.GetWindow(this)) == true) Sort(dlg.FolderName);
        }

        private void Sort(string? folder)
        {
            try
            {
                var counts = DownloadsOrganizer.SortNow(folder);
                int total = counts.Values.Sum();
                ShowResult(total == 0
                    ? Lang.T("ما فيه شي يحتاج ترتيب.")
                    : Lang.F("انتقل {0} ملف: ", total) + string.Join(Lang.T("، "), counts.Select(kv => $"{kv.Key} {kv.Value}")));
            }
            catch (Exception ex)
            {
                ShowResult(Lang.T("صار خطأ: ") + ex.Message);
            }
            UndoButton.IsEnabled = DownloadsOrganizer.CanUndo;
        }

        private void OnUndo(object sender, RoutedEventArgs e)
        {
            int n = DownloadsOrganizer.Undo();
            ShowResult(n == 0 ? Lang.T("ما فيه شي يرجع.") : Lang.F("رجع {0} ملف لمكانه.", n));
            UndoButton.IsEnabled = DownloadsOrganizer.CanUndo;
        }

        private void OnOpenFolder(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(DownloadsOrganizer.DownloadsFolder) { UseShellExecute = true }); }
            catch { }
        }

        private void ShowResult(string text)
        {
            ResultText.Text = text;
            ResultText.Visibility = Visibility.Visible;
        }
    }
}
