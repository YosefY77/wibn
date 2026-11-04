using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// Checks GitHub for a newer release (at start and every 12 hours). The user
    /// can update (the installer runs silently, keeps settings and chosen
    /// features, and starts WinPlus again) or ignore that version.
    /// </summary>
    internal static class UpdateChecker
    {
        public const string Repo = "YosefY77/wibn";
        private const string OurKey = @"Software\WinPlus\TaskbarScroll";

        public sealed record Release(Version Version, string Tag, string Notes, string InstallerUrl);

        public static Version Current =>
            Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

        public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Current.Build}";

        /// <summary>A newer release than this one, or null.</summary>
        public static async Task<Release?> CheckAsync(bool includeIgnored = false)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("WinPlus/" + CurrentText);
                string json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string tag = root.GetProperty("tag_name").GetString() ?? "";
                if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
                version = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
                var current = new Version(Current.Major, Current.Minor, Math.Max(0, Current.Build));
                if (version <= current) return null;
                if (!includeIgnored && IgnoredVersion == tag) return null;

                string? url = root.GetProperty("assets").EnumerateArray()
                    .Select(a => (name: a.GetProperty("name").GetString() ?? "", url: a.GetProperty("browser_download_url").GetString() ?? ""))
                    .FirstOrDefault(a => a.name.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase)).url;
                if (string.IsNullOrEmpty(url)) return null;
                string notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
                return new Release(version, tag, notes, url);
            }
            catch (Exception ex)
            {
                CrashLog.Note("update check failed: " + ex.Message);
                return null;
            }
        }

        public static string? IgnoredVersion
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(OurKey);
                return key?.GetValue("IgnoredVersion") as string;
            }
            set
            {
                using var key = Registry.CurrentUser.CreateSubKey(OurKey);
                if (value == null) key.DeleteValue("IgnoredVersion", false);
                else key.SetValue("IgnoredVersion", value);
            }
        }

        /// <summary>Downloads the installer and runs it silently. WinPlus must exit right after.</summary>
        public static async Task InstallAsync(Release release, IProgress<double>? progress, CancellationToken cancel)
        {
            string file = Path.Combine(Path.GetTempPath(), $"Wibn-Setup-{release.Tag}.exe");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("WinPlus/" + CurrentText);
                using var resp = await http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancel);
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? 0;
                using var input = await resp.Content.ReadAsStreamAsync(cancel);
                using var output = File.Create(file);
                byte[] buffer = new byte[1 << 16];
                long done = 0, last = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, cancel)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), cancel);
                    done += n;
                    if (total > 0 && done - last > total / 50) { last = done; progress?.Report((double)done / total); }
                }
            }
            // The installer keeps the chosen features, closes WinPlus and starts the new one.
            Process.Start(new ProcessStartInfo(file, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE=1")
            {
                UseShellExecute = true,
            });
        }
    }
}
