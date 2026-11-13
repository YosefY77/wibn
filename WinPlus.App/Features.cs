using System;
using System.Linq;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// The features the user chose (in the installer, or later in Settings → Features).
    /// A feature that's off doesn't show in the side menu and starts nothing in the
    /// background. Heavy extras (FFmpeg, sendme) are only downloaded for the
    /// features that need them.
    /// Stored in HKCU\Software\WinPlus\Features (the installer writes it); a
    /// missing value means the feature's default: on, except the newer ones that
        /// start off (e.g. a build run straight from the source, or an update).
    /// </summary>
    internal static class Features
    {
        public sealed record Feature(string Id, string Name, string Description, string[] NavKeys, string? Download = null, bool DefaultOn = true);

        public static readonly Feature[] All =
        {
            new("taskbar", Lang.T("التاسك بار"), Lang.T("سكرول بدل (...)، لون، حجم الأيقونات، السنة، الإخفاء مع ملء الشاشة"), new[] { "taskbar" }),
            new("desktop", Lang.T("سطح المكتب"), Lang.T("لون مربع التحديد ومؤشر الماوس"), new[] { "desktop" }),
            new("cleanup", Lang.T("التنظيف"), Lang.T("أكبر الملفات، الملفات المؤقتة، المكررة، سلة المهملات"), new[] { "big", "temp", "dup", "bin" }),
            new("hotkeys", Lang.T("اختصارات الكيبورد"), Lang.T("اختصاراتك الخاصة في أي مكان"), new[] { "hotkeys" }),
            new("specs", Lang.T("مواصفات الجهاز"), Lang.T("كل مواصفات جهازك: المعالج، الرام وكل قطعة فيها، كرت الشاشة، الأقراص، الشاشات، اللوحة الأم وويندوز"), new[] { "specs" }),
            new("ram", Lang.T("استهلاك الرام"), Lang.T("أكثر البرامج استهلاكًا للرام"), new[] { "ram" }),
            new("lock", Lang.T("قفل الملفات"), Lang.T("قفل الملفات والمجلدات بكلمة سر"), new[] { "lock" }),
            new("startup", Lang.T("برامج بدء التشغيل"), Lang.T("طفّي البرامج اللي تشتغل مع الجهاز"), new[] { "startup" }),
            new("downloads", Lang.T("ترتيب التحميلات"), Lang.T("توزيع الملفات على مجلدات حسب النوع"), new[] { "downloads" }),
            new("convert", Lang.T("تحويل الصيغ"), Lang.T("اسحب ملف واضغط Space. المقاطع والصوت تحتاج FFmpeg (تقريبًا 140 ميقا)"), new[] { "convert" }, "ffmpeg"),
            new("resize", Lang.T("تغيير الأبعاد"), Lang.T("اسحب صورة أو مقطع واضغط R"), new[] { "resize" }),
            new("compress", Lang.T("ضغط الملفات"), Lang.T("اسحب ملف واضغط C، بدون ما يكبر الملف أبد"), new[] { "compress" }),
            new("archive", Lang.T("ملف مضغوط"), Lang.T("اسحب ملفات أو مجلد واضغط Z ويصير ZIP أو 7Z أو RAR"), new[] { "archive" }, "7zip"),
            new("video", Lang.T("تحميل المقاطع"), Lang.T("Ctrl+H والصق رابط من يوتيوب، انستا، تويتر، ريديت، تيك توك، سبوتيفاي. يحتاج yt-dlp (تقريبًا 18 ميقا)"), new[] { "video" }, "ytdlp"),
            new("transfer", Lang.T("إرسال الملفات"), Lang.T("إرسال مباشر عبر الإنترنت. يحتاج sendme (كم ميقا)"), new[] { "transfer" }, "sendme"),
            new("capture", Lang.T("تصوير الشاشة"), Lang.T("صورة لمكان تحدده أو للشاشة كاملة، تسجيل فيديو، وحفظ آخر دقيقة. كل نوع باختصار تختاره. الفيديو يحتاج FFmpeg"), new[] { "capture" }, DefaultOn: false),
            new("phone", Lang.T("النسخ من الجوال"), Lang.T("انسخ في الآيفون أو أي جوال، وأرسله لحافظة الكمبيوتر بضغطة (وبالعكس). على نفس الواي فاي"), new[] { "phone" }, DefaultOn: false),
            new("volume", Lang.T("أصوات البرامج"), Lang.T("علّي أو خفّض صوت كل برنامج مفتوح لحاله، أو اكتمه"), new[] { "volume" }),
            new("game", Lang.T("وضع الألعاب"), Lang.T("Game Booster: يوقف خدمات الخلفية مثل Windows Update و SysMain أثناء اللعب ويرجّعها بعدها"), new[] { "game" }),
            new("widgets", "Widgets", Lang.T("نوافذ صغيرة على سطح المكتب تتحاذى مع الأيقونات، أولها أصوات البرامج"), new[] { "widgets" }),
            new("glass", "Liquid Glass", Lang.T("التأثير الزجاجي لشريط التحميل وقوائم الملفات وتصوير الشاشة والويدجتات، كله من مكان واحد"), new[] { "glass" }),
        };

        private const string Key = @"Software\WinPlus\Features";

        public static bool IsOn(string id)
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            if (key?.GetValue(id) is int v) return v != 0;
            return All.FirstOrDefault(f => f.Id == id)?.DefaultOn ?? true;
        }

        public static void Set(string id, bool on)
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            key.SetValue(id, on ? 1 : 0, RegistryValueKind.DWord);
        }

        /// <summary>Is the side-menu page with this key part of an enabled feature?</summary>
        public static bool IsNavOn(string navKey)
        {
            var f = All.FirstOrDefault(x => x.NavKeys.Contains(navKey));
            return f == null || IsOn(f.Id);
        }

        /// <summary>Is the extra download of this feature already here?</summary>
        public static bool IsDownloaded(Feature f) => f.Download switch
        {
            "ffmpeg" => Converter.FindFfmpeg() != null,
            "sendme" => P2PTransfer.IsInstalled,
            "ytdlp" => VideoDownloader.IsInstalled,
            "7zip" => Archiver.Has7z,
            _ => true,
        };

        public static System.Threading.Tasks.Task DownloadAsync(Feature f, IProgress<double>? progress) => f.Download switch
        {
            "ffmpeg" => Converter.DownloadFfmpegAsync(progress, default),
            "sendme" => P2PTransfer.InstallAsync(progress, default),
            "ytdlp" => VideoDownloader.InstallAsync(progress, default),
            "7zip" => Archiver.Download7zAsync(progress, default),
            _ => System.Threading.Tasks.Task.CompletedTask,
        };

        /// <summary>
        /// After installing: download the extras of the chosen features in the
        /// background (the installer itself stays small).
        /// </summary>
        public static async System.Threading.Tasks.Task DownloadMissingAsync()
        {
            foreach (var f in All.Where(f => f.Download != null && IsOn(f.Id) && !IsDownloaded(f)))
            {
                try { await DownloadAsync(f, null); }
                catch (Exception ex) { CrashLog.Write("download " + f.Download, ex); }
            }
        }
    }
}
