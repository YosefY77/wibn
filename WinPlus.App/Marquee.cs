using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinPlus
{
    /// <summary>
    /// A line of text that doesn't fit is shown whole: it waits a moment, slides from its start to its end, waits,
    /// and starts again. Text that fits just sits still (no animation, nothing running).
    ///
    /// The slide happens in a left-to-right box of its own (so it never gets mirrored), and only the text inside
    /// it takes the direction of its first letter: an Arabic title starts on the right, an English one on the
    /// left, and a title with both keeps its words in their right order.
    /// </summary>
    internal sealed class Marquee : Border
    {
        private const double Speed = 32;        // DIPs a second
        private const double WaitStart = 1.6;   // seconds at the start
        private const double WaitEnd = 1.2;     // and at the end

        private readonly TextBlock _text;
        private readonly Border _strip;         // holds the text; this is what slides
        private readonly TranslateTransform _move = new();
        private readonly FlowDirection? _own;   // the text's own direction, if it had one set
        private double _running = double.NaN;  // the slide running now (from → to); NaN = still
        private double _runningTo = double.NaN;

        public Marquee(TextBlock text)
        {
            _text = text;
            _own = text.ReadLocalValue(FlowDirectionProperty) != DependencyProperty.UnsetValue ? text.FlowDirection : null;
            ClipToBounds = true;
            FlowDirection = FlowDirection.LeftToRight;   // set once: never changes, so nothing gets mirrored by surprise
            text.TextTrimming = TextTrimming.None;
            text.TextWrapping = TextWrapping.NoWrap;
            _strip = new Border { Child = text, RenderTransform = _move, FlowDirection = FlowDirection.LeftToRight };
            Child = _strip;
            IsVisibleChanged += (_, _) => { Still(); InvalidateArrange(); };
            // A new title: its direction and its slide right away. (A title as wide as the last one doesn't make WPF
            // lay this out again, so without this an Arabic title could keep an English one's direction: reversed.)
            var textProp = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
            EventHandler changed = (_, _) => TextChanged();
            Loaded += (_, _) => { textProp.AddValueChanged(text, changed); TextChanged(); };
            Unloaded += (_, _) => textProp.RemoveValueChanged(text, changed);
        }

        private void TextChanged()
        {
            SetTextFlow(TextFlow(_text.Text) ?? _own ?? Outer());
            Still();   // starts again from the new title's start
            InvalidateMeasure();
            InvalidateArrange();
        }

        protected override Size MeasureOverride(Size constraint)
        {
            _strip.Measure(new Size(double.PositiveInfinity, constraint.Height));
            var d = _strip.DesiredSize;
            return new Size(Math.Min(d.Width, constraint.Width), d.Height);
        }

        protected override Size ArrangeOverride(Size size)
        {
            double full = _strip.DesiredSize.Width;
            double over = full - size.Width;
            var outer = Outer();
            var dir = TextFlow(_text.Text) ?? _own ?? outer;
            SetTextFlow(dir);
            if (over <= 1 || !IsVisible)
            {
                // Fits: still, on the same side as the lines around it (its alignment read the way the place it's in reads).
                double x = _text.TextAlignment switch
                {
                    TextAlignment.Center => (size.Width - full) / 2,
                    TextAlignment.Right => outer == FlowDirection.RightToLeft ? 0 : size.Width - full,
                    _ => outer == FlowDirection.RightToLeft ? size.Width - full : 0,
                };
                _strip.Arrange(new Rect(Math.Max(0, x), 0, Math.Min(full, size.Width), size.Height));
                Still();
                return size;
            }
            _strip.Arrange(new Rect(0, 0, full, size.Height));
            // Right to left: its start is the text's right end, so it starts moved left and slides right.
            double from = dir == FlowDirection.RightToLeft ? -over : 0, to = dir == FlowDirection.RightToLeft ? 0 : -over;
            if (Math.Abs(from - _running) > 0.5 || Math.Abs(to - _runningTo) > 0.5) Slide(from, to, over);
            return size;
        }

        private void SetTextFlow(FlowDirection f)
        {
            if (_text.FlowDirection != f) _text.FlowDirection = f;
        }

        /// <summary>The direction of what this sits in (the bar, the widget).</summary>
        private FlowDirection Outer() =>
            (VisualTreeHelper.GetParent(this) as FrameworkElement)?.FlowDirection ?? Lang.Flow;

        private void Slide(double from, double to, double over)
        {
            _running = from; _runningTo = to;
            double go = over / Speed;
            var a = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(WaitStart))));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(WaitStart + go)),
                new SineEase { EasingMode = EasingMode.EaseInOut }));
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(WaitStart + go + WaitEnd))));
            Timeline.SetDesiredFrameRate(a, 60);
            _move.BeginAnimation(TranslateTransform.XProperty, a);
        }

        private void Still()
        {
            if (double.IsNaN(_running) && _move.X == 0) return;
            _running = _runningTo = double.NaN;
            _move.BeginAnimation(TranslateTransform.XProperty, null);
            _move.X = 0;
        }

        /// <summary>The direction of the text's first letter that has one (Arabic or Hebrew: right to left).</summary>
        private static FlowDirection? TextFlow(string s)
        {
            foreach (char c in s)
            {
                if (c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿') return FlowDirection.RightToLeft;
                if (char.IsLetter(c)) return FlowDirection.LeftToRight;
            }
            return null;
        }
    }
}
