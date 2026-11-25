using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace WinPlus
{
    /// <summary>
    /// The wallpapers in the middle of the screen, like the wallpaper pickers on Linux: a row of
    /// cards that slides, the chosen one big in the middle under a lit frame, the others smaller and
    /// darker the further they are. ← → (or the mouse wheel) move, Enter (or a click on the middle
    /// card) sets it, Esc (or a click outside) closes. Comes in and goes out with a zoom and a fade.
    /// </summary>
    internal sealed class WallpaperSwitcher : Window
    {
        /// <summary>The one on screen now, if any.</summary>
        public static WallpaperSwitcher? Open { get; private set; }

        private readonly List<string> _items;
        private readonly Border[] _cards;
        private readonly bool[] _asked;
        private int _sel;
        private bool _closing;

        private readonly Grid _root = new();
        private readonly Border _dim = new();
        private readonly Grid _stage = new();
        private readonly Canvas _strip = new() { ClipToBounds = false };
        // The light around the middle card: a big blurred copy of it behind it, so each edge
        // shines in its own color. Two of them, to cross-fade when the middle card changes.
        private readonly Border[] _glows = { new(), new() };
        private int _glowFront;
        private readonly ImageSource?[] _thumbs;
        private readonly ScaleTransform _stageScale = new(0.94, 0.94);
        private readonly TranslateTransform _stageMove = new(0, 26);
        private System.Drawing.Rectangle _screen;   // pixels (system DPI)
        private double _cardW = 520, _cardH = 292;

        public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
            nameof(Position), typeof(double), typeof(WallpaperSwitcher),
            new PropertyMetadata(0.0, (d, _) => ((WallpaperSwitcher)d).Layout()));

        /// <summary>Which card is in the middle (between two while sliding).</summary>
        public double Position
        {
            get => (double)GetValue(PositionProperty);
            set => SetValue(PositionProperty, value);
        }

        public WallpaperSwitcher(List<string> items, string current)
        {
            _items = items;
            _cards = new Border[items.Count];
            _asked = new bool[items.Count];
            _thumbs = new ImageSource?[items.Count];
            _sel = Math.Max(0, items.FindIndex(p => string.Equals(p, current, StringComparison.OrdinalIgnoreCase)));

            Title = "Wibn wallpapers";
            WindowStyle = WindowStyle.None;
            // Not see-through: a see-through window this big is copied back from the graphics card every
            // frame, and the animations stutter. A picture of the screen behind it looks the same.
            Background = Brushes.Black;
            SourceInitialized += (_, _) =>
            {
                int on = 1;   // no window open/close animation of Windows' own
                DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(this).Handle, 3 /* TRANSITIONS_FORCEDISABLED */, ref on, 4);
            };
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            FlowDirection = FlowDirection.LeftToRight;   // ← is left on the screen, in both languages
            FontFamily = new FontFamily("Segoe UI");
            Content = _root;

            // The screen goes dark around the edges, a bit lighter in the middle.
            _dim.Background = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x8C, 0x05, 0x06, 0x08), 0),
                    new GradientStop(Color.FromArgb(0xD8, 0x02, 0x02, 0x03), 1),
                },
                RadiusX = 0.75, RadiusY = 0.85,
            };
            _dim.Opacity = 0;
            _dim.MouseLeftButtonDown += (_, _) => Dismiss();
            _root.Children.Add(_dim);

            _stage.Opacity = 0;
            _stage.RenderTransformOrigin = new Point(0.5, 0.5);
            _stage.RenderTransform = new TransformGroup { Children = { _stageScale, _stageMove } };
            foreach (var g in _glows)
            {
                g.Opacity = 0;
                g.IsHitTestVisible = false;
                g.CornerRadius = new CornerRadius(60);
                g.Effect = new BlurEffect { Radius = 110, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
                _stage.Children.Add(g);   // under the cards
            }
            _stage.Children.Add(_strip);
            _root.Children.Add(_stage);

            for (int i = 0; i < items.Count; i++) _cards[i] = MakeCard(i);

            PreviewKeyDown += OnKey;
            PreviewMouseWheel += (_, e) => { Go(_sel + (e.Delta > 0 ? -1 : 1)); e.Handled = true; };
            Deactivated += (_, _) => Dismiss();
            Closed += (_, _) => { if (Open == this) Open = null; };
        }

        // ---- Showing -------------------------------------------------------------------------------

        /// <summary>Full screen on the screen with the mouse, then comes in.</summary>
        public void ShowOnCursorScreen()
        {
            Open = this;
            var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            _screen = screen.Bounds;
            double scale = GetDpiForSystem() / 96.0;
            if (scale <= 0) scale = 1;
            Left = _screen.Left / scale;
            Top = _screen.Top / scale;
            Width = _screen.Width / scale;
            Height = _screen.Height / scale;

            // Card size from the screen: about a third of its width, 16:9.
            _cardW = Math.Clamp(Width * 0.34, 300, 680);
            _cardH = _cardW * 9 / 16;
            foreach (var c in _cards) { c.Width = _cardW; c.Height = _cardH; }
            foreach (var g in _glows) { g.Width = _cardW * 1.16; g.Height = _cardH * 1.24; }

            Position = _sel - (_items.Count > 1 ? 1.4 : 0);   // they slide in to the current one
            Background = new ImageBrush(ScreenPicture(_screen)) { Stretch = Stretch.Fill };
            Show();
            Activate();
            Focus();
            Keyboard.Focus(this);

            var quick = TimeSpan.FromMilliseconds(260);
            var smooth = TimeSpan.FromMilliseconds(520);
            var outEase = new QuinticEase { EasingMode = EasingMode.EaseOut };
            _dim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, quick));
            _stage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
            var pop = new DoubleAnimation(0.94, 1, smooth) { EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut } };
            _stageScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            _stageScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop.Clone());
            _stageMove.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(26, 0, smooth) { EasingFunction = outEase });
            BeginAnimation(PositionProperty, new DoubleAnimation(_sel, TimeSpan.FromMilliseconds(640)) { EasingFunction = outEase });
            UpdateGlow();
        }

        /// <summary>Goes out (zoom and fade), then closes.</summary>
        private Action? _afterClose;

        public void Dismiss(bool applied = false)
        {
            if (_closing) return;
            _closing = true;
            if (Open == this) Open = null;
            var t = TimeSpan.FromMilliseconds(applied ? 340 : 220);
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var fade = new DoubleAnimation(0, t) { EasingFunction = ease };
            fade.Completed += (_, _) => { Close(); _afterClose?.Invoke(); };
            _dim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, t));
            _stage.BeginAnimation(OpacityProperty, fade);
            // Set: it grows toward you as the wallpaper takes over. Closed: it sinks back a little.
            double to = applied ? 1.12 : 0.95;
            var zoom = new DoubleAnimation(to, t) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            _stageScale.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
            _stageScale.BeginAnimation(ScaleTransform.ScaleYProperty, zoom.Clone());
            if (!applied) _stageMove.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, t) { EasingFunction = ease });
        }

        // ---- Keys and mouse -----------------------------------------------------------------------------

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (_closing) return;
            switch (e.Key)
            {
                case Key.Right: case Key.Down: case Key.Tab: case Key.D: Go(_sel + 1); break;
                case Key.Left: case Key.Up: case Key.A: Go(_sel - 1); break;
                case Key.Home: Go(0); break;
                case Key.End: Go(_items.Count - 1); break;
                case Key.PageDown: Go(_sel + 5); break;
                case Key.PageUp: Go(_sel - 5); break;
                case Key.Enter: case Key.Space: ApplySelected(); break;
                case Key.Escape: Dismiss(); break;
                default: return;
            }
            e.Handled = true;
        }

        private void Go(int index)
        {
            if (_closing) return;
            index = Math.Clamp(index, 0, _items.Count - 1);
            if (index == _sel)
            {
                // At the end: a little bump so you feel it.
                if (_items.Count > 0 && !IsSliding) Bump(index == 0 ? -1 : 1);
                return;
            }
            _sel = index;
            // From wherever it is now (smooth even when keys are held down).
            BeginAnimation(PositionProperty, new DoubleAnimation(index, TimeSpan.FromMilliseconds(340))
                { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
            UpdateGlow();
        }

        private bool IsSliding => Math.Abs(Position - _sel) > 0.001;

        private void Bump(int dir)
        {
            var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(300) };
            a.KeyFrames.Add(new EasingDoubleKeyFrame(_sel + dir * 0.12, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)),
                new CubicEase { EasingMode = EasingMode.EaseOut }));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(_sel, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)),
                new CubicEase { EasingMode = EasingMode.EaseInOut }));
            BeginAnimation(PositionProperty, a);
        }

        private void ApplySelected()
        {
            if (_closing || _items.Count == 0) return;
            string path = _items[_sel];
            // The circle grows from the middle card: the middle of this screen.
            var origin = new System.Drawing.Point(_screen.Left + _screen.Width / 2, _screen.Top + _screen.Height / 2);
            // The list goes first, then the new wallpaper comes in on the real desktop (seen whole).
            _afterClose = () => Wallpapers.Apply(path, origin);
            Dismiss(applied: true);
        }

        // ---- Cards -------------------------------------------------------------------------------------

        private Border MakeCard(int i)
        {
            string path = _items[i];
            var grid = new Grid();
            // Darker the further it is from the middle.
            grid.Children.Add(new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(14), Opacity = 0, Tag = "shade" });

            var card = new Border
            {
                Width = _cardW, Height = _cardH,
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(Color.FromRgb(0x24, 0x26, 0x2B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Child = grid,
                Cursor = Cursors.Hand,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1),
                Visibility = Visibility.Collapsed,
            };
            card.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (i == _sel) ApplySelected();
                else Go(i);
            };
            _strip.Children.Add(card);
            return card;
        }

        /// <summary>Places every card for the current <see cref="Position"/>.</summary>
        private void Layout()
        {
            if (_cards.Length == 0 || ActualWidth <= 0 && Width <= 0) return;
            double w = ActualWidth > 0 ? ActualWidth : Width, h = ActualHeight > 0 ? ActualHeight : Height;
            double cx = w / 2, cy = h / 2;
            double p = Position;
            for (int i = 0; i < _cards.Length; i++)
            {
                var card = _cards[i];
                double d = i - p, a = Math.Abs(d);
                if (a > 4.6) { card.Visibility = Visibility.Collapsed; continue; }
                card.Visibility = Visibility.Visible;
                if (!_asked[i]) LoadThumb(i);

                // Smaller and darker away from the middle; spaced so they never overlap.
                double scale = a <= 1 ? 1 - 0.2 * a : Math.Max(0.5, 0.8 - 0.08 * (a - 1));
                double x = Math.Sign(d) * (a <= 1 ? a * 0.98 * _cardW : 0.98 * _cardW + (a - 1) * 0.84 * _cardW);
                Canvas.SetLeft(card, cx + x - _cardW / 2);
                Canvas.SetTop(card, cy - _cardH / 2);
                var st = (ScaleTransform)card.RenderTransform;
                st.ScaleX = st.ScaleY = scale;
                card.Opacity = a <= 1 ? 1 - 0.3 * a : Math.Max(0, 0.7 - 0.2 * (a - 1));
                Panel.SetZIndex(card, 1000 - (int)(a * 100));
                if (card.Child is Grid g && g.Children[0] is Border shade) shade.Opacity = Math.Min(1, a) * 0.42;
            }
        }

        private async void LoadThumb(int i)
        {
            _asked[i] = true;
            var pic = await Wallpapers.ThumbAsync(_items[i]);
            if (pic == null || _closing) return;
            _thumbs[i] = pic;
            if (i == _sel) UpdateGlow();
            var brush = new ImageBrush(pic) { Stretch = Stretch.UniformToFill, Opacity = 0 };
            _cards[i].Background = brush;
            brush.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        }

        /// <summary>The light behind the middle card takes its colors (a soft cross-fade).</summary>
        private void UpdateGlow()
        {
            if (_items.Count == 0 || _thumbs[_sel] is not { } pic) return;
            var old = _glows[_glowFront];
            _glowFront ^= 1;
            var glow = _glows[_glowFront];
            if (glow.Background is ImageBrush b && b.ImageSource == pic && glow.Opacity > 0.5) { _glowFront ^= 1; return; }
            glow.Background = new ImageBrush(pic) { Stretch = Stretch.Fill };
            var t = TimeSpan.FromMilliseconds(380);
            glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.95, t) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            old.BeginAnimation(OpacityProperty, new DoubleAnimation(0, t) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        }

        /// <summary>What's on this screen now.</summary>
        private static ImageSource ScreenPicture(System.Drawing.Rectangle r)
        {
            using var bmp = new System.Drawing.Bitmap(r.Width, r.Height);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
                g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
            IntPtr h = bmp.GetHbitmap();
            try
            {
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DeleteObject(h); }
        }

        [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    }
}
