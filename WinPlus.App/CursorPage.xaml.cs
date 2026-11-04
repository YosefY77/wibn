using System;
using System.Windows;
using System.Windows.Controls;

namespace WinPlus
{
    public partial class CursorPage : UserControl
    {
        private bool _loading = true;
        private bool _sizeDirty;
        // Redrawing the system pointer on every mouse move of the picker is wasteful.
        private readonly System.Windows.Threading.DispatcherTimer _applyTimer =
            new() { Interval = TimeSpan.FromMilliseconds(150) };

        public CursorPage()
        {
            InitializeComponent();
            EnabledBox.IsChecked = CursorTool.Enabled;
            HandBox.IsChecked = CursorTool.AlsoHand;
            SizeSlider.Value = CursorTool.Size;
            SizeText.Text = $"{CursorTool.Size} px";
            Picker.Color = CursorTool.Color;
            HotspotBox.SelectedIndex = CursorTool.Hotspot;
            if (CursorTool.Mode == 1) ModeImage.IsChecked = true; else ModeColor.IsChecked = true;
            UpdateModePanels();

            Picker.ColorChanged += argb =>
            {
                CursorTool.Color = argb;
                UpdatePreview();
                if (CursorTool.Mode != 0) { _loading = true; ModeColor.IsChecked = true; _loading = false; CursorTool.Mode = 0; UpdateModePanels(); }
                ScheduleApply(turnOn: true);
            };
            _applyTimer.Tick += (_, _) =>
            {
                _applyTimer.Stop();
                if (CursorTool.Enabled) Safe(CursorTool.Apply);
            };

            _loading = false;
            UpdatePreview();
        }

        private void ScheduleApply(bool turnOn)
        {
            if (turnOn && EnabledBox.IsChecked != true)
            {
                EnabledBox.IsChecked = true;  // choosing a look turns it on (applies there)
                return;
            }
            _applyTimer.Stop();
            _applyTimer.Start();
        }

        private void UpdatePreview()
        {
            try
            {
                int size = Math.Min(CursorTool.Size, 150);
                var bmp = CursorTool.Render(size, out _, out _);
                PreviewDark.Source = bmp;
                PreviewLight.Source = bmp;
            }
            catch { }
        }

        private void UpdateModePanels()
        {
            bool image = CursorTool.Mode == 1;
            Picker.Visibility = image ? Visibility.Collapsed : Visibility.Visible;
            ImagePanel.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnEnabledChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            CursorTool.Enabled = EnabledBox.IsChecked == true;
            if (CursorTool.Enabled) Safe(CursorTool.Apply);
            else Safe(CursorTool.Reset);
        }

        private void OnHandChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            CursorTool.AlsoHand = HandBox.IsChecked == true;
            if (CursorTool.Enabled) Safe(CursorTool.Apply);
        }

        private void OnModeChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            CursorTool.Mode = ModeImage.IsChecked == true ? 1 : 0;
            UpdateModePanels();
            UpdatePreview();
            // Picture mode without a picture yet: wait until one is chosen.
            if (CursorTool.Mode == 0 || CursorTool.ImagePath != null) ScheduleApply(turnOn: false);
        }

        private void OnPickImage(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = Lang.T("اختر صورة المؤشر"),
                Filter = Lang.T("صور ومؤشرات|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico;*.cur;*.ani"),
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                CursorTool.SetImage(dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this)!, Lang.T("ما قدرت أنسخ الصورة: ") + ex.Message, "Wibn");
                return;
            }
            UpdatePreview();
            ScheduleApply(turnOn: true);
        }

        private void OnHotspotChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            CursorTool.Hotspot = Math.Max(0, HotspotBox.SelectedIndex);
            UpdatePreview();
            ScheduleApply(turnOn: false);
        }

        private void OnSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SizeText == null) return;
            SizeText.Text = $"{(int)SizeSlider.Value} px";
            if (_loading) return;
            _sizeDirty = true;
        }

        // Windows' size is changed once the slider is let go (it redraws every pointer).
        private void OnSizeCommit(object sender, EventArgs e)
        {
            if (!_sizeDirty) return;
            _sizeDirty = false;
            int size = (int)SizeSlider.Value;
            Safe(() => CursorTool.Size = size);
            UpdatePreview();
        }

        private void OnReset(object sender, RoutedEventArgs e)
        {
            _applyTimer.Stop();
            _loading = true;
            EnabledBox.IsChecked = false;
            _loading = false;
            CursorTool.Enabled = false;
            Safe(CursorTool.Reset);
            Safe(() => CursorTool.Size = 32);
            _loading = true;
            SizeSlider.Value = 32;
            _loading = false;
            UpdatePreview();
        }

        private void Safe(Action action)
        {
            try { action(); }
            catch (Exception ex) { CrashLog.Write("Cursor", ex); }
        }
    }
}
