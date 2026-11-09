/* الثيم الزجاجي: لون/شفافية/ضبابية المستخدم تنرسل لنافذة ويندوز نفسها + غلاف الأغنية للخلفية الاحتياطية */
(function () {
  "use strict";
  var root = document.documentElement, lastKey = null, params = { color: "#8ab4ff", alpha: 0.7, blur: 75 }, timer = 0;
  function isGlass() { return root.getAttribute("data-theme") === "glass"; }
  function send() {
    var on = isGlass();
    var key = on ? [params.color, params.blur < 8 ? 0 : (params.blur < 55 ? 1 : 2)].join("|") : "off";   // اللون والشفافية ترسمهم الصفحة؛ ويندوز يحتاج بس درجة الغبش
    if (key === lastKey) return;
    lastKey = key;
    if (!/^(localhost|127\.0\.0\.1)$/.test(location.hostname)) return;   // البرنامج المثبت فقط
    if (!on) root.removeAttribute("data-glass-native");
    try {
      fetch("/api/window_glass", { method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ on: on, color: params.color, alpha: params.alpha, blur: params.blur }) })
        .then(function (r) { return r.json(); })
        .then(function (j) {
          if (j && j.native && isGlass()) root.setAttribute("data-glass-native", "1"); else root.removeAttribute("data-glass-native");
          window.__glassBlurOk = !(j && j.blur_ok === false);
          blurHint();
        })
        .catch(function () {});
    } catch (e) {}
  }
  /* الضبابية تحتاج «تأثيرات الشفافية» في ويندوز — لو مطفية نوضح للمستخدم ونعطيه زر يفتحها */
  function blurHint() {
    var rows = document.querySelectorAll(".glass-op-row");
    var row = rows[1];
    var old = document.getElementById("glass-blur-hint");
    if (!row || !isGlass() || window.__glassBlurOk !== false) { if (old) old.remove(); return; }
    if (old) return;
    var ar = !(typeof settings !== "undefined" && settings.lang === "en");
    var d = document.createElement("div");
    d.id = "glass-blur-hint";
    d.className = "glass-hint";
    d.innerHTML = "<span>" + (ar ? "الضبابية تحتاج تفعيل «تأثيرات الشفافية» في إعدادات ويندوز — الحين اللي ورا هارموني يبين بدون غبش."
                                 : "Blur needs Windows “Transparency effects” turned on — for now what's behind Harmoni shows without blur.") +
      '</span><button type="button" class="btn">' + (ar ? "افتح الإعداد" : "Open setting") + "</button>";
    d.querySelector("button").onclick = function () { fetch("/api/open_transparency_settings", { method: "POST" }).catch(function () {}); };
    row.insertAdjacentElement("afterend", d);
  }
  window.addEventListener("focus", function () { lastKey = null; schedule(); });   // رجع من إعدادات ويندوز: نعيد الفحص
  new MutationObserver(function () { if (window.__glassBlurOk === false) blurHint(); }).observe(document.documentElement, { childList: true, subtree: true });
  function schedule() { clearTimeout(timer); timer = setTimeout(send, 220); }   // وقت سحب الأشرطة: نحدّث النافذة بعد ما توقف (بدل كل حركة = وميض)
  window.addEventListener("harmoni-glass", function (e) {
    var d = (e && e.detail) || {};
    params = { color: d.color || params.color, alpha: typeof d.alpha === "number" ? d.alpha : params.alpha, blur: typeof d.blur === "number" ? d.blur : params.blur };
    schedule();
  });
  function syncArt() {
    var th = document.getElementById("np-thumb");
    var src = th && th.getAttribute("src");
    root.style.setProperty("--glass-art", src ? 'url("' + String(src).replace(/"/g, "%22") + '")' : "none");
  }
  function start() {
    new MutationObserver(schedule).observe(root, { attributes: true, attributeFilter: ["data-theme"] });
    var th = document.getElementById("np-thumb");
    if (th) new MutationObserver(syncArt).observe(th, { attributes: true, attributeFilter: ["src"] });
    try { if (isGlass() && typeof applyTheme === "function") applyTheme(); } catch (e) {}
    schedule(); syncArt();
    try { root.setAttribute("data-glass-ready", "1"); } catch (e) {}
  }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start); else start();
})();
