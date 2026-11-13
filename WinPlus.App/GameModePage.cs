using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace WinPlus
{
    /// <summary>The "Game mode" page: game booster mode's switch, what it stops, and your games.</summary>
    internal sealed class GameModePage : StackPanel
    {
        private readonly TextBlock _status;
        private readonly Button _manual;
        private readonly StackPanel _games = new();

        private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

        public GameModePage()
        {
            // ---- The switch ----
            var main = new CheckBox { Style = Res<Style>("Switch"), IsChecked = GameBoost.Enabled };
            main.Click += (_, _) =>
            {
                bool on = main.IsChecked == true;
                if (on && GameBoost.Services.Any(GameBoost.ServiceOn) && !GameBoost.TasksReady && !AskAdmin())
                {
                    main.IsChecked = false;
                    return;
                }
                GameBoost.Enabled = on;
                UpdateStatus();
            };
            Children.Add(Row(main, "", "Game Booster Mode",
                Lang.T("أول ما تشغّل لعبة يوقف خدمات الخلفية اللي تاكل من المعالج (مثل Windows Update و SysMain) ويسكّر برامج الخلفية اللي تختارها، ولما تطلع من اللعبة يرجّع كل شي لحاله")));

            _status = new TextBlock { Style = Res<Style>("RowDesc"), Margin = new Thickness(2, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
            _manual = new Button { Style = Res<Style>("FlatButton"), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            _manual.Click += (_, _) =>
            {
                if (GameBoost.ActiveGame == null && GameBoost.Services.Any(GameBoost.ServiceOn) && !GameBoost.TasksReady && !AskAdmin()) return;
                GameBoost.ToggleManual();
                UpdateStatus();
            };
            Children.Add(_status);
            Children.Add(_manual);

            // ---- Services ----
            Children.Add(Section(Lang.T("الخدمات اللي توقف أثناء اللعب")));
            var services = new UniformGrid { Columns = 2, Margin = new Thickness(-3, 0, -3, 0) };
            foreach (var s in GameBoost.Services)
            {
                var box = new CheckBox { Style = Res<Style>("Switch"), IsChecked = GameBoost.ServiceOn(s) };
                var svc = s;
                box.Click += (_, _) =>
                {
                    bool on = box.IsChecked == true;
                    if (on && GameBoost.Enabled && !GameBoost.TasksReady && !AskAdmin()) { box.IsChecked = false; return; }
                    GameBoost.SetServiceOn(svc, on);
                };
                var row = Row(box, "", s.Label, s.Description);
                row.Margin = new Thickness(3, 0, 3, 6);
                services.Children.Add(row);
            }
            Children.Add(services);
            Children.Add(Note(Lang.T("يرجّع بس الخدمات اللي هو وقفها. ويندوز ممكن يشغّل بعضها من نفسه لو احتاجها، وهذا عادي.")));

            // ---- Background apps ----
            Children.Add(Section(Lang.T("برامج الخلفية اللي تتسكّر")));
            var apps = new UniformGrid { Columns = 2, Margin = new Thickness(-3, 0, -3, 0) };
            foreach (var a in GameBoost.Apps)
            {
                var box = new CheckBox { Style = Res<Style>("Switch"), IsChecked = GameBoost.AppOn(a) };
                var app = a;
                box.Click += (_, _) => GameBoost.SetAppOn(app, box.IsChecked == true);
                var row = Row(box, "", a.Label, a.Description);
                row.Margin = new Thickness(3, 0, 3, 6);
                apps.Children.Add(row);
            }
            Children.Add(apps);

            // ---- Options ----
            Children.Add(Section(Lang.T("خيارات")));
            var priority = new CheckBox { Style = Res<Style>("Switch"), IsChecked = GameBoost.RaisePriority };
            priority.Click += (_, _) => GameBoost.RaisePriority = priority.IsChecked == true;
            Children.Add(Row(priority, "", Lang.T("أولوية أعلى للعبة"), Lang.T("ويندوز يعطي اللعبة من المعالج قبل البرامج الثانية")));
            var notify = new CheckBox { Style = Res<Style>("Switch"), IsChecked = GameBoost.Notify };
            notify.Click += (_, _) => GameBoost.Notify = notify.IsChecked == true;
            Children.Add(Row(notify, "", Lang.T("تنبيه لما يشتغل ويوقف"), Lang.T("رسالة صغيرة عند التاسك بار")));

            // ---- Games ----
            Children.Add(Section(Lang.T("ألعابك")));
            Children.Add(Note(Lang.T("يعرف لحاله ألعاب Steam و Epic و Xbox و Riot و GOG و EA و Ubisoft و Rockstar، وأي لعبة بملء الشاشة. لو لعبتك ما انعرفت، أضفها هنا.")));
            Children.Add(_games);
            var add = new Button { Style = Res<Style>("FlatButton"), Content = Lang.T("أضف لعبة…"), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            add.Click += (_, _) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = Lang.T("اختر ملف اللعبة (exe)"), Filter = Lang.T("البرامج") + " (*.exe)|*.exe", Multiselect = true };
                if (dlg.ShowDialog() != true) return;
                var list = GameBoost.MyGames;
                foreach (var f in dlg.FileNames) if (!list.Contains(f, StringComparer.OrdinalIgnoreCase)) list.Add(f);
                GameBoost.MyGames = list;
                FillGames();
            };
            Children.Add(add);
            FillGames();

            UpdateStatus();
            GameBoost.Changed += () => Dispatcher.BeginInvoke(UpdateStatus);
        }

        private static bool AskAdmin()
        {
            var answer = MessageBox.Show(
                Lang.T("عشان يوقف خدمات ويندوز يحتاج صلاحية المسؤول. ويندوز بيسألك مرة وحدة بس، وبعدها يشتغل لحاله بدون ما يسأل."),
                "Wibn", MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.OK, Lang.MsgOptions);
            if (answer != MessageBoxResult.OK) return false;
            if (GameBoost.InstallTasks()) return true;
            MessageBox.Show(Lang.T("ما انعطت الصلاحية، فما يقدر يوقف الخدمات."), "Wibn", MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK, Lang.MsgOptions);
            return false;
        }

        private void UpdateStatus()
        {
            string? game = GameBoost.ActiveGame;
            _status.Text = game != null
                ? Lang.F("شغّال الحين: {0}", game)
                : GameBoost.Enabled ? Lang.T("ينتظر لين تشغّل لعبة") : Lang.T("طافي");
            _status.Foreground = game != null ? new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59)) : Res<Brush>("Muted");
            _manual.Content = game != null ? Lang.T("وقّفه ورجّع كل شي") : Lang.T("شغّله الحين بدون لعبة");
        }

        private void FillGames()
        {
            _games.Children.Clear();
            foreach (var path in GameBoost.MyGames)
            {
                var remove = new Button { Style = Res<Style>("FlatButton"), Content = Lang.T("شيل"), Padding = new Thickness(12, 4, 12, 4) };
                string p = path;
                remove.Click += (_, _) =>
                {
                    GameBoost.MyGames = GameBoost.MyGames.Where(g => !string.Equals(g, p, StringComparison.OrdinalIgnoreCase)).ToList();
                    FillGames();
                };
                _games.Children.Add(Row(remove, "", Path.GetFileNameWithoutExtension(path), path));
            }
        }

        private static TextBlock Section(string text) =>
            new() { Text = text, Style = Res<Style>("Section"), FontSize = 18, Margin = new Thickness(2, 24, 0, 8) };

        private static TextBlock Note(string text) =>
            new() { Text = text, Style = Res<Style>("RowDesc"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 0, 8) };

        private static Border Row(UIElement control, string icon, string title, string desc)
        {
            DockPanel.SetDock(control, Dock.Right);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = title, Style = Res<Style>("RowTitle") });
            text.Children.Add(new TextBlock { Text = desc, Style = Res<Style>("RowDesc") });
            var dock = new DockPanel();
            dock.Children.Add(control);
            dock.Children.Add(new TextBlock { Text = icon, Style = Res<Style>("RowIcon") });
            dock.Children.Add(text);
            return new Border { Style = Res<Style>("Row"), Child = dock };
        }
    }
}
