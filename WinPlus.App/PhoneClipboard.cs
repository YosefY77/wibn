using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace WinPlus
{
    /// <summary>
    /// Copy on the phone, paste on the PC (and back). A tiny web server on the home network:
    ///   GET  /?k=CODE      a page for the phone's browser (paste → send, or take the PC's copy)
    ///   POST /clip?k=CODE  the body goes to the PC's clipboard: text, or a picture (image/*)
    ///   GET  /clip?k=CODE  the PC's copied text
    /// iPhones can't let any app read the clipboard in the background, so the phone side is
    /// the page or a Shortcut ("Get Clipboard" → "Get Contents of URL"), one tap (or a back
    /// tap) after copying. Every request needs the secret code from the QR, so others on the
    /// same Wi-Fi can't use it. Plain HTTP on the local network only (nothing leaves the house).
    /// </summary>
    internal static class PhoneClipboard
    {
        private const string Key = @"Software\WinPlus\Phone";
        private const int FirstPort = 51730;
        private const int MaxBody = 30 * 1024 * 1024;

        private static TcpListener? _listener;
        private static CancellationTokenSource? _stop;

        public static bool Running => _listener != null;
        public static int Port { get; private set; }

        /// <summary>Something arrived from the phone (a short preview of it), for the page.</summary>
        public static event Action<string>? Received;

        // ---- Settings ---------------------------------------------------------------------------

        /// <summary>The secret code in every address. Made once; a new one cuts off old Shortcuts.</summary>
        public static string Code
        {
            get
            {
                using var k = Registry.CurrentUser.CreateSubKey(Key);
                if (k.GetValue("Code") is string s && s.Length >= 16) return s;
                string code = NewCode();
                k.SetValue("Code", code);
                return code;
            }
        }

        public static void NewCodeNow()
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            k.SetValue("Code", NewCode());
        }

        private static string NewCode()
        {
            const string letters = "abcdefghjkmnpqrstuvwxyz23456789";   // nothing that looks alike
            var bytes = RandomNumberGenerator.GetBytes(20);
            return new string(bytes.Select(b => letters[b % letters.Length]).ToArray());
        }

        /// <summary>The PC's address on the home network (Wi-Fi or cable with a router), best first.</summary>
        public static List<string> Addresses()
        {
            var list = new List<(string Ip, int Rank)>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                string d = (ni.Description + " " + ni.Name).ToLowerInvariant();
                bool virtualOne = new[] { "virtual", "vmware", "hyper-v", "vethernet", "vpn", "tap-", "wintun", "wireguard", "zerotier", "tailscale", "loopback", "bluetooth" }.Any(d.Contains);
                var props = ni.GetIPProperties();
                bool router = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var a in props.UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var b = a.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue;   // no network
                    int rank = (router ? 0 : 10) + (virtualOne ? 20 : 0) + (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1);
                    list.Add((a.Address.ToString(), rank));
                }
            }
            return list.OrderBy(x => x.Rank).Select(x => x.Ip).Distinct().ToList();
        }

        public static string BaseUrl(string ip) => $"http://{ip}:{Port}";
        public static string PageUrl(string ip) => $"{BaseUrl(ip)}/?k={Code}";
        public static string ClipUrl(string ip) => $"{BaseUrl(ip)}/clip?k={Code}";

        // ---- On / off ---------------------------------------------------------------------------

        public static void Start()
        {
            if (_listener != null) return;
            int saved = Registry.CurrentUser.OpenSubKey(Key)?.GetValue("Port") is int p ? p : FirstPort;
            // The same port as last time if it's free (so Shortcuts keep working), else the next free one.
            foreach (int port in new[] { saved }.Concat(Enumerable.Range(FirstPort, 20)).Distinct())
            {
                try
                {
                    var l = new TcpListener(IPAddress.Any, port);
                    l.Start();
                    _listener = l;
                    Port = port;
                    using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue("Port", port, RegistryValueKind.DWord);
                    break;
                }
                catch (SocketException) { }
            }
            if (_listener == null) { CrashLog.Write("phone clipboard", new Exception("no free port")); return; }
            _stop = new CancellationTokenSource();
            var listener = _listener;
            var token = _stop.Token;
            Task.Run(() => AcceptLoop(listener, token));
        }

        public static void Stop()
        {
            _stop?.Cancel();
            try { _listener?.Stop(); } catch { }
            _listener = null;
            _stop = null;
        }

        private static async Task AcceptLoop(TcpListener listener, CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stop); }
                catch { return; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try { await Serve(client); }
                        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
                        catch (Exception ex) { CrashLog.Write("phone clipboard request", ex); }
                    }
                });
            }
        }

        // ---- One request (a small HTTP/1.1 reader: enough for Safari and Shortcuts) --------------

        private static async Task Serve(TcpClient client)
        {
            client.ReceiveTimeout = client.SendTimeout = 15000;
            using var stream = client.GetStream();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            // The head: request line + headers.
            var head = new List<byte>();
            var one = new byte[1];
            while (head.Count < 16384)
            {
                if (await stream.ReadAsync(one, cts.Token) == 0) return;
                head.Add(one[0]);
                int n = head.Count;
                if (n >= 4 && head[n - 4] == '\r' && head[n - 3] == '\n' && head[n - 2] == '\r' && head[n - 1] == '\n') break;
            }
            var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length < 2) return;
            string method = first[0].ToUpperInvariant(), target = first[1];
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in lines.Skip(1))
            {
                int c = l.IndexOf(':');
                if (c > 0) headers[l[..c].Trim()] = l[(c + 1)..].Trim();
            }

            string path = target.Split('?')[0];
            string code = Query(target, "k");
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(code), Encoding.ASCII.GetBytes(Code)))
            {
                await Reply(stream, 403, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(Lang.T("الرمز غلط. امسح الـ QR من جديد من Wibn.")));
                return;
            }

            if (method == "GET" && (path == "/" || path == "/index.html"))
            {
                await Reply(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(PhonePage.Html(code)));
                return;
            }
            if (method == "GET" && path == "/clip")
            {
                string text = await OnUi(() => Clipboard.ContainsText() ? Clipboard.GetText() : "");
                await Reply(stream, 200, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));
                return;
            }
            if (method == "POST" && path == "/clip")
            {
                if (headers.TryGetValue("Expect", out var expect) && expect.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"), cts.Token);
                byte[]? body = await ReadBody(stream, headers, cts.Token);
                if (body == null) { await Reply(stream, 413, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(Lang.T("كبير مرة"))); return; }
                string type = headers.TryGetValue("Content-Type", out var t) ? t.ToLowerInvariant() : "";
                string said = await Put(body, type);
                await Reply(stream, 200, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(said));
                return;
            }
            await Reply(stream, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not found"));
        }

        private static async Task<byte[]?> ReadBody(NetworkStream stream, Dictionary<string, string> headers, CancellationToken ct)
        {
            if (headers.TryGetValue("Transfer-Encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                var all = new MemoryStream();
                while (true)
                {
                    string sizeLine = await ReadLine(stream, ct);
                    int semi = sizeLine.IndexOf(';');
                    if (semi >= 0) sizeLine = sizeLine[..semi];
                    int size = Convert.ToInt32(sizeLine.Trim(), 16);
                    if (size == 0) { await ReadLine(stream, ct); break; }
                    if (all.Length + size > MaxBody) return null;
                    var chunk = new byte[size];
                    await stream.ReadExactlyAsync(chunk, ct);
                    all.Write(chunk);
                    await ReadLine(stream, ct);   // the chunk's line end
                }
                return all.ToArray();
            }
            int length = headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var v) ? v : 0;
            if (length > MaxBody) return null;
            var body = new byte[Math.Max(0, length)];
            await stream.ReadExactlyAsync(body, ct);
            return body;
        }

        private static async Task<string> ReadLine(NetworkStream stream, CancellationToken ct)
        {
            var sb = new StringBuilder();
            var one = new byte[1];
            while (sb.Length < 1024)
            {
                if (await stream.ReadAsync(one, ct) == 0) break;
                if (one[0] == '\n') break;
                if (one[0] != '\r') sb.Append((char)one[0]);
            }
            return sb.ToString();
        }

        private static string Query(string target, string name)
        {
            int q = target.IndexOf('?');
            if (q < 0) return "";
            foreach (var part in target[(q + 1)..].Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0 && part[..eq] == name) return Uri.UnescapeDataString(part[(eq + 1)..]);
            }
            return "";
        }

        private static async Task Reply(NetworkStream stream, int status, string type, byte[] body)
        {
            string reason = status switch { 200 => "OK", 403 => "Forbidden", 404 => "Not Found", 413 => "Payload Too Large", _ => "Error" };
            string head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n" +
                          "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            await stream.WriteAsync(body);
        }

        // ---- Onto the PC's clipboard -------------------------------------------------------------

        /// <summary>A picture (when it is one), else text (rich text from Notes turned plain). Returns what the phone is told.</summary>
        private static async Task<string> Put(byte[] body, string type)
        {
            if (body.Length == 0) return Lang.T("الحافظة فاضية");

            BitmapSource? image = null;
            if (type.StartsWith("image/") || !type.StartsWith("text/"))
            {
                try
                {
                    var decoder = BitmapDecoder.Create(new MemoryStream(body), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    image = decoder.Frames[0];
                    image.Freeze();
                }
                catch { image = null; }
            }

            if (image != null)
            {
                // Two ways at once: a plain 32-bit bitmap (Paint, Word, most programs) and PNG (browsers,
                // Discord, Office keep its see-through parts). WPF's own SetImage alone isn't read by everything.
                var bgra = new FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                bgra.Freeze();
                var png = new MemoryStream();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bgra));
                encoder.Save(png);
                await OnUi(() =>
                {
                    var data = new DataObject();
                    data.SetImage(bgra);
                    png.Position = 0;
                    data.SetData("PNG", png, false);
                    SetClipboard(() => Clipboard.SetDataObject(data, true));
                    return 0;
                });
                Arrived(Lang.T("صورة"));
                return Lang.T("وصلت الصورة للكمبيوتر");
            }

            string text = type.Contains("rtf") ? await OnUi(() => RtfToText(body)) : Encoding.UTF8.GetString(body);
            text = text.TrimEnd('\0');
            await OnUi(() => { SetClipboard(() => Clipboard.SetText(text)); return 0; });
            Arrived(text);
            return Lang.T("وصل للكمبيوتر");
        }

        private static void Arrived(string what)
        {
            string preview = what.Replace("\r", " ").Replace("\n", " ");
            if (preview.Length > 80) preview = preview[..80] + "…";
            App.Notify(Lang.T("وصل من الجوال"), preview);
            Received?.Invoke(preview);
        }

        /// <summary>The clipboard can be busy for a moment (another program has it open): try a few times.</summary>
        private static void SetClipboard(Action set)
        {
            for (int i = 0; ; i++)
            {
                try { set(); return; }
                catch (System.Runtime.InteropServices.COMException) when (i < 8) { Thread.Sleep(60); }
            }
        }

        private static string RtfToText(byte[] rtf)
        {
            try
            {
                var doc = new FlowDocument();
                var range = new TextRange(doc.ContentStart, doc.ContentEnd);
                range.Load(new MemoryStream(rtf), DataFormats.Rtf);
                return range.Text.TrimEnd('\r', '\n');
            }
            catch { return Encoding.UTF8.GetString(rtf); }
        }

        private static Task<T> OnUi<T>(Func<T> work) => Application.Current.Dispatcher.InvokeAsync(work).Task;
    }
}
