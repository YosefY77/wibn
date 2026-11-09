using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace WinPlus
{
    /// <summary>
    /// The text widget: your own words straight on the wallpaper, no card around them (its
    /// edge shows only while the mouse is over it). Double-click it (or "Write…" in its menu)
    /// to type; Esc or clicking elsewhere keeps it. From its menu: the font, the size, bold,
    /// where the lines line up and a shadow (to read it on any wallpaper); the "Color" item
    /// every widget has is the text's color here (white until you pick one).
    /// </summary>
    internal sealed class TextWidget : WidgetContent
    {
        private static readonly int[] Sizes = { 14, 18, 24, 32, 44, 60, 80, 110 };

        private readonly SolidColorBrush _color = new(Colors.White);
        private readonly TextBlock _shown;
        private readonly TextBox _editor;
        private readonly DropShadowEffect _shadow = new() { BlurRadius = 8, ShadowDepth = 1.5, Opacity = 0.75, Color = Colors.Black };
        private WidgetWindow? _window;

        public override bool Bare => true;

        public TextWidget()
        {
            _shown = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = _color, VerticalAlignment = VerticalAlignment.Center };
            _editor = new TextBox
            {
                AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
                Foreground = _color, CaretBrush = _color, VerticalAlignment = VerticalAlignment.Center,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed,
            };
            // Plain look (the app's text box style draws a box we don't want here).
            _editor.Style = null;
            _editor.Template = PlainTemplate();
            _editor.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { EndEdit(); e.Handled = true; } };
            _editor.LostKeyboardFocus += (_, _) => EndEdit();

            var grid = new Grid();
            grid.Children.Add(_shown);
            grid.Children.Add(_editor);
            Child = grid;
            Background = Brushes.Transparent;   // a double-click anywhere in it counts, not only on the letters

            MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2 && !Editing) { BeginEdit(); e.Handled = true; }
            };
        }

        public override void Start()
        {
            _window = Window.GetWindow(this) as WidgetWindow;
            if (_window != null) _window.Deactivated += (_, _) => EndEdit();
            ApplyLook();
            if (Text.Length == 0) Dispatcher.BeginInvoke(BeginEdit, System.Windows.Threading.DispatcherPriority.ApplicationIdle);   // new: straight to typing
        }

        public override void Stop() => EndEdit();

        /// <summary>The widgets' "Color": the text's color (white when none is picked).</summary>
        public override void SetAccent(Color color) =>
            _color.Color = string.IsNullOrEmpty(Info?.Color) ? Colors.White : color;

        // ---- Its options ----------------------------------------------------------------------------

        private string Opt(string key, string fallback) => Info?.Options.TryGetValue(key, out var v) == true ? v : fallback;

        private void SetOpt(string key, string value)
        {
            if (Info == null) return;
            Info.Options[key] = value;
            Widgets.Save();
            ApplyLook();
        }

        private string Text => Opt("text", "");
        private int Size => int.TryParse(Opt("size", "32"), out var s) ? s : 32;
        private bool Bold => Opt("bold", "1") == "1";
        private bool Shadow => Opt("shadow", "1") == "1";
        private string Align => Opt("align", "center");
        /// <summary>Its own font; empty = the widgets' font (from the app's Widgets page).</summary>
        private string FontName => Opt("font", "");

        private void ApplyLook()
        {
            // Its own font, or none set so it follows the widgets' font (also when that changes).
            if (FontName.Length > 0)
            {
                _shown.FontFamily = _editor.FontFamily = new FontFamily(FontName);
            }
            else
            {
                _shown.ClearValue(TextBlock.FontFamilyProperty);
                _editor.ClearValue(Control.FontFamilyProperty);
            }
            var weight = Bold ? FontWeights.Bold : FontWeights.Normal;
            // Left / Right are the line's start / end: WPF flips them in Arabic (right to left).
            var align = Align switch { "start" => TextAlignment.Left, "end" => TextAlignment.Right, _ => TextAlignment.Center };
            _editor.FontSize = Size; _editor.FontWeight = weight;
            _shown.FontSize = Size; _shown.FontWeight = weight;
            _shown.TextAlignment = _editor.TextAlignment = align;
            _shown.Text = Text;
            _shown.Effect = Shadow ? _shadow : null;
            _editor.Effect = Shadow ? _shadow : null;
            ToolTip = Text.Length == 0 ? Lang.T("دبل كليك عشان تكتب") : null;
            if (Text.Length == 0 && !Editing)
            {
                _shown.Text = Lang.T("دبل كليك عشان تكتب");
                _shown.Opacity = 0.6;
            }
            else _shown.Opacity = 1;
        }

        // ---- Typing ---------------------------------------------------------------------------------

        private bool Editing => _editor.Visibility == Visibility.Visible;

        private void BeginEdit()
        {
            if (Editing) return;
            _window ??= Window.GetWindow(this) as WidgetWindow;
            _window?.BeginTyping();
            _editor.Text = Text;
            _editor.Visibility = Visibility.Visible;
            _shown.Visibility = Visibility.Hidden;
            _editor.Focus();
            Keyboard.Focus(_editor);
            _editor.CaretIndex = _editor.Text.Length;
        }

        private void EndEdit()
        {
            if (!Editing) return;
            _editor.Visibility = Visibility.Collapsed;
            _shown.Visibility = Visibility.Visible;
            if (Info != null && _editor.Text != Text)
            {
                Info.Options["text"] = _editor.Text.TrimEnd();
                Widgets.Save();
            }
            _window?.EndTyping();
            ApplyLook();
        }

        // ---- The menu -------------------------------------------------------------------------------

        public override IEnumerable<Control> MenuItems()
        {
            var write = new MenuItem { Header = Lang.T("اكتب…") };
            write.Click += (_, _) => Dispatcher.BeginInvoke(BeginEdit);   // after the menu closes
            yield return write;

            // The font: the widgets' own font, then the nice ones on this PC.
            var fonts = new MenuItem { Header = Lang.T("الخط") };
            var same = new MenuItem { Header = Lang.T("نفس خط الويدجتات"), Tag = "" };
            same.Click += (_, _) => SetOpt("font", "");
            fonts.Items.Add(same);
            fonts.Items.Add(new Separator());
            var installed = new HashSet<string>(Fonts.SystemFontFamilies.Select(f => f.Source), StringComparer.OrdinalIgnoreCase);
            foreach (var name in WidgetsPage.Picks.Where(installed.Contains))
            {
                var n = name;
                var item = new MenuItem { Header = n, Tag = n, FontFamily = new FontFamily(n) };
                item.Click += (_, _) => SetOpt("font", n);
                fonts.Items.Add(item);
            }
            fonts.SubmenuOpened += (_, e) =>
            {
                if (e.OriginalSource != fonts) return;
                foreach (var i in fonts.Items.OfType<MenuItem>())
                    i.IsChecked = string.Equals((string)i.Tag, FontName, StringComparison.OrdinalIgnoreCase);
            };
            yield return fonts;

            var size = new MenuItem { Header = Lang.T("حجم الخط") };
            foreach (int s in Sizes)
            {
                int v = s;
                var item = new MenuItem { Header = v.ToString(), Tag = v, FlowDirection = FlowDirection.LeftToRight };
                item.Click += (_, _) => SetOpt("size", v.ToString());
                size.Items.Add(item);
            }
            size.SubmenuOpened += (_, e) =>
            {
                if (e.OriginalSource != size) return;
                foreach (var i in size.Items.OfType<MenuItem>()) i.IsChecked = (int)i.Tag == Size;
            };
            yield return size;

            var align = new MenuItem { Header = Lang.T("المحاذاة") };
            foreach (var (key, label) in new[] { ("start", "بداية السطر"), ("center", "الوسط"), ("end", "نهاية السطر") })
            {
                var k = key;
                var item = new MenuItem { Header = Lang.T(label), Tag = k };
                item.Click += (_, _) => SetOpt("align", k);
                align.Items.Add(item);
            }
            align.SubmenuOpened += (_, e) =>
            {
                if (e.OriginalSource != align) return;
                foreach (var i in align.Items.OfType<MenuItem>()) i.IsChecked = (string)i.Tag == Align;
            };
            yield return align;

            var bold = new MenuItem { Header = Lang.T("عريض"), IsCheckable = true };
            bold.Click += (_, _) => SetOpt("bold", bold.IsChecked ? "1" : "0");
            bold.Loaded += (_, _) => bold.IsChecked = Bold;
            yield return bold;

            var shadow = new MenuItem { Header = Lang.T("ظل (يبان على أي خلفية)"), IsCheckable = true };
            shadow.Click += (_, _) => SetOpt("shadow", shadow.IsChecked ? "1" : "0");
            shadow.Loaded += (_, _) => shadow.IsChecked = Shadow;
            yield return shadow;
        }

        /// <summary>A text box that's only its text and caret.</summary>
        private static ControlTemplate PlainTemplate()
        {
            var t = new ControlTemplate(typeof(TextBox));
            var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
            host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            host.SetValue(FrameworkElement.FocusVisualStyleProperty, null);
            t.VisualTree = host;
            return t;
        }
    }
}
