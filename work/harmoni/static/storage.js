/* الأغاني المحفوظة على الجهاز (عشان تشتغل فوراً): الحد الأقصى، المستخدم، والمسح — من الإعدادات بالكمبيوتر بس. */
(function () {
  "use strict";
  if (["localhost", "127.0.0.1"].indexOf(location.hostname) < 0) return;
  function L(ar, en) { return (typeof settings !== "undefined" && settings.lang === "en") ? en : ar; }
  var st = null;
  function api(body) {
    return fetch("/api/audio_cache", body ? { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) } : {})
      .then(function (r) { return r.json(); }).then(function (j) { if (j && j.options) st = j; return j; });
  }
  function label(mb) {
    if (!mb) return L("لا تحفظ", "Don't save");
    return mb >= 1000 ? (mb / 1000) + L(" جيجا", " GB") : mb + L(" ميجا", " MB");
  }
  function paint() {
    var body = document.getElementById("settings-body");
    if (!body || !st) return;
    var g = document.getElementById("storage-settings");
    if (!g) {
      g = document.createElement("div");
      g.className = "settings-group";
      g.id = "storage-settings";
      g.dataset.key = "storage";
      g.innerHTML = '<div class="settings-head"></div><p class="sp-link-desc st-desc"></p>' +
        '<div class="settings-options st-opts"></div><p class="st-use duck-live" role="status"></p>' +
        '<button type="button" class="opt-btn st-clear"></button>';
      body.appendChild(g);
      g.querySelector(".st-clear").onclick = function () {
        var b = this;
        b.disabled = true;
        api({ clear: true }).then(sync).catch(function () {}).then(function () { b.disabled = false; });
      };
    }
    sync();
  }
  function sync() {
    var g = document.getElementById("storage-settings");
    if (!g || !st) return;
    g.querySelector(".settings-head").textContent = L("الأغاني المحفوظة بالجهاز", "Songs saved on this PC");
    g.querySelector(".st-desc").textContent = L(
      "هارموني يحفظ الأغاني اللي تسمعها كثير على جهازك عشان تشتغل فوراً وبدون نت. هنا تحدد كم مساحة ياخذ. لو المساحة الفاضية بالجهاز قلّت عن ٢ جيجا يوقف الحفظ تلقائياً.",
      "Harmoni keeps songs you play often on your PC so they start instantly. Pick how much space it may use. Saving pauses automatically when free disk space drops below 2 GB.");
    var box = g.querySelector(".st-opts");
    box.innerHTML = "";
    st.options.forEach(function (mb) {
      var b = document.createElement("button");
      b.type = "button";
      b.className = "opt-btn" + (mb === st.limit_mb ? " active" : "");
      b.textContent = label(mb);
      b.onclick = function () { api({ limit_mb: mb }).then(sync).catch(function () {}); };
      box.appendChild(b);
    });
    var t = L("مستخدم: ", "Used: ") + st.used_mb + L(" ميجا (", " MB (") + st.count + L(" أغنية) · فاضي بالجهاز: ", " songs) · free disk: ") + st.free_gb + L(" جيجا", " GB");
    if (st.paused) t += L(" — ⚠️ الحفظ موقف لأن الجهاز شبه ممتلي", " — ⚠️ saving paused, disk almost full");
    g.querySelector(".st-use").textContent = t;
    g.querySelector(".st-clear").textContent = L("امسح الأغاني المحفوظة", "Clear saved songs");
  }
  var orig = window.renderSettings;
  if (typeof orig === "function") {
    window.renderSettings = function () { var r = orig.apply(this, arguments); try { api().then(paint).catch(function () {}); } catch (e) {} return r; };
  }
  api().then(paint).catch(function () {});
})();
