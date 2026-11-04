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

        public StartupPage()
        {
            InitializeComponent();
            Loaded += async (_, _) => await LoadAsync();
        }

        private async Task LoadAsync()
        {
            SummaryText.Text = Lang.T("جاري القراءة…");
            // Reading the registry and file versions is quick, but keep the UI free.
            var entries = await Task.Run(StartupApps.Load);
            _rows = entries.Select(e => new Row
            {
                Entry = e,
                Detail = (e.Publisher.Length > 0 ? e.Publisher + "  ·  " : "") + e.Command,
                Icon = IconFor(e.ExePath),
            }).ToList();
            AppList.ItemsSource = _rows;
            UpdateSummary();
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
                if (path == null || !File.Exists(path)) return null;
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon == null) return null;
                var img = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                                                              BitmapSizeOptions.FromEmptyOptions());
                img.Freeze();
                return img;
            }
            catch { return null; }
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

        private async void OnRefresh(object sender, RoutedEventArgs e) => await LoadAsync();

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
                    Lang.T("ما تغيّر. هذا البرنامج لكل المستخدمين ويحتاج موافقة المسؤول."),
                    "Wibn", MessageBoxButton.OK, MessageBoxImage.Information,
                    MessageBoxResult.OK, Lang.MsgOptions);
            }
            row.Changed();
            box.IsChecked = row.Entry.Enabled;  // show the real state
            UpdateSummary();
        }
    }
}
