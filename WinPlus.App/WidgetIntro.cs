using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace WinPlus
{
    /// <summary>
    /// How a widget comes onto the desktop: it starts hidden and fades in (fully see-through to
    /// fully there). When it's done the animation is removed, so nothing of it stays in memory.
    /// </summary>
    internal static class WidgetIntro
    {
        // Each widget's latest intro, so an older one finishing doesn't end a newer one early.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Grid, DoubleAnimation> Latest = new();

        /// <summary>Hidden until it plays (call before the window first shows, so it never flashes in).</summary>
        public static void Prepare(Grid root)
        {
            if (!AnimSettings.IntroOn) return;   // turned off on the Animations page: it just shows
            root.BeginAnimation(UIElement.OpacityProperty, null);
            root.Opacity = 0;
        }

        /// <summary>Plays it once (after a delay, so the widgets come one after another).</summary>
        public static void Play(Grid root, TimeSpan delay)
        {
            if (root.Opacity != 0) return;   // not hidden for an intro (turned off, or already shown)
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(AnimSettings.IntroMs))
            {
                BeginTime = delay, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            Latest.AddOrUpdate(root, fade);
            fade.Completed += (_, _) =>
            {
                if (!Latest.TryGetValue(root, out var now) || now != fade) return;   // a newer intro is playing
                Latest.Remove(root);
                root.BeginAnimation(UIElement.OpacityProperty, null);   // nothing of it stays
                root.Opacity = 1;
            };
            root.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }
}
