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
        private static string L(string ar, string en) => Lang.English ? en : ar;

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
            "transfer" => Transfer(),
            "capture" => Capture(),
            "volume" => Volume(),
            "specs" => Specs(),
            "phone" => Phone(),
            "game" => GameHelp(),
            "widgets" => WidgetsHelp(),
            "glass" => Glass(),
            "anim" => Animations(),
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
                  "يخفي تاسك بار ويندوز ويحط بداله تاسك بار من Wibn: ستارت والبحث، برامجك المثبتة والمفتوحة (كليك يفتحها أو يصغّرها، وقف عليها بالماوس وتشوف صورة حية لنوافذها)، ولغة الكيبورد (كليك عليها يغيّرها)، والنت والصوت (حرّك عجلة الماوس عليه عشان ترفع أو تنزّل الصوت) والساعة. تختار شكله: كامل أو عائم (الويدجتات اللي على الأطراف تتحاذى معه، وبينه وبين الويدجتات نفس المسافة اللي بين الويدجتات) أو دوك بالنص، وسماكته، وينزل ويختفي لما تكبّر نافذة أو يكون برنامج أو لعبة بملء الشاشة (النافذة المكبّرة تاخذ الشاشة كلها) (ودّ الماوس لآخر الشاشة تحت ويطلع، وشيل الماوس عنه وينزل، وتقدر تطفي هالشي)، والخلفية زجاج (بإعدادات صفحة Liquid Glass) أو لون أو شفاف، وحجم الأيقونات ومكانها. اسحب أي برنامج عليه عشان تثبّته، واسحب المثبتة عشان ترتبها. ولو تبيه أقصر، اسحبه من طرفه اليمين أو اليسار لجوّا (الطرفين يتحركون مع بعض ويبقى بالنص)، ودبل كليك على الطرف يرجّعه بطوله الكامل. وأيقونات البرامج اللي جنب الساعة (ديسكورد، ستيم…) تطلع فيه نفسه: تختار من صفحة التاسك بار أيها يبان، والباقي تحت السهم. كليك وكليك يمين عليها مثل ويندوز بالضبط.",
                  "Hides Windows' taskbar and puts Wibn's in its place: Start and search, your pinned and open apps (click to open or minimize; hover to see live pictures of their windows), the keyboard language (click it to switch), network and sound (scroll the mouse wheel on it to change the volume) and the clock. Pick its shape: full, floating (the widgets at the far sides line up with it, and it sits as far from the widgets as they are from each other) or a centered dock, and its thickness; it slides down out of sight when a window is maximized (it then takes the whole screen) or an app or game is full screen (move the mouse to the bottom edge to slide it up, move away and it slides down, and you can turn this off); a glass (with the Liquid Glass page's settings), color or see-through background; and the icons' size and position. Drag any app onto it to pin it, and drag pinned ones to reorder them. To make it shorter, drag its right or left end inward (both ends move together and it stays centered); double-click an end to make it full length again. The icons next to the clock (Discord, Steam…) show on it too: choose on the Taskbar page which ones show, the rest go under the arrow. Click and right-click them just like on Windows.",
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
                  "تعطي الشريط لون بالضبط اللي تختاره، أو تخليه زجاجي يبين اللي وراه مغبّش، أو شفاف.",
                  "Give the bar exactly the color you pick, or make it glass that blurs what's behind, or see-through.",
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
                  "الشريط يختفي لما يكون عندك برنامج مكبّر أو ملء الشاشة، ويرجع لما تودّي الماوس تحت. وتقدر تضيف السنة للتاريخ.",
                  "The bar hides when a window is maximized or full screen, and comes back when the mouse reaches the bottom. You can also add the year to the date.",
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
                  "تغيّر لون مؤشر الماوس وحجمه.", "Change the mouse pointer's color and size.",
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
                  "كل برنامج يشتغل مع الجهاز له مفتاح، تطفيه ويوقف يشتغل لحاله (ويبقى موجود).",
                  "Every app that starts with the PC has a switch; turn it off and it stops starting by itself (it stays installed).",
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
            });

        private static HelpPage Transfer() => new(L("إرسال الملفات", "Send files"),
            L("ترسل أي ملف لأي أحد عبر الإنترنت.", "Send any file to anyone over the internet."),
            new[]
            {
                I("من جهاز لجهاز مباشرة", "Straight from PC to PC",
                  "تختار الملف ويطلع لك رمز، ترسله للشخص ويحطه عنده وينزل الملف مباشرة من جهازك.",
                  "Pick the file and get a code; the other person enters it and the file comes straight from your PC.",
                  "بدون حد للحجم ولا رفع على مواقع، والملف ما يمر على أحد.",
                  "No size limit, no uploading to websites, and the file doesn't pass through anyone.",
                  a =>
                  {
                      a.Monitor(14, 30);
                      a.Monitor(166, 30);
                      a.Arrow(100, 56, 160, 56, Green);
                      a.Rect(88, 104, 84, 22, Panel, 6, 255, Line);
                      a.Center(130, 115, "blob-42-sun", 10, Ink, true);
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

        private static HelpPage Phone() => new(L("النسخ من الجوال", "Copy from the phone"),
            L("الحافظة بين جوالك والكمبيوتر.", "The clipboard between your phone and PC."),
            new[]
            {
                I("انسخ هنا والصق هناك", "Copy here, paste there",
                  "تنسخ في الجوال وترسله لحافظة الكمبيوتر بضغطة، أو تاخذ المنسوخ من الكمبيوتر للجوال. كله داخل شبكة بيتك.",
                  "Copy on the phone and send it to the PC's clipboard with one tap, or take what the PC copied to the phone. All inside your home network.",
                  "روابط وأكواد ونصوص تنتقل بلحظة، بدون ما ترسلها لنفسك في الواتس.",
                  "Links, codes and text move in a moment, without messaging them to yourself.",
                  a =>
                  {
                      a.Phone(26, 32);
                      a.Monitor(160, 40);
                      a.Arrow(84, 62, 150, 62, Green);
                      a.Arrow(150, 92, 84, 92, Blue);
                      a.Glyph(117, 128, "", 16, Muted);
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
            });

        private static HelpPage WidgetsHelp() => new("Widgets",
            L("نوافذ صغيرة على سطح المكتب.", "Small windows on your desktop."),
            new[]
            {
                I("ويدجتات تتحاذى مع الأيقونات", "Widgets that line up with your icons",
                  "تضيف ويدجتات مثل الطقس ومواقيت الصلاة والتقويم واستهلاك الجهاز وسرعة الإنترنت وموجات الصوت، وتتحاذى مع أيقونات سطح المكتب. تسحبها وتكبّرها من زاويتها. لما تكبّر نافذة أو يصير شي بملء الشاشة، الويدجتات اللي على نفس الشاشة تختفي عشان تخفف الرام والمعالج، وترجع أول ما تصغّرها. ولما يشتغل Wibn، الويدجتات تطلع وحدة ورا الثانية من فوق لتحت.",
                  "Add widgets like weather, prayer times, a calendar, PC usage, internet speed and sound waves; they line up with your desktop icons. Drag them and resize from their corner. When a window is maximized or something is full screen, the widgets on that screen hide to save RAM and CPU, and come back as soon as it is restored. When Wibn starts, the widgets appear one after another from top to bottom.",
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
                  "شريط على سطح المكتب تخليه أفقي أو عمودي (كليك يمين ← الاتجاه). تضيف فيه برامج وملفات (اسحبها عليه)، أو من كليك يمين ← أضف: لقطة شاشة، تسجيل الشاشة، كتم المايك، كتم نفسك في ديسكورد، قفل الجهاز، أو أي اختصار كيبورد. زر المايك يصير أحمر لما يكون مكتوم. لديسكورد: حط نفس الاختصار (Ctrl+Shift+M للمايك و Ctrl+Shift+D للصوت) في ديسكورد ← الإعدادات ← Keybinds عشان يشتغل وديسكورد بالخلفية.",
                  "A bar on your desktop, horizontal or vertical (right-click → Direction). Add apps and files (drag them onto it), or from right-click → Add: a screenshot, screen recording, muting the mic, muting yourself in Discord, locking the PC, or any keyboard shortcut. The mic button turns red while muted. For Discord: set the same shortcut (Ctrl+Shift+M for the mic, Ctrl+Shift+D for sound) in Discord → Settings → Keybinds so it works while Discord is in the background.",
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
                  "ويدجت موجات تتحرك مع أي صوت يطلع من السماعة، أو مع صوتك لما تتكلم في المايك. كليك يمين عليه ← جهاز الصوت عشان تختار سماعة أو مايك معيّن، و ← شكل الموجات عشان تغيّر بين الأعمدة، الأعمدة من الوسط، الموجات الناعمة، خط الصوت، الدائرة والنقاط. لونها يمشي مع لون الويدجت. لما تختار مايك، ويندوز يبين علامة المايك بالتاسك بار لأنه يسمعه.",
                  "A widget with waves that move with any sound from your speakers, or with your voice when you talk into the mic. Right-click it → Sound device to pick a speaker or mic, and → Wave style to switch between bars, bars from the middle, smooth waves, sound line, circle and dots. It uses the widget's color. When a mic is picked, Windows shows the mic sign on the taskbar since it's listening.",
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
            L("حركة ظهور الويدجتات وسرعة فتح النوافذ.", "How widgets come in, and how fast windows open."),
            new[]
            {
                I("ظهور الويدجتات من فوق لتحت", "Widgets showing from the top down",
                  "لما يشتغل Wibn أو تضيف ويدجت، كل ويدجت يطلع من فوق لتحت، والويدجتات تطلع وحدة ورا الثانية من أعلى الشاشة لتحت. تشغّلها أو تطفيها، وتختار مدة الحركة (أو تضغط سريعة، عادية، بطيئة، بطيئة جدًا)، والفرق بين كل ويدجت والثاني (0 = كلها مع بعض). زر \"جرّبها الحين\" يعيدها على الويدجتات اللي قدامك. بعد ما تخلص الحركة ما يبقى منها شي في الرام.",
                  "When Wibn starts or you add a widget, each widget shows from the top down, one after another from the top of the screen. Turn it on or off, pick how long it takes (or press Fast, Normal, Slow, Very slow), and the wait between one widget and the next (0 = all together). \"Try it now\" plays it again on the widgets on screen. Once it's done, nothing of it stays in RAM.",
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

        private static HelpPage General() => new(L("الإعدادات العامة", "General settings"),
            L("إعدادات البرنامج نفسه.", "Settings for the app itself."),
            new[]
            {
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
