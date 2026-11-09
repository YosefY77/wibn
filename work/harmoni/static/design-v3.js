/* التصميم الجديد (v3) — شكل الفقاعة على البرنامج كله + الحركة.
   - يشتغل بالكمبيوتر مع الثيم الداكن/المخصص (الفاتح والزجاجي والجوال يبقون على شكلهم)
   - الإعدادات ← «تصميم البرنامج»: الجديد / الكلاسيكي (يرجّع الشكل القديم كامل على طول) */
(function () {
  "use strict";
  var KEY = "hm_design";
  var root = document.documentElement;
  function L(ar, en) { return (typeof settings !== "undefined" && settings.lang === "en") ? en : ar; }
  function want() { try { return localStorage.getItem(KEY) !== "classic"; } catch (e) { return true; } }
  function mobile() { return root.classList.contains("hm-m") || (window.matchMedia && window.matchMedia("(max-width: 820px)").matches); }
  var reduce = window.matchMedia ? window.matchMedia("(prefers-reduced-motion: reduce)") : { matches: false };

  function apply() {
    var th = root.getAttribute("data-theme") || "dark";
    var on = want() && !mobile() && th !== "light" && th !== "glass";
    root.classList.toggle("v3", on);
    return on;
  }

  /* ---------- الخلفية: صورة الأغنية الحالية مغبّشة (تتبدل بتلاشي) ---------- */
  var bd = null, imgs = [], cur = 0, lastSrc = "";
  function backdrop() {
    if (bd) return;
    bd = document.createElement("div");
    bd.id = "v3-backdrop";
    bd.setAttribute("aria-hidden", "true");
    bd.innerHTML = '<div class="glow"></div><img alt=""><img alt="">';
    document.body.insertBefore(bd, document.body.firstChild);
    imgs = bd.querySelectorAll("img");
  }
  function setArt(src) {
    if (!bd || src === lastSrc) return;
    lastSrc = src;
    var next = imgs[1 - cur], prev = imgs[cur];
    if (!src) { prev.classList.remove("on"); next.classList.remove("on"); return; }
    next.onload = function () { next.classList.add("on"); prev.classList.remove("on"); cur = 1 - cur; };
    next.src = src;
  }
  function syncArt() {
    var th = document.getElementById("np-thumb");
    var src = th && th.getAttribute("src");
    setArt(src && !/^data:image\/gif/.test(src) ? src : "");
  }

  /* ---------- الحركة ---------- */
  function animateView(v) {
    if (!root.classList.contains("v3") || !v) return;
    v.classList.remove("v3-enter");
    void v.offsetWidth;
    v.classList.add("v3-enter");
    setTimeout(function () { v.classList.remove("v3-enter"); }, 420);
    stagger(v);
  }
  // البطاقات تطلع ورا بعض (أول 14 بس — الباقي على طول عشان ما تنتظر)
  function stagger(scope) {
    if (!root.classList.contains("v3")) return;
    var cards = (scope || document).querySelectorAll(".track-card:not(.v3-seen), .plv-row:not(.v3-seen)");
    var n = 0;
    cards.forEach(function (c) {
      c.classList.add("v3-seen");
      if (c.offsetParent === null) return;   // مخفية
      if (n < 14) {
        c.style.setProperty("--v3-d", (n * 28) + "ms");
        c.classList.add("v3-card-in");
        (function (el) { setTimeout(function () { el.classList.remove("v3-card-in"); }, 500 + n * 28); })(c);
      }
      n++;
    });
  }
  function swap(el) {
    if (!el || !root.classList.contains("v3")) return;
    el.classList.remove("v3-swap"); void el.offsetWidth; el.classList.add("v3-swap");
    setTimeout(function () { el.classList.remove("v3-swap"); }, 420);
  }
  function playMorph() {
    if (!root.classList.contains("v3") || reduce.matches) return;
    var ip = document.getElementById("icon-play"), ipa = document.getElementById("icon-pause");
    var shown = ip && ip.style.display !== "none" ? ip : ipa;
    if (shown && shown.animate) shown.animate([{ transform: "scale(.55)", opacity: .2 }, { transform: "scale(1)", opacity: 1 }],
      { duration: 180, easing: "cubic-bezier(.2,.8,.2,1)" });
  }

  /* ---------- الإعدادات ---------- */
  function settingsGroup() {
    var body = document.getElementById("settings-body");
    if (!body) return;
    var g = document.getElementById("design-settings");
    if (!g) {
      g = document.createElement("div");
      g.className = "settings-group";
      g.id = "design-settings";
      g.dataset.key = "design";
      g.innerHTML = '<div class="settings-head"></div><p class="sp-link-desc ds-desc"></p><div class="settings-options ds-opts"></div>';
      body.insertBefore(g, body.firstChild);
    }
    g.querySelector(".settings-head").textContent = L("تصميم البرنامج", "App design");
    g.querySelector(".ds-desc").textContent = L(
      "الجديد: نفس شكل الفقاعة — خلفية من صورة الأغنية، ألواح زجاجية، وحركة ناعمة (مع الثيم الداكن أو المخصص). الكلاسيكي: الشكل القديم.",
      "New: the bubble look — album-art background, glass panels and smooth motion (with the dark or custom theme). Classic: the previous look.");
    var box = g.querySelector(".ds-opts");
    box.innerHTML = "";
    [["v3", L("الجديد", "New")], ["classic", L("الكلاسيكي", "Classic")]].forEach(function (o) {
      var b = document.createElement("button");
      b.type = "button";
      b.className = "opt-btn" + ((want() ? "v3" : "classic") === o[0] ? " active" : "");
      b.textContent = o[1];
      b.onclick = function () {
        try { localStorage.setItem(KEY, o[0]); } catch (e) {}
        apply(); syncArt(); settingsGroup();
      };
      box.appendChild(b);
    });
  }
  var orig = window.renderSettings;
  if (typeof orig === "function") {
    window.renderSettings = function () { var r = orig.apply(this, arguments); try { settingsGroup(); } catch (e) {} return r; };
  }

  /* ---------- التشغيل ---------- */
  function start() {
    backdrop();
    apply();
    syncArt();
    new MutationObserver(apply).observe(root, { attributes: true, attributeFilter: ["data-theme", "class"] });
    var th = document.getElementById("np-thumb");
    if (th) new MutationObserver(function () { syncArt(); swap(th); }).observe(th, { attributes: true, attributeFilter: ["src"] });
    var t = document.getElementById("np-title");
    if (t) new MutationObserver(function () { swap(document.querySelector(".np-meta")); }).observe(t, { childList: true, characterData: true, subtree: true });
    var ip = document.getElementById("icon-play");
    if (ip) new MutationObserver(playMorph).observe(ip, { attributes: true, attributeFilter: ["style"] });
    // الصفحات: لما تصير صفحة «active» تدخل بحركة
    document.querySelectorAll(".view").forEach(function (v) {
      new MutationObserver(function () { if (v.classList.contains("active") && !v.__v3was) animateView(v); v.__v3was = v.classList.contains("active"); })
        .observe(v, { attributes: true, attributeFilter: ["class"] });
      v.__v3was = v.classList.contains("active");
    });
    // بطاقات جديدة (بحث، اقتراحات…) تطلع ورا بعض
    var q = false;
    new MutationObserver(function (muts) {
      if (q || !root.classList.contains("v3")) return;
      for (var i = 0; i < muts.length; i++) {
        var a = muts[i].addedNodes;
        for (var j = 0; j < a.length; j++) {
          var n = a[j];
          if (n.nodeType === 1 && (n.classList.contains("track-card") || n.classList.contains("plv-row") || (n.querySelector && n.querySelector(".track-card,.plv-row")))) {
            q = true;
            requestAnimationFrame(function () { q = false; stagger(document); });
            return;
          }
        }
      }
    }).observe(document.body, { childList: true, subtree: true });
    window.addEventListener("resize", apply);
    try { root.setAttribute("data-design-ready", "1"); } catch (e) {}
  }
  window.harmoniDesign = { apply: apply, set: function (v) { try { localStorage.setItem(KEY, v); } catch (e) {} apply(); } };
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start); else start();
})();
