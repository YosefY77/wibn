using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// Downloads videos (or just the audio) from a link: YouTube, Instagram, X,
    /// Reddit, TikTok and the hundreds of other sites yt-dlp knows. Spotify songs
    /// are encrypted, so for those the same song is looked up on YouTube.
    ///
    /// yt-dlp.exe (open source) is downloaded once into %LOCALAPPDATA%\WinPlus\yt-dlp.
    /// FFmpeg, when there, gives the best quality (separate video + audio joined)
    /// and MP3; without it, the best single file the site offers.
    ///
    /// Nothing runs in the background: one shortcut (RegisterHotKey) and, while a
    /// download is going, one yt-dlp process.
    /// </summary>
    internal static class VideoDownloader
    {
        private const string OurKey = @"Software\WinPlus\Video";
        private const int HotkeyId = 0xB001;
        private const int WM_HOTKEY = 0x0312;

        public static string Folder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPlus", "yt-dlp");
        public static string ExePath => Path.Combine(Folder, "yt-dlp.exe");
        public static bool IsInstalled => File.Exists(ExePath);

        // ---- Settings ---------------------------------------------------------------------

        /// <summary>The shortcut's own switch (the feature itself is in Features).</summary>
        public static bool Enabled
        {
            get => GetInt("Enabled", 1) != 0;
            set { SetInt("Enabled", value ? 1 : 0); Refresh(); }
        }

        /// <summary>Ctrl+H by default.</summary>
        public static uint HotkeyMods
        {
            get => (uint)GetInt("HotkeyMods", (int)HotkeyManager.MOD_CONTROL);
            set => SetInt("HotkeyMods", (int)value);
        }

        public static uint HotkeyKey
        {
            get => (uint)GetInt("HotkeyKey", 0x48 /* H */);
            set => SetInt("HotkeyKey", (int)value);
        }

        public static string HotkeyText => HotkeyManager.KeyText(HotkeyMods, HotkeyKey);

        /// <summary>false = video, true = audio only (MP3).</summary>
        public static bool AudioOnly
        {
            get => GetInt("AudioOnly", 0) != 0;
            set => SetInt("AudioOnly", value ? 1 : 0);
        }

        /// <summary>Where finished files go (the user's Downloads folder by default).</summary>
        public static string SaveFolder
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(OurKey);
                if (key?.GetValue("Folder") is string f && f.Length > 0 && Directory.Exists(f)) return f;
                return DownloadsOrganizer.DownloadsFolder;
            }
            set
            {
                using var key = Registry.CurrentUser.CreateSubKey(OurKey);
                if (string.IsNullOrEmpty(value)) key.DeleteValue("Folder", false);
                else key.SetValue("Folder", value);
            }
        }

        /// <summary>
        /// A cookies.txt the user exported from their own browser (signed in to Instagram,
        /// Reddit, X…). Used only when a site wants a sign-in. Chrome and Edge lock their
        /// sign-ins, so the tools can't read them straight from the browser.
        /// </summary>
        public static string? CookiesFile
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(OurKey);
                return key?.GetValue("CookiesFile") is string f && File.Exists(f) ? f : null;
            }
            set
            {
                using var key = Registry.CurrentUser.CreateSubKey(OurKey);
                if (string.IsNullOrEmpty(value)) key.DeleteValue("CookiesFile", false);
                else key.SetValue("CookiesFile", value);
            }
        }

        public static bool IsDefaultFolder
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(OurKey);
                return key?.GetValue("Folder") is not string f || f.Length == 0;
            }
        }

        // ---- Shortcut -----------------------------------------------------------------------

        private static HwndSource? _window;
        private static bool _registered;
        private static VideoBar? _bar;

        /// <summary>Did Windows refuse the shortcut (another program has it)?</summary>
        public static bool HotkeyFailed { get; private set; }

        public static void Init(HwndSource window)
        {
            if (_window != null) return;
            _window = window;
            window.AddHook(WndProc);
        }

        /// <summary>Registers / drops the shortcut to match the settings.</summary>
        public static void Refresh()
        {
            if (_window == null) return;
            if (_registered) { UnregisterHotKey(_window.Handle, HotkeyId); _registered = false; }
            HotkeyFailed = false;
            if (!Features.IsOn("video") || !Enabled) return;
            _registered = RegisterHotKey(_window.Handle, HotkeyId, HotkeyMods | 0x4000 /* MOD_NOREPEAT */, HotkeyKey);
            HotkeyFailed = !_registered;
        }

        /// <summary>Lets go of the shortcut for a moment (while a new one is being picked).</summary>
        public static void Pause()
        {
            if (_window != null && _registered) { UnregisterHotKey(_window.Handle, HotkeyId); _registered = false; }
        }

        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                handled = true;
                ShowBar();
            }
            return IntPtr.Zero;
        }

        public static void ShowBar()
        {
            try
            {
                if (_bar != null) { _bar.Dismiss(); return; }   // the shortcut again closes it
                _bar = new VideoBar();
                _bar.Closed += (_, _) => _bar = null;
                _bar.LinkEntered += url => _ = DownloadAsync(url);
                _bar.Show();
            }
            catch (Exception ex)
            {
                CrashLog.Write("VideoBar", ex);
                _bar = null;
            }
        }

        // ---- Links ----------------------------------------------------------------------------

        /// <summary>A web link? (what was pasted, trimmed)</summary>
        public static bool IsLink(string text, out Uri? uri)
        {
            uri = null;
            text = text.Trim();
            if (text.Length < 8 || text.Contains(' ')) return false;
            if (!text.StartsWith("http", StringComparison.OrdinalIgnoreCase)) text = "https://" + text;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var u)) return false;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
            if (!u.Host.Contains('.')) return false;
            uri = u;
            return true;
        }

        /// <summary>The site's name for the bar ("YouTube"), or null.</summary>
        public static string? SiteName(Uri uri)
        {
            string h = uri.Host.ToLowerInvariant();
            bool Is(string d) => h == d || h.EndsWith("." + d);
            if (Is("youtube.com") || Is("youtu.be")) return "YouTube";
            if (Is("instagram.com")) return "Instagram";
            if (Is("twitter.com") || Is("x.com")) return "X";
            if (Is("reddit.com") || Is("redd.it")) return "Reddit";
            if (Is("tiktok.com")) return "TikTok";
            if (Is("spotify.com") || Is("spotify.link")) return "Spotify";
            if (Is("facebook.com") || Is("fb.watch")) return "Facebook";
            if (Is("snapchat.com")) return "Snapchat";
            if (Is("twitch.tv")) return "Twitch";
            if (Is("soundcloud.com")) return "SoundCloud";
            if (Is("vimeo.com")) return "Vimeo";
            return null;
        }

        // ---- Download -------------------------------------------------------------------------

        private static int _active;

        public static async Task DownloadAsync(Uri uri)
        {
            string site = SiteName(uri) ?? uri.Host;
            Interlocked.Increment(ref _active);
            App.SetTrayText(Lang.T("Wibn: يتحمّل من ") + site + "…");
            string temp = Path.Combine(Path.GetTempPath(), "Wibn-video-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                if (!File.Exists(ExePath))
                {
                    App.Notify(Lang.T("تحميل المقاطع"), Lang.T("أول مرة: ينزّل أداة التحميل (yt-dlp)، لحظات…"));
                    await InstallAsync(null, CancellationToken.None);
                }
                await UpdateAsync(force: false);   // once a day: sites change, the tools catch up fast

                uri = await ResolveShareLinkAsync(uri, site);
                string target = uri.ToString();
                bool audio = AudioOnly;
                if (site == "Spotify")
                {
                    // Spotify's audio is encrypted: find the same song on YouTube.
                    string? song = await SpotifySongAsync(uri);
                    if (song == null)
                        throw new UserMessage(Lang.T("هذا الرابط من سبوتيفاي مو أغنية وحدة. انسخ رابط أغنية (مو ألبوم أو قائمة)."));
                    target = "ytsearch1:" + song + " audio";
                    audio = true;
                }
                Directory.CreateDirectory(temp);

                var (files, error) = await FetchAsync(uri, site, target, audio, temp);
                if (files.Count == 0)
                {
                    // Maybe the site changed since the last update: update now and try once more.
                    if (await UpdateAsync(force: true))
                    {
                        var again = await FetchAsync(uri, site, target, audio, temp);
                        files = again.files;
                        error = again.error ?? error;
                    }
                }
                if (files.Count == 0)
                {
                    CrashLog.Write("video download " + site, new Exception(error));
                    throw new UserMessage(Explain(error));
                }

                // Move them to the chosen folder (a new name if one is there already).
                string folder = SaveFolder;
                Directory.CreateDirectory(folder);
                var saved = new List<string>();
                foreach (var f in files)
                {
                    string dest = Converter.UniquePath(Path.Combine(folder, Path.GetFileName(f)));
                    DownloadsOrganizer.Skip(dest);   // it's where the user asked for it: don't sort it away
                    await Task.Run(() => File.Move(f, dest));
                    saved.Add(dest);
                }
                ShowInFolder(saved);
            }
            catch (UserMessage ex)
            {
                App.Notify(Lang.T("ما تحمّل"), ex.Message);
            }
            catch (Exception ex)
            {
                CrashLog.Write("video download", ex);
                App.Notify(Lang.T("ما تحمّل"), Lang.T("صار خطأ. تأكد من النت ومن الرابط."));
            }
            finally
            {
                try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
                if (Interlocked.Decrement(ref _active) == 0) App.SetTrayText(null);
            }
        }

        /// <summary>
        /// Tries the ways to get what's behind the link, best first, until one gives files:
        ///   * yt-dlp: videos (and audio) from almost any site;
        ///   * gallery-dl: posts with pictures (Instagram, TikTok photo posts, X, Reddit…),
        ///     all of them when a post has several;
        ///   * the same again with the browser's sign-in, when a site wants one;
        ///   * Instagram: the public embed page, as a last resort.
        /// </summary>
        private static async Task<(List<string> files, string? error)> FetchAsync(Uri uri, string site, string target, bool audio, string temp)
        {
            var errors = new StringBuilder();
            bool pictures = !audio && site != "YouTube" && site != "Spotify" && site != "SoundCloud" && site != "Twitch";
            // Instagram posts and TikTok photo posts are mostly pictures: gallery-dl first.
            bool reel = Regex.IsMatch(uri.AbsolutePath, "/(reel|reels|tv)/");
            bool picturesFirst = pictures && ((site == "Instagram" && !reel) || uri.AbsolutePath.Contains("/photo/"));

            async Task<List<string>> Try(Func<Task<(List<string>, string?)>> run)
            {
                var (got, err) = await run();
                if (got.Count == 0 && err != null) errors.AppendLine(err);
                return got;
            }

            var order = new List<Func<string?, Task<List<string>>>>();
            Func<string?, Task<List<string>>> video = cookies => Try(() => RunYtDlpAsync(target, audio, temp, site, cookies));
            Func<string?, Task<List<string>>> gallery = cookies => Try(() => RunGalleryDlAsync(target, temp, cookies));
            if (picturesFirst) { order.Add(gallery); order.Add(video); }
            else { order.Add(video); if (pictures && site != "TikTok") order.Add(gallery); }   // TikTok: tikwm below is quicker

            foreach (var step in order)
            {
                var got = await step(null);
                if (got.Count > 0) return (got, null);
            }

            // TikTok hides some posts behind a sign-in ("may not be comfortable for some
            // audiences"); a public download service gets them without one.
            if (site == "TikTok")
            {
                var got = await TikwmAsync(uri, audio, temp, errors);
                if (got.Count > 0) return (got, null);
            }

            // Wants a sign-in (or is rate limited): use the browser's, like being on the site.
            if (NeedsLogin(errors.ToString()))
            {
                foreach (var browser in Browsers())
                    foreach (var step in order)
                    {
                        var got = await step(browser);
                        if (got.Count > 0) return (got, null);
                    }
            }

            if (site == "Instagram" && !audio)
            {
                var got = await InstagramEmbedAsync(uri, temp);
                if (got.Count > 0) return (got, null);
            }
            return (new List<string>(), errors.ToString());
        }

        private const string DesktopAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
        private const string PhoneAgent =
            "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1";

        /// <summary>
        /// Share links (vm.tiktok.com/…, tiktok.com/t/…, instagram.com/share/…, t.co/…)
        /// only lead to the post when opened like a browser; the tools got sent to the
        /// home page instead ("Unsupported URL: tiktok.com/"). Follow them here.
        /// </summary>
        private static async Task<Uri> ResolveShareLinkAsync(Uri uri, string site)
        {
            string path = uri.AbsolutePath;
            bool share = site switch
            {
                "TikTok" => !path.Contains("/video/") && !path.Contains("/photo/") && !path.StartsWith("/@"),
                "Instagram" => path.StartsWith("/share/"),
                _ => uri.Host is "t.co" or "redd.it" or "fb.watch" || path.Contains("/s/") && site == "Reddit",
            };
            if (!share) return uri;

            bool IsPost(Uri u) => site switch
            {
                "TikTok" => u.AbsolutePath.Contains("/video/") || u.AbsolutePath.Contains("/photo/"),
                "Instagram" => Regex.IsMatch(u.AbsolutePath, @"/(p|reel|reels|tv)/"),
                _ => u.Host != uri.Host,
            };

            foreach (var agent in new[] { DesktopAgent, PhoneAgent })
            {
                try
                {
                    using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true };
                    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
                    http.DefaultRequestHeaders.UserAgent.ParseAdd(agent);
                    http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
                    http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.8");
                    Uri current = uri;
                    for (int hop = 0; hop < 8; hop++)
                    {
                        using var resp = await http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead);
                        var location = resp.Headers.Location;
                        if (location != null && (int)resp.StatusCode is >= 300 and < 400)
                        {
                            current = location.IsAbsoluteUri ? location : new Uri(current, location);
                            if (IsPost(current)) return Clean(current);
                            continue;
                        }
                        // Landed on a page: it names the post it is about.
                        string html = await resp.Content.ReadAsStringAsync();
                        foreach (var pattern in new[]
                        {
                            "<link[^>]+rel=\"canonical\"[^>]+href=\"([^\"]+)\"",
                            "<meta[^>]+property=\"og:url\"[^>]+content=\"([^\"]+)\"",
                        })
                        {
                            var m = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
                            if (m.Success && Uri.TryCreate(WebUtility.HtmlDecode(m.Groups[1].Value), UriKind.Absolute, out var found) && IsPost(found))
                                return Clean(found);
                        }
                        break;
                    }
                }
                catch (Exception ex)
                {
                    CrashLog.Write("share link " + uri, ex);
                }
            }
            return uri;   // let the tools try it as it is
        }

        /// <summary>The post's address without the tracking part after "?".</summary>
        private static Uri Clean(Uri u) =>
            u.Host.Contains("tiktok") || u.Host.Contains("instagram") ? new Uri(u.GetLeftPart(UriPartial.Path)) : u;

        /// <summary>
        /// tikwm.com: a free public service that hands out a TikTok post's video (without
        /// the watermark), its pictures or its sound, no sign-in needed.
        /// </summary>
        private static async Task<List<string>> TikwmAsync(Uri uri, bool audio, string temp, StringBuilder errors)
        {
            var saved = new List<string>();
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd(DesktopAgent);
                string json = await http.GetStringAsync("https://www.tikwm.com/api/?hd=1&url=" + Uri.EscapeDataString(uri.ToString()));
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("code", out var code) || code.GetInt32() != 0 || !root.TryGetProperty("data", out var data))
                {
                    errors.AppendLine("tikwm: " + (root.TryGetProperty("msg", out var msg) ? msg.ToString() : json[..Math.Min(200, json.Length)]));
                    return saved;
                }
                string Str(string name) => data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                string Abs(string u) => u.StartsWith("http") ? u : "https://www.tikwm.com" + (u.StartsWith("/") ? "" : "/") + u;

                string id = Str("id");
                string title = string.Join("_", Str("title").Split(Path.GetInvalidFileNameChars())).Trim();
                if (title.Length > 80) title = title[..80].Trim();
                string name = (title.Length > 0 ? title + " " : "") + "[" + (id.Length > 0 ? id : "tiktok") + "]";

                var files = new List<(string url, string ext)>();
                if (audio)
                {
                    string music = Str("music");
                    if (music.Length > 0) files.Add((Abs(music), ".mp3"));
                }
                else if (data.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0)
                {
                    foreach (var img in images.EnumerateArray())
                        if (img.ValueKind == JsonValueKind.String) files.Add((Abs(img.GetString()!), ".jpg"));
                }
                else
                {
                    string video = Str("hdplay");
                    if (video.Length == 0) video = Str("play");
                    if (video.Length > 0) files.Add((Abs(video), ".mp4"));
                }

                int n = 1;
                foreach (var (url, ext) in files.Take(50))
                {
                    string file = Path.Combine(temp, files.Count > 1 ? $"{name}_{n++}{ext}" : name + ext);
                    using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                    {
                        resp.EnsureSuccessStatusCode();
                        using var input = await resp.Content.ReadAsStreamAsync();
                        using var output = File.Create(file);
                        await input.CopyToAsync(output);
                    }
                    saved.Add(file);
                }
                if (saved.Count == 0) errors.AppendLine("tikwm: nothing in the answer");
            }
            catch (Exception ex)
            {
                errors.AppendLine("tikwm: " + ex.Message);
            }
            return saved;
        }

        private static bool NeedsLogin(string error)
        {
            string e = error.ToLowerInvariant();
            return e.Contains("login") || e.Contains("log in") || e.Contains("cookies") || e.Contains("sign in")
                || e.Contains("authentication") || e.Contains("blocked by network security")   // Reddit
                || e.Contains("rate-limit") || e.Contains("rate limit") || e.Contains("401") || e.Contains("403");
        }

        /// <summary>Sign-ins the tools can use: the user's cookies.txt first, then Firefox.</summary>
        private static IEnumerable<string> Browsers()
        {
            if (CookiesFile is string file) yield return "file:" + file;
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (Directory.Exists(Path.Combine(roaming, "Mozilla", "Firefox", "Profiles"))) yield return "firefox";
            // Chrome, Edge, Brave and Opera lock and encrypt their sign-ins now (the tools
            // fail with "Could not copy" / "DPAPI"), so trying them only wastes time.
        }

        private sealed class UserMessage : Exception
        {
            public UserMessage(string m) : base(m) { }
        }

        private static string Explain(string? error)
        {
            string e = (error ?? "").ToLowerInvariant();
            if (e.Contains("private") || NeedsLogin(e))
                return CookiesFile == null
                    ? Lang.T("الموقع يبي تسجيل دخول. اضغط \"سجّل دخول\" في Wibn ← تحميل المقاطع وجرّب مرة ثانية.")
                    : Lang.T("المنشور خاص أو حسابك ما يقدر يشوفه، أو تسجيل الدخول انتهى: اضغط \"سجّل دخول\" مرة ثانية.");
            if (e.Contains("unable to download") || e.Contains("timed out") || e.Contains("getaddrinfo") || e.Contains("connection"))
                return Lang.T("مشكلة في النت. جرّب مرة ثانية.");
            if (e.Contains("not available") || e.Contains("removed") || e.Contains("404") || e.Contains("not found"))
                return Lang.T("المنشور انحذف أو مو متاح.");
            if (e.Contains("unsupported url") || e.Contains("no suitable extractor"))
                return Lang.T("هذا الموقع ما ينفع منه التحميل، أو الرابط مو رابط مقطع أو صورة.");
            return Lang.T("ما قدرت أحمّل من هذا الرابط.");
        }

        /// <summary>Finished files in the temp folder (not the half-done ones).</summary>
        private static List<string> FinishedFiles(string temp) =>
            Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".part") && !f.EndsWith(".ytdl") && !f.Contains(".temp.") && !f.EndsWith(".json"))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

        /// <summary>Runs yt-dlp; returns the finished files (in <paramref name="temp"/>) or the error text.</summary>
        private static async Task<(List<string> files, string? error)> RunYtDlpAsync(string target, bool audio, string temp, string site, string? cookies)
        {
            string? ffmpeg = Converter.FindFfmpeg();
            var args = new List<string>
            {
                "--no-mtime", "--windows-filenames", "--no-part", "--newline",
                "--encoding", "utf-8", "--color", "never",
                "-P", temp,
                "-o", "%(title).100B [%(id)s].%(ext)s",
                "--print", "after_move:WPFILE:%(filepath)s",
                "--progress", "--progress-template", "download:WPPROG:%(progress._percent_str)s",
            };
            // A YouTube link inside a playlist means that one video; elsewhere a link
            // with several (an Instagram or X post) means all of them.
            if (site == "YouTube") args.Add("--no-playlist");
            else args.AddRange(new[] { "--yes-playlist", "-I", "1:30" });
            if (site == "TikTok") args.AddRange(new[] { "--impersonate", "chrome" });   // TikTok blocks plain requests
            AddCookies(args, cookies);
            if (ffmpeg != null)
            {
                args.Add("--ffmpeg-location");
                args.Add(Path.GetDirectoryName(ffmpeg)!);
            }
            if (audio)
            {
                if (ffmpeg != null) args.AddRange(new[] { "-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "0" });
                else args.AddRange(new[] { "-f", "ba[ext=m4a]/ba/b" });
            }
            else if (ffmpeg != null)
            {
                // Best quality, as an MP4 that plays everywhere.
                args.AddRange(new[] { "-f", "bv*[ext=mp4]+ba[ext=m4a]/bv*+ba/b", "--merge-output-format", "mp4" });
            }
            else
            {
                args.AddRange(new[] { "-f", "b[ext=mp4]/b" });  // one file with sound
            }
            args.Add("--");
            args.Add(target);

            var (exit, error) = await RunToolAsync(ExePath, args, temp, line =>
            {
                if (line.StartsWith("WPPROG:")) App.SetTrayText(Lang.T("Wibn: يتحمّل ") + line.Substring(7).Trim());
            });

            // This yt-dlp can't pretend to be a browser: the same without it.
            if (site == "TikTok" && error.Contains("mpersonat", StringComparison.OrdinalIgnoreCase) && FinishedFiles(temp).Count == 0)
            {
                int i = args.IndexOf("--impersonate");
                if (i >= 0) args.RemoveRange(i, 2);
                (exit, error) = await RunToolAsync(ExePath, args, temp, null);
            }

            var files = FinishedFiles(temp);
            return (files, files.Count == 0 ? "yt-dlp: " + error : null);
        }

        /// <summary>Runs gallery-dl (pictures, and the videos in the same post).</summary>
        private static async Task<(List<string> files, string? error)> RunGalleryDlAsync(string target, string temp, string? cookies)
        {
            if (!File.Exists(GalleryPath))
            {
                try { await InstallGalleryAsync(CancellationToken.None); }
                catch (Exception ex) { return (new List<string>(), "gallery-dl install: " + ex.Message); }
            }
            var args = new List<string> { "-D", temp, "--no-part", "--no-mtime", "--range", "1-50" };
            AddCookies(args, cookies);
            args.Add("--");
            args.Add(target);
            var (exit, error) = await RunToolAsync(GalleryPath, args, temp, null);
            var files = FinishedFiles(temp);
            return (files, files.Count == 0 ? "gallery-dl: " + error : null);
        }

        /// <summary>"file:&lt;path&gt;" = the user's cookies.txt; otherwise a browser name. Same flags in both tools.</summary>
        private static void AddCookies(List<string> args, string? cookies)
        {
            if (cookies == null) return;
            if (cookies.StartsWith("file:")) args.AddRange(new[] { "--cookies", cookies.Substring(5) });
            else args.AddRange(new[] { "--cookies-from-browser", cookies });
        }

        /// <summary>Runs a tool hidden; returns its exit code and what it said on stderr.</summary>
        private static async Task<(int exit, string error)> RunToolAsync(string exe, List<string> args, string workDir, Action<string>? onLine)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = workDir,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["PYTHONIOENCODING"] = "utf-8";

            var errors = new StringBuilder();
            using var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine?.Invoke(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null && errors.Length < 4000) errors.AppendLine(e.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            var done = p.WaitForExitAsync();
            if (await Task.WhenAny(done, Task.Delay(TimeSpan.FromMinutes(30))) != done)
            {
                try { p.Kill(true); } catch { }
                return (-1, "timed out");
            }
            return (p.ExitCode, errors.ToString());
        }

        /// <summary>
        /// Instagram's public embed page lists a post's pictures and videos without a
        /// sign-in. Used when the tools can't get a post.
        /// </summary>
        private static async Task<List<string>> InstagramEmbedAsync(Uri uri, string temp)
        {
            var saved = new List<string>();
            try
            {
                var m = Regex.Match(uri.AbsolutePath, @"/(?:p|reel|reels|tv)/([A-Za-z0-9_-]+)");
                if (!m.Success) return saved;
                string code = m.Groups[1].Value;
                using var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd(DesktopAgent);
                http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
                http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
                http.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "iframe");
                http.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");
                http.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
                http.DefaultRequestHeaders.Referrer = new Uri("https://www.google.com/");
                using var resp = await http.GetAsync($"https://www.instagram.com/p/{code}/embed/captioned/");
                string html = await resp.Content.ReadAsStringAsync();

                // A video post shows its cover picture here: that's not the video, so leave it.
                bool video = html.Contains("EmbeddedMediaVideo") || html.Contains("\"is_video\":true") || html.Contains("VideoSpritePlayButton");

                // The post's picture: <img class="EmbeddedMediaImage" … src="…">
                // (the other pictures on the page are the account's, in its hover card).
                string? picture = null;
                var tag = Regex.Match(html, "<img[^>]*class=\"EmbeddedMediaImage\"[^>]*>", RegexOptions.IgnoreCase);
                if (tag.Success)
                {
                    // srcset lists sizes ("url 640w, url 1080w"): the last is the biggest.
                    var srcset = Regex.Match(tag.Value, "srcset=\"([^\"]+)\"");
                    if (srcset.Success)
                        picture = WebUtility.HtmlDecode(srcset.Groups[1].Value).Split(',').Last().Trim().Split(' ')[0];
                    if (string.IsNullOrEmpty(picture))
                    {
                        var src = Regex.Match(tag.Value, "\\ssrc=\"([^\"]+)\"");
                        if (src.Success) picture = WebUtility.HtmlDecode(src.Groups[1].Value);
                    }
                }
                CrashLog.Note($"instagram embed {code}: HTTP {(int)resp.StatusCode}, {html.Length} chars, picture={(picture != null)}, video={video}");
                if (picture == null || video) return saved;

                string path = Path.Combine(temp, $"instagram_{code}.jpg");
                var bytes = await http.GetByteArrayAsync(picture);
                await File.WriteAllBytesAsync(path, bytes);
                saved.Add(path);

                // A post with several pictures: without a sign-in Instagram only shows the first.
                if (IsCarouselPicture(picture))
                    App.Notify(Lang.T("انستقرام"), Lang.T("تحمّلت أول صورة بس. انستقرام ما يعطي باقي صور البوست إلا لما تكون مسجّل دخول: اضغط \"سجّل دخول\" في Wibn ← تحميل المقاطع وجرّب مرة ثانية."));
            }
            catch (Exception ex)
            {
                CrashLog.Write("instagram embed", ex);
            }
            return saved;
        }

        /// <summary>Instagram marks each picture's address with what it is (base64 "efg"): one of several?</summary>
        private static bool IsCarouselPicture(string url)
        {
            try
            {
                var m = Regex.Match(url, "[?&]efg=([^&]+)");
                if (!m.Success) return false;
                string b64 = Uri.UnescapeDataString(m.Groups[1].Value).Replace('-', '+').Replace('_', '/');
                b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
                return Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Contains("CAROUSEL", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>"Artist - Song" of a Spotify track link, or null (album, playlist…).</summary>
        private static async Task<string?> SpotifySongAsync(Uri uri)
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Wibn");
            using var resp = await http.GetAsync(uri);
            var final = resp.RequestMessage?.RequestUri ?? uri;   // spotify.link → open.spotify.com
            if (!final.AbsolutePath.Contains("/track/")) return null;
            string html = await resp.Content.ReadAsStringAsync();

            // <title>Song - song and lyrics by Artist | Spotify</title>
            var m = Regex.Match(html, @"<title>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string t = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
                t = Regex.Replace(t, @"\s*\|\s*Spotify\s*$", "");
                var by = Regex.Match(t, @"^(.*?) - (?:song(?: and lyrics)?|single|lyrics) by (.*)$", RegexOptions.IgnoreCase);
                if (by.Success) return by.Groups[2].Value.Trim() + " - " + by.Groups[1].Value.Trim();
                if (t.Length > 0 && !t.Equals("Spotify", StringComparison.OrdinalIgnoreCase)) return t;
            }
            // og:title (song) + og:description ("Artist · Album · Song · 2020")
            string? Meta(string prop)
            {
                var mm = Regex.Match(html, "<meta[^>]+property=\"" + prop + "\"[^>]+content=\"([^\"]*)\"", RegexOptions.IgnoreCase);
                return mm.Success ? WebUtility.HtmlDecode(mm.Groups[1].Value).Trim() : null;
            }
            string? title = Meta("og:title");
            string? artist = Meta("og:description")?.Split('·').FirstOrDefault()?.Trim();
            if (!string.IsNullOrEmpty(title)) return string.IsNullOrEmpty(artist) ? title : artist + " - " + title;
            return null;
        }

        /// <summary>Opens the folder in File Explorer with the file(s) selected.</summary>
        public static void ShowInFolder(IReadOnlyList<string> paths)
        {
            if (paths.Count == 0) return;
            var pidls = new List<IntPtr>();
            IntPtr folder = IntPtr.Zero;
            try
            {
                if (paths.Count == 1)
                {
                    IntPtr pidl = ILCreateFromPathW(paths[0]);
                    if (pidl != IntPtr.Zero)
                    {
                        pidls.Add(pidl);
                        if (SHOpenFolderAndSelectItems(pidl, 0, null, 0) == 0) return;
                    }
                }
                else
                {
                    // Several files (an Instagram post with many pictures): select them all.
                    folder = ILCreateFromPathW(Path.GetDirectoryName(paths[0])!);
                    foreach (var p in paths)
                    {
                        IntPtr pidl = ILCreateFromPathW(p);
                        if (pidl != IntPtr.Zero) pidls.Add(pidl);
                    }
                    if (folder != IntPtr.Zero && pidls.Count > 0)
                    {
                        var children = pidls.Select(ILFindLastID).ToArray();
                        if (SHOpenFolderAndSelectItems(folder, (uint)children.Length, children, 0) == 0) return;
                    }
                }
            }
            catch { }
            finally
            {
                foreach (var p in pidls) ILFree(p);
                if (folder != IntPtr.Zero) ILFree(folder);
            }
            try { Process.Start("explorer.exe", $"/select,\"{paths[0]}\""); } catch { }
        }

        public static void ShowInFolder(string path) => ShowInFolder(new[] { path });

        // ---- The tools ---------------------------------------------------------------------------

        /// <summary>gallery-dl.exe (open source): pictures from posts. Next to yt-dlp.</summary>
        public static string GalleryPath => Path.Combine(Folder, "gallery-dl.exe");

        /// <summary>Downloads yt-dlp (and gallery-dl) from GitHub, whichever is missing.</summary>
        public static async Task InstallAsync(IProgress<double>? progress, CancellationToken cancel)
        {
            if (!File.Exists(ExePath))
            {
                // The nightly build: site changes are fixed there first.
                try { await InstallToolAsync("yt-dlp/yt-dlp-nightly-builds", "yt-dlp.exe", ExePath, progress, cancel); }
                catch (OperationCanceledException) { throw; }
                catch { await InstallToolAsync("yt-dlp/yt-dlp", "yt-dlp.exe", ExePath, progress, cancel); }
            }
            // The pictures tool is a bonus: videos work without it, so a problem here
            // doesn't stop anything (it's tried again when a post needs it).
            if (!File.Exists(GalleryPath))
            {
                try { await InstallGalleryAsync(cancel); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { CrashLog.Write("gallery-dl install", ex); }
            }
            SetInt("UpdatedAt", NowMinutes());
        }

        /// <summary>
        /// gallery-dl's Windows exe. Its file name has changed between releases, and
        /// not every release has one, so look through the recent ones (GitHub, then
        /// its Codeberg mirror).
        /// </summary>
        private static async Task InstallGalleryAsync(CancellationToken cancel)
        {
            static bool IsWindowsExe(string name)
            {
                string n = name.ToLowerInvariant();
                return n.StartsWith("gallery-dl") && n.EndsWith(".exe") && !n.Contains("x86") && !n.Contains("32") && !n.Contains("arm");
            }
            var tried = new List<string>();
            foreach (var api in new[]
            {
                "https://api.github.com/repos/mikf/gallery-dl/releases?per_page=8",
                "https://codeberg.org/api/v1/repos/mikf/gallery-dl/releases?limit=8",
            })
            {
                try
                {
                    await DownloadFromReleasesAsync(api, IsWindowsExe, GalleryPath, null, cancel);
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { tried.Add(ex.Message); }
            }
            throw new InvalidOperationException("gallery-dl: " + string.Join(" | ", tried));
        }

        private static Task InstallToolAsync(string repo, string asset, string path,
                                             IProgress<double>? progress, CancellationToken cancel) =>
            DownloadFromReleasesAsync($"https://api.github.com/repos/{repo}/releases?per_page=5",
                                      name => name == asset, path, progress, cancel);

        /// <summary>The newest release (of a GitHub / Codeberg release list) with a matching file: download it.</summary>
        private static async Task DownloadFromReleasesAsync(string api, Func<string, bool> wanted, string path,
                                                            IProgress<double>? progress, CancellationToken cancel)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Wibn");
            string json = await http.GetStringAsync(api, cancel);
            using var doc = JsonDocument.Parse(json);
            string? url = null;
            var names = new List<string>();
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) continue;
                if (!release.TryGetProperty("assets", out var assets)) continue;
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.GetProperty("name").GetString() ?? "";
                    names.Add(name);
                    if (wanted(name)) { url = a.GetProperty("browser_download_url").GetString(); break; }
                }
                if (url != null) break;
            }
            if (string.IsNullOrEmpty(url))
                throw new InvalidOperationException("no matching file at " + api + " (files: " + string.Join(", ", names.Take(20)) + ")");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string download = path + ".download";
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? 0;
                using var input = await resp.Content.ReadAsStreamAsync(cancel);
                using var file = File.Create(download);
                byte[] buffer = new byte[1 << 16];
                long done = 0, lastReport = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, cancel)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n), cancel);
                    done += n;
                    if (total > 0 && done - lastReport > total / 50) { lastReport = done; progress?.Report((double)done / total); }
                }
            }
            File.Move(download, path, true);
        }

        private static int NowMinutes() => (int)((DateTime.UtcNow - DateTime.UnixEpoch).TotalMinutes);

        /// <summary>
        /// Updates both tools: once a day before downloading, or (force) after a failed
        /// download, at most once an hour. True if it ran.
        /// </summary>
        private static async Task<bool> UpdateAsync(bool force)
        {
            int now = NowMinutes();
            int last = GetInt("UpdatedAt", 0);
            if (now - last < (force ? 60 : 24 * 60)) return false;
            SetInt("UpdatedAt", now);
            await RunUpdateAsync(ExePath, "--update-to", "nightly");
            if (File.Exists(GalleryPath)) await RunUpdateAsync(GalleryPath, "--update");
            return true;
        }

        private static async Task RunUpdateAsync(string exe, params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                if (p == null) return;
                var done = p.WaitForExitAsync();
                if (await Task.WhenAny(done, Task.Delay(TimeSpan.FromMinutes(2))) != done) { try { p.Kill(); } catch { } }
            }
            catch { }
        }

        // ---- Helpers --------------------------------------------------------------------------

        private static int GetInt(string name, int fallback)
        {
            using var key = Registry.CurrentUser.OpenSubKey(OurKey);
            return key?.GetValue(name) is int v ? v : fallback;
        }

        private static void SetInt(string name, int value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(OurKey);
            key.SetValue(name, value, RegistryValueKind.DWord);
        }

        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr ILCreateFromPathW(string path);
        [DllImport("shell32.dll")] private static extern void ILFree(IntPtr pidl);
        [DllImport("shell32.dll")] private static extern IntPtr ILFindLastID(IntPtr pidl);
        [DllImport("shell32.dll")] private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl,
            [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[]? apidl, uint flags);
    }
}
