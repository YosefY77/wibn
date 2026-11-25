using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace WinPlus
{
    /// <summary>
    /// While Wibn's taskbar is dragged: a see-through highlight on the screen edge it would go to.
    /// It never takes the mouse or the focus.
    /// </summary>
    internal sealed class EdgePreview : Window
    {
        private readonly Border _box;
        private IntPtr _hwnd;

        public EdgePreview()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            _box = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(2), Margin = new Thickness(4) };
            Content = _box;
            _hwnd = new WindowInteropHelper(this).EnsureHandle();
            long ex = GetWindowLongPtr(_hwnd, -20).ToInt64();
            SetWindowLongPtr(_hwnd, -20, new IntPtr(ex | 0x80 /* TOOLWINDOW */ | 0x20 /* TRANSPARENT */ | 0x08000000 /* NOACTIVATE */));
        }

        /// <summary>Shows it over this strip (screen pixels).</summary>
        public void ShowAt(CustomTaskbar.RECT r, Color accent)
        {
            _box.Background = new SolidColorBrush(Color.FromArgb(0x50, accent.R, accent.G, accent.B));
            _box.BorderBrush = new SolidColorBrush(Color.FromArgb(0xD0, accent.R, accent.G, accent.B));
            if (!IsVisible) Show();
            SetWindowPos(_hwnd, new IntPtr(-1) /* TOPMOST */, r.Left, r.Top, r.Width, r.Height, 0x0010 /* NOACTIVATE */);
        }

        /// <summary>The edge of the screen under this point that's nearest to it, and the bar's strip there
        /// (0 = bottom, 1 = top, 2 = left, 3 = right, like <see cref="CustomTaskbar.Edge"/>).</summary>
        public static (int Edge, CustomTaskbar.RECT Strip) NearestEdge(int x, int y, IntPtr barWindow)
        {
            IntPtr mon = MonitorFromPoint(new POINT { X = x, Y = y }, 2 /* NEAREST */);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(mon, ref mi);
            var m = mi.rcMonitor;
            // How far the mouse is from each edge, as a part of the screen's size (so a wide screen
            // doesn't make the sides harder to reach).
            double w = Math.Max(1, m.Right - m.Left), h = Math.Max(1, m.Bottom - m.Top);
            var d = new[] { (m.Bottom - y) / h, (y - m.Top) / h, (x - m.Left) / w, (m.Right - x) / w };
            int edge = 0;
            for (int i = 1; i < 4; i++) if (d[i] < d[edge]) edge = i;
            return (edge, CustomTaskbar.EdgeStrip(m, barWindow, edge));
        }

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public CustomTaskbar.RECT rcMonitor, rcWork; public uint dwFlags; }
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT p, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
        [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
        [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
