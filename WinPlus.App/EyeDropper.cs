using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WinPlus
{
    /// <summary>
    /// Takes a color from anywhere on the screen: a see-through window covers every screen,
    /// a magnifier with the color's code follows the mouse, a click takes the color under it.
    /// Esc or a right click cancels.
    /// </summary>
    internal static class EyeDropper
    {
        private const int Cells = 11;     // pixels shown in the magnifier, each way
        private const int CellPx = 10;    // how big each one is drawn

        private static Window? _open;

        /// <summary>Lets the user pick a color; <paramref name="done"/> gets it (0xFFRRGGBB), or null if cancelled.</summary>
        public static void Pick(Action<uint?> done)
        {
            if (_open != null) return;

            var canvas = new Canvas();
            var win = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, ResizeMode = ResizeMode.NoResize,
                // Almost invisible but still takes the clicks (a fully clear window lets them through).
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                Topmost = true, ShowInTaskbar = false, ShowActivated = true, Cursor = Cursors.Cross,
                Left = SystemParameters.VirtualScreenLeft, Top = SystemParameters.VirtualScreenTop,
                Width = SystemParameters.VirtualScreenWidth, Height = SystemParameters.VirtualScreenHeight,
                Content = canvas, Title = "Eyedropper",
            };

            // The magnifier: the pixels around the mouse, big, with the middle one marked, and the color's code.
            var zoom = new Image { Width = Cells * CellPx, Height = Cells * CellPx, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(zoom, BitmapScalingMode.NearestNeighbor);
            var zoomBox = new Grid { Width = Cells * CellPx, Height = Cells * CellPx, ClipToBounds = true };
            zoomBox.Children.Add(zoom);
            zoomBox.Children.Add(new Rectangle
            {
                Width = CellPx + 2, Height = CellPx + 2, Stroke = Brushes.White, StrokeThickness = 1.5,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.9 },
            });
            var swatch = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1) };
            var hex = new TextBlock { Foreground = Brushes.White, FontFamily = new FontFamily("Consolas"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            var info = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 8, 2, 0) };
            info.Children.Add(swatch);
            info.Children.Add(hex);
            var panel = new StackPanel();
            panel.Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = zoomBox, BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1) });
            panel.Children.Add(info);
            panel.Children.Add(new TextBlock
            {
                Text = Lang.T("كليك عشان تاخذ اللون، Esc للإلغاء"), FontSize = 11, Margin = new Thickness(2, 6, 2, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA3, 0xAA)),
            });
            var magnifier = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1E, 0x1F, 0x22)), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10), Child = panel, IsHitTestVisible = false,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.5 },
            };
            canvas.Children.Add(magnifier);

            uint current = 0xFF000000;
            bool finished = false;
            void Finish(uint? result)
            {
                if (finished) return;
                finished = true;
                _open = null;
                win.Close();
                done(result);
            }

            void Update()
            {
                if (!GetCursorPos(out var p)) return;
                var shot = Grab(p.X - Cells / 2, p.Y - Cells / 2);
                if (shot != null)
                {
                    zoom.Source = shot.Value.Image;
                    current = shot.Value.Middle;
                }
                var c = Color.FromRgb((byte)(current >> 16), (byte)(current >> 8), (byte)current);
                swatch.Background = new SolidColorBrush(c);
                hex.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";

                // Next to the mouse, flipped to the other side near the screen's edges.
                var m = Mouse.GetPosition(canvas);
                magnifier.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var size = magnifier.DesiredSize;
                double x = m.X + 24, y = m.Y + 24;
                if (x + size.Width > canvas.ActualWidth) x = m.X - 24 - size.Width;
                if (y + size.Height > canvas.ActualHeight) y = m.Y - 24 - size.Height;
                Canvas.SetLeft(magnifier, Math.Max(0, x));
                Canvas.SetTop(magnifier, Math.Max(0, y));
            }

            // A timer, not only mouse moves: what's under the mouse (a video) can change by itself.
            var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            timer.Tick += (_, _) => Update();
            win.MouseMove += (_, _) => Update();
            win.MouseLeftButtonDown += (_, e) => { e.Handled = true; Update(); Finish(current); };
            win.MouseRightButtonDown += (_, e) => { e.Handled = true; Finish(null); };
            win.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Finish(null); } };
            win.Deactivated += (_, _) => Finish(null);   // Alt+Tab or the like
            win.Closed += (_, _) => { timer.Stop(); _open = null; if (!finished) { finished = true; done(null); } };
            win.ContentRendered += (_, _) => { Update(); timer.Start(); };

            _open = win;
            win.Show();
            win.Activate();
        }

        /// <summary>The screen's pixels in a small square from (x, y), and the color of the middle one.</summary>
        private static (BitmapSource Image, uint Middle)? Grab(int x, int y)
        {
            try
            {
                using var bmp = new System.Drawing.Bitmap(Cells, Cells, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                    g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(Cells, Cells));
                var mid = bmp.GetPixel(Cells / 2, Cells / 2);
                var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, Cells, Cells), System.Drawing.Imaging.ImageLockMode.ReadOnly, bmp.PixelFormat);
                try
                {
                    var src = BitmapSource.Create(Cells, Cells, 96, 96, PixelFormats.Bgr32, null, data.Scan0, data.Stride * Cells, data.Stride);
                    src.Freeze();
                    return (src, 0xFF000000 | ((uint)mid.R << 16) | ((uint)mid.G << 8) | mid.B);
                }
                finally { bmp.UnlockBits(data); }
            }
            catch { return null; }
        }

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    }
}
