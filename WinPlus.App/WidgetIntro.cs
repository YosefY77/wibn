using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinPlus
{
    /// <summary>
    /// How a widget comes onto the desktop: it shows from the top down. A clip (a rectangle that
    /// grows downward) is used rather than a fading mask: a widget's window is see-through and
    /// redrawn whole on every frame, and a clip costs far less to draw, so it stays smooth.
    /// When it's done, the clip and its animation are removed, so nothing of it stays in memory.
    /// </summary>
    internal static class WidgetIntro
    {
        private const double Wide = 100_000;   // wider than any widget: only the height matters

        /// <summary>Hidden until it plays (call before the window first shows, so it never flashes in).</summary>
        public static void Prepare(Grid root)
        {
            if (!AnimSettings.IntroOn) return;   // turned off on the Animations page: it just shows
            root.Clip = new RectangleGeometry(new Rect(0, 0, Wide, 0));
        }

        /// <summary>Plays it once (after a delay, so the widgets come one after another).</summary>
        public static void Play(Grid root, TimeSpan delay)
        {
            if (root.Clip is not RectangleGeometry clip || clip.Rect.Height != 0 || clip.Rect.Width != Wide) return;
            double h = root.ActualHeight;
            if (h <= 0) { Finish(root, clip); return; }
            var grow = new RectAnimation(new Rect(0, 0, Wide, 0), new Rect(0, 0, Wide, h), TimeSpan.FromMilliseconds(AnimSettings.IntroMs))
            {
                BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            grow.Completed += (_, _) => Finish(root, clip);
            clip.BeginAnimation(RectangleGeometry.RectProperty, grow);
        }

        /// <summary>Nothing of it stays: no clip, no running animation.</summary>
        private static void Finish(Grid root, RectangleGeometry clip)
        {
            clip.BeginAnimation(RectangleGeometry.RectProperty, null);
            if (root.Clip == clip) root.Clip = null;   // not a newer one (played again meanwhile)
        }
    }
}
