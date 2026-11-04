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
    /// missing value means "on" (e.g. a build run straight from the source).
    /// </summary>
    internal static class Features
    {
        public sealed record Feature(string Id, string Name, string Description, string[] NavKeys, string? Download = null);

        public static readonly Feature[] All =
        {
            new("taskbar", Lang.T("التاسك بار"), Lang.T("سكرول بدل (...)، لون، حجم الأيقونات، السنة، الإخفاء مع ملء الشاشة"), new[] { "taskbar" }),
            new("desktop", Lang.T("سطح المكتب"), Lang.T("لون مربع التحديد ومؤشر الماوس"), new[] { "desktop" }),
            new("cleanup", Lang.T("التنظيف"), Lang.T("أكبر الملفات، الملفات المؤقتة، المكررة، سلة المهملات"), new[] { "big", "temp", "dup", "bin" }),
            new("hotkeys", Lang.T("اختصارات الكيبورد"), Lang.T("اختصاراتك الخاصة في أي مكان"), new[] { "hotkeys" }),
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
        };

        private const string Key = @"Software\WinPlus\Features";

        public static bool IsOn(string id)
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(id) is not int v || v != 0;
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
