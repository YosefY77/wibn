using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WinPlus
{
    /// <summary>
    /// Smooth mouse-wheel scrolling for a ScrollViewer: the wheel moves a target, and the view
    /// glides to it every frame, slowing down as it gets there. Turning the wheel again while it
    /// glides adds to the target, so fast turns go further without jumps. The scroll bar and the
    /// keyboard still move it directly.
    /// </summary>
    internal static class SmoothScroll
    {
        private const double PixelsPerNotch = 110;   // one wheel step (120)
        private const double Speed = 16;             // higher = gets there sooner

        public static void Attach(ScrollViewer viewer)
        {
            double target = 0;
            bool gliding = false;
            var clock = new Stopwatch();

            void Frame(object? sender, EventArgs e)
            {
                double dt = Math.Min(clock.Elapsed.TotalSeconds, 0.05);
                clock.Restart();
                double now = viewer.VerticalOffset;
                double gap = target - now;
                if (Math.Abs(gap) < 0.5)
                {
                    viewer.ScrollToVerticalOffset(target);
                    Stop();
                    return;
                }
                viewer.ScrollToVerticalOffset(now + gap * (1 - Math.Exp(-Speed * dt)));
            }

            void Stop()
            {
                if (!gliding) return;
                gliding = false;
                CompositionTarget.Rendering -= Frame;
            }

            viewer.PreviewMouseWheel += (_, e) =>
            {
                if (viewer.ScrollableHeight <= 0) return;
                e.Handled = true;
                if (!gliding) target = viewer.VerticalOffset;
                target = Math.Clamp(target - e.Delta / 120.0 * PixelsPerNotch, 0, viewer.ScrollableHeight);
                if (gliding) return;
                gliding = true;
                clock.Restart();
                CompositionTarget.Rendering += Frame;
            };

            // Dragged by its scroll bar, keys, or the content changed: start from where it is.
            viewer.PreviewMouseLeftButtonDown += (_, _) => Stop();
            viewer.PreviewKeyDown += (_, _) => Stop();
            viewer.Unloaded += (_, _) => Stop();
            viewer.IsVisibleChanged += (_, _) => { if (!viewer.IsVisible) Stop(); };
        }
    }
}
