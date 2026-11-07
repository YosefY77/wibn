using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace WinPlus
{
    /// <summary>
    /// Settings page of the R (resize), C (compress) and Z (archive) menus: how to use it, its
    /// on/off switch and what it can do. Built in code (the two pages only differ
    /// in their text).
    /// </summary>
    internal sealed class ToolPage : StackPanel
    {
        public ToolPage(MediaTools.Mode mode)
        {
            bool resize = mode == MediaTools.Mode.Resize;
            bool archive = mode == MediaTools.Mode.Archive;
            string key = resize ? "R" : archive ? "Z" : "C";

            // How to use it
            var hero = new DockPanel();
            var badge = new Border
            {
                Width = 64, Height = 64, CornerRadius = new CornerRadius(32), Margin = new Thickness(0, 0, 16, 0),
                Background = new SolidColorBrush(Color.FromArgb(0x1F, 0x0A, 0x84, 0xFF)),
                Child = new TextBlock
                {
                    Text = key, FontSize = 26, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            DockPanel.SetDock(badge, Dock.Left);
            hero.Children.Add(badge);
            var heroText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            heroText.Children.Add(new TextBlock { Text = resize ? Lang.T("اسحب صورة أو مقطع واضغط R") : archive ? Lang.T("اسحب ملفات أو مجلد واضغط Z") : Lang.T("اسحب ملف واضغط C"), FontSize = 17, FontWeight = FontWeights.SemiBold });
            heroText.Children.Add(new TextBlock
            {
                Foreground = (Brush)Application.Current.FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
                Text = archive
                    ? Lang.T("وأنت تسحب من المستكشف أو سطح المكتب، اضغط Z وتطلع قائمة دائرية تحت الماوس. أفلت الملفات على الصيغة اللي تبيها ويطلع ملف مضغوط واحد جنبها، والأصل يبقى مثل ما هو. Z مرة ثانية أو Esc يسكّرها.")
                    : resize
                    ? Lang.T("وأنت تسحب من المستكشف أو سطح المكتب، اضغط R وتطلع قائمة دائرية تحت الماوس. أفلت الملف على المقاس اللي تبيه والملف نفسه يتغير. R مرة ثانية أو Esc يسكّرها. الأصل ينحفظ 30 يوم عشان ترجع له.")
                    : Lang.T("وأنت تسحب من المستكشف أو سطح المكتب، اضغط C وتطلع قائمة دائرية تحت الماوس. أفلت الملف على المستوى اللي تبيه والملف نفسه يصغر. C مرة ثانية أو Esc يسكّرها. لو النتيجة مو أصغر يبقى الملف مثل ما هو، والأصل ينحفظ 30 يوم."),
            });
            hero.Children.Add(heroText);
            Children.Add(new Border
            {
                Background = (Brush)Application.Current.FindResource("RowBg"), BorderBrush = (Brush)Application.Current.FindResource("Line"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(20),
                Margin = new Thickness(0, 0, 0, 12), Child = hero,
            });

            // On / off
            var box = new CheckBox { Style = (Style)Application.Current.FindResource("Switch"), IsChecked = DragSpaceHook.IsKeyOn(mode, ignoreFeature: true) };
            box.Click += (_, _) => DragSpaceHook.SetKeyOn(mode, box.IsChecked == true);
            Children.Add(Row(box, "", Lang.F("القائمة مع {0}", key), Lang.T("تشتغل بس وأنت تسحب ملف، وما تأثر على الكتابة")));

            // What it does
            Children.Add(new TextBlock { Text = resize ? Lang.T("المقاسات") : archive ? Lang.T("الصيغ") : Lang.T("المستويات"), Style = (Style)Application.Current.FindResource("Section"), FontSize = 18, Margin = new Thickness(2, 24, 0, 8) });
            var grid = new UniformGrid { Columns = 2, Margin = new Thickness(-3, 0, -3, 0) };
            if (archive)
            {
                grid.Children.Add(Card("ZIP", Lang.T("ينفتح في أي جهاز بدون برامج")));
                grid.Children.Add(Card("7Z", Lang.T("أصغر حجم. أول مرة يحمّل أداة 7-Zip الرسمية (أقل من 1 ميقا)")));
                grid.Children.Add(Card("RAR", Archiver.FindRar() != null
                    ? Lang.T("عن طريق WinRAR اللي في جهازك")
                    : Lang.T("يطلع بس لو WinRAR مثبّت (ملفات RAR ما تنسوى إلا بـ WinRAR)")));
                grid.Children.Add(Card("TAR.GZ", Lang.T("للينكس والماك")));
            }
            else if (resize)
            {
                grid.Children.Add(Card(Lang.T("الصور"), Lang.T("25% · 50% · 75% · 150% · 200% · عرض 1920 / 1280 / 800 بكسل (ما يكبّر الصورة لو أصغر)")));
                grid.Children.Add(Card(Lang.T("المقاطع"), Lang.T("4K · 1440p · 1080p · 720p · 480p · 360p (ما يكبّر المقطع لو أصغر). يحتاج FFmpeg")));
            }
            else
            {
                grid.Children.Add(Card("PNG", Lang.T("بدون فقد: نفس الصورة بالضبط · عالي/متوسط/صغير: ألوان أقل")));
                grid.Children.Add(Card("JPG · WEBP · AVIF · GIF", Lang.T("عالي (ما يبان الفرق) · متوسط · صغير")));
                grid.Children.Add(Card(Lang.T("المقاطع"), Lang.T("عالي · متوسط · صغير، H.264 بكرت الشاشة. يحتاج FFmpeg")));
                grid.Children.Add(Card(Lang.T("الصوت"), Lang.T("MP3/M4A/OGG/OPUS بثلاث مستويات · WAV ← FLAC بنفس الصوت")));
                grid.Children.Add(Card(Lang.T("أي ملف ثاني"), Lang.T("ZIP جنب الملف والأصل يبقى")));
            }
            Children.Add(grid);
        }

        private static Border Row(UIElement control, string icon, string title, string desc)
        {
            DockPanel.SetDock(control, Dock.Right);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.FindResource("RowTitle") });
            text.Children.Add(new TextBlock { Text = desc, Style = (Style)Application.Current.FindResource("RowDesc") });
            var dock = new DockPanel();
            dock.Children.Add(control);
            dock.Children.Add(new TextBlock { Text = icon, Style = (Style)Application.Current.FindResource("RowIcon") });
            dock.Children.Add(text);
            return new Border { Style = (Style)Application.Current.FindResource("Row"), Child = dock };
        }

        private static Border Card(string title, string desc)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.FindResource("RowTitle"), FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = desc, Style = (Style)Application.Current.FindResource("RowDesc") });
            return new Border { Style = (Style)Application.Current.FindResource("Row"), Margin = new Thickness(3, 0, 3, 6), Child = text };
        }
    }
}
