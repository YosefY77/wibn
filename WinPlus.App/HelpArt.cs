using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WinPlus
{
    /// <summary>
    /// A small drawing kit for the help pictures: a 260 × 150 canvas and simple shapes
    /// (a screen, the taskbar, windows, files, keys, the mouse, arrows…) to show what a
    /// feature does at a glance. Drawn in code, so they're sharp at any size and follow
    /// the app's colors.
    /// </summary>
    internal sealed class HelpArt
    {
        public const double W = 260, H = 150;

        public static readonly Color Back = Color.FromRgb(0x17, 0x18, 0x1B);
        public static readonly Color Line = Color.FromRgb(0x3A, 0x3D, 0x44);
        public static readonly Color Panel = Color.FromRgb(0x2B, 0x2D, 0x31);
        public static readonly Color Ink = Color.FromRgb(0xF2, 0xF3, 0xF5);
        public static readonly Color Muted = Color.FromRgb(0xA0, 0xA3, 0xAA);
        public static readonly Color Blue = Color.FromRgb(0x4C, 0x8D, 0xFF);
        public static readonly Color Green = Color.FromRgb(0x34, 0xC7, 0x59);
        public static readonly Color Red = Color.FromRgb(0xFF, 0x45, 0x3A);
        public static readonly Color Yellow = Color.FromRgb(0xF4, 0xC0, 0x4E);
        public static readonly Color Purple = Color.FromRgb(0xAF, 0x52, 0xDE);
        public static readonly Color Orange = Color.FromRgb(0xFF, 0x95, 0x00);
        public static readonly Color Wall = Color.FromRgb(0x24, 0x3B, 0x5E);

        private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
        public readonly Canvas Canvas = new() { Width = W, Height = H, ClipToBounds = true };

        public static SolidColorBrush B(Color c, byte a = 255) => new(Color.FromArgb(a, c.R, c.G, c.B));

        /// <summary>The finished picture: the canvas in a rounded frame.</summary>
        public FrameworkElement Done() => new Border
        {
            Width = W, Height = H, CornerRadius = new CornerRadius(12), Background = B(Back),
            BorderBrush = B(Line), BorderThickness = new Thickness(1), Child = Canvas, ClipToBounds = true,
            FlowDirection = FlowDirection.LeftToRight,
        };

        private T Put<T>(T e, double x, double y) where T : UIElement
        {
            Canvas.SetLeft(e, x);
            Canvas.SetTop(e, y);
            Canvas.Children.Add(e);
            return e;
        }

        // ---- Basic shapes ------------------------------------------------------------------------

        public Rectangle Rect(double x, double y, double w, double h, Color fill, double r = 4, byte alpha = 255, Color? stroke = null, double thick = 1)
            => Put(new Rectangle
            {
                Width = w, Height = h, RadiusX = r, RadiusY = r, Fill = B(fill, alpha),
                Stroke = stroke is { } s ? B(s) : null, StrokeThickness = stroke != null ? thick : 0,
            }, x, y);

        public Rectangle Dashed(double x, double y, double w, double h, Color c, byte fillAlpha = 0x30)
        {
            var r = Rect(x, y, w, h, c, 2, fillAlpha, c, 1.5);
            r.StrokeDashArray = new DoubleCollection { 3, 2 };
            return r;
        }

        public Ellipse Dot(double cx, double cy, double d, Color c, byte alpha = 255)
            => Put(new Ellipse { Width = d, Height = d, Fill = B(c, alpha) }, cx - d / 2, cy - d / 2);

        public TextBlock Text(double x, double y, string text, double size = 11, Color? color = null, bool bold = false)
            => Put(new TextBlock
            {
                Text = text, FontSize = size, Foreground = B(color ?? Ink), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                FontFamily = new FontFamily("Segoe UI"),
            }, x, y);

        /// <summary>Text centered on a point.</summary>
        public TextBlock Center(double cx, double cy, string text, double size = 11, Color? color = null, bool bold = false)
        {
            var t = new TextBlock
            {
                Text = text, FontSize = size, Foreground = B(color ?? Ink), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                FontFamily = new FontFamily("Segoe UI"),
            };
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Put(t, cx - t.DesiredSize.Width / 2, cy - t.DesiredSize.Height / 2);
        }

        public TextBlock Glyph(double cx, double cy, string glyph, double size, Color c)
        {
            var t = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = size, Foreground = B(c) };
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Put(t, cx - t.DesiredSize.Width / 2, cy - t.DesiredSize.Height / 2);
        }

        public void Arrow(double x1, double y1, double x2, double y2, Color c, double thick = 2)
        {
            Canvas.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = B(c), StrokeThickness = thick, StrokeStartLineCap = PenLineCap.Round });
            double a = Math.Atan2(y2 - y1, x2 - x1), len = 7;
            var head = new Polygon
            {
                Fill = B(c),
                Points = new PointCollection
                {
                    new(x2, y2),
                    new(x2 - len * Math.Cos(a - 0.45), y2 - len * Math.Sin(a - 0.45)),
                    new(x2 - len * Math.Cos(a + 0.45), y2 - len * Math.Sin(a + 0.45)),
                },
            };
            Canvas.Children.Add(head);
        }

        /// <summary>The mouse pointer, its tip at (x, y).</summary>
        public void Cursor(double x, double y, double scale = 1, Color? fill = null)
        {
            var p = new Path
            {
                Data = Geometry.Parse("M0,0 L0,16 L4.5,12 L7.5,18.5 L10,17.4 L7.2,11 L12.5,11 Z"),
                Fill = B(fill ?? Colors.White), Stroke = B(Colors.Black), StrokeThickness = 1,
                RenderTransform = new ScaleTransform(scale, scale),
            };
            Put(p, x, y);
        }

        // ---- Things -----------------------------------------------------------------------------

        /// <summary>A screen with wallpaper; returns nothing, fills the canvas.</summary>
        public void Screen() => Rect(0, 0, W, H, Wall, 0, 0xFF);

        /// <summary>The taskbar along the bottom with app icons (their colors), the running ones with a dot.</summary>
        public void Taskbar(Color[] apps, int running = 0, double y = H - 24, double x0 = 70, double size = 14, double gap = 8, Color? back = null, bool clock = true)
        {
            Rect(0, y, W, H - y, back ?? Panel, 0);
            double x = x0;
            for (int i = 0; i < apps.Length; i++)
            {
                Rect(x, y + (H - y - size) / 2 - 1, size, size, apps[i], 3);
                if (i < running) Rect(x + size / 2 - 3, H - 4, 6, 2, Muted, 1);
                x += size + gap;
            }
            if (clock) Text(W - 38, y + 5, "9:41", 10, Muted);
        }

        public static readonly Color[] Apps =
            { Blue, Green, Orange, Red, Purple, Yellow, Color.FromRgb(0x40, 0xC8, 0xE0), Color.FromRgb(0xFF, 0x5F, 0xA2) };

        /// <summary>A window with a title bar.</summary>
        public void Window(double x, double y, double w, double h, string title = "", Color? body = null)
        {
            Rect(x, y, w, h, body ?? Panel, 6, 255, Line);
            Rect(x, y, w, 16, Color.FromRgb(0x22, 0x23, 0x27), 6);
            Rect(x, y + 10, w, 6, Color.FromRgb(0x22, 0x23, 0x27), 0);
            if (title.Length > 0) Text(x + 7, y + 1, title, 9, Muted);
            Dot(x + w - 9, y + 8, 6, Red);
        }

        /// <summary>A file: a page with its type on it.</summary>
        public void File(double x, double y, string type, Color c, double scale = 1)
        {
            double w = 30 * scale, h = 38 * scale;
            Rect(x, y, w, h, Color.FromRgb(0xE8, 0xEA, 0xEE), 3 * scale);
            Rect(x, y + h - 14 * scale, w, 14 * scale, c, 3 * scale);
            Center(x + w / 2, y + h - 7 * scale, type, 8 * scale, Colors.White, true);
        }

        public void Folder(double cx, double cy, double size = 30, Color? c = null) => Glyph(cx, cy, "", size, c ?? Yellow);

        /// <summary>A keyboard key.</summary>
        public double Key(double x, double y, string label, double h = 24)
        {
            var t = new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI") };
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = Math.Max(h, t.DesiredSize.Width + 14);
            Rect(x, y + 3, w, h, Color.FromRgb(0x10, 0x11, 0x13), 5);
            Rect(x, y, w, h, Color.FromRgb(0x3A, 0x3D, 0x44), 5);
            Center(x + w / 2, y + h / 2, label, 11, Ink, true);
            return w;
        }

        /// <summary>Keys with "+" between them; returns where they end.</summary>
        public double Keys(double x, double y, params string[] labels)
        {
            for (int i = 0; i < labels.Length; i++)
            {
                if (i > 0) { Center(x + 6, y + 12, "+", 12, Muted); x += 12; }
                x += Key(x, y, labels[i]) + 2;
            }
            return x;
        }

        /// <summary>A bar filled to `part` (0–1).</summary>
        public void Bar(double x, double y, double w, double part, Color c, double h = 7)
        {
            Rect(x, y, w, h, Line, h / 2);
            Rect(x, y, Math.Max(h, w * part), h, c, h / 2);
        }

        /// <summary>An on/off switch.</summary>
        public void Switch(double x, double y, bool on)
        {
            Rect(x, y, 26, 14, on ? Blue : Line, 7);
            Dot(on ? x + 19 : x + 7, y + 7, 10, Colors.White);
        }

        public void Slider(double x, double y, double w, double part, Color c)
        {
            Bar(x, y, w, part, c, 4);
            Dot(x + w * part, y + 2, 11, Colors.White);
        }

        /// <summary>A phone outline.</summary>
        public void Phone(double x, double y, double w = 46, double h = 86)
        {
            Rect(x, y, w, h, Color.FromRgb(0x10, 0x11, 0x13), 9, 255, Muted, 1.5);
            Rect(x + 4, y + 8, w - 8, h - 16, Panel, 3);
        }

        /// <summary>A laptop/monitor outline.</summary>
        public void Monitor(double x, double y, double w = 80, double h = 54)
        {
            Rect(x, y, w, h, Color.FromRgb(0x10, 0x11, 0x13), 4, 255, Muted, 1.5);
            Rect(x + 4, y + 4, w - 8, h - 8, Wall, 2);
            Rect(x + w / 2 - 8, y + h, 16, 8, Muted, 0);
            Rect(x + w / 2 - 18, y + h + 8, 36, 3, Muted, 1);
        }

        /// <summary>A desktop icon (a square with a label under it).</summary>
        public void Icon(double x, double y, Color c, string label = "")
        {
            Rect(x, y, 22, 22, c, 4);
            if (label.Length > 0) Center(x + 11, y + 30, label, 8, Ink);
        }
    }
}
