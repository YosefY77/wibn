using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinPlus
{
    public partial class DesktopPage : UserControl
    {
        private bool _loading = true;
        private readonly System.Windows.Threading.DispatcherTimer _timer =
            new() { Interval = TimeSpan.FromMilliseconds(120) };
        private uint _pending;

        public DesktopPage()
        {
            InitializeComponent();
            EnabledBox.IsChecked = SelectionColor.Enabled;
            BorderBox.IsChecked = SelectionColor.Border;
            Picker.Color = SelectionColor.Color;
            UpdatePreview(SelectionColor.Color);

            // Changing system colors repaints every window, so not on every mouse move.
            Picker.ColorChanged += argb =>
            {
                _pending = argb;
                UpdatePreview(argb);
                _timer.Stop();
                _timer.Start();
            };
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                SelectionColor.Color = _pending;
                if (EnabledBox.IsChecked != true) EnabledBox.IsChecked = true;  // picking turns it on
            };
            _loading = false;
        }

        private void UpdatePreview(uint argb)
        {
            var c = Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            Preview.Background = new SolidColorBrush(Color.FromArgb(0x55, c.R, c.G, c.B));
            Preview.BorderBrush = BorderBox.IsChecked == true
                ? new SolidColorBrush(c)
                : new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
        }

        private void OnEnabledChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            SelectionColor.Enabled = EnabledBox.IsChecked == true;
        }

        private void OnReset(object sender, RoutedEventArgs e)
        {
            _timer.Stop();
            SelectionColor.ResetToWindowsDefault();
            _loading = true;
            EnabledBox.IsChecked = false;
            Picker.Color = SelectionColor.Color;
            _loading = false;
            UpdatePreview(SelectionColor.Color);
        }

        private void OnBorderChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            SelectionColor.Border = BorderBox.IsChecked == true;
            UpdatePreview(Picker.Color);
        }
    }
}
