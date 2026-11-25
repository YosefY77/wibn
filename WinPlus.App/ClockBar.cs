using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace WinPlus
{
    /// <summary>
    /// The clock piece of a split taskbar when it's on another edge than the apps (e.g. the clock at
    /// the top, the apps at the bottom): a window of its own that holds the piece the taskbar built.
    /// The taskbar places it, shows and hides it with itself, and gives it its glass.
    /// </summary>
    internal sealed class ClockBar : Window
    {
        public readonly Grid Host = new();
        public IntPtr Handle { get; }
        public CustomTaskbar.RECT Placed { get; private set; }

        public ClockBar()
        {
            Title = "Wibn clock";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Background = Brushes.Transparent;
            AllowsTransparency = true;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            Content = Host;
            Handle = new WindowInteropHelper(this).EnsureHandle();
            TaskbarApps.Ours.Add(Handle);
            long ex = GetWindowLongPtr(Handle, -20).ToInt64();
            SetWindowLongPtr(Handle, -20, (IntPtr)(ex | 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */));
            int yes = 1;
            DwmSetWindowAttribute(Handle, 12 /* DWMWA_EXCLUDED_FROM_PEEK */, ref yes, 4);
            HwndSource.FromHwnd(Handle)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == 0x21 /* WM_MOUSEACTIVATE */) { handled = true; return (IntPtr)3 /* MA_NOACTIVATE */; }
                return IntPtr.Zero;
            });
            Closed += (_, _) => TaskbarApps.Ours.Remove(Handle);
        }

        /// <summary>Holds this piece (taken out of wherever it was).</summary>
        public void SetPiece(FrameworkElement piece)
        {
            if (piece.Parent is Panel p) p.Children.Remove(piece);
            Host.Children.Clear();
            piece.HorizontalAlignment = HorizontalAlignment.Stretch;
            Host.Children.Add(piece);
        }

        private int _dy = int.MinValue;   // how far it's pushed off its place (sliding with the taskbar)

        /// <summary>Puts it there (screen pixels), pushed down/up by dy while sliding, and shows it; true if its place changed.</summary>
        public bool PlaceAt(CustomTaskbar.RECT r, int dy = 0)
        {
            bool moved = !(r.Left == Placed.Left && r.Top == Placed.Top && r.Right == Placed.Right && r.Bottom == Placed.Bottom);
            Placed = r;
            bool show = !IsVisible;
            if (moved || show || dy != _dy)
                SetWindowPos(Handle, new IntPtr(-1) /* TOPMOST */, r.Left, r.Top + dy, r.Width, r.Height, 0x0010 /* NOACTIVATE */);
            _dy = dy;
            if (show)
            {
                Show();
                SetWindowPos(Handle, new IntPtr(-1), r.Left, r.Top + dy, r.Width, r.Height, 0x0010);
                moved = true;
            }
            return moved;
        }

        public void HideIt()
        {
            if (IsVisible) Hide();
            Placed = default;
            _dy = int.MinValue;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
        [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    }
}
