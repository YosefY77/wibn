using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace WinPlus
{
    /// <summary>
    /// The suggestion box (General page): what someone writes goes to the developer's own Discord channel through
    /// a webhook. A webhook can only post into that channel, nobody can read it through it, so only the developer
    /// sees the suggestions. Nothing about the sender goes with it (no name, no PC name): just the text, Wibn's
    /// version and the app's language.
    /// </summary>
    internal static class Suggestions
    {
        /// <summary>
        /// The Discord channel's webhook link (Channel settings → Integrations → Webhooks), from suggestions.webhook
        /// next to the project, put inside the app when it's built. It's not in the code: the repo is public, and bots
        /// look for these links there to spam them. No file = the box is off.
        /// </summary>
        private static readonly string WebhookUrl = ReadWebhook();

        private static string ReadWebhook()
        {
            try
            {
                using var s = typeof(Suggestions).Assembly.GetManifestResourceStream("suggestions.webhook");
                if (s == null) return "";
                using var r = new System.IO.StreamReader(s);
                string url = r.ReadToEnd().Trim();
                return url.StartsWith("https://discord.com/api/webhooks/") ? url : "";
            }
            catch { return ""; }
        }

        public const int MaxLength = 1500;
        private const int WaitSeconds = 60;   // one suggestion a minute, so the channel can't be flooded from here

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
        private static DateTime _lastSent = DateTime.MinValue;

        public static bool Ready => WebhookUrl.Length > 0;

        /// <summary>Sends it. null when it went, otherwise what to tell the user (already in the app's language).</summary>
        public static async Task<string?> Send(string text)
        {
            text = text.Trim();
            if (text.Length == 0) return Lang.T("اكتب اقتراحك أول");
            if (!Ready) return Lang.T("صندوق الاقتراحات مو جاهز في هذي النسخة");
            int wait = WaitSeconds - (int)(DateTime.UtcNow - _lastSent).TotalSeconds;
            if (wait > 0) return string.Format(Lang.T("استنى {0} ثانية قبل الاقتراح الجاي"), wait);
            if (text.Length > MaxLength) text = text[..MaxLength];

            string body = JsonSerializer.Serialize(new
            {
                username = "Wibn",
                content = $"💡 **Wibn {UpdateChecker.CurrentText}** · {(Lang.English ? "English" : "العربية")}\n{text}",
                allowed_mentions = new { parse = Array.Empty<string>() },   // an @everyone in it pings nobody
            });
            try
            {
                using var res = await Http.PostAsync(WebhookUrl, new StringContent(body, Encoding.UTF8, "application/json"));
                if (!res.IsSuccessStatusCode)
                {
                    CrashLog.Note($"suggestion: webhook answered {(int)res.StatusCode}");
                    return Lang.T("ما قدرت أرسل الاقتراح، جرّب بعدين");
                }
                _lastSent = DateTime.UtcNow;
                return null;
            }
            catch (Exception ex)
            {
                CrashLog.Note("suggestion: " + ex.Message);
                return Lang.T("ما قدرت أرسل الاقتراح، تأكد من الإنترنت");
            }
        }
    }
}
