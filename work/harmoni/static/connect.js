/* Harmoni Connect (جهة الكمبيوتر): يرسل حالة التشغيل لصفحة الجوال وينفذ أوامرها،
   + زر «📱 تحكم من الجوال» بالقائمة يطلع QR، + إعدادات «التحكم من الجوال» و«الفقاعة». */
(function () {
  "use strict";
  if (["localhost", "127.0.0.1"].indexOf(location.hostname) < 0 || window.top !== window) return;
  function L(ar, en) { return (typeof settings !== "undefined" && settings.lang === "en") ? en : ar; }
  function esc(s) { return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]; }); }
  function post(url, body) {
    return fetch(url, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body || {}) }).then(function (r) { return r.json(); });
  }

  /* ===== المزامنة مع الجوال ===== */
  var phone = false, needLists = true, busy = false;
  function snapshot() { try { return JSON.parse(window.__harmoniBubble.state()); } catch (e) { return null; } }
  function lists() {
    try {
      var P = window.__harmoniPanel, l = JSON.parse(P.lists());
      l.playlists = (l.playlists || []).map(function (p) {
        var full = {};
        try { full = JSON.parse(P.playlist(p.id) || "{}"); } catch (e) {}
        p.items = (full.items || []).slice(0, 300);
        return p;
      });
      return l;
    } catch (e) { return null; }
  }
  function run(c) {
    var P = window.__harmoniPanel;
    if (!P || !c) return;
    try {
      if (c.action === "play") P.play(String(c.kind || "likes"), +c.value || 0);
      else if (c.action === "play_track") P.playTrack(c.value);
      else P.cmd(c.action, typeof c.value === "number" ? c.value : (c.value == null ? null : +c.value));
    } catch (e) {}
  }
  function tick() {
    if (busy) return schedule();
    busy = true;
    var body = { state: snapshot() };
    if (needLists) { body.lists = lists(); needLists = false; }
    post("/api/remote/host", body).then(function (j) {
      (j.cmds || []).forEach(run);
      if (j.cmds && j.cmds.length) setTimeout(tick, 150);
      phone = !!j.phone;
      if (j.need_lists) needLists = true;
      var dot = document.getElementById("hc-live");
      if (dot) dot.classList.toggle("on", phone);
    }).catch(function () {}).then(function () { busy = false; schedule(); });
  }
  var timer = 0;
  function schedule() { clearTimeout(timer); timer = setTimeout(tick, phone ? 500 : 2500); }
  window.addEventListener("harmoni-library", function () { needLists = true; });
  setTimeout(tick, 1500);

  /* ===== نافذة QR ===== */
  var linkIdx = 0, links = [];
  function openQR(reset) {
    post("/api/remote/info", reset ? { reset: true } : {}).then(function (j) {
      links = j.links || [];
      linkIdx = Math.min(linkIdx, Math.max(0, links.length - 1));
      showModal("📱 " + L("تحكم من الجوال", "Control from your phone"), '<div class="hc-box" id="hc-box"></div>');
      paintQR();
    }).catch(function () { try { toast(L("ما قدرنا نجهز الرابط", "Couldn't prepare the link")); } catch (e) {} });
  }
  function paintQR() {
    var box = document.getElementById("hc-box");
    if (!box) return;
    if (!links.length) { box.innerHTML = '<p class="hc-note">' + esc(L("ما لقينا عنوان للجهاز على الشبكة.", "No network address found for this PC.")) + "</p>"; return; }
    var l = links[linkIdx], svg = "";
    try { svg = HarmoniQR.svg(l.url, 230); } catch (e) {}
    var kinds = links.map(function (x, i) {
      return '<button type="button" class="opt-btn' + (i === linkIdx ? " active" : "") + '" data-li="' + i + '">' +
        esc(x.kind === "internet" ? L("من أي مكان (إنترنت)", "Anywhere (internet)") : L("نفس شبكة الواي فاي", "Same Wi-Fi")) + "</button>";
    }).join("");
    box.innerHTML =
      '<p class="hc-note">' + esc(L("امسح الرمز بكاميرا جوالك، وتقدر تتحكم بالأغاني والصوت والبلاي لستات وأنت تلعب — بدون Alt+Tab.",
        "Scan with your phone camera to control songs, volume and playlists while you play — no Alt+Tab.")) + "</p>" +
      (links.length > 1 ? '<div class="settings-options hc-kinds">' + kinds + "</div>" : "") +
      '<div class="hc-qr">' + svg + "</div>" +
      '<div class="hc-link"><input class="search-input" dir="ltr" readonly value="' + esc(l.url) + '"><button type="button" class="btn" id="hc-copy">' + esc(L("نسخ", "Copy")) + "</button></div>" +
      '<p class="hc-status"><span id="hc-live" class="hc-live' + (phone ? " on" : "") + '"></span><span class="hc-on">' + esc(L("جوال متصل ✓", "Phone connected ✓")) +
      '</span><span class="hc-off">' + esc(L("بانتظار الجوال…", "Waiting for a phone…")) + "</span></p>" +
      (l.kind === "wifi" ? '<p class="hc-note small">' + esc(L("لازم الجوال يكون على نفس الواي فاي. لو ما فتح: اسمح لهارموني من جدار حماية ويندوز (الشبكات الخاصة).",
        "Your phone must be on the same Wi-Fi. If it won't open, allow Harmoni in Windows Firewall (private networks).")) + "</p>" : "") +
      '<p class="hc-note small">🔒 ' + esc(L("الرابط سري — أي أحد معه يقدر يتحكم بالتشغيل. تقدر تلغيه من الإعدادات.",
        "The link is private — anyone with it can control playback. You can revoke it in Settings.")) + "</p>";
    box.querySelectorAll("[data-li]").forEach(function (b) { b.onclick = function () { linkIdx = +b.dataset.li; paintQR(); }; });
    var copy = document.getElementById("hc-copy");
    if (copy) copy.onclick = function () {
      try { navigator.clipboard.writeText(l.url).then(function () { toast(L("انتسخ الرابط ✓", "Link copied ✓")); }); } catch (e) {}
    };
  }
  window.harmoniConnectQR = openQR;

  /* زر 📱 جنب زر «إضافة لبلاي لست» بالمشغل تحت */
  function navButton() {
    var pl = document.getElementById("np-pl");
    if (!pl || document.getElementById("hc-btn")) return;
    var b = document.createElement("button");
    b.type = "button";
    b.id = "hc-btn";
    b.className = pl.className + " hc-btn";
    b.innerHTML = '<svg viewBox="0 0 24 24"><path d="M8 2.5h8A2.5 2.5 0 0 1 18.5 5v14a2.5 2.5 0 0 1-2.5 2.5H8A2.5 2.5 0 0 1 5.5 19V5A2.5 2.5 0 0 1 8 2.5zm0 2v15h8v-15H8zm2.5 12.5h3v1.5h-3V17z"/></svg>';
    b.onclick = function () { openQR(false); };
    pl.insertAdjacentElement("afterend", b);
    label();
  }
  function label() {
    var b = document.getElementById("hc-btn");
    if (b) b.title = L("تحكم من الجوال (QR)", "Control from your phone (QR)");
  }

  /* ===== الإعدادات: التحكم من الجوال + الفقاعة ===== */
  var bubble = null;
  function bubbleApi(patch) {
    return (patch ? post("/api/bubble_config", patch) : fetch("/api/bubble_config").then(function (r) { return r.json(); }))
      .then(function (j) { if (j && j.position) bubble = j; return bubble; });
  }
  function settingsGroups() {
    var body = document.getElementById("settings-body");
    if (!body) return;
    if (!document.getElementById("connect-settings")) {
      var g = document.createElement("div");
      g.className = "settings-group";
      g.id = "connect-settings";
      g.dataset.key = "connect";
      g.innerHTML = '<div class="settings-head"></div><p class="sp-link-desc hc-d"></p>' +
        '<div class="settings-options"><button type="button" class="btn btn-primary" id="hc-open"></button><button type="button" class="btn" id="hc-reset"></button></div>';
      body.appendChild(g);
      g.querySelector("#hc-open").onclick = function () { openQR(false); };
      g.querySelector("#hc-reset").onclick = function () {
        openQR(true);
        try { toast(L("تم — الأجهزة القديمة انفصلت، امسح الرمز الجديد", "Done — old phones were disconnected, scan the new code")); } catch (e) {}
      };
    }
    var cg = document.getElementById("connect-settings");
    cg.querySelector(".settings-head").textContent = L("التحكم من الجوال (Harmoni Connect)", "Phone remote (Harmoni Connect)");
    cg.querySelector(".hc-d").textContent = L("تحكم بالأغاني والصوت والبلاي لستات من جوالك وأنت تلعب. افتح الرمز وامسحه بالكاميرا.",
      "Control songs, volume and playlists from your phone while gaming. Open the code and scan it with your camera.");
    cg.querySelector("#hc-open").textContent = "📱 " + L("إظهار رمز QR", "Show QR code");
    cg.querySelector("#hc-reset").textContent = L("فصل كل الأجوال (رابط جديد)", "Disconnect all phones (new link)");

    if (!bubble) return;
    if (!document.getElementById("bubble-settings")) {
      var b = document.createElement("div");
      b.className = "settings-group";
      b.id = "bubble-settings";
      b.dataset.key = "bubble";
      b.innerHTML = '<div class="settings-head"></div><p class="sp-link-desc bb-d"></p>' +
        '<label class="duck-row"><input type="checkbox" id="bb-auto"><span class="bb-auto-t"></span></label>' +
        '<label class="duck-row"><input type="checkbox" id="bb-pin"><span class="bb-pin-t"></span></label>' +
        '<div class="bb-pos-t sp-link-desc"></div><div class="settings-options bb-pos">' +
        '<button type="button" class="opt-btn" data-pos="left"></button><button type="button" class="opt-btn" data-pos="center"></button><button type="button" class="opt-btn" data-pos="right"></button></div>';
      body.appendChild(b);
      b.querySelector("#bb-auto").onchange = function (e) { bubbleApi({ auto: e.target.checked }).then(settingsGroups); };
      b.querySelector("#bb-pin").onchange = function (e) { bubbleApi({ pinned: e.target.checked }).then(settingsGroups); };
      b.querySelectorAll("[data-pos]").forEach(function (x) { x.onclick = function () { bubbleApi({ position: x.dataset.pos }).then(settingsGroups); }; });
    }
    var bg = document.getElementById("bubble-settings");
    bg.querySelector(".settings-head").textContent = L("فقاعة التشغيل (Bubble)", "Playback bubble");
    bg.querySelector(".bb-d").textContent = L("لما يكون هارموني مصغّر: ودّ الماوس لأعلى الشاشة وتطلع الفقاعة، أو اضغط Alt+B. حط الماوس على الخط اللي فوقها عشان تغير مكانها أو تثبتها.",
      "When Harmoni is minimized, move the mouse to the top of the screen (or press Alt+B). Hover the line on top of it to move or pin it.");
    bg.querySelector(".bb-auto-t").textContent = L("تطلع لما أودّي الماوس لأعلى الشاشة", "Show when the mouse reaches the top of the screen");
    bg.querySelector(".bb-pin-t").textContent = L("تثبيت الفقاعة (تبقى ظاهرة وهارموني مصغّر)", "Pin the bubble (stays visible while Harmoni is minimized)");
    bg.querySelector(".bb-pos-t").textContent = L("مكانها فوق:", "Position at the top:");
    bg.querySelector("#bb-auto").checked = !!bubble.auto;
    bg.querySelector("#bb-pin").checked = !!bubble.pinned;
    var names = { left: L("يسار", "Left"), center: L("المنتصف", "Center"), right: L("يمين", "Right") };
    bg.querySelectorAll("[data-pos]").forEach(function (x) { x.textContent = names[x.dataset.pos]; x.classList.toggle("active", x.dataset.pos === bubble.position); });
  }

  var orig = window.renderSettings;
  if (typeof orig === "function") {
    window.renderSettings = function () { var r = orig.apply(this, arguments); try { settingsGroups(); } catch (e) {} return r; };
  }
  function start() {
    navButton();
    bubbleApi().then(settingsGroups).catch(function () { settingsGroups(); });
    new MutationObserver(function () { label(); }).observe(document.documentElement, { attributes: true, attributeFilter: ["lang"] });
    // الإعدادات ممكن تتغير من الفقاعة نفسها (الخط اللي فوق) — نحدّث كل شوي لو صفحة الإعدادات مفتوحة
    setInterval(function () {
      var v = document.getElementById("view-settings");
      if (v && v.classList.contains("active")) bubbleApi().then(settingsGroups).catch(function () {});
    }, 4000);
    try { document.documentElement.setAttribute("data-connect-ready", "1"); } catch (e) {}
  }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start); else start();
})();
