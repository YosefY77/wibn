/* تخفيض الصوت التلقائي أثناء الكلام (Audio Ducking) — إعدادات البرنامج على الكمبيوتر بس.
   السيرفر (ducking.py) يراقب صوت ديسكورد/الألعاب ويخفض صوت هارموني من خلاط ويندوز، وهنا الإعدادات + مؤشر مباشر. */
(function () {
  "use strict";
  if (["localhost", "127.0.0.1"].indexOf(location.hostname) < 0) return;
  function L(ar, en) { return (typeof settings !== "undefined" && settings.lang === "en") ? en : ar; }
  var st = null, live = 0;
  function api(body) {
    return fetch("/api/ducking", body ? { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) } : {})
      .then(function (r) { return r.json(); }).then(function (j) { if (j && j.config) st = j; return j; });
  }
  function liveText() {
    if (!st) return "";
    if (!st.available) return L("هالخاصية تشتغل على ويندوز بس", "Available on Windows only");
    if (!st.config.enabled) return L("متوقف", "Off");
    if (st.active) return L("🔉 فيه كلام الحين — الموسيقى منخفضة", "🔉 Someone is talking — music lowered") + (st.who ? " (" + st.who.replace(/\.exe$/i, "") + ")" : "");
    return L("🎧 يسمع… بيخفض الصوت أول ما أحد يتكلم", "🎧 Listening… will lower the music when someone talks");
  }
  function paint() {
    var body = document.getElementById("settings-body");
    if (!body || !st) return;
    var g = document.getElementById("ducking-settings");
    if (!g) {
      g = document.createElement("div");
      g.className = "settings-group";
      g.id = "ducking-settings";
      g.dataset.key = "ducking";
      g.innerHTML =
        '<div class="settings-head"></div><p class="sp-link-desc duck-desc"></p>' +
        '<label class="duck-row"><input type="checkbox" id="duck-on"><span class="duck-on-t"></span></label>' +
        '<div class="duck-opts">' +
          '<label class="duck-field"><span class="duck-amt-t"></span> <b id="duck-amt-v"></b><input type="range" id="duck-amt" min="10" max="95" step="5"></label>' +
          '<label class="duck-field"><span class="duck-sens-t"></span> <b id="duck-sens-v"></b><input type="range" id="duck-sens" min="1" max="100" step="1"></label>' +
          '<label class="duck-field"><span class="duck-rel-t"></span> <b id="duck-rel-v"></b><input type="range" id="duck-rel" min="0.3" max="5" step="0.1"></label>' +
          '<div class="duck-src settings-options">' +
            '<button type="button" class="opt-btn" data-src="voice"></button>' +
            '<button type="button" class="opt-btn" data-src="any"></button>' +
          '</div><p class="duck-src-hint sp-link-desc"></p>' +
        '</div><p id="duck-live" class="duck-live" role="status"></p>';
      body.appendChild(g);
      g.querySelector("#duck-on").onchange = function (e) { api({ enabled: e.target.checked }).then(sync); };
      [["duck-amt", "amount"], ["duck-sens", "sensitivity"], ["duck-rel", "release"]].forEach(function (p) {
        var el = g.querySelector("#" + p[0]);
        el.oninput = function () { labels(g, p[1], +el.value); };
        el.onchange = function () { var o = {}; o[p[1]] = +el.value; api(o).then(sync); };
      });
      g.querySelectorAll("[data-src]").forEach(function (b) { b.onclick = function () { api({ source: b.dataset.src }).then(sync); }; });
    }
    sync();
  }
  function labels(g, key, v) {
    if (key === "amount") g.querySelector("#duck-amt-v").textContent = v + "%";
    if (key === "sensitivity") g.querySelector("#duck-sens-v").textContent = v + "%";
    if (key === "release") g.querySelector("#duck-rel-v").textContent = (+v).toFixed(1) + L(" ث", "s");
  }
  function sync() {
    var g = document.getElementById("ducking-settings");
    if (!g || !st) return;
    var c = st.config;
    g.querySelector(".settings-head").textContent = L("تخفيض الصوت أثناء الكلام", "Lower music while people talk");
    g.querySelector(".duck-desc").textContent = L("لما تلعب مع أصحابك ويتكلم أحد في ديسكورد أو اللعبة، هارموني يخفض صوت الموسيقى تلقائياً لين يخلص الكلام، وبعدين يرجعه.",
      "When you play with friends and someone talks on Discord or in-game, Harmoni lowers the music until they stop, then brings it back.");
    g.querySelector(".duck-on-t").textContent = L("تفعيل التخفيض التلقائي", "Enable automatic ducking");
    g.querySelector(".duck-amt-t").textContent = L("نسبة التخفيض", "Lower by");
    g.querySelector(".duck-sens-t").textContent = L("الحساسية", "Sensitivity");
    g.querySelector(".duck-rel-t").textContent = L("يرجع الصوت بعد السكوت بـ", "Restore after silence");
    var on = g.querySelector("#duck-on");
    on.checked = !!c.enabled;
    on.disabled = !st.available;
    g.querySelector(".duck-opts").classList.toggle("off", !c.enabled);
    g.querySelector("#duck-amt").value = c.amount; labels(g, "amount", c.amount);
    g.querySelector("#duck-sens").value = c.sensitivity; labels(g, "sensitivity", c.sensitivity);
    g.querySelector("#duck-rel").value = c.release; labels(g, "release", c.release);
    var bv = g.querySelector('[data-src="voice"]'), ba = g.querySelector('[data-src="any"]');
    bv.textContent = L("ديسكورد وبرامج المكالمات", "Discord & voice apps");
    ba.textContent = L("أي صوت ثاني (والألعاب)", "Any other sound (incl. games)");
    bv.classList.toggle("active", c.source === "voice");
    ba.classList.toggle("active", c.source === "any");
    g.querySelector(".duck-src-hint").textContent = c.source === "any"
      ? L("ينتبه لأي برنامج يطلع صوت — حتى أصوات اللعبة نفسها. لو يخفض كثير، نزّل الحساسية أو اختار ديسكورد بس.", "Reacts to any app making sound — including game effects. If it ducks too often, lower the sensitivity or pick Discord only.")
      : L("ينتبه للكلام اللي يجي من ديسكورد وTeamSpeak وتيمز وواتساب وغيرها — وأصوات اللعبة ما تأثر.", "Reacts to voice from Discord, TeamSpeak, Teams, WhatsApp, etc. — game sounds don't trigger it.");
    g.querySelector("#duck-live").textContent = liveText();
  }
  function poll() {
    var view = document.getElementById("view-settings");
    var visible = view && view.classList.contains("active") && document.getElementById("ducking-settings");
    if (visible && st && st.config.enabled) api().then(sync).catch(function () {});
  }
  var orig = window.renderSettings;
  if (typeof orig === "function") {
    window.renderSettings = function () { var r = orig.apply(this, arguments); try { paint(); } catch (e) {} return r; };
  }
  api().then(paint).catch(function () {});
  live = setInterval(poll, 700);
  try { document.documentElement.setAttribute("data-ducking-ready", "1"); } catch (e) {}
})();
