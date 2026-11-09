using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinPlus
{
    /// <summary>
    /// The "From the phone" page: a QR to open the phone page, how to make the one-tap iPhone
    /// Shortcuts, the last thing that came, and a new secret code. Also makes the phone page itself.
    /// </summary>
    internal sealed class PhonePage : StackPanel
    {
        private static Style St(string key) => (Style)Application.Current.FindResource(key);

        private readonly Image _qr = new() { Width = 220, Height = 220, Margin = new Thickness(0, 4, 0, 0) };
        private readonly TextBlock _address, _last, _status;
        private readonly ComboBox _ips = new() { Width = 200, VerticalAlignment = VerticalAlignment.Center };

        public PhonePage()
        {
            RenderOptions.SetBitmapScalingMode(_qr, BitmapScalingMode.NearestNeighbor);   // sharp squares
            _status = new TextBlock { Style = St("RowDesc"), FontSize = 13, Margin = new Thickness(2, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
            Children.Add(_status);

            // The QR and the address it holds.
            _address = new TextBlock { Style = St("RowDesc"), FlowDirection = FlowDirection.LeftToRight, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            var qrText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 0, 0), MaxWidth = 460 };
            qrText.Children.Add(new TextBlock { Style = St("RowTitle"), Text = Lang.T("امسحه بكاميرا الآيفون") });
            qrText.Children.Add(new TextBlock
            {
                Style = St("RowDesc"), TextWrapping = TextWrapping.Wrap,
                Text = Lang.T("لازم الجوال والكمبيوتر على نفس الواي فاي. تفتح صفحة تلصق فيها وترسل، أو تاخذ المنسوخ من الكمبيوتر. ومن نفس الصفحة تسوي الاختصار اللي يرسل بضغطة."),
            });
            qrText.Children.Add(_address);
            var ipRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            ipRow.Children.Add(new TextBlock { Style = St("RowDesc"), Text = Lang.T("عنوان الكمبيوتر:"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            ipRow.Children.Add(_ips);
            qrText.Children.Add(ipRow);
            var qrRow = new DockPanel();
            var qrBox = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(12), Padding = new Thickness(8), Child = _qr };
            DockPanel.SetDock(qrBox, Dock.Left);
            qrRow.Children.Add(qrBox);
            qrRow.Children.Add(qrText);
            Children.Add(new Border { Style = St("Row"), Margin = new Thickness(0, 0, 0, 10), Child = qrRow });
            _ips.SelectionChanged += (_, _) => ShowQr();

            // The Shortcut, step by step (the phone page has the address to copy).
            Children.Add(new TextBlock { Style = St("Section"), FontSize = 18, Margin = new Thickness(2, 16, 0, 8), Text = Lang.T("بضغطة وحدة (اختصار الآيفون)") });
            Children.Add(Steps(Lang.T("أرسل اللي نسخته للكمبيوتر"), new[]
            {
                Lang.T("افتح تطبيق «الاختصارات» ← + ← أضف إجراء «الحصول على الحافظة»."),
                Lang.T("أضف «الحصول على محتويات URL»، والصق فيه الرابط من صفحة الجوال (زر «انسخ رابط الاختصار»)."),
                Lang.T("اضغط السهم جنبه: الطريقة POST، ونص الطلب «ملف» واختر «الحافظة»."),
                Lang.T("سمّه «أرسل للكمبيوتر». تقدر تشغله من الويدجت، أو من الإعدادات ← تسهيلات الاستخدام ← اللمس ← الضغط على الخلف ← ضغطتين."),
            }));
            Children.Add(Steps(Lang.T("خذ اللي نسخته في الكمبيوتر"), new[]
            {
                Lang.T("اختصار جديد ← «الحصول على محتويات URL» بنفس الرابط (الطريقة GET)."),
                Lang.T("بعده «نسخ إلى الحافظة». سمّه «من الكمبيوتر»."),
            }));

            // The last thing that came, and a new code.
            Children.Add(new TextBlock { Style = St("Section"), FontSize = 18, Margin = new Thickness(2, 16, 0, 8), Text = Lang.T("آخر شي وصل") });
            _last = new TextBlock { Style = St("RowDesc"), FontSize = 13, TextWrapping = TextWrapping.Wrap, Text = Lang.T("ما وصل شي للحين") };
            Children.Add(new Border { Style = St("Row"), Margin = new Thickness(0, 0, 0, 6), Child = _last });

            var renew = new Button { Style = St("FlatButton"), Content = Lang.T("رمز جديد"), VerticalAlignment = VerticalAlignment.Center };
            renew.Click += (_, _) =>
            {
                if (MessageBox.Show(Lang.T("الاختصارات والصفحة اللي على الجوال بتوقف، ولازم تمسح الـ QR وتحدّث الرابط في الاختصار من جديد. تكمل؟"),
                        "Wibn", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                PhoneClipboard.NewCodeNow();
                ShowQr();
            };
            Children.Add(Row(renew, "", Lang.T("الرمز السري"),
                Lang.T("كل طلب من الجوال لازم يكون فيه هالرمز (هو داخل الـ QR)، عشان أحد ثاني على نفس الواي فاي ما يقدر يرسل لك. إذا تبي تقطع أي جوال، سوّ رمز جديد.")));

            Children.Add(new TextBlock
            {
                Style = St("RowDesc"), Margin = new Thickness(2, 12, 0, 0), TextWrapping = TextWrapping.Wrap,
                Text = Lang.T("إذا طلع لك تنبيه جدار الحماية من ويندوز، اختر «السماح» للشبكات الخاصة، وإلا الجوال ما يقدر يوصل. وإذا الصفحة ما فتحت، تأكد إن شبكة الواي فاي في ويندوز «خاصة» مو «عامة»."),
            });

            Loaded += (_, _) => { PhoneClipboard.Received += OnReceived; Refresh(); };
            Unloaded += (_, _) => PhoneClipboard.Received -= OnReceived;
        }

        private void OnReceived(string preview) => Dispatcher.BeginInvoke(() => _last.Text = $"{DateTime.Now:t} · {preview}");

        private void Refresh()
        {
            var ips = PhoneClipboard.Addresses();
            string? was = _ips.SelectedItem as string;
            _ips.Items.Clear();
            foreach (var ip in ips) _ips.Items.Add(ip);
            _ips.SelectedItem = was != null && ips.Contains(was) ? was : ips.FirstOrDefault();
            if (!PhoneClipboard.Running) _status.Text = Lang.T("الميزة طافية. شغّلها من الإعدادات العامة ← الميزات.");
            else if (ips.Count == 0) _status.Text = Lang.T("الكمبيوتر مو متصل بشبكة. وصّله بالواي فاي أو الراوتر.");
            else _status.Text = Lang.T("شغال. انسخ في الآيفون وأرسله، ويصير في حافظة الكمبيوتر على طول (Ctrl+V).");
            ShowQr();
        }

        private void ShowQr()
        {
            if (_ips.SelectedItem is not string ip || !PhoneClipboard.Running) { _qr.Source = null; _address.Text = ""; return; }
            string url = PhoneClipboard.PageUrl(ip);
            _address.Text = url;
            _qr.Source = Qr(url);
        }

        private static BitmapSource? Qr(string text)
        {
            try
            {
                using var gen = new QRCoder.QRCodeGenerator();
                using var data = gen.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
                byte[] png = new QRCoder.PngByteQRCode(data).GetGraphic(8);
                var img = new BitmapImage();
                img.BeginInit();
                img.StreamSource = new MemoryStream(png);
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch (Exception ex) { CrashLog.Write("phone qr", ex); return null; }
        }

        private static Border Steps(string title, string[] steps)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Style = St("RowTitle"), Text = title, Margin = new Thickness(0, 0, 0, 4) });
            for (int i = 0; i < steps.Length; i++)
                panel.Children.Add(new TextBlock { Style = St("RowDesc"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), Text = $"{i + 1}. {steps[i]}" });
            return new Border { Style = St("Row"), Margin = new Thickness(0, 0, 0, 6), Child = panel };
        }

        private static Border Row(FrameworkElement control, string icon, string title, string desc)
        {
            var dock = new DockPanel();
            DockPanel.SetDock(control, Dock.Right);
            dock.Children.Add(control);
            var glyph = new TextBlock { Style = St("RowIcon"), Text = icon };
            DockPanel.SetDock(glyph, Dock.Left);
            dock.Children.Add(glyph);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            text.Children.Add(new TextBlock { Style = St("RowTitle"), Text = title });
            text.Children.Add(new TextBlock { Style = St("RowDesc"), Text = desc, TextWrapping = TextWrapping.Wrap });
            dock.Children.Add(text);
            return new Border { Style = St("Row"), Margin = new Thickness(0, 0, 0, 6), Child = dock };
        }

        // ---- The page on the phone --------------------------------------------------------------

        /// <summary>The phone page: paste → send, take the PC's copy, and the Shortcut's address.
        /// The phone's browser can't read or write the clipboard on a plain http page, so it's a box
        /// you paste into, and a copy button that selects the text and copies it the old way.</summary>
        public static string Html(string code)
        {
            string E(string arabic) => WebUtility.HtmlEncode(Lang.T(arabic));
            string dir = Lang.English ? "ltr" : "rtl", lang = Lang.English ? "en" : "ar";
            string js(string arabic) => System.Text.Json.JsonSerializer.Serialize(Lang.T(arabic));
            return $$"""
<!doctype html>
<html lang="{{lang}}" dir="{{dir}}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="apple-mobile-web-app-capable" content="yes">
<meta name="apple-mobile-web-app-title" content="Wibn">
<meta name="theme-color" content="#111214">
<title>Wibn</title>
<style>
  :root { color-scheme: dark; --bg:#111214; --card:#1d1f23; --line:#2c2f35; --ink:#f2f3f5; --muted:#9a9ea7; --accent:#4c8dff; --ok:#34c759; }
  * { box-sizing: border-box; }
  body { margin:0; background:var(--bg); color:var(--ink); font: 16px/1.5 -apple-system, "SF Pro Text", "Segoe UI", Tahoma, sans-serif;
         padding: max(16px, env(safe-area-inset-top)) 16px max(24px, env(safe-area-inset-bottom)); }
  h1 { font-size: 22px; margin: 4px 2px 14px; }
  .card { background:var(--card); border:1px solid var(--line); border-radius:16px; padding:14px; margin-bottom:14px; }
  h2 { font-size: 16px; margin: 0 0 10px; }
  textarea { width:100%; min-height:120px; resize:vertical; background:#0d0e10; color:var(--ink); border:1px solid var(--line);
             border-radius:12px; padding:10px; font:inherit; }
  button { width:100%; margin-top:10px; padding:12px; border:0; border-radius:12px; font:inherit; font-weight:600;
           background:var(--accent); color:#fff; }
  button.soft { background:#2a2d33; }
  .msg { min-height:1.5em; margin-top:8px; color:var(--muted); font-size:14px; }
  .msg.ok { color:var(--ok); }
  ol { margin:0; padding-inline-start:20px; color:var(--muted); font-size:14px; }
  li { margin:4px 0; }
  code { display:block; direction:ltr; text-align:left; word-break:break-all; background:#0d0e10; border:1px solid var(--line);
         border-radius:10px; padding:8px; font-size:13px; color:var(--ink); margin-top:8px; }
</style>
</head>
<body>
<h1>Wibn</h1>

<div class="card">
  <h2>{{E("أرسل للكمبيوتر")}}</h2>
  <textarea id="out" placeholder="{{E("الصق هنا")}}"></textarea>
  <button id="send">{{E("أرسل")}}</button>
  <div class="msg" id="sendMsg"></div>
</div>

<div class="card">
  <h2>{{E("من الكمبيوتر")}}</h2>
  <textarea id="in" readonly placeholder="{{E("اللي نسخته في الكمبيوتر يطلع هنا")}}"></textarea>
  <button class="soft" id="get">{{E("جيب المنسوخ")}}</button>
  <button class="soft" id="copy">{{E("انسخه في الجوال")}}</button>
  <div class="msg" id="getMsg"></div>
</div>

<div class="card">
  <h2>{{E("بضغطة وحدة (اختصار)")}}</h2>
  <ol>
    <li>{{E("افتح تطبيق «الاختصارات» ← + ← «الحصول على الحافظة».")}}</li>
    <li>{{E("بعده «الحصول على محتويات URL» والصق هالرابط:")}}</li>
  </ol>
  <code id="url"></code>
  <button class="soft" id="copyUrl">{{E("انسخ رابط الاختصار")}}</button>
  <ol start="3">
    <li>{{E("اضغط السهم: الطريقة POST، ونص الطلب «ملف» ← «الحافظة».")}}</li>
    <li>{{E("شغّله بعد ما تنسخ، أو حطه على الضغط على خلف الجوال (الإعدادات ← تسهيلات الاستخدام ← اللمس ← الضغط على الخلف).")}}</li>
  </ol>
  <div class="msg" id="urlMsg"></div>
</div>

<script>
  const code = {{System.Text.Json.JsonSerializer.Serialize(code)}};
  const clip = location.origin + "/clip?k=" + encodeURIComponent(code);
  const $ = id => document.getElementById(id);
  $("url").textContent = clip;

  function say(id, text, ok) { const m = $(id); m.textContent = text; m.className = "msg" + (ok ? " ok" : ""); }

  // The old way of copying (works on a plain http page): select the text and copy it.
  function copyText(text) {
    const t = document.createElement("textarea");
    t.value = text; t.setAttribute("readonly", ""); t.style.position = "fixed"; t.style.opacity = "0";
    document.body.appendChild(t); t.focus(); t.select(); t.setSelectionRange(0, text.length);
    let done = false; try { done = document.execCommand("copy"); } catch (e) {}
    document.body.removeChild(t);
    return done;
  }

  $("send").onclick = async () => {
    const text = $("out").value;
    if (!text) { say("sendMsg", {{js("الصق شي أول")}}); return; }
    try {
      const r = await fetch(clip, { method: "POST", headers: { "Content-Type": "text/plain; charset=utf-8" }, body: text });
      say("sendMsg", await r.text(), r.ok);
      if (r.ok) $("out").value = "";
    } catch (e) { say("sendMsg", {{js("ما وصل. تأكد إن الكمبيوتر شغال وعلى نفس الواي فاي.")}}); }
  };

  $("get").onclick = async () => {
    try {
      const r = await fetch(clip);
      const text = await r.text();
      $("in").value = text;
      say("getMsg", text ? {{js("هذا اللي في الكمبيوتر")}} : {{js("ما فيه كلام منسوخ في الكمبيوتر")}}, !!text);
    } catch (e) { say("getMsg", {{js("ما قدرت أوصل للكمبيوتر.")}}); }
  };

  $("copy").onclick = () => {
    const text = $("in").value;
    if (!text) { say("getMsg", {{js("جيب المنسوخ أول")}}); return; }
    say("getMsg", copyText(text) ? {{js("انتسخ في الجوال")}} : {{js("ما قدرت أنسخ، اضغط على الكلام مطولاً وانسخه")}}, true);
  };

  $("copyUrl").onclick = () => say("urlMsg", copyText(clip) ? {{js("انتسخ الرابط")}} : {{js("اضغط على الرابط مطولاً وانسخه")}}, true);
</script>
</body>
</html>
""";
        }
    }
}
