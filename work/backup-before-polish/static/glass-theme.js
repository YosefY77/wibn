/* الثيم الزجاجي: لون/شفافية/ضبابية المستخدم تنرسل لنافذة ويندوز نفسها + غلاف الأغنية للخلفية الاحتياطية */
(function () {
  "use strict";
  var root = document.documentElement, lastKey = null, params = { color: "#8ab4ff", alpha: 0.7, blur: 75 }, timer = 0;
  function isGlass() { return root.getAttribute("data-theme") === "glass"; }
  function send() {
    var on = isGlass();
    var key = on ? [params.color, params.alpha.toFixed(2), params.blur >= 15 ? 1 : 0].join("|") : "off";
    if (key === lastKey) return;
    lastKey = key;
    if (!/^(localhost|127\.0\.0\.1)$/.test(location.hostname)) return;   // البرنامج المثبت فقط
    if (!on) root.removeAttribute("data-glass-native");
    try {
      fetch("/api/window_glass", { method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ on: on, color: params.color, alpha: params.alpha, blur: params.blur }) })
        .then(function (r) { return r.json(); })
        .then(function (j) { if (j && j.native && isGlass()) root.setAttribute("data-glass-native", "1"); else root.removeAttribute("data-glass-native"); })
        .catch(function () {});
    } catch (e) {}
  }
  function schedule() { clearTimeout(timer); timer = setTimeout(send, 90); }
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
