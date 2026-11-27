using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace WinPlus
{
    /// <summary>
    /// The suggestion box (General page). What someone writes goes to a small relay of the developer's
    /// (feedback-worker/, a Cloudflare Worker) that posts it into the developer's Discord channel.
    ///
    /// Nothing secret is in the app: the Discord webhook is only in the relay, so pulling strings out of
    /// Wibn.exe gets nothing that can be abused (versions up to 2.7 had the webhook inside; that one is
    /// retired). The relay, not the app, enforces the limits (one message a minute per sender, length,
    /// no mentions or clickable links). Nothing about the sender goes with it: no name, no PC name,
    /// no files; just the text, the kind, Wibn's version and the app's language.
    /// </summary>
    internal static class Suggestions
    {
        /// <summary>The relay's address (public, not a secret). Empty = the box is hidden.</summary>
        private const string RelayUrl = "https://wibn-feedback.royal-table-online.workers.dev/send";

        public const int MaxLength = 1500;
        private const int WaitSeconds = 60;   // the relay enforces this too; here it just saves a pointless try

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
        private static DateTime _lastSent = DateTime.MinValue;

        public static bool Ready => RelayUrl.StartsWith("https://");

        public enum Kind { Suggestion, Complaint, Bug }

        /// <summary>Sends it. null when it went, otherwise what to tell the user (already in the app's language).</summary>
        public static async Task<string?> Send(string text, Kind kind)
        {
            text = text.Trim();
            if (text.Length < 3) return Lang.T("اكتب رسالتك أول");
            if (!Ready) return Lang.T("صندوق الاقتراحات مو جاهز في هذي النسخة");
            int wait = WaitSeconds - (int)(DateTime.UtcNow - _lastSent).TotalSeconds;
            if (wait > 0) return Lang.F("استنى {0} ثانية قبل الرسالة الجاية", wait);
            if (text.Length > MaxLength) text = text[..MaxLength];

            string body = JsonSerializer.Serialize(new
            {
                kind = kind switch { Kind.Complaint => "complaint", Kind.Bug => "bug", _ => "suggestion" },
                text,
                version = UpdateChecker.CurrentText,
                lang = Lang.English ? "en" : "ar",
            });
            try
            {
                using var res = await Http.PostAsync(RelayUrl, new StringContent(body, Encoding.UTF8, "application/json"));
                if ((int)res.StatusCode == 429) return Lang.T("استنى دقيقة قبل الرسالة الجاية");
                if (!res.IsSuccessStatusCode)
                {
                    CrashLog.Note($"suggestion: relay answered {(int)res.StatusCode}");
                    return Lang.T("ما قدرت أرسل الرسالة، جرّب بعدين");
                }
                _lastSent = DateTime.UtcNow;
                return null;
            }
            catch (Exception ex)
            {
                CrashLog.Note("suggestion: " + ex.Message);
                return Lang.T("ما قدرت أرسل الرسالة، تأكد من الإنترنت");
            }
        }
    }
}
