using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WinPlus
{
    /// <summary>
    /// Inline HSV color picker: a saturation/brightness square, a hue bar and
    /// a hex box. Raises ColorChanged (0xFFRRGGBB) while the user picks.
    /// </summary>
    public partial class ColorPicker : UserControl
    {
        private double _h, _s = 0, _v = 0.13;   // hue 0..360, saturation/value 0..1
        private bool _updatingHex;

        public event Action<uint>? ColorChanged;

        public ColorPicker()
        {
            InitializeComponent();
            UpdateVisuals();
        }

        /// <summary>The current color as 0xFFRRGGBB. Setting it doesn't raise ColorChanged.</summary>
        public uint Color
        {
            get => ToArgb(HsvToRgb(_h, _s, _v));
            set
            {
                var c = System.Windows.Media.Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
                RgbToHsv(c, out double h, out _s, out _v);
                // Keep the hue for grays/black so the hue bar doesn't jump.
                if (_s > 0 && _v > 0) _h = h;
                UpdateVisuals();
            }
        }

        // ---- Mouse --------------------------------------------------------

        private void OnSvDown(object sender, MouseButtonEventArgs e)
        {
            SvArea.CaptureMouse();
            PickSv(e.GetPosition(SvArea));
        }

        private void OnSvMove(object sender, MouseEventArgs e)
        {
            if (SvArea.IsMouseCaptured) PickSv(e.GetPosition(SvArea));
        }

        private void OnSvUp(object sender, MouseButtonEventArgs e) => SvArea.ReleaseMouseCapture();

        private void OnHueDown(object sender, MouseButtonEventArgs e)
        {
            HueArea.CaptureMouse();
            PickHue(e.GetPosition(HueArea));
        }

        private void OnHueMove(object sender, MouseEventArgs e)
        {
            if (HueArea.IsMouseCaptured) PickHue(e.GetPosition(HueArea));
        }

        private void OnHueUp(object sender, MouseButtonEventArgs e) => HueArea.ReleaseMouseCapture();

        private void PickSv(Point p)
        {
            _s = Math.Clamp(p.X / Math.Max(1, SvArea.ActualWidth), 0, 1);
            _v = 1 - Math.Clamp(p.Y / Math.Max(1, SvArea.ActualHeight), 0, 1);
            UpdateVisuals();
            ColorChanged?.Invoke(Color);
        }

        private void PickHue(Point p)
        {
            _h = Math.Clamp(p.X / Math.Max(1, HueArea.ActualWidth), 0, 1) * 360;
            if (_h >= 360) _h = 359.99;
            UpdateVisuals();
            ColorChanged?.Invoke(Color);
        }

        private void OnAreaSizeChanged(object sender, SizeChangedEventArgs e) => UpdateVisuals();

        // ---- Hex ----------------------------------------------------------

        private void OnHexKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) ApplyHex();
        }

        private void OnHexLostFocus(object sender, RoutedEventArgs e) => ApplyHex();

        private void ApplyHex()
        {
            if (_updatingHex) return;
            string text = HexBox.Text.Trim().TrimStart('#');
            if (text.Length == 6 &&
                uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            {
                uint argb = 0xFF000000 | rgb;
                if (argb != Color)
                {
                    Color = argb;
                    ColorChanged?.Invoke(Color);
                }
            }
            UpdateVisuals();  // also puts back a valid value if the text was wrong
        }

        // ---- Drawing ------------------------------------------------------

        private void UpdateVisuals()
        {
            var color = HsvToRgb(_h, _s, _v);
            HueLayer.Background = new SolidColorBrush(HsvToRgb(_h, 1, 1));
            HueThumb.Fill = new SolidColorBrush(HsvToRgb(_h, 1, 1));
            SvThumb.Fill = new SolidColorBrush(color);
            Preview.Background = new SolidColorBrush(color);

            Canvas.SetLeft(SvThumb, _s * SvArea.ActualWidth - SvThumb.Width / 2);
            Canvas.SetTop(SvThumb, (1 - _v) * SvArea.ActualHeight - SvThumb.Height / 2);
            Canvas.SetLeft(HueThumb, _h / 360 * HueArea.ActualWidth - HueThumb.Width / 2);

            if (!HexBox.IsKeyboardFocused)
            {
                _updatingHex = true;
                HexBox.Text = $"{color.R:X2}{color.G:X2}{color.B:X2}";
                _updatingHex = false;
            }
        }

        private static uint ToArgb(System.Windows.Media.Color c) =>
            0xFF000000 | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

        private static System.Windows.Media.Color HsvToRgb(double h, double s, double v)
        {
            double c = v * s;
            double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
            double m = v - c;
            (double r, double g, double b) = (h / 60) switch
            {
                < 1 => (c, x, 0.0),
                < 2 => (x, c, 0.0),
                < 3 => (0.0, c, x),
                < 4 => (0.0, x, c),
                < 5 => (x, 0.0, c),
                _ => (c, 0.0, x),
            };
            return System.Windows.Media.Color.FromRgb(
                (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }

        private static void RgbToHsv(System.Windows.Media.Color c, out double h, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double d = max - min;
            v = max;
            s = max == 0 ? 0 : d / max;
            if (d == 0) h = 0;
            else if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }
    }
}
