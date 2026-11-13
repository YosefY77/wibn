using System;
using System.Collections.Generic;
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
    /// The live previews over an app's button: one picture per window (drawn by Windows
    /// itself, so it moves), its title and a close button. Click one to go to it.
    ///
    /// The panel looks like the taskbar (glass, its color, or see-through), so it's a
    /// see-through window. Windows only draws live pictures on normal windows, so each
    /// picture sits on a small window of its own, exactly over its spot; the mouse goes
    /// through it to the panel underneath.
    /// </summary>
    internal sealed class TaskbarPreview : Window
    {
        private static TaskbarPreview? _current;
        private static readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromMilliseconds(350) };

        private readonly AppButton _button;
        private readonly IntPtr _hwnd;
        private readonly double _scale;
        private readonly Grid _host = new();
        private readonly Border _card = new() { CornerRadius = new CornerRadius(10) };
        private readonly SolidColorBrush _ink = new(Colors.White), _hover = new(Colors.Transparent), _activeBack = new(Colors.Transparent);
        private readonly List<(Border Spot, ThumbHost Host, IntPtr Thumb)> _thumbs = new();
        private LiquidGlass? _glass;
        private bool _closed;

        static TaskbarPreview()
        {
            _hide.Tick += (_, _) =>
            {
                _hide.Stop();
                if (_current != null && !_current.IsMouseOver && !_current._button.IsMouseOver) HideNow();
            };
        }

        public static bool IsShownFor(AppButton b) => _current != null && _current._button == b;

        public static void ShowFor(AppButton button, TaskbarBar bar, TaskbarApps.Group group)
        {
            _hide.Stop();
            if (_current != null && _current._button == button) return;
            HideNow();
            try { _current = new TaskbarPreview(button, bar, group); }
            catch (Exception ex) { CrashLog.Write("taskbar preview", ex); _current = null; }
        }

        public static void HideSoon() { _hide.Stop(); _hide.Start(); }

        public static void HideNow()
        {
            _hide.Stop();
            var c = _current;
            _current = null;
            c?.CloseIt();
        }

        private TaskbarPreview(AppButton button, TaskbarBar bar, TaskbarApps.Group group)
        {
            _button = button;
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

            bool pictures = group.Windows.Count <= 6;
            var list = new StackPanel
            {
                Orientation = pictures && !bar.Vertical ? Orientation.Horizontal : Orientation.Vertical,
                Margin = new Thickness(6),
            };
            foreach (var w in group.Windows) list.Children.Add(Cell(w, pictures));
            _card.Child = list;
            MouseEnter += (_, _) => _hide.Stop();
            MouseLeave += (_, _) => HideSoon();

            // Measure, put it over the button, then show it, its glass and the pictures.
            _card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = _card.DesiredSize;
            var b = button.PointToScreen(new Point(0, 0));
            var b2 = button.PointToScreen(new Point(button.ActualWidth, button.ActualHeight));
            double bl = Math.Min(b.X, b2.X), br = Math.Max(b.X, b2.X), bt = Math.Min(b.Y, b2.Y), bb = Math.Max(b.Y, b2.Y);
            double ww = size.Width * _scale, h = size.Height * _scale, gap = 10 * _scale;
            double x, y;
            if (bar.Vertical)
            {
                y = (bt + bb) / 2 - h / 2;
                bool rightSide = bl < SystemParameters.VirtualScreenWidth * _scale / 2;
                x = rightSide ? br + gap : bl - ww - gap;
            }
            else
            {
                x = (bl + br) / 2 - ww / 2;
                y = bar.AtTop ? bb + gap : bt - h - gap;
            }
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)((bl + br) / 2), (int)((bt + bb) / 2))).Bounds;
            x = Math.Clamp(x, screen.Left + 4, Math.Max(screen.Left + 4, screen.Right - ww - 4));
            y = Math.Clamp(y, screen.Top + 4, Math.Max(screen.Top + 4, screen.Bottom - h - 4));
            Left = x / _scale;
            Top = y / _scale;
            Show();
            UpdateLayout();
            StartGlass();
            PlaceThumbs();
        }

        // ---- Look (the taskbar's) ----------------------------------------------------------------------

        private void ApplyLook()
        {
            int look = CustomTaskbar.Look, opacity = Math.Clamp(CustomTaskbar.Opacity, 0, 100), rgb = CustomTaskbar.BackColor;
            var color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            _card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            _card.BorderThickness = new Thickness(1);
            switch (look)
            {
                case 1:
                    _card.Background = new SolidColorBrush(color);
                    SetInk(Widgets.Lightness(color) > 0.6);
                    break;
                case 2:
                    _card.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Max(1, opacity * 255 / 100), color.R, color.G, color.B));
                    SetInk(Widgets.Lightness(color) > 0.6 && opacity >= 50);
                    break;
                default:   // glass; dark gray until it's ready (or with the glass turned off)
                    _card.Background = new SolidColorBrush(Color.FromArgb(0xE8, 0x24, 0x25, 0x29));
                    SetInk(false);
                    break;
            }
        }

        private void SetInk(bool lightBehind)
        {
            var c = lightBehind ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xF5, 0xF5, 0xF7);
            _ink.Color = c;
            _hover.Color = Color.FromArgb(0x22, c.R, c.G, c.B);
            _activeBack.Color = Color.FromArgb(0x14, c.R, c.G, c.B);
        }

        private void StartGlass()
        {
            if (_closed || CustomTaskbar.Look != 0 || !GlassSettings.Taskbar.On) return;
            try
            {
                // It floats over windows: the glass shows what's on the screen behind it.
                var glass = new LiquidGlass(this, _host, _card, new Thickness(0), 10, GlassSettings.Taskbar, tint: CustomTaskbar.GlassTint());
                if (!glass.Available) return;
                _glass = glass;
                glass.LightBehindChanged += light => SetInk(light);
                glass.Start();
            }
            catch (Exception ex) { CrashLog.Write("taskbar preview glass", ex); }
        }

        // ---- Cells ------------------------------------------------------------------------------------

        private FrameworkElement Cell(TaskbarApps.Win w, bool picture)
        {
            var title = new TextBlock
            {
                Text = w.Title, FontSize = 12, Foreground = _ink, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0),
            };
            var x = new TextBlock
            {
                Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10,
                Foreground = _ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            var close = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(4), Background = Brushes.Transparent, Child = x };
            close.MouseEnter += (_, _) => { close.Background = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)); x.Foreground = Brushes.White; };
            close.MouseLeave += (_, _) => { close.Background = Brushes.Transparent; x.Foreground = _ink; };
            close.MouseLeftButtonUp += (_, e) => { e.Handled = true; TaskbarApps.Close(w.Hwnd); HideNow(); };
            var head = new DockPanel { Height = 26 };
            DockPanel.SetDock(close, Dock.Right);
            head.Children.Add(close);
            head.Children.Add(title);

            var stack = new StackPanel();
            stack.Children.Add(head);
            if (picture)
            {
                // The picture's window, and its size (keeping the window's shape).
                var thumbHost = new ThumbHost(_hwnd);
                double pw = 200, ph = 120;
                IntPtr thumb = IntPtr.Zero;
                if (DwmRegisterThumbnail(thumbHost.Handle, w.Hwnd, out thumb) == 0)
                {
                    if (DwmQueryThumbnailSourceSize(thumb, out SIZE s) == 0 && s.cx > 0 && s.cy > 0)
                    {
                        double aspect = (double)s.cx / s.cy;
                        if (aspect >= pw / ph) ph = Math.Max(40, pw / aspect); else pw = Math.Max(60, ph * aspect);
                    }
                }
                else thumb = IntPtr.Zero;
                var spot = new Border { Width = pw, Height = ph, Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
                stack.Children.Add(new Border { Width = 204, Height = 124, Child = spot });
                _thumbs.Add((spot, thumbHost, thumb));
            }
            else title.MaxWidth = 300;

            var cell = new Border
            {
                Child = stack, Padding = new Thickness(6), CornerRadius = new CornerRadius(6),
                Width = picture ? 220 : double.NaN, MinWidth = picture ? 0 : 220,
            };
            Brush rest = w.Hwnd == TaskbarApps.Foreground() ? _activeBack : Brushes.Transparent;
            cell.Background = rest;
            cell.MouseEnter += (_, _) => cell.Background = _hover;
            cell.MouseLeave += (_, _) => cell.Background = rest;
            cell.MouseLeftButtonUp += (_, e) => { e.Handled = true; TaskbarBar.Flashing.Remove(w.Hwnd); TaskbarApps.Activate(w.Hwnd); HideNow(); };
            cell.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { TaskbarApps.Close(w.Hwnd); HideNow(); } };
            return cell;
        }

        /// <summary>Puts each picture's window exactly over its spot and shows the picture there.</summary>
        private void PlaceThumbs()
        {
            foreach (var (spot, host, thumb) in _thumbs)
            {
                if (thumb == IntPtr.Zero) continue;
                // Real screen corners (right even when the layout is mirrored).
                var a = spot.PointToScreen(new Point(0, 0));
                var z = spot.PointToScreen(new Point(spot.ActualWidth, spot.ActualHeight));
                int l = (int)Math.Round(Math.Min(a.X, z.X)), t = (int)Math.Round(Math.Min(a.Y, z.Y));
                int w = (int)Math.Round(Math.Abs(z.X - a.X)), h = (int)Math.Round(Math.Abs(z.Y - a.Y));
                host.ShowAt(l, t, w, h);
                var props = new DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
                    rcDestination = new RECTI { Left = 0, Top = 0, Right = w, Bottom = h }, opacity = 255, fVisible = true, fSourceClientAreaOnly = false,
                };
                DwmUpdateThumbnailProperties(thumb, ref props);
            }
        }

        private void CloseIt()
        {
            if (_closed) return;
            _closed = true;
            _glass?.Dispose();
            _glass = null;
            foreach (var (_, host, thumb) in _thumbs)
            {
                if (thumb != IntPtr.Zero) DwmUnregisterThumbnail(thumb);
                host.CloseIt();
            }
            _thumbs.Clear();
            TaskbarApps.Ours.Remove(_hwnd);
            try { Close(); } catch { }
        }

        /// <summary>
        /// A small plain window that only carries one live picture. It stays over the panel
        /// (owned by it) and lets the mouse through to it, so the panel's hover and clicks work.
        /// </summary>
        private sealed class ThumbHost
        {
            private readonly HwndSource _src;
            public IntPtr Handle => _src.Handle;

            public ThumbHost(IntPtr owner)
            {
                var p = new HwndSourceParameters("Wibn preview picture")
                {
                    WindowStyle = unchecked((int)0x80000000) /* WS_POPUP */,
                    ExtendedWindowStyle = 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */ | 0x8 /* TOPMOST */,
                    ParentWindow = IntPtr.Zero, Width = 1, Height = 1,
                };
                _src = new HwndSource(p);
                _src.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
                {
                    if (msg == 0x84 /* WM_NCHITTEST */) { handled = true; return new IntPtr(-1); }   // HTTRANSPARENT: to the panel below
                    if (msg == 0x21 /* WM_MOUSEACTIVATE */) { handled = true; return (IntPtr)3; }
                    return IntPtr.Zero;
                });
                SetWindowLongPtr(_src.Handle, -8 /* GWLP_HWNDPARENT: owner */, owner);
                TaskbarApps.Ours.Add(_src.Handle);
            }

            public void ShowAt(int x, int y, int w, int h) =>
                SetWindowPos(_src.Handle, new IntPtr(-1) /* TOPMOST */, x, y, w, h, 0x0010 /* NOACTIVATE */ | 0x0040 /* SHOWWINDOW */);

            public void CloseIt()
            {
                TaskbarApps.Ours.Remove(_src.Handle);
                try { _src.Dispose(); } catch { }
            }
        }

        // ---- Native ---------------------------------------------------------------------------------

        private const int DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_OPACITY = 0x4, DWM_TNP_VISIBLE = 0x8, DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

        [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] private struct RECTI { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DWM_THUMBNAIL_PROPERTIES
        {
            public int dwFlags;
            public RECTI rcDestination, rcSource;
            public byte opacity;
            [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
            [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
        }

        [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
        [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    }
}
