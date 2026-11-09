using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinPlus
{
    /// <summary>The Widgets page: the widgets you can add, and the ones on the desktop.</summary>
    internal sealed class WidgetsPage : StackPanel
    {
        private static Style St(string key) => (Style)Application.Current.FindResource(key);
        private readonly StackPanel _onDesktop = new();
        private readonly TextBlock _none;

        public WidgetsPage()
        {
            Children.Add(new TextBlock { Style = St("Section"), FontSize = 18, Margin = new Thickness(2, 8, 0, 8), Text = Lang.T("أضف ويدجت") });
            foreach (var kind in Widgets.Kinds)
            {
                var add = new Button { Style = St("AccentButton"), Content = Lang.T("أضف لسطح المكتب"), VerticalAlignment = VerticalAlignment.Center };
                add.Click += (_, _) => Widgets.Add(kind.Type);
                Children.Add(Row(add, kind.Icon, kind.Name, kind.Description));
            }

            Children.Add(new TextBlock { Style = St("Section"), FontSize = 18, Margin = new Thickness(2, 24, 0, 8), Text = Lang.T("على سطح المكتب") });
            _none = new TextBlock { Style = St("RowDesc"), FontSize = 13, Margin = new Thickness(2, 0, 0, 0), Text = Lang.T("ما فيه ويدجت على سطح المكتب للحين") };
            Children.Add(_none);
            Children.Add(_onDesktop);

            Children.Add(new TextBlock
            {
                Style = St("RowDesc"), Margin = new Thickness(2, 16, 0, 0),
                Text = Lang.T("اسحب الويدجت من أي مكان فاضي فيه ويتحاذى مع أيقونات سطح المكتب. اسحب زاويته تحت عشان تغيّر حجمه من 2×2 إلى 6×6 أيقونات. كليك يمين عليه للحجم أو تشيله."),
            });

            Loaded += (_, _) => { Widgets.Changed += OnChanged; Refresh(); };
            Unloaded += (_, _) => Widgets.Changed -= OnChanged;
        }

        private void OnChanged() => Dispatcher.BeginInvoke(Refresh);

        private void Refresh()
        {
            _onDesktop.Children.Clear();
            foreach (var info in Widgets.All.ToList())
            {
                var kind = Widgets.KindOf(info.Type);
                if (kind == null) continue;
                var remove = new Button { Style = St("FlatButton"), Content = Lang.T("شيله"), VerticalAlignment = VerticalAlignment.Center };
                string id = info.Id;
                remove.Click += (_, _) => Widgets.Remove(id);
                _onDesktop.Children.Add(Row(remove, kind.Icon, kind.Name, $"{info.W} × {info.H}"));
            }
            _none.Visibility = _onDesktop.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static Border Row(FrameworkElement control, string icon, string title, string desc)
        {
            var dock = new DockPanel();
            DockPanel.SetDock(control, Dock.Right);
            dock.Children.Add(control);
            var glyph = new TextBlock { Style = St("RowIcon"), Text = icon };
            DockPanel.SetDock(glyph, Dock.Left);
            dock.Children.Add(glyph);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            text.Children.Add(new TextBlock { Style = St("RowTitle"), Text = title });
            text.Children.Add(new TextBlock { Style = St("RowDesc"), Text = desc });
            dock.Children.Add(text);
            return new Border { Style = St("Row"), Margin = new Thickness(0, 0, 0, 6), Child = dock };
        }
    }
}
