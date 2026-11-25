using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace WinPlus
{
    /// <summary>
    /// A small window with Wibn's color picker (and its eyedropper), in place of Windows' old
    /// color dialog: used where a color is picked from a menu (widgets).
    /// </summary>
    internal static class ColorPickerWindow
    {
        /// <summary>Asks for a color; null if cancelled.</summary>
        public static Color? Ask(Color now, IntPtr owner = default)
        {
            var picker = new ColorPicker { Width = 300, Color = 0xFF000000u | ((uint)now.R << 16) | ((uint)now.G << 8) | now.B };

            Color? result = null;
            var ok = Button(Lang.T("تمام"), primary: true);
            var cancel = Button(Lang.T("إلغاء"), primary: false);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);

            var body = new StackPanel { Margin = new Thickness(18) };
            body.Children.Add(picker);
            body.Children.Add(buttons);

            var win = new Window
            {
                Title = Lang.T("اختر لون"), SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = owner == IntPtr.Zero ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2D, 0x31)), Topmost = true, ShowInTaskbar = false,
                Content = body,
            };
            if (owner != IntPtr.Zero) new WindowInteropHelper(win).Owner = owner;
            win.SourceInitialized += (_, _) => DarkTitle(win);

            ok.MouseLeftButtonUp += (_, _) =>
            {
                uint c = picker.Color;
                result = Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c);
                win.Close();
            };
            cancel.MouseLeftButtonUp += (_, _) => win.Close();
            win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };

            win.ShowDialog();
            return result;
        }

        private static Border Button(string text, bool primary)
        {
            var back = primary ? Color.FromRgb(0x58, 0x65, 0xF2) : Color.FromRgb(0x3A, 0x3C, 0x42);
            var hover = primary ? Color.FromRgb(0x6A, 0x76, 0xF5) : Color.FromRgb(0x46, 0x48, 0x4F);
            var b = new Border
            {
                Background = new SolidColorBrush(back), CornerRadius = new CornerRadius(6), Padding = new Thickness(18, 7, 18, 7),
                Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand,
                Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 13 },
            };
            b.MouseEnter += (_, _) => b.Background = new SolidColorBrush(hover);
            b.MouseLeave += (_, _) => b.Background = new SolidColorBrush(back);
            return b;
        }

        private static void DarkTitle(Window w)
        {
            try
            {
                int on = 1;
                DwmSetWindowAttribute(new WindowInteropHelper(w).Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    }
}
