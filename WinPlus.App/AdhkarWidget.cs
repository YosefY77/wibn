using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinPlus
{
    /// <summary>
    /// The adhkar widget: one dhikr with where it's from; a click (a press without moving it)
    /// shows another at random. They come in a shuffled round, so none repeats until all have
    /// shown. The text grows or shrinks to fill the widget; dragging still moves the widget.
    /// </summary>
    internal sealed class AdhkarWidget : WidgetContent
    {
        private sealed record Dhikr(string Text, string Source);

        private static readonly Dhikr[] All =
        {
            new("سبحان الله وبحمده، سبحان الله العظيم", "البخاري ومسلم"),
            new("لا إله إلا الله وحده لا شريك له، له الملك وله الحمد، وهو على كل شيء قدير", "البخاري ومسلم"),
            new("سبحان الله، والحمد لله، ولا إله إلا الله، والله أكبر", "مسلم"),
            new("لا حول ولا قوة إلا بالله", "البخاري ومسلم"),
            new("أستغفر الله وأتوب إليه", "البخاري"),
            new("اللهم صل وسلم على نبينا محمد", "من صلّى عليّ صلاة صلّى الله عليه بها عشرًا · مسلم"),
            new("سبحان الله وبحمده، عدد خلقه، ورضا نفسه، وزنة عرشه، ومداد كلماته", "مسلم"),
            new("رضيت بالله ربًا، وبالإسلام دينًا، وبمحمد ﷺ نبيًا", "أبو داود والترمذي"),
            new("حسبي الله لا إله إلا هو، عليه توكلت وهو رب العرش العظيم", "أبو داود"),
            new("اللهم إنك عفو تحب العفو فاعف عني", "الترمذي"),
            new("يا حي يا قيوم برحمتك أستغيث، أصلح لي شأني كله، ولا تكلني إلى نفسي طرفة عين", "النسائي في الكبرى والحاكم"),
            new("اللهم أعني على ذكرك وشكرك وحسن عبادتك", "أبو داود والنسائي"),
            new("لا إله إلا أنت سبحانك إني كنت من الظالمين", "الأنبياء: 87"),
            new("ربنا آتنا في الدنيا حسنة وفي الآخرة حسنة وقنا عذاب النار", "البقرة: 201"),
            new("رب اشرح لي صدري، ويسر لي أمري", "طه: 25–26"),
            new("رب زدني علمًا", "طه: 114"),
            new("اللهم إني أسألك علمًا نافعًا، ورزقًا طيبًا، وعملًا متقبلًا", "ابن ماجه"),
            new("اللهم إني أسألك العفو والعافية في الدنيا والآخرة", "ابن ماجه"),
            new("يا مقلب القلوب ثبت قلبي على دينك", "الترمذي"),
            new("اللهم أنت ربي لا إله إلا أنت، خلقتني وأنا عبدك، وأنا على عهدك ووعدك ما استطعت، أعوذ بك من شر ما صنعت، أبوء لك بنعمتك علي، وأبوء بذنبي فاغفر لي، فإنه لا يغفر الذنوب إلا أنت", "سيد الاستغفار · البخاري"),
            new("بسم الله الذي لا يضر مع اسمه شيء في الأرض ولا في السماء وهو السميع العليم", "أبو داود والترمذي"),
            new("أعوذ بكلمات الله التامات من شر ما خلق", "مسلم"),
            new("سبحان الله العظيم وبحمده", "غُرست له نخلة في الجنة · الترمذي"),
            new("الحمد لله", "الحمد لله تملأ الميزان · مسلم"),
            new("لا إله إلا الله", "أفضل الذكر لا إله إلا الله · الترمذي"),
            new("اللهم آت نفسي تقواها، وزكها أنت خير من زكاها، أنت وليها ومولاها", "مسلم"),
            new("اللهم إني أعوذ بك من الهم والحزن، والعجز والكسل، والبخل والجبن، وضلع الدين وغلبة الرجال", "البخاري"),
            new("ربنا لا تزغ قلوبنا بعد إذ هديتنا وهب لنا من لدنك رحمة إنك أنت الوهاب", "آل عمران: 8"),
            new("ربنا اغفر لي ولوالدي وللمؤمنين يوم يقوم الحساب", "إبراهيم: 41"),
            new("حسبنا الله ونعم الوكيل", "آل عمران: 173"),
        };

        private readonly SolidColorBrush _ink = new(Color.FromRgb(0xF2, 0xF3, 0xF5));
        private readonly SolidColorBrush _inkMuted = new(Color.FromRgb(0xA0, 0xA3, 0xAA));
        private readonly TextBlock _text, _source;
        private readonly StackPanel _panel;
        private readonly Random _random = new();
        private readonly Queue<int> _round = new();
        private int _shown = -1;
        private Point? _pressAt;
        private Window? _window;

        public AdhkarWidget()
        {
            _text = new TextBlock
            {
                FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = _ink, LineHeight = 34,
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FlowDirection = FlowDirection.RightToLeft,
            };
            _source = new TextBlock
            {
                FontSize = 13, Foreground = _inkMuted, Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FlowDirection = FlowDirection.RightToLeft,
            };
            // A set width, scaled as a whole to the widget: long ones get smaller, short ones bigger.
            _panel = new StackPanel { Width = 280, VerticalAlignment = VerticalAlignment.Center };
            _panel.Children.Add(_text);
            _panel.Children.Add(_source);
            Child = new Viewbox { Child = _panel, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, Margin = new Thickness(4) };
            Background = Brushes.Transparent;   // a click anywhere in it counts
            ToolTip = Lang.T("اضغط عشان يطلع ذكر ثاني");
            Cursor = Cursors.Hand;

            // A press and a let-go at the same spot = next one; a drag moves the widget as usual.
            PreviewMouseLeftButtonDown += (_, e) => _pressAt = PointToScreen(e.GetPosition(this));
            SizeChanged += (_, _) => Fit();
        }

        public override void Start()
        {
            if (_shown < 0) Show(Next(), animate: false);
            if (_window == null && Window.GetWindow(this) is { } w)
            {
                _window = w;
                // The widget's window takes the mouse while it might be dragged, so the let-go is heard there.
                w.PreviewMouseLeftButtonUp += (_, e) =>
                {
                    if (_pressAt is not { } at) return;
                    _pressAt = null;
                    var now = PointToScreen(e.GetPosition(this));
                    if (Math.Abs(now.X - at.X) < 5 && Math.Abs(now.Y - at.Y) < 5) Show(Next(), animate: true);
                };
            }
        }

        /// <summary>The line width that lets this dhikr be biggest in this widget: a long one in a
        /// wide widget gets long lines, a short one in a tall widget breaks into more lines.</summary>
        private void Fit()
        {
            double boxW = ActualWidth - 8, boxH = ActualHeight - 8;
            if (boxW < 20 || boxH < 20) return;
            double best = 0, bestWidth = _panel.Width;
            for (double w = 140; w <= 1100; w += 20)
            {
                _panel.Width = w;
                _panel.Measure(new Size(w, double.PositiveInfinity));
                double scale = Math.Min(boxW / w, boxH / Math.Max(1, _panel.DesiredSize.Height));
                if (scale > best + 0.001) { best = scale; bestWidth = w; }
            }
            _panel.Width = bestWidth;
        }

        public override void SetLightBehind(bool light)
        {
            var d = new Duration(TimeSpan.FromMilliseconds(200));
            _ink.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(light ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xF2, 0xF3, 0xF5), d));
            _inkMuted.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(light ? Color.FromArgb(0xB0, 0x3C, 0x3C, 0x43) : Color.FromRgb(0xA0, 0xA3, 0xAA), d));
        }

        public override IEnumerable<Control> MenuItems()
        {
            var next = new MenuItem { Header = Lang.T("ذكر ثاني") };
            next.Click += (_, _) => Show(Next(), animate: true);
            yield return next;
        }

        /// <summary>The next one from a shuffled round (never the one on screen right now).</summary>
        private int Next()
        {
            if (_round.Count == 0)
            {
                var order = Enumerable.Range(0, All.Length).OrderBy(_ => _random.Next()).ToList();
                if (order.Count > 1 && order[0] == _shown) (order[0], order[^1]) = (order[^1], order[0]);
                foreach (var i in order) _round.Enqueue(i);
            }
            return _round.Dequeue();
        }

        private void Show(int index, bool animate)
        {
            _shown = index;
            void Set()
            {
                _text.Text = All[index].Text;
                _source.Text = All[index].Source;
                Fit();
            }
            if (!animate) { Set(); return; }
            var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
            fadeOut.Completed += (_, _) =>
            {
                Set();
                _panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            };
            _panel.BeginAnimation(OpacityProperty, fadeOut);
        }
    }
}
