using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using static WinPlus.HelpArt;

namespace WinPlus
{
    /// <summary>One feature in a page's help: what it does, why it's worth it, and a picture.</summary>
    internal sealed record HelpItem(string Title, string What, string Why, Func<FrameworkElement> Picture);

    /// <summary>A page's help: a line about the page, then its features.</summary>
    internal sealed record HelpPage(string Title, string Intro, HelpItem[] Items);

    /// <summary>
    /// The help behind the "!" on every page. Each text is in both languages here (not in
    /// Lang.En), since it's long and only shown in this one place.
    /// </summary>
    internal static class HelpContent
    {
        private static string L(string ar, string en) => Lang.English ? en : Lang.T(ar);   // (Arabic: in فصحى)

        private static HelpItem I(string arTitle, string enTitle, string arWhat, string enWhat, string arWhy, string enWhy, Action<HelpArt> draw)
            => new(L(arTitle, enTitle), L(arWhat, enWhat), L(arWhy, enWhy), () => { var a = new HelpArt(); draw(a); return a.Done(); });

        public static HelpPage? For(string page) => page switch
        {
            "taskbar" => Taskbar(),
            "desktop" => Desktop(),
            "big" => Big(),
            "temp" => Temp(),
            "dup" => Dup(),
            "bin" => Bin(),
            "hotkeys" => Hotkeys(),
            "ram" => Ram(),
            "lock" => Lock(),
            "startup" => Startup(),
            "downloads" => Downloads(),
            "convert" => Convert(),
            "resize" => Resize(),
            "compress" => Compress(),
            "archive" => Archive(),
            "video" => Video(),
            "capture" => Capture(),
            "volume" => Volume(),
            "specs" => Specs(),
            "game" => GameHelp(),
            "widgets" => WidgetsHelp(),
            "glass" => Glass(),
            "anim" => Animations(),
            "wallpapers" => WallpapersHelp(),
            "general" => General(),
            _ => null,
        };

        // ---- Taskbar ------------------------------------------------------------------------------

        private static HelpPage Taskbar() => new(L("التاسك بار", "Taskbar"),
            L("كل اللي تبي تغيّره في شريط المهام: شكله ولونه.",
              "Everything you'd change about the taskbar: its look and color."),
            new[]
            {
                I("تاسك بار Wibn", "Wibn taskbar",
                  "يخفي تاسك بار ويندوز ويحط بداله تاسك بار من Wibn: ستارت والبحث، برامجك المثبتة والمفتوحة (كليك يفتحها أو يصغّرها، وقف عليها بالماوس وتشوف صورة حية لنوافذها واضغط على الصورة عشان تروح لهالنافذة بالذات. ولو البرنامج فيه أكثر من نافذة، كليك عليه يطلّع كل نوافذه وكليك ثاني يخفيها كلها. وحرّك عجلة الماوس على أيقونة برنامج عشان ترفع أو توطّي صوته هو بس)، ولغة الكيبورد (كليك عليها يغيّرها)، والنت والصوت (حرّك عجلة الماوس عليه عشان ترفع أو تنزّل الصوت) والساعة. تختار شكله: كامل أو عائم (الويدجتات اللي على الأطراف تتحاذى معه، وبينه وبين الويدجتات نفس المسافة اللي بين الويدجتات) أو دوك بالنص، وسماكته، وينزل ويختفي لما تكبّر نافذة أو يكون برنامج أو لعبة بملء الشاشة (النافذة المكبّرة تاخذ الشاشة كلها) (ودّ الماوس لآخر الشاشة تحت ويطلع، وشيل الماوس عنه وينزل، ولما تضغط زر ويندوز وتنفتح قائمة ابدأ أو البحث يطلع معها، وتقدر تطفي هالشي)، والخلفية زجاج (بإعدادات صفحة Liquid Glass، يوريك اللي وراه على طول مثل الفيديو والنوافذ، وتطلع في السكرين شوت (PrintScreen وWin+Shift+S وتصوير Wibn)، وفي البث والتسجيل مثل ديسكورد وOBS تطلع مع خيار \"يظهر في البث\"، ويصير لحاله مع OBS) أو لون أو شفاف، وحجم الأيقونات ومكانها. اسحب أي برنامج عليه عشان تثبّته، واسحب المثبتة عشان ترتبها. ولو تبيه أقصر، اسحبه من طرفه اليمين أو اليسار لجوّا (الطرفين يتحركون مع بعض ويبقى بالنص)، ودبل كليك على الطرف يرجّعه بطوله الكامل. تصغيره ما يحرّك الويدجتات. وتقدر تنقله لفوق أو تحت: امسكه من مكان فاضي فيه واسحبه لفوق أو تحت، يطلع لك مكانه الجديد ملوّن، وأول ما تفلته ينتقل. النوافذ المكبّرة والويدجتات يفسحون له مكان ويتوزعون من جديد، وEsc يلغي السحب. ولو تبي الساعة واللي حولها في تاسك بار لحالها، شغّل \"الساعة في تاسك بار منفصل\" من صفحة التاسك بار: البرامج تصير في قطعة والساعة في قطعة ثانية، وكل وحدة لها زجاجها. ولو تبي الساعة فوق والبرامج تحت (أو العكس)، امسك قطعة الساعة من مكان فاضي واسحبها لفوق (تروح لنفس الجهة اللي كانت فيها آخر مرة، يمين أو يسار، وتغيّرها من كليك يمين عليها ← جهة تاسك بار الساعة)، واسحبها لنفس طرف البرامج عشان ترجع جنبها. النوافذ والويدجتات يفسحون لها مكان. وتقدر تطوّل أي قطعة: اسحب طرف قطعة البرامج (الطرفين لو هي بالنص) أو طرف قطعة الساعة الداخلي (اللي جهة النص) لبرّا عشان تطول، ولجوّا عشان تقصر، ودبل كليك على الطرف يرجّعها على قد اللي فيها. ولو الساعة في تاسك بار لحالها، اضغط Shift وإنت تسحب طرفها، ويتوازن مع أقرب ويدجت فوقه (أو تحته) ويصيرون بنفس العرض. وكليك يمين على مكان فاضي في التاسك بار يطلّع القائمة عند الماوس. ولو طوّلت قطعة الساعة، كليك يمين عليها ← مكان الساعة في التاسك بار عشان تختار يمين أو يسار أو الوسط. وأيقونات البرامج اللي جنب الساعة (ديسكورد، ستيم…) تطلع فيه نفسه: تختار من صفحة التاسك بار أيها يبان، والباقي تحت السهم. كليك وكليك يمين عليها مثل ويندوز بالضبط.",
                  "Hides Windows' taskbar and puts Wibn's in its place: Start and search, your pinned and open apps (click to open or minimize; hover to see live pictures of their windows and click a picture to go to that window. An app with several windows: a click brings them all up, another click minimizes them all. Scroll the mouse wheel on an app's icon to turn just that app's volume up or down), the keyboard language (click it to switch), network and sound (scroll the mouse wheel on it to change the volume) and the clock. Pick its shape: full, floating (the widgets at the far sides line up with it, and it sits as far from the widgets as they are from each other) or a centered dock, and its thickness; it slides down out of sight when a window is maximized (it then takes the whole screen) or an app or game is full screen (move the mouse to the bottom edge to slide it up, move away and it slides down; it also comes up when you press the Windows key and Start or search opens; and you can turn this off); a glass (with the Liquid Glass page's settings; it shows what's behind it live, videos and windows too, and the bars show in screenshots (PrintScreen, Win+Shift+S, Wibn's capture), and in streams and recordings like Discord and OBS with \"Show in streams\", which turns on by itself with OBS), color or see-through background; and the icons' size and position. Drag any app onto it to pin it, and drag pinned ones to reorder them. To make it shorter, drag its right or left end inward (both ends move together and it stays centered); double-click an end to make it full length again. Shortening it doesn't move the widgets. You can also move it to the top or bottom: hold an empty spot on it and drag it up or down; its new place lights up, and it moves there when you let go. Maximized windows and the widgets make room for it and spread out again; Esc cancels the drag. To have the clock and what's around it on a taskbar of their own, turn on \"Clock on a separate taskbar\" on the Taskbar page: the apps go on one piece and the clock on another, each with its own glass. To have the clock at the top and the apps at the bottom (or the other way round), hold an empty spot of the clock's piece and drag it up (it goes to the same side, right or left, it was on last time; change it with right-click → Clock bar side); drag it to the apps' edge to put it back beside them. Windows and widgets make room for it. You can make either piece longer: drag the end of the apps' piece (either end when it's in the middle) or the inner end of the clock's piece (the one toward the middle) outward to lengthen it, inward to shorten it; double-click the end to make it just as long as what's on it again. With the clock on a bar of its own, hold Shift while dragging its end and it lines up with the nearest widget over (or under) it, so they're just as wide. Right-clicking an empty spot of the taskbar opens its menu right at the mouse. If you lengthen the clock's piece, right-click it → Clock position in its bar to pick right, left or middle. The icons next to the clock (Discord, Steam…) show on it too: choose on the Taskbar page which ones show, the rest go under the arrow. Click and right-click them just like on Windows.",
                  "تاسك بار على ذوقك بدون ما تلمس ويندوز. لو صار أي خطأ أو تسكّر Wibn، تاسك بار ويندوز يرجع لحاله.",
                  "A taskbar to your taste without touching Windows. If anything goes wrong or Wibn closes, Windows' taskbar comes right back.",
                  a =>
                  {
                      a.Screen();
                      a.Window(30, 12, 200, 88, "Wibn");
                      a.Rect(50, H - 40, 160, 30, Panel, 10, 0xE8);
                      a.Rect(62, H - 32, 6, 6, Blue, 1.5); a.Rect(69, H - 32, 6, 6, Blue, 1.5);
                      a.Rect(62, H - 25, 6, 6, Blue, 1.5); a.Rect(69, H - 25, 6, 6, Blue, 1.5);
                      for (int i = 0; i < 4; i++) a.Rect(86 + i * 22, H - 33, 15, 15, Apps[i], 4);
                      a.Rect(91, H - 15, 5, 2, Muted, 1);
                      a.Rect(109, H - 15, 12, 2, Blue, 1);
                      a.Text(174, H - 32, "10:42", 9, Ink);
                  }),
                I("تثبيت التاسك بارات", "Lock the taskbars",
                  "كليك يمين على أي تاسك بار (تاسك بار Wibn، قطعة الساعة، أو أي تاسك بار إضافي) واختر \"تثبيت\": كل التاسك بارات تثبت بمكانها وطولها. السحب ما ينقلها لطرف ثاني ولا يقصّرها ولا يطوّلها، وخيارات المكان في القائمة تتعطل. ترجع تقدر تحركها لما تشيل علامة \"تثبيت\" من نفس القائمة.",
                  "Right-click any taskbar (Wibn's taskbar, the clock's piece, or any extra bar) and pick \"Lock\": every bar stays in its place and at its length. Dragging won't move them to another edge or make them shorter or longer, and the menu's place options are turned off. Untick \"Lock\" in the same menu to move them again.",
                  "ما تتحرك بالغلط وإنت تسحب شي أو تضغط بسرعة.",
                  "They never move by accident while you drag something or click in a hurry.",
                  a =>
                  {
                      a.Screen();
                      a.Rect(30, H - 40, 200, 30, Panel, 10, 0xE8);
                      for (int i = 0; i < 4; i++) a.Rect(70 + i * 22, H - 33, 15, 15, Apps[i], 4);
                      a.Rect(150, 40, 90, 26, Panel, 6);
                      a.Glyph(162, 53, "\uE72E", 12, Blue);
                      a.Text(176, 45, "Lock", 10, Ink);
                  }),
                I("تاسك بار المعلومات (إضافي)", "Info bar (extra)",
                  "شريط إضافي تشغّله من صفحة التاسك بار ← تاسك بار المعلومات، وتحطه يمين أو يسار أو فوق أو تحت على أي شاشة. يعرض الساعة والتاريخ (والهجري لو تبي)، وكم باقي على الصلاة الجاية، واستهلاك المعالج وكرت الشاشة وحرارتهم، والرام، وسرعة النت ومساحة القرص. شغّل أو طفّ أي معلومة من الصفحة أو بكليك يمين على الشريط. شكله نفس تاسك بار Wibn بالضبط (الشكل والخلفية واللون والسماكة من إعداداته). اسحب طرفه عشان تقصّره أو تطوّله، ودبل كليك على الطرف يرجّعه كامل. وعشان تنقله: اضغط عليه واسحبه للطرف اللي تبيه، ويطلع لك وين بينحط. ولو الطرف فيه قطعة من تاسك بار Wibn (مثل الساعة لحالها فوق يمين) ينحط جنبها على نفس الخط، في الجهة اللي تفلته فيها (يسار أو يمين)، ويطلع وينزل معها. وتقدر تختار جهته (يسار أو يمين) وجهة تاسك بار الساعة من الصفحة أو بكليك يمين عليهم، ولو حطيتهم الثنين بنفس الجهة يجون جنب بعض. النوافذ المكبّرة توقف عنده وما تغطيه، وينزلق لبرّا مع الألعاب والمقاطع بملء الشاشة، وودّ الماوس لطرف الشاشة ويرجع. حرارة كرت الشاشة من ويندوز نفسه؛ أما حرارة المعالج فأغلب الأجهزة المكتبية ما تعطيها لويندوز، فشغّل برنامج LibreHardwareMonitor (مجاني) وتطلع لحالها. وتقدر تضيف تاسك بارات إضافية بالعدد اللي تبيه (من الصفحة ← أضف تاسك بار، أو كليك يمين على الشريط)، كل واحد بمكانه وباللي يعرضه، بشرط تبقى نص الشاشة للنوافذ (واللي جنب بعض على نفس الخط ما ياخذون منها)، وتبدّل ترتيبهم بالأسهم في الصفحة أو بكليك يمين ← مكانه ← قدّمه أو أخّره (اللي قبل يصير أقرب لطرف الشاشة)؛ ولو ما في مكان يقولك. وتقدر تحط فيها الويدجتس (الطقس، اللي يشتغل الحين، الأصوات، التقويم وغيرها): كل ويدجت تطلع على الشريط نفسه بشكل صغير يناسب سماكته (مثل الحرارة، أو الأغنية مع أزرار التشغيل، أو أيقونات الاختصارات) وتتحدث لحالها، وكليك يمين عليها لخياراتها ولونها (مثل لون موجات الصوت، أو نفس لون كتابة التاسك بار).",
                  "An extra bar you turn on from the Taskbar page → Info bar, and put on the right, left, top or bottom of any screen. It shows the time and date (and the Hijri date if you like), the countdown to the next prayer, the processor's and graphics card's use and temperature, memory, network speed and disk space. Turn any of them on or off on the page or by right-clicking the bar. It looks exactly like Wibn's taskbar (shape, background, color and thickness from its settings). Drag its end to make it shorter or longer; double-click the end for full length. To move it: press on it and drag it to the edge you want; it shows where it'll go. If that edge has a piece of Wibn's taskbar (like the clock on its own at the top right), it sits beside it on the same line, on the side you drop it (left or right), and comes and goes with it. You can pick its side (left or right) and the clock bar's side on the page or by right-clicking them; put both on the same side and they sit next to each other. Maximized windows stop at it instead of covering it, and it slides away for full-screen games and videos; move the mouse to that screen edge and it comes back. The graphics card's temperature comes from Windows itself; most desktops don't give Windows the processor's, so run LibreHardwareMonitor (free) and it shows up by itself. You can add as many extra bars as you like (on the page → Add a taskbar, or right-click a bar), each with its own place and what it shows, as long as half the screen is left for windows (bars side by side on one line take none of it), and change their order with the arrows on the page or right-click → Position → Move earlier or Move later (an earlier one sits nearer the screen edge); if there's no room it tells you. You can also put widgets on them (weather, now playing, volume, calendar and more): each one shows on the bar itself in a small form that fits its thickness (the temperature, the song with play buttons, your shortcuts' icons…) and keeps itself up to date; right-click it for its options and its color (like the sound waves' color, or the same as the taskbar text).",
                  "كل اللي تبي تراقبه قدامك على طول، بدون ما تفتح إدارة المهام أو ودجت، وفي المكان الفاضي اللي يناسبك بشاشتك.",
                  "Everything you want to keep an eye on, always in front of you, without opening Task Manager or a widget, in whatever free spot suits your screen.",
                  a =>
                  {
                      a.Screen();
                      a.Window(20, 14, 170, 90, "Wibn");
                      a.Rect(W - 52, 6, 44, H - 12, Panel, 8, 0xE8);
                      a.Center(W - 30, 18, "10:42", 8, Ink);
                      a.Center(W - 30, 38, "0:42:10", 7, Yellow);
                      a.Center(W - 30, 58, "CPU", 6, Muted);
                      a.Center(W - 30, 68, "54°", 8, Ink);
                      a.Center(W - 30, 88, "GPU", 6, Muted);
                      a.Center(W - 30, 98, "61°", 8, Ink);
                      a.Center(W - 30, 118, "RAM", 6, Muted);
                      a.Center(W - 30, 128, "52%", 8, Ink);
                  }),
                I("شكل زر ستارت", "Start button look",
                  "في تاسك بار Wibn تقدر تغيّر زر ويندوز (ستارت): من صفحة التاسك بار ← شكل زر ويندوز (ستارت) اختر شعار ويندوز أو شعار Wibn أو وحدة من الأيقونات (بيت، نجمة، قلب، يد ألعاب…)، أو اضغط \"صورة من جهازك\" وحط أي صورة (PNG أو JPG أو ICO…) وتصير هي الزر. وغيّر لون الأيقونة من منتقي الألوان، و\"اللون الأصلي\" يرجّع أزرق ويندوز. الصور تبقى بألوانها، وWibn ياخذ منها نسخة، فلو حذفت الأصلية ما يتأثر الزر.",
                  "On Wibn's taskbar you can change the Windows (Start) button: on the Taskbar page → Windows button (Start) look, pick Windows' logo, Wibn's logo or one of the icons (home, star, heart, game controller…), or press \"Picture from your PC\" and choose any picture (PNG, JPG, ICO…) to make it the button. Change the icon's color with the color picker; \"Original color\" brings back Windows' blue. Pictures keep their own colors, and Wibn keeps a copy, so deleting the original doesn't affect the button.",
                  "زر ستارت على ذوقك، يمشي مع لون التاسك بار أو صورتك المفضلة.",
                  "A Start button to your taste, matching your taskbar's color or a favorite picture.",
                  a =>
                  {
                      a.Screen();
                      a.Rect(30, H - 40, 200, 30, Panel, 10, 0xE8);
                      a.Glyph(50, H - 25, "", 16, Red);
                      for (int i = 0; i < 4; i++) a.Rect(72 + i * 22, H - 33, 15, 15, Apps[i], 4);
                      double[] xs = { 40, 76, 112, 148 };
                      string[] gl = { "", "", "", "" };
                      Color[] cs = { Blue, Yellow, Red, Purple };
                      for (int i = 0; i < 4; i++)
                      {
                          a.Rect(xs[i] + 30, 30, 28, 28, Panel, 6);
                          a.Glyph(xs[i] + 44, 44, gl[i], 15, cs[i]);
                      }
                      a.Rect(xs[2] + 29, 29, 30, 30, Red, 7, 0x40);
                      a.Cursor(xs[2] + 48, 50);
                  }),
                I("لاقط الألوان", "Eyedropper",
                  "في كل مكان فيه اختيار لون في Wibn (لون التاسك بار، زر ستارت، الويدجتات، الماوس…) فيه زر لاقط جنب كود اللون. اضغطه وودّ الماوس على أي شي في الشاشة: تطلع عدسة مكبّرة فيها اللون اللي تحت الماوس وكوده. كليك ياخذ اللون، وEsc أو كليك يمين يلغي.",
                  "Everywhere you pick a color in Wibn (taskbar color, Start button, widgets, cursor…) there's an eyedropper button next to the color code. Press it and move the mouse over anything on the screen: a magnifier shows the color under the mouse and its code. Click to take the color; Esc or a right click cancels.",
                  "تاخذ نفس لون خلفيتك أو أي برنامج بالضبط، بدون ما تدوّر عليه بالعين.",
                  "Match your wallpaper or any app's color exactly, without hunting for it by eye.",
                  a =>
                  {
                      a.Screen();
                      a.Rect(30, 24, 90, 90, Purple, 10);
                      a.Rect(150, 30, 90, 70, Panel, 10, 0xF0);
                      a.Rect(162, 42, 40, 40, Purple, 6);
                      a.Text(162, 86, "#8E5CF6", 9, Ink);
                      a.Cursor(76, 70);
                  }),
                I("أيقونات بيضاء وعلامة الرسائل", "White icons and unread badge",
                  "من صفحة التاسك بار شغّل \"أيقونات بيضاء\" وتصير كل أيقونات تاسك بار Wibn (البرامج واللي جنب الساعة) بالأبيض وشفافة شوي، والأجزاء الفاتحة فيها تبقى أوضح. طفّه وترجع ألوانها. وأي برنامج عنده رسائل ما قريتها (ديسكورد، المتصفح، واتساب…) يطلع على أيقونته نقطة حمرا، ولو فيه عدد يطلع الرقم. Wibn ياخذها من اسم نافذة البرنامج مثل \"(3) Discord\". ولما تضغط على برنامج، نافذته تطلع فوق كل النوافذ.",
                  "On the Taskbar page turn on \"White icons\" and every icon on Wibn's taskbar (apps and the ones next to the clock) turns white and a little see-through, with their bright parts clearer. Turn it off to bring their colors back. Any app with unread messages (Discord, your browser, WhatsApp…) gets a red dot on its icon, with the number when there is one. Wibn reads it from the app's window name, like \"(3) Discord\". And clicking an app brings its window above all the others.",
                  "تاسك بار هادي بلون واحد، وما يفوتك أي إشعار.",
                  "A calm one-color taskbar, and you never miss a message.",
                  a =>
                  {
                      a.Screen();
                      a.Rect(30, H - 40, 200, 30, Panel, 10, 0xE8);
                      for (int i = 0; i < 5; i++) a.Rect(56 + i * 26, H - 33, 15, 15, Colors.White, 4, 0xC0);
                      a.Rect(119, H - 37, 8, 8, Red, 4);
                  }),
                I("تعرف النافذة قبل تضغط", "Spot the window before you click",
                  "وقف بالماوس على أيقونة برنامج في تاسك بار Wibn وتطلع صور نوافذه، وخلّ الماوس على صورة نافذة (أو اسمها) لحظة: تطلع النافذة نفسها بمكانها وبحجمها على الشاشة، شفافة ومنوّرة ووراها ضباب، عشان تعرف أي وحدة هي. شيل الماوس عن الصورة وتختفي، واضغط عليها وتروح لها. ولو النافذة مصغّرة تطلع في المكان اللي بترجع له، ولو هي ظاهرة قدامك أصلاً ما تطلع. تطفيها من صفحة التاسك بار ← \"تعرف النافذة قبل تضغط\".",
                  "Hover an app's icon on Wibn's taskbar to see its windows' pictures, then keep the mouse on one picture (or its name) for a moment: the window itself shows in its own place and size on the screen, see-through and lit up over a blur, so you know which one it is. Move off the picture and it goes; click it to go to that window. A minimized window shows where it'll come back to; one that's already in plain sight doesn't show. Turn it off on the Taskbar page → \"Spot the window before you click\".",
                  "لما يكون عندك كذا نافذة لنفس البرنامج، تعرف اللي تبيها بدون ما تفتحهم وحدة وحدة.",
                  "With several windows of the same app, you find the one you want without opening them one by one.",
                  a =>
                  {
                      a.Screen();
                      a.Rect(40, 14, 180, 80, Colors.White, 8, 0x40);
                      a.Rect(40, 14, 180, 12, Colors.White, 8, 0x60);
                      a.Rect(90, H - 78, 80, 40, Panel, 8, 0xF0);
                      a.Rect(98, H - 72, 64, 28, Blue, 4, 0xC0);
                      a.Rect(30, H - 32, 200, 26, Panel, 10, 0xE8);
                      for (int i = 0; i < 4; i++) a.Rect(76 + i * 26, H - 27, 15, 15, Apps[i], 4);
                  }),
                I("تكبير الأيقونات تحت الماوس", "Magnify under the mouse",
                  "الأيقونة اللي تحت الماوس تكبر، واللي جنبها تكبر شوي.",
                  "The icon under the mouse grows, and its neighbors a little.",
                  "تعرف وين بتضغط بالضبط، والشريط يصير حي وممتع.",
                  "You see exactly what you're about to click, and the bar feels alive.",
                  a =>
                  {
                      a.Screen();
                      a.Rect(0, H - 26, W, 26, Panel, 0);
                      double[] sizes = { 14, 14, 18, 28, 18, 14, 14 };
                      double x = 40;
                      for (int i = 0; i < sizes.Length; i++)
                      {
                          a.Rect(x, H - 6 - sizes[i], sizes[i], sizes[i], Apps[i], 4);
                          x += sizes[i] + 8;
                      }
                      a.Cursor(118, H - 20);
                  }),
                I("اللون والزجاج", "Color and glass",
                  "تعطي الشريط لون بالضبط اللي تختاره، أو تخليه زجاجي يبين اللي وراه مغبّش (ياخذ صورة اللي وراه مرة، ويحدّثها بس بعد ما تحرّك نافذة أو تكبّرها أو تصغّرها، عشان ما يثقّل الجهاز؛ ولحظة التصغير والتكبير يبين الخلفية بس عشان ما يتلوّن من النافذة)، أو شفاف.",
                  "Give the bar exactly the color you pick, or make it glass that blurs what's behind (it takes one picture of what's behind and updates it only after a window is moved, resized, minimized or restored, so it never slows your PC down; while a window shrinks or grows it shows just the wallpaper, so the window's colors don't flash on it), or see-through.",
                  "يمشي مع خلفيتك وذوقك، بدل لون ويندوز الثابت.",
                  "It matches your wallpaper and taste, instead of Windows' fixed color.",
                  a =>
                  {
                      a.Screen();
                      a.Rect(0, 22, W, 24, Purple, 0);
                      a.Rect(0, 62, W, 24, Panel, 0, 0x90);
                      a.Rect(0, 102, W, 24, Blue, 0);
                      for (int r = 0; r < 3; r++)
                          for (int i = 0; i < 6; i++) a.Rect(60 + i * 22, 27 + r * 40, 14, 14, Colors.White, 3, 0xC0);
                  }),
                I("الإخفاء مع ملء الشاشة والسنة", "Hide in full screen, and the year",
                  "الشريط يختفي لما يكون عندك برنامج مكبّر أو ملء الشاشة، ويرجع لما تودّي الماوس تحت. ولو الساعة أو تاسك بار المعلومات فوق، ترفع الماوس لأعلى الشاشة فوقها بالضبط (مو أي مكان فوق) وتطلع هي بروحها بدون الشريط اللي تحت. ومع خيار \"بالألعاب يطلع بزر ويندوز بس\"، وأنت تلعب الماوس ما يطلّعه أبد (عشان ما يطلع وأنت تحرك الكاميرا)، يطلع بس لما تضغط زر الويندوز. وتقدر تضيف السنة للتاريخ.",
                  "The bar hides when a window is maximized or full screen, and comes back when the mouse reaches the bottom. If the clock or the info bar is at the top, move the mouse to the top edge right over them (not just anywhere along the top) and only they come down, without the bottom bar. With \"In games, only the Windows key shows it\", the mouse never brings it up while you play (so it doesn't pop up as you move the camera); only the Windows key does. You can also add the year to the date.",
                  "مساحة كاملة للعبة أو المقطع، والتاريخ كامل بنظرة.",
                  "The whole screen for a game or video, and the full date at a glance.",
                  a =>
                  {
                      a.Screen();
                      a.Window(10, 8, 240, 112, L("ملء الشاشة", "Full screen"));
                      a.Dashed(0, H - 22, W, 22, Muted, 0x10);
                      a.Arrow(130, H - 30, 130, H - 6, Muted, 1.5);
                      a.Text(176, 54, "1/11/2026", 13, Ink, true);
                  }),
                I("يظهر في البث", "Show in streams",
                  "الزجاج يوريك اللي ورا الأشرطة على طول، وعشان ما يصوّر نفسه الأشرطة ما تطلع في البث والتسجيل. لما تبث، الأشرطة ترجع تطلع فيه، وزجاجها يبقى يوريك النوافذ اللي وراها (تتحدّث كم مرة بالثانية، وبعض الألعاب ما تبان فيه). يصير لحاله لما يكون OBS أو Streamlabs أو XSplit أو Twitch Studio شغّال. ولديسكورد وغيره شغّله بنفسك: كليك يمين على التاسك بار أو الساعة أو تاسك بار المعلومات ← \"يظهر في البث\"، أو من صفحة التاسك بار.",
                  "The glass shows what's behind the bars live, and so it doesn't copy itself the bars don't show in streams and recordings. While you stream they show in it again, and their glass still shows the windows behind them (updated a few times a second; some games don't show in it). It's on by itself while OBS, Streamlabs, XSplit or Twitch Studio is running. For Discord and others turn it on yourself: right-click the taskbar, the clock or an info bar → \"Show in streams\", or on the Taskbar page.",
                  "اللي يشوفون البث يشوفون التاسك بار حقك مثل ما تشوفه.",
                  "Your viewers see your taskbar just as you do.",
                  a =>
                  {
                      a.Screen();
                      a.Rect(8, 6, 240, 18, Panel, 6, 0xC0);
                      a.Window(150, 40, 100, 70, L("البث", "Stream"));
                      a.Rect(156, 64, 88, 8, Panel, 3, 0xC0);
                  }),
                I("يختفي لما تكون نافذة تحته", "Hides when a window is under it",
                  "لو رفعت نافذة وصار شريط يغطي عليها (التاسك بار أو الساعة أو تاسك بار المعلومات)، الشريط اللي فوقها بس ينسحب عشان تبان النافذة كاملة، والباقي يظلون مكانهم. يرجع لما تبعد النافذة عنه، أو تلصق الماوس على حافة الشاشة فوقه بالضبط. وأنت تسحب النافذة ما يتحرك شي، ويقرر أول ما تفلتها. تشغّله وتطفيه من كليك يمين على التاسك بار أو الساعة أو تاسك بار المعلومات ← \"يختفي لما تكون نافذة تحته\"، أو من صفحة التاسك بار.",
                  "If you raise a window and a bar ends up covering it (the taskbar, the clock or an info bar), only the bar over it slides away so the whole window shows; the others stay where they are. It comes back when the window moves away from it, or when you push the mouse against the screen's edge right over it. Nothing moves while you're dragging the window: it's decided as soon as you let go. Turn it on or off by right-clicking the taskbar, the clock or an info bar → \"Hide when a window is under it\", or on the Taskbar page.",
                  "النافذة كلها تبان، بدون ما تطفي الأشرطة أو تنزّل النافذة.",
                  "The whole window shows, without turning the bars off or moving the window down.",
                  a =>
                  {
                      a.Screen();
                      a.Rect(8, 6, 70, 18, Panel, 6, 0xC0);
                      a.Dashed(84, 6, 90, 18, Muted, 0x10);
                      a.Rect(180, 6, 70, 18, Panel, 6, 0xC0);
                      a.Window(70, 12, 120, 100, L("نافذة", "Window"));
                  }),
            });

        // ---- Desktop -----------------------------------------------------------------------------

        private static HelpPage Desktop() => new(L("سطح المكتب", "Desktop"),
            L("شكل سطح المكتب والمجلدات ومؤشر الماوس.", "The look of the desktop, folders and the mouse pointer."),
            new[]
            {
                I("لون مربع التحديد", "Selection box color",
                  "المربع اللي يطلع لما تسحب الماوس عشان تحدد ملفات، تعطيه اللون اللي تبيه، وحدوده بعد.",
                  "The box that appears when you drag to select files gets the color you want, its border too.",
                  "تشوف وش حددت بوضوح، وبلون يمشي مع خلفيتك.",
                  "See clearly what you've selected, in a color that suits your wallpaper.",
                  a =>
                  {
                      a.Screen();
                      a.Icon(30, 24, Blue, "a.png");
                      a.Icon(80, 24, Green, "b.mp4");
                      a.Icon(30, 78, Orange, "c.pdf");
                      a.Icon(80, 78, Red, "d.zip");
                      a.Dashed(18, 14, 100, 110, Purple, 0x45);
                      a.Cursor(118, 124);
                  }),
                I("مؤشر الماوس", "Mouse pointer",
                  "تغيّر لون مؤشر الماوس وحجمه. وأصغر حجم في ويندوز 32 بكسل، فلو تبي مؤشرك الخاص أصغر من كذا نزّل \"حجم المؤشر الخاص\" لين 25%.",
                  "Change the mouse pointer's color and size. Windows' smallest size is 32 px; to make your own pointer smaller than that, lower \"Custom pointer size\" down to 25%.",
                  "تلقاه بسرعة على الشاشة، خصوصًا لو عندك أكثر من شاشة.",
                  "Find it fast on screen, especially with more than one monitor.",
                  a =>
                  {
                      a.Screen();
                      a.Cursor(40, 45, 1);
                      a.Cursor(100, 35, 2, Yellow);
                      a.Cursor(180, 25, 3, Green);
                  }),
            });

        // ---- Cleanup -----------------------------------------------------------------------------

        private static HelpPage Big() => new(L("أكبر الملفات", "Biggest files"),
            L("يلقى لك الملفات اللي ماخذة أكثر مساحة في القرص.", "Finds the files taking the most space on your drive."),
            new[]
            {
                I("أكبر 200 ملف", "The 200 biggest files",
                  "يمر على القرص كله ويرتب لك أكبر الملفات، وتقدر ترسل اللي ما تبيه لسلة المهملات. ملفات ويندوز ما تطلع عشان ما تحذفها بالغلط.",
                  "Goes through the whole drive and lists the biggest files; send the ones you don't want to the recycle bin. Windows' own files never show, so you can't delete them by mistake.",
                  "تفضّي مساحة كبيرة بدقايق، بدل ما تدوّر مجلد مجلد.",
                  "Free up a lot of space in minutes, instead of going folder by folder.",
                  a =>
                  {
                      string[] names = { "game.iso", "movie.mkv", "backup.zip", "video.mp4" };
                      double[] part = { 0.95, 0.7, 0.45, 0.25 };
                      string[] size = { "48 GB", "12 GB", "6 GB", "2 GB" };
                      for (int i = 0; i < 4; i++)
                      {
                          a.Text(14, 16 + i * 32, names[i], 11, Ink);
                          a.Bar(14, 33 + i * 32, 180, part[i], i == 0 ? Red : Blue);
                          a.Text(204, 26 + i * 32, size[i], 11, Muted);
                      }
                  }),
            });

        private static HelpPage Temp() => new(L("الملفات المؤقتة", "Temporary files"),
            L("ملفات ويندوز والبرامج يسوونها مؤقتًا وينسونها.", "Files Windows and apps make for a while and then forget."),
            new[]
            {
                I("تنظيف المؤقتة والكاش", "Clean temporary files and cache",
                  "يحذف الملفات المؤقتة وكاش البرامج والمتصفحات. ويندوز والبرامج يرجعون يسوونها لما يحتاجونها، فحذفها آمن.",
                  "Deletes temporary files and app and browser caches. Windows and apps make them again when needed, so it's safe.",
                  "يرجّع لك قيقات من المساحة بضغطة، وأحيانًا يحل مشاكل البرامج.",
                  "Gets back gigabytes with one click, and sometimes fixes misbehaving apps.",
                  a =>
                  {
                      a.Glyph(70, 70, "", 56, Muted);
                      a.Text(30, 16, "Temp", 11, Muted);
                      a.Text(90, 10, "Cache", 11, Muted);
                      a.Arrow(120, 70, 165, 70, Green);
                      a.Center(210, 60, "+ 8.4 GB", 18, Green, true);
                      a.Center(210, 84, L("صارت فاضية", "freed"), 11, Muted);
                  }),
            });

        private static HelpPage Dup() => new(L("الملفات المكررة", "Duplicate files"),
            L("يلقى الملفات اللي عندك منها أكثر من نسخة.", "Finds files you have more than one copy of."),
            new[]
            {
                I("نسخ متطابقة 100%", "100% identical copies",
                  "يقارن محتوى الملفات نفسه، مو بس الاسم. فلو صورة محفوظة مرتين باسمين مختلفين يطلعها.",
                  "Compares what's inside the files, not just the names. A photo saved twice under two names still shows up.",
                  "تحذف النسخ الزايدة وأنت متطمّن إنها نفس الشي بالضبط.",
                  "Delete the extra copies knowing they're exactly the same.",
                  a =>
                  {
                      a.File(40, 40, "JPG", Blue, 1.6);
                      a.File(170, 40, "JPG", Blue, 1.6);
                      a.Center(130, 70, "=", 34, Green, true);
                      a.Center(64, 120, "IMG_01.jpg", 10, Muted);
                      a.Center(194, 120, L("نسخة.jpg", "copy.jpg"), 10, Muted);
                  }),
            });

        private static HelpPage Bin() => new(L("سلة المهملات", "Recycle bin"),
            L("وين يروح اللي تحذفه من البرنامج.", "Where what you delete in the app goes."),
            new[]
            {
                I("كل شي يقدر يرجع", "Everything can come back",
                  "اللي تحذفه من صفحات التنظيف ما ينحذف نهائيًا، يروح لسلة مهملات ويندوز أول.",
                  "What you delete from the cleanup pages isn't gone for good: it goes to Windows' recycle bin first.",
                  "لو حذفت شي بالغلط ترجعه بسهولة.",
                  "Deleted something by mistake? Bring it back easily.",
                  a =>
                  {
                      a.File(40, 50, "PDF", Red, 1.3);
                      a.Arrow(90, 70, 150, 70, Muted);
                      a.Glyph(190, 70, "", 50, Muted);
                      a.Arrow(170, 110, 70, 110, Green);
                      a.Center(120, 128, L("رجّعه", "Restore"), 11, Green, true);
                  }),
            });

        // ---- Tools -------------------------------------------------------------------------------

        private static HelpPage Hotkeys() => new(L("اختصارات الكيبورد", "Keyboard shortcuts"),
            L("اختصاراتك الخاصة اللي تشتغل في أي مكان.", "Your own shortcuts that work anywhere."),
            new[]
            {
                I("اختصار لأي شي", "A shortcut for anything",
                  "تختار أزرار، وتختار وش تفتح: برنامج، ملف، مجلد أو موقع. وتشتغل في أي مكان حتى لو البرنامج مخفي جنب الساعة.",
                  "Pick the keys and what they open: an app, a file, a folder or a website. They work anywhere, even with the app hidden by the clock.",
                  "تفتح اللي تستخدمه كل يوم بلحظة، بدون ما تدوّره.",
                  "Open what you use every day in a moment, without looking for it.",
                  a =>
                  {
                      double x = a.Keys(18, 30, "Ctrl", "Alt", "N");
                      a.Arrow(x + 6, 42, x + 34, 42, Ink);
                      a.Window(x + 42, 16, 252 - x - 42, 60, "Notepad");
                      x = a.Keys(18, 100, "Ctrl", "Alt", "Y");
                      a.Arrow(x + 6, 112, x + 34, 112, Ink);
                      a.Glyph(x + 60, 112, "", 26, Blue);
                  }),
            });

        private static HelpPage Ram() => new(L("استهلاك الرام", "RAM usage"),
            L("وش البرامج اللي ماخذة أكثر رام الحين.", "Which apps take the most memory right now."),
            new[]
            {
                I("أكثر البرامج استهلاكًا", "The hungriest apps",
                  "قائمة البرامج مرتبة حسب الرام اللي ماخذته، مع كل نوافذها مجموعة.",
                  "Your apps sorted by the memory they use, all their windows added up.",
                  "تعرف ليش الجهاز ثقيل، وتسكّر اللي ماخذ أكثر من حقه.",
                  "Find out why the PC is slow and close what's taking too much.",
                  a =>
                  {
                      string[] names = { "Chrome", "Discord", "Photoshop", "Spotify" };
                      double[] part = { 0.9, 0.5, 0.38, 0.2 };
                      string[] size = { "3.1 GB", "1.4 GB", "1.1 GB", "520 MB" };
                      for (int i = 0; i < 4; i++)
                      {
                          a.Rect(14, 18 + i * 32, 14, 14, Apps[i], 3);
                          a.Text(34, 15 + i * 32, names[i], 11, Ink);
                          a.Bar(110, 21 + i * 32, 90, part[i], part[i] > 0.8 ? Red : Blue);
                          a.Text(208, 15 + i * 32, size[i], 10, Muted);
                      }
                  }),
            });

        private static HelpPage Lock() => new(L("قفل الملفات", "File lock"),
            L("تقفل أي ملف أو مجلد بكلمة سر.", "Lock any file or folder with a password."),
            new[]
            {
                I("قفل بكلمة سر", "Lock with a password",
                  "الملف يتشفر تشفير حقيقي (AES-256) ويصير بصيغة .wplock، ومحد يقدر يفتحه إلا بكلمة السر. وتقدر تقفل من الكليك يمين على أي ملف.",
                  "The file is really encrypted (AES-256) into a .wplock, and nobody opens it without the password. You can also lock from the right-click menu.",
                  "ملفاتك الخاصة تبقى خاصة، حتى لو أحد استخدم جهازك أو أخذ الملف.",
                  "Your private files stay private, even if someone uses your PC or takes the file.",
                  a =>
                  {
                      a.File(30, 46, "PDF", Red, 1.4);
                      a.Arrow(84, 72, 120, 72, Ink);
                      a.Glyph(146, 72, "", 34, Yellow);
                      a.Arrow(172, 72, 196, 72, Ink);
                      a.File(204, 46, "LOCK", Purple, 1.4);
                      a.Center(130, 124, "••••••", 16, Muted, true);
                  }),
                I("يرجع يتقفل لحاله", "Locks itself again",
                  "تفتحه بكلمة السر وتشتغل عليه، ولما تسكّر البرنامج اللي فاتحه يرجع يتقفل بنفس الكلمة.",
                  "Open it with the password and work on it; once you close the app it's open in, it locks again with the same password.",
                  "ما تنسى ملف مفتوح بدون قفل.", "You never leave a file unlocked by accident.",
                  a =>
                  {
                      a.Glyph(50, 70, "", 34, Green);
                      a.Window(90, 36, 90, 70, "Word");
                      a.Arrow(186, 70, 206, 70, Ink);
                      a.Glyph(228, 70, "", 34, Yellow);
                  }),
            });

        private static HelpPage Startup() => new(L("برامج بدء التشغيل", "Startup apps"),
            L("البرامج اللي تشتغل لحالها مع تشغيل الجهاز.", "The apps that start by themselves with your PC."),
            new[]
            {
                I("طفّي اللي ما تحتاجه", "Turn off what you don't need",
                  "كل برنامج يشتغل مع الجهاز له مفتاح، تطفيه ويوقف يشتغل لحاله (ويبقى موجود). تطلع حتى تطبيقات المتجر (مثل ChatGPT)، والقائمة تتحدث لحالها أول ما يتغير شي.",
                  "Every app that starts with the PC has a switch; turn it off and it stops starting by itself (it stays installed). Store apps (like ChatGPT) show too, and the list updates by itself the moment anything changes.",
                  "الجهاز يقلع أسرع، والرام تبقى فاضية للي تحتاجه.",
                  "The PC starts faster, and memory stays free for what you need.",
                  a =>
                  {
                      string[] names = { "Steam", "Discord", "Spotify", "OneDrive" };
                      bool[] on = { false, true, false, true };
                      for (int i = 0; i < 4; i++)
                      {
                          a.Rect(18, 18 + i * 30, 14, 14, Apps[i + 1], 3);
                          a.Text(40, 15 + i * 30, names[i], 11, Ink);
                          a.Switch(196, 18 + i * 30, on[i]);
                      }
                  }),
            });

        private static HelpPage Downloads() => new(L("ترتيب التحميلات", "Organize downloads"),
            L("الملفات اللي تنزل تتوزع على مجلدات حسب نوعها.", "Downloaded files are sorted into folders by type."),
            new[]
            {
                I("ترتيب تلقائي", "Automatic sorting",
                  "أول ما يخلص تحميل ملف، يروح لمجلده: الصور مع الصور، المقاطع مع المقاطع، وهكذا. وتقدر ترتب الموجود أو أي مجلد ثاني، وتتراجع.",
                  "As soon as a download finishes, it goes to its folder: pictures with pictures, videos with videos… You can also sort what's there or any other folder, and undo it.",
                  "مجلد التحميلات يبقى مرتب للأبد، وتلقى الملف بسرعة.",
                  "Your Downloads stays tidy for good, and files are easy to find.",
                  a =>
                  {
                      a.Glyph(40, 75, "", 40, Blue);
                      a.Arrow(70, 75, 120, 32, Muted, 1.5);
                      a.Arrow(70, 75, 120, 75, Muted, 1.5);
                      a.Arrow(70, 75, 120, 118, Muted, 1.5);
                      a.Folder(140, 32, 22);
                      a.Folder(140, 75, 22);
                      a.Folder(140, 118, 22);
                      a.Text(160, 25, L("الصور", "Pictures"), 11, Ink);
                      a.Text(160, 68, L("المقاطع", "Videos"), 11, Ink);
                      a.Text(160, 111, L("المستندات", "Documents"), 11, Ink);
                  }),
            });

        // ---- Files -------------------------------------------------------------------------------

        private static HelpPage Convert() => new(L("تحويل الصيغ", "Convert formats"),
            L("تحوّل الصور والمقاطع والصوت لأي صيغة ثانية بسحبة.", "Turn pictures, videos and audio into another format with a drag."),
            new[]
            {
                I("اسحب واضغط Space", "Drag and press Space",
                  "وأنت تسحب أي ملف من المستكشف أو سطح المكتب، اضغط Space وتطلع قائمة دائرية بالصيغ. حط الملف على الصيغة وخلاص.",
                  "While dragging any file from Explorer or the desktop, press Space and a round menu of formats appears. Drop the file on one and you're done.",
                  "بدون مواقع تحويل ولا برامج ثانية، وملفاتك ما تطلع من جهازك.",
                  "No converter websites or other apps, and your files never leave your PC.",
                  a =>
                  {
                      a.Screen();
                      a.Dot(150, 72, 104, Panel, 0xF0);
                      a.Dot(150, 72, 40, Back);
                      string[] fmt = { "PNG", "JPG", "WEBP", "GIF", "ICO", "PDF" };
                      for (int i = 0; i < fmt.Length; i++)
                      {
                          double ang = -Math.PI / 2 + i * Math.PI * 2 / fmt.Length;
                          a.Center(150 + 36 * Math.Cos(ang), 72 + 36 * Math.Sin(ang), fmt[i], 9, i == 0 ? Blue : Ink, true);
                      }
                      a.File(30, 30, "HEIC", Orange);
                      a.Cursor(52, 56);
                      a.Key(20, 112, "Space");
                  }),
                I("ترجع للأصل", "Go back to the original",
                  "الملف الأصلي ينحفظ مخفي 30 يوم، فلو غيّرت رايك ترجعه.",
                  "The original is kept hidden for 30 days, so you can change your mind.",
                  "تجرّب أي تحويل بدون خوف.", "Try any conversion without worry.",
                  a =>
                  {
                      a.File(40, 50, "MP4", Purple, 1.4);
                      a.Arrow(95, 62, 160, 62, Ink);
                      a.File(180, 50, "MP3", Green, 1.4);
                      a.Arrow(160, 100, 95, 100, Yellow);
                      a.Center(128, 120, L("30 يوم", "30 days"), 11, Yellow, true);
                  }),
            });

        private static HelpPage Resize() => new(L("تغيير الأبعاد", "Resize"),
            L("تكبّر أو تصغّر الصور والمقاطع.", "Make pictures and videos bigger or smaller."),
            new[]
            {
                I("اسحب واضغط R", "Drag and press R",
                  "وأنت تسحب صورة أو مقطع اضغط R، وتختار الحجم اللي تبيه.",
                  "While dragging a picture or video, press R and pick the size you want.",
                  "صورة على قد اللي يطلبه موقع أو برنامج، بثواني.",
                  "A picture at exactly the size a website or app asks for, in seconds.",
                  a =>
                  {
                      a.Rect(20, 20, 110, 80, Wall, 4, 255, Line);
                      a.Center(75, 60, "4000×3000", 10, Ink);
                      a.Arrow(140, 60, 170, 60, Ink);
                      a.Rect(180, 40, 56, 42, Wall, 3, 255, Line);
                      a.Center(208, 61, "1280×960", 8, Ink);
                      a.Key(110, 112, "R");
                  }),
            });

        private static HelpPage Compress() => new(L("ضغط الملفات", "Compress"),
            L("تصغّر حجم الصور والمقاطع والصوت.", "Shrink pictures, videos and audio."),
            new[]
            {
                I("اسحب واضغط C", "Drag and press C",
                  "وأنت تسحب الملف اضغط C، ويطلع لك ملف أصغر بنفس الشكل تقريبًا. وما يطلع أكبر من الأصل أبد.",
                  "While dragging the file press C, and you get a smaller file that looks about the same. It's never bigger than the original.",
                  "ترسل المقاطع في الواتس والديسكورد بدون حد الحجم، وتوفر مساحة.",
                  "Send videos on WhatsApp and Discord within their size limits, and save space.",
                  a =>
                  {
                      a.File(30, 34, "MP4", Purple, 1.6);
                      a.Center(54, 112, "120 MB", 12, Ink, true);
                      a.Arrow(100, 64, 150, 64, Ink);
                      a.File(180, 46, "MP4", Purple, 1.1);
                      a.Center(196, 112, "18 MB", 12, Green, true);
                      a.Key(112, 82, "C");
                  }),
            });

        private static HelpPage Archive() => new(L("ملف مضغوط", "Archive"),
            L("تحط ملفات ومجلدات في ملف واحد مضغوط.", "Put files and folders into one compressed file."),
            new[]
            {
                I("اسحب واضغط Z", "Drag and press Z",
                  "تسحب ملفات أو مجلد وتضغط Z، ويصير ZIP أو 7Z أو RAR.",
                  "Drag files or a folder and press Z: you get a ZIP, 7Z or RAR.",
                  "ترسل مجموعة ملفات كملف واحد، وبحجم أصغر.",
                  "Send a bunch of files as one, and smaller.",
                  a =>
                  {
                      a.File(20, 30, "JPG", Blue);
                      a.File(40, 50, "PDF", Red);
                      a.File(60, 70, "DOC", Blue);
                      a.Arrow(110, 70, 160, 70, Ink);
                      a.File(180, 44, "ZIP", Yellow, 1.5);
                      a.Key(122, 96, "Z");
                  }),
            });

        private static HelpPage Video() => new(L("تحميل المقاطع", "Download videos"),
            L("تحمّل أي مقطع أو أغنية من رابطها.", "Download any video or song from its link."),
            new[]
            {
                I("Ctrl+H والصق الرابط", "Ctrl+H and paste the link",
                  "من أي مكان اضغط Ctrl+H والصق رابط من يوتيوب أو انستا أو تويتر أو تيك توك أو سبوتيفاي، وتختار الجودة أو صوت بس.",
                  "From anywhere press Ctrl+H and paste a link from YouTube, Instagram, X, TikTok or Spotify, then pick the quality or audio only.",
                  "بدون مواقع تحميل مليانة إعلانات وروابط مشبوهة.",
                  "No download websites full of ads and shady links.",
                  a =>
                  {
                      a.Keys(16, 16, "Ctrl", "H");
                      a.Rect(16, 56, 228, 26, Panel, 13, 255, Line);
                      a.Text(28, 61, "youtube.com/watch?v=…", 11, Muted);
                      a.Rect(16, 98, 228, 30, Panel, 8);
                      a.Bar(26, 116, 150, 0.7, Blue, 5);
                      a.Text(26, 100, "video.mp4", 9, Ink);
                      a.Text(190, 108, "70%", 10, Muted);
                  }),
                I("قص المقطع قبل التحميل", "Trim before downloading",
                  "في صفحة تحميل المقاطع اضغط \"قص مقطع\" والصق رابط يوتيوب: يطلع لك اسم المقطع وصورته وطوله. اسحب الدائرتين على الشريط (أو اكتب الوقت مثل 1:30، أو استخدم أزرار ± الثواني) عشان تحدد من وين يبدأ ووين ينتهي، واختر فيديو أو صوت، واضغط \"قص وحمّل\". \"شوف البداية في يوتيوب\" يفتح المقطع من نفس الثانية عشان تتأكد. القص يحتاج FFmpeg، ولو مو موجود ينزّله لحاله أول مرة.",
                  "On the Download videos page press \"Trim\" and paste a YouTube link: you see the video's name, picture and length. Drag the two circles on the bar (or type a time like 1:30, or use the ± seconds buttons) to pick where it starts and ends, choose video or audio, and press \"Trim & download\". \"See the start on YouTube\" opens the video at that second so you can check. Trimming needs FFmpeg; if it's missing, it's downloaded by itself the first time.",
                  "يتحمّل الجزء اللي تبيه بس، فيخلص أسرع والملف أصغر.",
                  "Only the part you want is downloaded, so it's faster and the file is smaller.",
                  a =>
                  {
                      a.Rect(16, 20, 228, 26, Panel, 13, 255, Line);
                      a.Text(28, 25, "youtube.com/watch?v=…", 11, Muted);
                      a.Rect(26, 74, 208, 8, Line, 4);
                      a.Rect(70, 74, 100, 8, Blue, 4);
                      a.Dot(70, 78, 16, Colors.White);
                      a.Dot(170, 78, 16, Colors.White);
                      a.Text(56, 92, "0:42", 10, Ink);
                      a.Text(156, 92, "2:10", 10, Ink);
                      a.Rect(80, 112, 100, 22, Blue, 6);
                      a.Center(130, 123, "✂", 12, Colors.White, true);
                  }),
                I("تسجيل الدخول وحماية حسابك", "Signing in, and keeping your account safe",
                  "\"سجّل دخول\" في صفحة تحميل المقاطع يفتح نافذة داخل Wibn، تسجّل فيها دخولك في انستقرام أو ريديت أو X أو تيك توك أو فيسبوك. كلمة السر ما تمر على Wibn، والجلسة تنحفظ مشفّرة بحساب ويندوز حقك، فلو أحد نسخ الملف لجهاز ثاني ما يقدر يقرأه. وقت التحميل بس تنكتب نسخة مؤقتة لأداة التحميل وتنحذف على طول. Wibn يتمهّل بين الطلبات، ولو الموقع طلب تأكيد إنه أنت يوقف ويقولك. تقدر تختار ملف cookies.txt بنفسك (وWibn يعرض يحذف الأصلي)، و\"استخدم تسجيل دخول متصفحك\" (المتصفح الافتراضي في جهازك) طافي إلا لو شغّلته. \"إزالة\" يمسح كل شي. أدوات التحميل تنزل من GitHub ويتأكد Wibn من بصمتها (SHA-256) قبل يشغّلها.",
                  "\"Sign in\" on the Download videos page opens a window inside Wibn where you sign in to Instagram, Reddit, X, TikTok or Facebook. Your password never passes through Wibn, and the session is saved encrypted with your Windows account, so a copy of the file on another PC can't be read. Only while downloading is a short-lived copy written for the download tool, and it's deleted right after. Wibn waits between requests, and if the site asks to confirm it's you, it stops and tells you. You can pick a cookies.txt yourself (Wibn offers to delete the original), and \"Use your browser's sign-in\" (your default browser) is off unless you turn it on. \"Remove\" wipes it all. The download tools come from GitHub, and Wibn checks their fingerprint (SHA-256) before running them.",
                  "حسابك ما يطلع من جهازك، وما ينسرق لو أحد أخذ ملفاته.",
                  "Your account stays on your PC, and can't be stolen by someone taking its files.",
                  a =>
                  {
                      a.Rect(80, 50, 100, 70, Panel, 10, 255, Line);
                      a.Rect(100, 22, 60, 40, Line, 30);
                      a.Rect(112, 34, 36, 30, Panel, 18);
                      a.Center(130, 86, "🔒", 22, Ink, false);
                      a.Text(70, 128, "cookies.dat (DPAPI)", 10, Muted);
                  }),
            });

        private static HelpPage Capture() => new(L("تصوير الشاشة", "Screen capture"),
            L("صور وفيديو للشاشة، كل نوع باختصار.", "Screenshots and video of the screen, each with its own shortcut."),
            new[]
            {
                I("صورة لمكان تحدده", "A picture of an area",
                  "تحدد المكان بالماوس وتنحفظ صورته وتنسخ، أو صوّر الشاشة كاملة.",
                  "Select an area with the mouse and it's saved and copied, or capture the whole screen.",
                  "تلصقها على طول في أي محادثة.", "Paste it straight into any chat.",
                  a =>
                  {
                      a.Screen();
                      a.Window(20, 14, 150, 100, "Chrome");
                      a.Rect(0, 0, W, H, Colors.Black, 0, 0x50);
                      a.Dashed(60, 40, 110, 70, Ink, 0x20);
                      a.Cursor(170, 110);
                      a.Glyph(220, 30, "", 26, Ink);
                  }),
                I("تسجيل فيديو وآخر دقيقة", "Record, and the last minute",
                  "تسجّل الشاشة فيديو، أو تخليه يحفظ آخر دقيقة صارت بضغطة، مثل اللقطات في الألعاب.",
                  "Record the screen, or keep the last minute and save it with one press, like game clips.",
                  "ما يفوتك موقف صار فجأة.", "Never miss something that just happened.",
                  a =>
                  {
                      a.Screen();
                      a.Window(20, 14, 220, 90, L("لعبة", "Game"));
                      a.Dot(36, 124, 12, Red);
                      a.Text(48, 116, "REC", 11, Red, true);
                      a.Bar(90, 121, 150, 1, Blue, 6);
                      a.Text(176, 104, L("آخر دقيقة", "Last minute"), 10, Muted);
                  }),
            });

        private static HelpPage Volume() => new(L("أصوات البرامج", "App volumes"),
            L("صوت كل برنامج لحاله.", "Each app's volume on its own."),
            new[]
            {
                I("علّي ونزّل كل برنامج", "Turn each app up or down",
                  "كل برنامج يشغّل صوت له شريط، تعلّيه أو تنزّله أو تكتمه.",
                  "Every app playing sound gets a slider to raise, lower or mute it.",
                  "تسمع صاحبك في الديسكورد فوق صوت اللعبة، بدون ما تدوّر في إعدادات ويندوز.",
                  "Hear your friend on Discord over the game, without digging through Windows settings.",
                  a =>
                  {
                      string[] names = { L("لعبة", "Game"), "Discord", "Spotify" };
                      double[] part = { 0.35, 0.9, 0.6 };
                      for (int i = 0; i < 3; i++)
                      {
                          a.Rect(18, 24 + i * 38, 18, 18, Apps[i + 2], 4);
                          a.Text(44, 20 + i * 38, names[i], 11, Ink);
                          a.Slider(44, 38 + i * 38, 170, part[i], Blue);
                      }
                      a.Glyph(236, 104, "", 16, Red);
                  }),
            });

        private static HelpPage Specs() => new(L("مواصفات الجهاز", "PC specs"),
            L("كل شي عن جهازك في مكان واحد.", "Everything about your PC in one place."),
            new[]
            {
                I("كل القطع", "Every part",
                  "المعالج، الرام وكل قطعة فيها، كرت الشاشة، الأقراص، الشاشات، اللوحة الأم وويندوز. وتنسخها كلها بضغطة.",
                  "Processor, RAM and every stick, graphics card, drives, screens, motherboard and Windows. Copy it all with one click.",
                  "تعرف إذا لعبة أو برنامج يشتغل عندك، وترسل مواصفاتك لأحد يساعدك.",
                  "Know if a game or app will run, and send your specs to whoever is helping you.",
                  a =>
                  {
                      string[] k = { "CPU", "RAM", "GPU", "SSD" };
                      string[] v = { "Ryzen 7 7800X3D", "32 GB DDR5", "RTX 4070", "1 TB NVMe" };
                      for (int i = 0; i < 4; i++)
                      {
                          a.Text(18, 16 + i * 30, k[i], 11, Muted, true);
                          a.Text(64, 16 + i * 30, v[i], 11, Ink);
                      }
                      a.Glyph(230, 124, "", 18, Blue);
                  }),
            });


        private static HelpPage GameHelp() => new(L("وضع الألعاب", "Game mode"),
            L("خل المعالج كله للعبة.", "Give the whole processor to your game."),
            new[]
            {
                I("Game Booster Mode", "Game Booster Mode",
                  "شغّله مرة وخلاص: أول ما تفتح لعبة يوقف Windows Update و SysMain وخدمات الخلفية اللي تختارها، ويسكّر برامج مثل OneDrive و Teams لو اخترتها، ويرفع أولوية اللعبة. أول ما تطلع منها يرجّع كل شي لحاله. يعرف ألعاب Steam و Epic و Xbox وغيرها لحاله، وتقدر تضيف لعبتك. أول مرة ويندوز يطلب صلاحية المسؤول مرة وحدة بس.",
                  "Turn it on once: as soon as a game starts it stops Windows Update, SysMain and the background services you pick, closes apps like OneDrive and Teams if you choose, and raises the game's priority. When you leave the game everything comes back by itself. It recognizes Steam, Epic, Xbox and other games on its own, and you can add yours. The first time, Windows asks for admin rights just once.",
                  "تحديث أو فهرسة تشتغل فجأة وسط اللعب تسبب تقطيع ولاق. هنا ما تشتغل إلا بعد ما تخلص.",
                  "An update or indexing kicking in mid-game causes stutter and lag. Here they wait until you're done.",
                  a =>
                  {
                      a.Screen();
                      a.Window(20, 14, 130, 100, "Game");
                      a.Glyph(85, 70, "", 34, Green);
                      a.Rect(164, 22, 84, 22, Panel, 6);
                      a.Text(170, 26, "Update", 10, Muted);
                      a.Glyph(236, 33, "", 10, Red);
                      a.Rect(164, 52, 84, 22, Panel, 6);
                      a.Text(170, 56, "SysMain", 10, Muted);
                      a.Glyph(236, 63, "", 10, Red);
                      a.Rect(164, 82, 84, 22, Panel, 6);
                      a.Text(170, 86, "OneDrive", 10, Muted);
                      a.Glyph(236, 93, "", 10, Red);
                  }),
                I("خفّف ويندوز", "Lighten Windows",
                  "من صفحة وضع الألعاب ← خفّف ويندوز، تختار اللي تبيه: توقيف تسجيل الألعاب في الخلفية (اللي ياخذ من كرت الشاشة طول الوقت)، تشغيل وضع اللعب حق ويندوز، وضع الطاقة \"أفضل أداء\"، توقيف الحركات والظلال الصغيرة، توقيف شفافية ويندوز، توقيف الإعلانات والبرامج اللي ويندوز ينزّلها من نفسه، وتوقيف برامج المتجر في الخلفية. زر \"خفّف بالمقترح\" يشغّل الأهم مرة وحدة، و\"رجّع ويندوز مثل ما كان\" يرجّع كل شي. كله من إعداداتك أنت: ما يلمس ملفات النظام ولا يحذف شي ولا يحتاج صلاحية المسؤول، وWibn يحفظ اللي كان قبل كل تغيير ويرجّعه بالضبط، ولو حذفت Wibn يرجّعها كلها لحالها.",
                  "On the Game mode page → Lighten Windows, pick what you want: turn off background game recording (which costs graphics card all the time), turn on Windows' Game Mode, set the power mode to \"Best performance\", turn off small animations and shadows, turn off Windows' transparency, stop ads and apps Windows installs by itself, and stop Store apps running in the background. \"Apply recommended\" turns on the main ones at once, and \"Put Windows back as it was\" undoes it all. It's all your own settings: nothing touches system files, nothing is deleted, no admin rights needed, and Wibn keeps what was there before each change and puts it back exactly; uninstalling Wibn puts it all back by itself.",
                  "جهاز يعلّق حتى مع أقل إعدادات بالقرافكس غالبًا السبب ويندوز نفسه في الخلفية. هنا تخففه بضغطة، وترجّعه بضغطة.",
                  "A PC that stutters even on the lowest graphics settings is often Windows itself working in the background. Here you lighten it with one click, and undo it with one click.",
                  a =>
                  {
                      a.Screen();
                      a.Rect(30, 20, 200, 26, Panel, 6);
                      a.Text(40, 25, "Game DVR", 10, Muted);
                      a.Glyph(218, 33, "\uE73E", 10, Green);
                      a.Rect(30, 52, 200, 26, Panel, 6);
                      a.Text(40, 57, "Animations", 10, Muted);
                      a.Glyph(218, 65, "\uE73E", 10, Green);
                      a.Rect(30, 84, 200, 26, Panel, 6);
                      a.Text(40, 89, "Best performance", 10, Muted);
                      a.Glyph(218, 97, "\uE73E", 10, Green);
                  }),
            });

        private static HelpPage WidgetsHelp() => new("Widgets",
            L("نوافذ صغيرة على سطح المكتب.", "Small windows on your desktop."),
            new[]
            {
                I("ويدجتات تتحاذى مع الأيقونات", "Widgets that line up with your icons",
                  "تضيف ويدجتات مثل الطقس ومواقيت الصلاة والتقويم واستهلاك الجهاز وسرعة الإنترنت وموجات الصوت، وتتحاذى مع أيقونات سطح المكتب. تسحبها وتكبّرها من زاويتها. لما تكبّر نافذة أو يصير شي بملء الشاشة، الويدجتات اللي على نفس الشاشة تختفي عشان تخفف الرام والمعالج، وترجع أول ما تصغّرها. ولما يشتغل Wibn، الويدجتات تظهر وحدة ورا الثانية شوي شوي.",
                  "Add widgets like weather, prayer times, a calendar, PC usage, internet speed and sound waves; they line up with your desktop icons. Drag them and resize from their corner. When a window is maximized or something is full screen, the widgets on that screen hide to save RAM and CPU, and come back as soon as it is restored. When Wibn starts, the widgets fade in one after another.",
                  "اللي تحتاجه قدامك أول ما تشوف سطح المكتب.",
                  "What you need, right there on your desktop.",
                  a =>
                  {
                      a.Screen();
                      for (int r = 0; r < 4; r++) a.Icon(10, 8 + r * 34, Apps[r]);
                      a.Rect(44, 8, 90, 64, Panel, 10, 0xE0);
                      a.Center(89, 30, "24°", 20, Ink, true);
                      a.Center(89, 54, L("صافي", "Clear"), 10, Muted);
                      a.Rect(140, 8, 64, 64, Panel, 10, 0xE0);
                      a.Bar(148, 26, 48, 0.6, Blue, 5);
                      a.Bar(148, 42, 48, 0.3, Blue, 5);
                      a.Rect(44, 78, 160, 64, Panel, 10, 0xE0);
                      a.Center(124, 104, "♪  Song", 12, Ink, true);
                  }),
                I("لون كتابة الويدجتات", "Widgets' text color",
                  "من صفحة الويدجتات ← لون الكتابة اختر \"لون أختاره\" وحدد اللون من المنتقي، وتصير كل كتابة الويدجتات بهاللون: الكتابة البيضاء والرمادية مع بعض (الرمادية تبقى أخف شوي بنفس اللون). ترجّعها \"تلقائي\" وتصير الكتابة غامقة أو فاتحة لحالها حسب اللي وراها. الويدجتات اللي لها لون كتابة خاص فيها (مثل ويدجت الكتابة) تبقى على لونها.",
                  "On the Widgets page → Text color, choose \"Pick a color\" and pick it, and all the widgets' text takes that color: the white and the gray text together (the gray stays a little softer, in the same color). Set it back to \"Automatic\" and the text turns dark or light by itself with what's behind it. Widgets with their own text color (like the text widget) keep theirs.",
                  "الكتابة تبان واضحة على أي خلفية، حتى لو التلقائي ما ضبط معها.",
                  "Text that's clear on any wallpaper, even where automatic doesn't get it right.",
                  a =>
                  {
                      a.Screen();
                      a.Rect(40, 24, 180, 96, Panel, 12, 0xC0);
                      a.Text(60, 44, "24°", 22, Yellow);
                      a.Text(60, 80, "Riyadh", 11, Color.FromArgb(0xC8, Yellow.R, Yellow.G, Yellow.B));
                      a.Text(150, 80, "Mon", 11, Color.FromArgb(0xC8, Yellow.R, Yellow.G, Yellow.B));
                  }),
                I("الاختصارات بالأبيض والأسود", "Shortcuts in black and white",
                  "في ويدجت الاختصارات، كليك يمين عليه واختر \"أبيض وأسود\"، وتصير كل أيقونات برامجك ومجلداتك رمادية.",
                  "On the shortcuts widget, right-click it and choose \"Black and white\": every app and folder icon turns gray.",
                  "شكل هادي ومرتب يمشي مع أي خلفية، والأيقونات الملونة ما تشتت عينك.",
                  "A calm, tidy look that suits any wallpaper, with no colorful icons pulling your eye.",
                  a =>
                  {
                      a.Screen();
                      Color[] colors = { Blue, Green, Orange, Red, Purple, Yellow };
                      a.Rect(14, 20, 104, 104, Panel, 10, 0xE0);
                      a.Rect(142, 20, 104, 104, Panel, 10, 0xE0);
                      for (int i = 0; i < 6; i++)
                      {
                          double x = 26 + i % 3 * 30, y = 38 + i / 3 * 38;
                          a.Rect(x, y, 20, 20, colors[i], 5);
                          byte g = (byte)((colors[i].B * 29 + colors[i].G * 150 + colors[i].R * 77) >> 8);
                          a.Rect(x + 128, y, 20, 20, Color.FromRgb(g, g, g), 5);
                      }
                      a.Arrow(122, 72, 138, 72, Ink, 1.5);
                  }),
                I("شريط الإجراءات السريعة", "Quick actions bar",
                  "شريط على سطح المكتب تخليه أفقي أو عمودي (كليك يمين ← الاتجاه). تضيف فيه برامج وملفات (اسحبها عليه)، أو من كليك يمين ← أضف: لقطة شاشة، تسجيل الشاشة، كتم المايك، قفل الجهاز، أو أي اختصار كيبورد. زر المايك يصير أحمر لما يكون مكتوم.",
                  "A bar on your desktop, horizontal or vertical (right-click → Direction). Add apps and files (drag them onto it), or from right-click → Add: a screenshot, screen recording, muting the mic, locking the PC, or any keyboard shortcut. The mic button turns red while muted.",
                  "اللي تسويه كل يوم بكليك وحدة، بدون ما تحفظ اختصارات.",
                  "What you do every day in one click, with no shortcuts to remember.",
                  a =>
                  {
                      a.Screen();
                      a.Rect(40, 52, 180, 40, Panel, 10, 0xE0);
                      a.Glyph(62, 72, "", 18, Ink);
                      a.Glyph(96, 72, "", 18, Red);
                      a.Glyph(130, 72, "", 18, Ink);
                      a.Rect(154, 62, 20, 20, Blue, 5);
                      a.Rect(188, 62, 20, 20, Green, 5);
                      a.Cursor(100, 80);
                  }),
                I("موجات الصوت", "Sound waves",
                  "ويدجت موجات تتحرك مع أي صوت يطلع من السماعة، أو مع صوتك لما تتكلم في المايك. كليك يمين عليه ← جهاز الصوت عشان تختار سماعة أو مايك معيّن، و ← شكل الموجات عشان تغيّر بين الأعمدة، الأعمدة من الوسط، الموجات الناعمة، خط الصوت، الدائرة والنقاط. لونها يمشي مع لون الويدجت. لما تختار مايك، ويندوز يبين علامة المايك بالتاسك بار لأنه يسمعه. ولو حطيته على التاسك بار، حرّك عجلة الماوس عليه عشان ترفع صوت الجهاز أو توطّيه.",
                  "A widget with waves that move with any sound from your speakers, or with your voice when you talk into the mic. Right-click it → Sound device to pick a speaker or mic, and → Wave style to switch between bars, bars from the middle, smooth waves, sound line, circle and dots. It uses the widget's color. When a mic is picked, Windows shows the mic sign on the taskbar since it's listening. On a taskbar, scroll the mouse wheel on it to turn the device's volume up or down.",
                  "سطح مكتبك يتحرك مع الأغاني والكلام، وتشوف بعينك إن المايك شغّال.",
                  "Your desktop moves with your music and voice, and you can see your mic is working.",
                  a =>
                  {
                      a.Screen();
                      a.Rect(40, 40, 180, 70, Panel, 10, 0xE0);
                      double[] hs = { 0.3, 0.6, 0.9, 0.7, 0.5, 0.8, 1.0, 0.6, 0.4, 0.7, 0.5, 0.3, 0.6, 0.4, 0.2 };
                      for (int i = 0; i < hs.Length; i++)
                      {
                          double bh = 6 + hs[i] * 44;
                          a.Rect(52 + i * 11, 100 - bh, 7, bh, Blue, 3.5);
                      }
                      a.Glyph(232, 30, "", 16, Ink);
                  }),
                I("التقويم والمواعيد", "Calendar and appointments",
                  "ويدجت يبين تاريخ اليوم بالميلادي أو الهجري (أم القرى) ومواعيدك الجاية بألوانها. اضغط عليه ويفتح التقويم في نص الشاشة: اختر يوم، واكتب اسم الموعد ووقته (أو طول اليوم)، واختر لونه، وهل تبي تنبيه من الويندوز وقته أو قبله بـ 10 أو 30 دقيقة أو ساعة أو يوم (موعد طول اليوم ينبهك الساعة 8 الصبح). اضغط على أي موعد عشان تعدّله، وزر الحذف يطلع لما تمر عليه. تبدّل بين ميلادي وهجري من فوق التقويم أو من كليك يمين على الويدجت، ومن كليك يمين ← يبين تختار: اسم اليوم، السنة، التاريخ الثاني، المواعيد الجاية، والأرقام العربية ١٢٣. التنبيه يشتغل بس وWibn شغّال.",
                  "A widget that shows today's date in Gregorian or Hijri (Umm al-Qura) and your coming appointments in their colors. Click it and the calendar opens in the middle of the screen: pick a day, type the appointment's name and time (or all day), pick its color, and whether you want a Windows notification at its time or 10 or 30 minutes, an hour or a day before (an all-day one reminds you at 8 in the morning). Click an appointment to edit it; the delete button shows when you hover it. Switch between Gregorian and Hijri at the top of the calendar or by right-clicking the widget, and from right-click → Shows pick: day name, year, the other date, coming appointments, and Arabic-Indic digits ١٢٣. Reminders work only while Wibn is running.",
                  "مواعيدك قدامك على سطح المكتب، والجهاز يذكّرك فيها.",
                  "Your appointments right on your desktop, and your PC reminds you of them.",
                  a =>
                  {
                      a.Screen();
                      a.Rect(14, 22, 84, 100, Panel, 10, 0xE0);
                      a.Center(56, 40, L("الخميس", "Thursday"), 10, Blue, true);
                      a.Center(56, 64, "13", 26, Ink, true);
                      a.Center(56, 88, L("نوفمبر", "November"), 10, Ink);
                      a.Center(56, 104, L("22 جمادى الأولى", "22 Jumada I"), 8, Muted);
                      a.Rect(112, 14, 136, 116, Panel, 10, 0xE8);
                      for (int r = 0; r < 5; r++)
                          for (int c = 0; c < 7; c++)
                          {
                              double x = 124 + c * 17, y = 30 + r * 18;
                              if (r == 2 && c == 4) a.Rect(x - 7, y - 7, 14, 14, Blue, 4);
                              a.Dot(x, y, 3, Ink, 0x90);
                          }
                      a.Dot(158, 74, 4, Green);
                      a.Dot(209, 92, 4, Orange);
                      a.Rect(124, 112, 60, 8, Green, 3);
                      a.Glyph(232, 116, "", 11, Ink);
                      a.Cursor(70, 76);
                  }),
                I("قائمة كليك يمين لسطح المكتب", "Desktop right-click menu",
                  "كليك يمين على مكان فاضي في سطح المكتب يفتح قائمة Wibn بنفس شكل ولون الويدجتات (الزجاج أو اللون اللي اخترته لها)، وفيها نفس أوامر ويندوز: عرض (حجم الأيقونات وترتيبها وإظهارها)، فرز حسب، تحديث، لصق، جديد (مجلد، اختصار، مستند نصي)، إعدادات الشاشة، تخصيص، والطرفية. «عرض المزيد من الخيارات» يفتح قائمة ويندوز الأصلية. كليك يمين على أيقونة، أو مع Shift، يبقى قائمة ويندوز. تقدر تطفيها من صفحة الويدجتات.",
                  "Right-clicking an empty spot on the desktop opens Wibn's menu with the widgets' look and color (the glass or the color you picked for them), with Windows' same commands: View (icon size, arranging, showing them), Sort by, Refresh, Paste, New (folder, shortcut, text document), Display settings, Personalize and Terminal. \"Show more options\" opens Windows' own menu. Right-clicking an icon, or with Shift, stays Windows' menu. Turn it off on the Widgets page.",
                  "سطح مكتبك كله بنفس الشكل، حتى القائمة.",
                  "Your whole desktop in one look, menus included.",
                  a =>
                  {
                      a.Screen();
                      a.Rect(70, 20, 130, 104, Panel, 10, 0xE8);
                      for (int r = 0; r < 6; r++) a.Rect(82, 30 + r * 15, r == 1 ? 90 : 70, 6, Ink, 3, 0x90);
                      a.Rect(74, 44, 122, 14, Blue, 6, 0x60);
                  }),
                I("ملفات سطح المكتب من الزاوية", "Desktop files from the corner",
                  "ودّ الماوس لطرف الشاشة اليسار فوق التاسك بار، ويطلع لسان (بس إذا ما فيه برنامج مغطي هالمكان). اضغطه وتطلع قائمة بكل ملفات سطح المكتب. كليك يمين على أي ملف: قص، نسخ، تغيير الاسم، حذف، والخصائص.",
                  "Move the mouse to the left edge just above the taskbar and a tab comes out (only when no app window covers that spot). Click it for a panel with everything on your desktop. Right-click a file to cut, copy, rename, delete it or open its properties.",
                  "توصل لملفات سطح المكتب بسرعة وتتعامل معها مثل المستكشف.",
                  "Reach your desktop files quickly and handle them like in Explorer.",
                  a =>
                  {
                      a.Screen();
                      a.Rect(0, H - 18, W, 18, Panel, 0);
                      a.Rect(0, 40, 22, 92, Panel, 6);
                      a.Rect(0, 40, 6, 92, Panel, 0);
                      var t = a.Center(11, 86, L("سطح المكتب", "Desktop"), 9, Ink, true);
                      t.RenderTransformOrigin = new Point(0.5, 0.5);
                      t.RenderTransform = new RotateTransform(-90);
                      a.Cursor(4, 96);
                      a.Rect(70, 20, 170, 100, Panel, 10, 0xF0);
                      for (int i = 0; i < 4; i++) a.Folder(92 + i * 38, 50, 18);
                      for (int i = 0; i < 3; i++) a.Icon(82 + i * 38, 80, Apps[i]);
                  }),
                I("ترتيب الأنواع في قائمة سطح المكتب", "Arranging the kinds in the desktop panel",
                  "قائمة سطح المكتب تقسّم ملفاتك لأقسام: برامج، ألعاب، مجلدات، ملفات، وكل قسم بعنوانه. اضغط زر الترتيب فوق (جنب الدبوس) ويطلع لك الأقسام، اسحب أي واحد لفوق أو لتحت وحطه بالترتيب اللي تبيه، والقائمة تعرض الأقسام بهالترتيب على طول وتتذكره. اختصارات ألعاب ستيم وإيبك وغيرها تنحسب ألعاب، واختصار المجلد ينحسب مجلد. ولو انحسب شي غلط (مثل لعبة متثبتة بمجلد خاص)، كليك يمين عليه ← \"تصنيف كـ…\" واختر لعبة أو برنامج أو ملف، وينتقل لقسمه على طول ويتذكره Wibn.",
                  "The desktop panel splits your files into sections: programs, games, folders, files, each under its own title. Click the arrange button at the top (by the pin) to see the sections; drag any of them up or down into the order you want, and the panel shows them in that order right away and remembers it. Shortcuts to Steam, Epic and other games count as games, and a shortcut to a folder counts as a folder. If something lands in the wrong section (like a game installed in a folder of its own), right-click it → \"Classify as…\" and pick game, program or file: it moves to that section right away and Wibn remembers it.",
                  "تلقى اللي تبيه أول شي: ألعابك فوق لو أنت تلعب، أو برامج الشغل لو تشتغل.",
                  "What you want comes first: your games on top if you play, your work apps if you work.",
                  a =>
                  {
                      a.Screen();
                      a.Rect(70, 20, 170, 100, Panel, 10, 0xF0);
                      a.Rect(105, 34, 100, 16, Muted, 5, 0x60);
                      a.Rect(105, 56, 100, 16, Blue, 5);
                      a.Rect(105, 78, 100, 16, Muted, 5, 0x60);
                      a.Cursor(190, 66);
                  }),
            });

        private static HelpPage Glass() => new("Liquid Glass",
            L("التأثير الزجاجي لكل الميزات من مكان واحد.", "The glass look for every feature, from one place."),
            new[]
            {
                I("زجاج مثل الآيفون", "Glass like the iPhone",
                  "شريط التحميل وقوائم الملفات وتصوير الشاشة والويدجتات يصير لها خلفية زجاجية تبين اللي وراها مغبّش ومنحني عند الأطراف. وتعدّل قوة التغبيش والانحناء والتعتيم.",
                  "The download bar, file menus, screen capture and widgets get a glass background that shows what's behind, blurred and bent at the edges. Adjust the blur, bend and tint.",
                  "شكل حديث وناعم يمشي مع خلفيتك.",
                  "A modern, smooth look that suits your wallpaper.",
                  a =>
                  {
                      a.Screen();
                      a.Dot(60, 50, 70, Orange);
                      a.Dot(200, 110, 90, Purple);
                      a.Rect(50, 36, 160, 78, Colors.White, 18, 0x30, Colors.White);
                      a.Center(130, 75, "Liquid Glass", 14, Ink, true);
                  }),
            });

        private static HelpPage Animations() => new(L("الحركات", "Animations"),
            L("حركة ظهور الويدجتات وتصغير النوافذ وسرعة فتحها.", "How widgets come in, how windows minimize, and how fast they open."),
            new[]
            {
                I("ظهور الويدجتات تدريجيًا", "Widgets fading in",
                  "لما يشتغل Wibn أو تضيف ويدجت، كل ويدجت يبدأ مخفي ويظهر شوي شوي، والويدجتات تظهر وحدة ورا الثانية من أعلى الشاشة لتحت. تشغّلها أو تطفيها، وتختار مدة الحركة (أو تضغط سريعة، عادية، بطيئة، بطيئة جدًا)، والفرق بين كل ويدجت والثاني (0 = كلها مع بعض). زر \"جرّبها الحين\" يعيدها على الويدجتات اللي قدامك. بعد ما تخلص الحركة ما يبقى منها شي في الرام.",
                  "When Wibn starts or you add a widget, each widget starts hidden and fades in, one after another from the top of the screen. Turn it on or off, pick how long it takes (or press Fast, Normal, Slow, Very slow), and the wait between one widget and the next (0 = all together). \"Try it now\" plays it again on the widgets on screen. Once it's done, nothing of it stays in RAM.",
                  "سطح مكتبك يطلع بشكل مرتب وناعم، بالسرعة اللي تحبها.",
                  "Your desktop comes in neat and smooth, at the speed you like.",
                  a =>
                  {
                      a.Screen();
                      a.Rect(30, 20, 90, 50, Panel, 10, 0xE0);
                      a.Rect(140, 20, 90, 50, Panel, 10, 0x90);
                      a.Rect(30, 84, 90, 30, Panel, 10, 0x60);
                      a.Arrow(250, 24, 250, 110, Ink, 1.5);
                  }),
                I("حركة تصغير النوافذ", "Minimize animation",
                  "من صفحة الحركات ← حركة التصغير اختر كيف تختفي أي نافذة لما تصغّرها: جيني (مثل الماك: النافذة تنثني وتنسحب لأيقونتها في التاسك بار)، أو تصغر للأيقونة، أو تلاشي، أو تطيح لتحت، أو تبعد وتختفي. وتختار مدتها، و\"جرّبها على نافذة Wibn\" يصغّر نافذة Wibn عشان تشوفها. لو تاسك بار Wibn شغّال تروح النافذة لأيقونتها بالضبط، وإلا تروح للتاسك بار تحتها. Wibn يطفي حركة ويندوز للتصغير والتكبير وتبقى حركات Wibn بس، ولما تسكّر Wibn ترجع حركة ويندوز. لما ترجّع النافذة من التاسك بار تطلع على طول بدون حركة.",
                  "On the Animations page → Minimize animation, pick how any window goes away when you minimize it: genie (like the Mac: the window bends and is pulled into its taskbar icon), shrink into the icon, fade, fall down, or zoom away. Pick how long it takes; \"Try it on Wibn's window\" minimizes Wibn's window so you can see it. With Wibn's taskbar on, the window goes right into its icon; otherwise into the taskbar below it. Wibn turns off Windows' own minimize and maximize animation so only Wibn's play; close Wibn and Windows' animation comes back. Restoring a window from the taskbar shows it at once, with no animation.",
                  "نوافذك تختفي بحركة حلوة مثل الماك.",
                  "Your windows go away with a nice Mac-like animation.",
                  a =>
                  {
                      a.Screen();
                      a.Rect(60, 16, 140, 50, Panel, 10, 0xE8);
                      a.Rect(95, 76, 70, 22, Panel, 8, 0x90);
                      a.Rect(118, 108, 24, 14, Panel, 5, 0x60);
                      a.Arrow(130, 66, 130, 106, Ink, 1.5);
                  }),
                I("سرعة فتح النوافذ", "How fast windows open",
                  "نافذة سطح المكتب ونافذة التقويم تفتح بحركة تكبر شوي وتبان. تختار كم تاخذ، وتتسكّر بأسرع من كذا شوي. 0 = تطلع على طول بدون حركة.",
                  "The desktop's and the calendar's windows open by growing a little as they fade in. Pick how long it takes; closing is a bit quicker. 0 = they show at once with no animation.",
                  "النوافذ تفتح بالسرعة اللي تريّحك.",
                  "Windows open at the pace you like.",
                  a =>
                  {
                      a.Screen();
                      a.Rect(60, 30, 140, 90, Panel, 12, 0xE8);
                      a.Rect(80, 44, 100, 62, Panel, 10, 0x60);
                      a.Center(130, 75, "0.17s", 13, Ink, true);
                  }),
            });

        private static HelpPage WallpapersHelp() => new(L("الخلفيات", "Wallpapers"),
            L("خلفياتك كلها في مكان واحد، وتبدّل بينها بضغطة.", "All your wallpapers in one place, switched with one key press."),
            new[]
            {
                I("قائمة الخلفيات بنص الشاشة", "The wallpaper list in the middle of the screen",
                  "أضف صور أو مقاطع (أو مجلد كامل، أو اسحبها على الصفحة). اضغط الاختصار (Ctrl + Alt + W، وتقدر تغيّره) وتطلع خلفياتك بنص الشاشة مثل لينكس: الخلفية المختارة كبيرة بالنص، واللي حولها أصغر وأغمق. تنقّل بالأسهم أو عجلة الماوس، Home و End للأول والأخير، واضغط Enter (أو كليك على اللي بالنص) وتصير خلفيتك، وEsc أو كليك برّا يسكّرها. وفي الصفحة: كليك على أي خلفية يطبّقها، ووقف عليها بالماوس عشان تقدّمها أو تأخرها أو تشيلها من القائمة (الملف نفسه يبقى).",
                  "Add pictures or videos (or a whole folder, or drag them onto the page). Press the shortcut (Ctrl + Alt + W; you can change it) and your wallpapers come up in the middle of the screen, Linux style: the chosen one big in the middle, the ones around it smaller and darker. Move with the arrows or the mouse wheel, Home and End for the first and last, and press Enter (or click the middle one) to set it; Esc or a click outside closes it. On the page: click any wallpaper to set it, and hover it to move it earlier or later or take it off the list (the file itself stays).",
                  "تغيّر جو جهازك بثانية بدون ما تدوّر في المجلدات وإعدادات ويندوز.",
                  "Change your PC's mood in a second, without digging through folders and Windows settings.",
                  a =>
                  {
                      a.Screen();
                      a.Rect(0, 0, W, H, Back, 0, 0x90);
                      a.Rect(14, 48, 56, 32, Purple, 6, 0x70);
                      a.Rect(W - 70, 48, 56, 32, Orange, 6, 0x70);
                      a.Rect(80, 36, 112, 60, Blue, 9, 0xFF, Blue, 3);
                      a.Keys(96, 108, "←", "→", "Enter");
                  }),
                I("حركة التبديل والخلفيات المتحركة", "The transition and animated wallpapers",
                  "الخلفية الجديدة تدخل بحركة تختارها: دائرة تكبر من النص، أو معيّن يكبر، أو تلاشي، أو مسح ناعم، أو موجة، أو انزلاق، أو ستارة، أو مربعات، أو حركة عشوائية كل مرة، أو بدون. والمقاطع (MP4، WebM، MOV…) تصير خلفية متحركة تحت أيقونات سطح المكتب، صوتها مكتوم وتعيد لحالها، ولما تكبّر نافذة على الشاشة الرئيسية أو يكون شي بملء الشاشة تصير صورة ثابتة (آخر إطار منها) وتتسكّر عشان ترجع الرام والمعالج مثل الخلفية العادية، وتكمل من نفس اللحظة لما تصغّر النافذة (تقدر تطفي هالشي). كرت الشاشة يشغّلها، وتختار كم إطار بالثانية: توفير (15) أو عادي (30) أو ناعم (60). ويندوز ياخذ صورة منها كخلفيته، فلو سكّرت Wibn يبقى سطح المكتب بنفس الشكل، ولما يشتغل Wibn ترجع تتحرك.",
                  "The new wallpaper comes in with the animation you pick: a circle growing from the middle, a growing diamond, a fade, a soft wipe, a wave, a slide, blinds, tiles, a random one each time, or none. Videos (MP4, WebM, MOV…) become an animated wallpaper under the desktop icons, muted and looping, and when a window is maximized on the main screen or something is full screen they turn into a still picture (their last frame) and close, so memory and processor use go back to a normal wallpaper's; they carry on from the same moment when you restore the window (you can turn this off). The graphics card plays them, and you pick how many frames a second: Saver (15), Normal (30) or Smooth (60). Windows gets a frame of it as its own wallpaper, so if you close Wibn the desktop still looks the same, and it moves again when Wibn starts.",
                  "سطح مكتب حي وأنيق، بدون برنامج خلفيات ثاني يشتغل بالخلفية.",
                  "A lively, elegant desktop, without another wallpaper app running in the background.",
                  a =>
                  {
                      a.Screen();
                      a.Dot(130, 66, 90, Purple, 0xD0);
                      a.Dot(130, 66, 50, Orange, 0xD0);
                      a.Glyph(130, 66, "\uE768", 20, Ink);
                  }),
            });

        private static HelpPage General() => new(L("الإعدادات العامة", "General settings"),
            L("إعدادات البرنامج نفسه.", "Settings for the app itself."),
            new[]
            {
                I("حفظ الإعدادات في ملف", "Settings in a file",
                  "\"احفظ في ملف\" يحط كل إعدادات Wibn في ملف صغير (.wibn): التاسك بار والأشرطة والويدجتات والزجاج والاختصارات والمواعيد وغيرها. و\"جيب من ملف\" يرجّعها منه، على نفس الجهاز أو جهاز ثاني: Wibn يعيد التشغيل وتصير إعداداته مثل اللي في الملف، وإعداداتك اللي كانت قبل تنحفظ احتياط في settings-backup.wibn داخل مجلد Wibn. الملف فيه الإعدادات بس: ما فيه برامجك (البرامج المثبتة في التاسك بار، وبرامج وملفات ودجة الاختصارات والشريط السريع) لأنها ممكن ما تكون عند غيرك، ولا فيه تسجيل دخولك لتحميل المقاطع، فتقدر تشاركه.",
                  "\"Save to a file\" puts all of Wibn's settings in one small file (.wibn): the taskbar, the bars, widgets, glass, shortcuts, appointments and more. \"Load from a file\" brings them back, on the same PC or another one: Wibn restarts with the file's settings, and the ones you had are kept first in settings-backup.wibn in Wibn's folder. The file holds settings only: not your programs (apps pinned to the taskbar, the programs and files in shortcut and quick bar widgets), since others may not have them, nor your sign-in for downloading videos, so it's safe to share.",
                  "تنقل Wibn بشكله كامل لجهاز جديد، أو ترجع لإعداداتك لو غيّرت شي وما عجبك.",
                  "Move Wibn just as you set it up to a new PC, or go back to your settings after a change you didn't like.",
                  a =>
                  {
                      a.Screen();
                      a.Rect(70, 30, 120, 76, Panel, 10, 0xE0);
                      a.Rect(84, 46, 60, 8, Ink, 3, 0xC0);
                      a.Rect(84, 62, 90, 6, Muted, 3, 0x90);
                      a.Rect(84, 76, 80, 6, Muted, 3, 0x90);
                      a.Rect(150, 88, 30, 12, Blue, 4);
                  }),
                I("خيار التثبيت في قائمة النافذة", "Pin option in the window menu",
                  "شغّل \"خيار التثبيت في قائمة النافذة\" من الإعدادات العامة، وبعدها كليك يمين على شريط عنوان أي نافذة (أو Alt+Space) تطلع قائمتها اللي فيها التصغير والتكبير والإغلاق، وفيها خيار جديد قبل الإغلاق: \"Pin on top\" مع أيقونة دبوس. تختاره وتصير النافذة فوق كل النوافذ حتى لو ضغطت على غيرها، ويطلع جنبه علامة صح. تختاره مرة ثانية وترجع النافذة عادية. يطلع الخيار في النافذة من أول ما تصير قدامك. بعض البرامج اللي تسوي قائمة شريط العنوان بنفسها (مثل كروم) ما يطلع فيها، ونوافذ البرامج اللي تشتغل كمسؤول ما يقدر Wibn يثبّتها. لو طفيت الخيار أو سكّرت Wibn ينشال من القوائم. ونافذة Wibn نفسها ما تبقى فوق النوافذ إلا إذا ثبّتها من قائمتها.",
                  "Turn on \"Pin option in the window menu\" on the General page; then right-click any window's title bar (or press Alt+Space) and its menu, the one with minimize, maximize and close, has a new item before Close: \"Pin on top\" with a pin icon. Pick it and the window stays above all the others, even when you click another one, with a check mark beside the item. Pick it again and the window is normal again. The item shows in a window once it has been in front. Some apps that draw their own title-bar menu (like Chrome) don't show it, and windows of apps run as admin can't be pinned by Wibn. Turn the option off or close Wibn and the item goes from the menus. Wibn's own window never stays above the others unless you pin it from its menu.",
                  "تخلي فيديو أو ملاحظة أو حاسبة دايم قدامك وأنت تشتغل على شي ثاني.",
                  "Keep a video, a note or a calculator always in front while you work on something else.",
                  a =>
                  {
                      a.Screen();
                      a.Rect(40, 24, 180, 92, Panel, 10, 0xE0);
                      a.Rect(160, 30, 12, 8, Blue, 3);
                      a.Rect(178, 30, 36, 8, Muted, 3, 0x90);
                      a.Rect(52, 50, 120, 8, Ink, 3, 0xC0);
                      a.Rect(52, 66, 90, 6, Muted, 3, 0x90);
                  }),
                I("المفضلة والإخفاء في القائمة", "Favorites and hiding in the menu",
                  "اضغط كليك يمين على أي صفحة في القائمة الجانبية، أو على أي خيار داخل الصفحات: \"إضافة للمفضلة\" يحط له اختصار فوق في القائمة الجانبية تحت \"خيارات مفضلة\" ويبقى بمكانه الأصلي بعد (الخيار يفتح صفحته ويوريك مكانه)، و\"إخفاء\" يشيله. ترجّع المخفية من كليك يمين ← \"الخيارات المخفية\". واضغط على اسم أي تصنيف (أو السهم جنبه) عشان تطوي صفحاته أو تفتحها.",
                  "Right-click any page in the side menu, or any option inside a page: \"Add to favorites\" puts a shortcut to it at the top of the side menu under \"Favorites\", and it stays in its own place too (an option opens its page and shows where it is), and \"Hide\" takes it away. Bring hidden ones back with right-click → \"Hidden\". Click a group's name (or the arrow by it) to fold or open its pages.",
                  "اللي تستخدمه كثير يكون أول شي، واللي ما تحتاجه ما يزحم القائمة.",
                  "What you use most comes first, and what you don't need stays out of the way.",
                  a =>
                  {
                      a.Screen();
                      a.Rect(60, 22, 90, 92, Panel, 8, 0xE0);
                      a.Rect(70, 32, 40, 5, Muted, 2, 0x90);
                      a.Rect(70, 44, 66, 8, Blue, 3);
                      a.Rect(70, 60, 40, 5, Muted, 2, 0x90);
                      a.Rect(70, 72, 66, 8, Ink, 3, 0xA0);
                      a.Rect(70, 88, 66, 8, Ink, 3, 0xA0);
                      a.Rect(130, 70, 70, 34, Panel, 6, 0xF0);
                      a.Rect(138, 78, 50, 6, Ink, 3, 0xC0);
                      a.Rect(138, 92, 40, 6, Ink, 3, 0xC0);
                  }),
                I("ادعم Wibn", "Support Wibn",
                  "Wibn مجاني. في الإعدادات العامة ← ادعم Wibn، زر \"Buy me a coffee\" يفتح صفحة PayPal في المتصفح، وتقدر تتبرع بالمبلغ اللي تبيه. ما يحتاج حساب في Wibn، وما ينرسل أي شي من جهازك.",
                  "Wibn is free. On the General page → Support Wibn, the \"Buy me a coffee\" button opens a PayPal page in your browser where you can give any amount. No Wibn account is needed, and nothing is sent from your PC.",
                  "تساعد Wibn يكمل ويتطور.",
                  "You help Wibn keep going and get better.",
                  a =>
                  {
                      a.Screen();
                      a.Rect(60, 30, 140, 76, Panel, 10, 0xE0);
                      a.Rect(74, 44, 80, 8, Ink, 3, 0xC0);
                      a.Rect(74, 58, 100, 6, Ink, 3, 0x80);
                      a.Rect(74, 78, 60, 18, Blue, 5);
                  }),
                I("صندوق الاقتراحات والشكاوى", "Suggestions and complaints box",
                  "في الإعدادات العامة ← صندوق الاقتراحات، اختر النوع (اقتراح، شكوى، أو مشكلة في البرنامج)، اكتب رسالتك واضغط \"أرسل\". توصل للمطوّر بس، وما ينرسل معها اسمك ولا اسم جهازك ولا أي ملف: بس رقم نسخة Wibn واللغة. رسالة وحدة كل دقيقة. Wibn ما فيه أي كلمة سر أو رابط سري: الرسالة تروح لوسيط عند المطوّر هو اللي يوصلها، ويمنع السبام، والروابط اللي فيها ما تنضغط.",
                  "On the General page → Suggestion box, pick the kind (suggestion, complaint or bug in the app), write your message and press \"Send\". Only the developer gets it, and your name, your PC's name and no files go with it: just Wibn's version and the language. One message a minute. Wibn holds no password or secret link: the message goes to a relay of the developer's that passes it on, stops spam, and makes links in it unclickable.",
                  "صوتك يوصل للمطوّر مباشرة، بدون حساب وبدون ما تكشف نفسك.",
                  "Your voice reaches the developer directly, with no account and without giving yourself away.",
                  a =>
                  {
                      a.Screen();
                      a.Rect(50, 26, 160, 90, Panel, 10, 0xE0);
                      a.Rect(62, 38, 60, 14, Line, 4);
                      a.Rect(62, 58, 136, 30, Ink, 4, 0x30);
                      a.Rect(62, 94, 44, 16, Blue, 5);
                  }),
                I("يشتغل مع ويندوز", "Starts with Windows",
                  "البرنامج يشتغل لحاله جنب الساعة أول ما يقلع الجهاز، وإغلاق النافذة يخليه يشتغل في الخلفية.",
                  "The app starts by the clock as soon as the PC starts, and closing the window keeps it running in the background.",
                  "كل الميزات جاهزة دايمًا بدون ما تتذكر تفتحه.",
                  "Every feature is always ready without remembering to open it.",
                  a =>
                  {
                      a.Screen();
                      a.Taskbar(Apps[..4], 0, x0: 60, size: 14);
                      a.Rect(196, H - 21, 18, 18, Blue, 4);
                      a.Arrow(205, 90, 205, H - 28, Ink);
                      a.Glyph(205, 70, "", 24, Green);
                  }),
                I("المزايا والتحديثات", "Features and updates",
                  "تشغّل أو تطفي أي ميزة كاملة، واللي تطفيها تختفي من القائمة وما تشتغل في الخلفية. والتحديثات تنزل من هنا.",
                  "Turn whole features on or off; the ones you turn off disappear from the menu and do nothing in the background. Updates come from here too.",
                  "البرنامج يصير خفيف وفيه بس اللي تستخدمه.",
                  "The app stays light, with only what you use.",
                  a =>
                  {
                      string[] names = { L("التاسك بار", "Taskbar"), L("التنظيف", "Cleanup"), L("تحميل المقاطع", "Downloads"), "Widgets" };
                      bool[] on = { true, true, false, true };
                      for (int i = 0; i < 4; i++)
                      {
                          a.Text(18, 14 + i * 30, names[i], 11, Ink);
                          a.Switch(150, 16 + i * 30, on[i]);
                      }
                      a.Glyph(220, 70, "", 28, Blue);
                  }),
            });
    }
}
