using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinPlus
{
    /// <summary>
    /// The panel under the arrow next to the clock: the hidden icons. It's its own window,
    /// placed once and pinned at the side that faces the taskbar, so it never moves when
    /// icons come and go. It looks like the taskbar: glass, the same color, or see-through.
    /// A click anywhere else closes it.
    /// </summary>
    internal sealed class TrayFlyout : Window
    {
        private readonly TaskbarBar _bar;
        private readonly FrameworkElement _anchor;
        private readonly IntPtr _hwnd;
        private readonly Grid _host = new();
        private readonly Border _card = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(6) };
        private readonly SolidColorBrush _ink = new(Colors.White);
        private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(80) };
        private LiquidGlass? _glass;
        private double _scale = 1;
        private Point? _pin;           // the anchored corner, in screen pixels
        private bool _closed;

        public TrayFlyout(TaskbarBar bar, FrameworkElement anchor)
        {
            _bar = bar;
            _anchor = anchor;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            FlowDirection = bar.FlowDirection;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            _host.Children.Add(_card);
            Content = _host;

            _hwnd = new WindowInteropHelper(this).EnsureHandle();
            TaskbarApps.Ours.Add(_hwnd);
            long ex = GetWindowLongPtr(_hwnd, -20).ToInt64();
            SetWindowLongPtr(_hwnd, -20, (IntPtr)(ex | 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */));
            var src = HwndSource.FromHwnd(_hwnd);
            src.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == 0x21 /* WM_MOUSEACTIVATE */) { handled = true; return (IntPtr)3; }
                return IntPtr.Zero;
            });
            _scale = src.CompositionTarget.TransformToDevice.M11;

            ApplyLook();
            Fill();
            SizeChanged += (_, _) =>
            {
                PlaceAtPin();
                if (_glass != null) { _glass.Dispose(); _glass = null; Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(StartGlass)); }
            };
            Loaded += (_, _) => StartGlass();
            _watch.Tick += (_, _) => Watch();

            Place();
            Show();
            _watch.Start();
        }

        public bool IsOpen => !_closed;

        // ---- Look -----------------------------------------------------------------------------------

        private void ApplyLook()
        {
            int look = CustomTaskbar.Look, opacity = Math.Clamp(CustomTaskbar.Opacity, 0, 100), rgb = CustomTaskbar.BackColor;
            var color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            _card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            _card.BorderThickness = new Thickness(1);
            switch (look)
            {
                case 1:   // the taskbar's color
                    _card.Background = new SolidColorBrush(color);
                    SetInk(Widgets.Lightness(color) > 0.6);
                    break;
                case 2:   // see-through, as strong as the taskbar's
                    _card.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Max(1, opacity * 255 / 100), color.R, color.G, color.B));
                    SetInk(Widgets.Lightness(color) > 0.6 && opacity >= 50);
                    break;
                default:  // glass (the Liquid Glass page's), dark gray until it's ready
                    _card.Background = new SolidColorBrush(Color.FromArgb(0xE8, 0x24, 0x25, 0x29));
                    SetInk(false);
                    break;
            }
        }

        private void SetInk(bool lightBehind) =>
            _ink.Color = lightBehind ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xF5, 0xF5, 0xF7);

        private void StartGlass()
        {
            if (_closed || CustomTaskbar.Look != 0 || !GlassSettings.Taskbar.On) return;
            try
            {
                // What's on the screen behind it (it floats over windows, unlike the taskbar).
                var glass = new LiquidGlass(this, _host, _card, new Thickness(0), 10, GlassSettings.Taskbar, tint: CustomTaskbar.GlassTint());
                if (!glass.Available) return;
                _glass = glass;
                glass.LightBehindChanged += light => SetInk(light);
                glass.Start();
            }
            catch (Exception ex) { CrashLog.Write("tray flyout glass", ex); }
        }

        // ---- Content --------------------------------------------------------------------------------

        /// <summary>The hidden icons (called again when icons change; the panel stays where it is).</summary>
        public void Fill()
        {
            var hidden = TrayHost.Icons.Where(i => !i.Hidden && i.Image != null && !TrayHost.OnBar(i)).ToList();
            var stack = new StackPanel();
            if (hidden.Count == 0)
                stack.Children.Add(new TextBlock { Text = Lang.T("كل الأيقونات ظاهرة"), Foreground = _ink, Opacity = 0.75, FontSize = 12, Margin = new Thickness(6, 4, 6, 6) });
            else
            {
                var wrap = new WrapPanel { MaxWidth = 5 * 40, Orientation = Orientation.Horizontal };
                foreach (var i in hidden) wrap.Children.Add(new TrayIconView(_bar, i, 40, onBar: false));
                stack.Children.Add(wrap);
            }
            var arrange = new TextBlock
            {
                Text = Lang.T("اختر وش يبان في التاسك بار…"), FontSize = 11, Foreground = _ink, Opacity = 0.65,
                Margin = new Thickness(6, 6, 6, 2), Cursor = Cursors.Hand, TextDecorations = TextDecorations.Underline,
            };
            arrange.MouseLeftButtonUp += (_, e) => { e.Handled = true; CloseIt(); (Application.Current as App)?.ShowTaskbarSettings(); };
            stack.Children.Add(arrange);
            _card.Child = stack;
        }

        // ---- Place ----------------------------------------------------------------------------------

        /// <summary>Works out the corner that touches the taskbar, once, from the arrow's place.</summary>
        private void Place()
        {
            var a = _anchor.PointToScreen(new Point(0, 0));
            var z = _anchor.PointToScreen(new Point(_anchor.ActualWidth, _anchor.ActualHeight));
            double l = Math.Min(a.X, z.X), r = Math.Max(a.X, z.X), t = Math.Min(a.Y, z.Y), b = Math.Max(a.Y, z.Y);
            double gap = 8 * _scale;
            // The pin is the middle of the side that faces the taskbar.
            if (_bar.Vertical)
            {
                bool leftBar = l < System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)l, (int)t)).Bounds.Left + 200 * _scale;
                _pin = new Point(leftBar ? r + gap : l - gap, (t + b) / 2);
            }
            else _pin = new Point((l + r) / 2, _bar.AtTop ? b + gap : t - gap);
            _card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            PlaceAt(_card.DesiredSize.Width * _scale, _card.DesiredSize.Height * _scale);
        }

        private void PlaceAtPin()
        {
            if (_pin == null || ActualWidth <= 0) return;
            PlaceAt(ActualWidth * _scale, ActualHeight * _scale);
        }

        private void PlaceAt(double w, double h)
        {
            var p = _pin!.Value;
            double x, y;
            if (_bar.Vertical)
            {
                bool rightOfPin = p.X > _anchor.PointToScreen(new Point(0, 0)).X;
                x = rightOfPin ? p.X : p.X - w;
                y = p.Y - h / 2;
            }
            else
            {
                x = p.X - w / 2;
                y = _bar.AtTop ? p.Y : p.Y - h;   // grows away from the taskbar; the edge next to it stays put
            }
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)p.X, (int)p.Y)).Bounds;
            x = Math.Clamp(x, screen.Left + 4, Math.Max(screen.Left + 4, screen.Right - w - 4));
            y = Math.Clamp(y, screen.Top + 4, Math.Max(screen.Top + 4, screen.Bottom - h - 4));
            Left = x / _scale;
            Top = y / _scale;
        }

        // ---- Closing ----------------------------------------------------------------------------------

        /// <summary>A click outside it (and outside the arrow) closes it.</summary>
        private void Watch()
        {
            if (_closed) return;
            bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0 || (GetAsyncKeyState(0x04) & 0x8000) != 0;
            if (!down || IsMouseOver || _anchor.IsMouseOver) return;
            CloseIt();
        }

        public void CloseIt()
        {
            if (_closed) return;
            _closed = true;
            _watch.Stop();
            _glass?.Dispose();
            _glass = null;
            TaskbarApps.Ours.Remove(_hwnd);
            try { Close(); } catch { }
        }

        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    }
}
