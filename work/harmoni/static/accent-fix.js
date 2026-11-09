/* لون مقروء للنصوص والخطوط اللي تستخدم لون التمييز:
   لو لون التمييز قريب من لون الخلفية (مثلاً أسود على ثيم غامق) نفتّحه/نغمّقه تلقائياً. */
(function () {
  "use strict";
  function parse(c) {
    c = String(c || "").trim();
    var m = c.match(/^#([0-9a-f]{3}|[0-9a-f]{6})$/i);
    if (m) {
      var h = m[1].length === 3 ? m[1].split("").map(function (x) { return x + x; }).join("") : m[1];
      return [parseInt(h.slice(0, 2), 16), parseInt(h.slice(2, 4), 16), parseInt(h.slice(4, 6), 16)];
    }
    m = c.match(/rgba?\(\s*([\d.]+)[,\s]+([\d.]+)[,\s]+([\d.]+)/i);
    return m ? [+m[1], +m[2], +m[3]] : null;
  }
  function lum(rgb) {
    var a = rgb.map(function (v) { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); });
    return 0.2126 * a[0] + 0.7152 * a[1] + 0.0722 * a[2];
  }
  function contrast(a, b) { var x = lum(a), y = lum(b); return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05); }
  function hex(rgb) { return "#" + rgb.map(function (v) { return Math.round(v).toString(16).padStart(2, "0"); }).join(""); }
  function mix(rgb, to, t) { return rgb.map(function (v, i) { return v + (to[i] - v) * t; }); }
  function readable(accent, bg) {
    if (!accent || !bg) return null;
    if (contrast(accent, bg) >= 3) return hex(accent);
    var target = lum(bg) < 0.4 ? [255, 255, 255] : [0, 0, 0];
    for (var t = 0.1; t <= 1.0001; t += 0.05) {
      var c = mix(accent, target, t);
      if (contrast(c, bg) >= 4.5) return hex(c);
    }
    return hex(target);
  }
  function bgOf(el) {
    var cs = getComputedStyle(el);
    return parse(cs.getPropertyValue("--bg2")) || parse(cs.getPropertyValue("--bg")) || parse(getComputedStyle(document.body).backgroundColor) || [13, 17, 19];
  }
  function sync(el) {
    el = el || document.documentElement;
    try {
      var accent = parse(getComputedStyle(el).getPropertyValue("--accent"));
      var out = readable(accent, bgOf(el));
      if (out && el.style.getPropertyValue("--accent-text") !== out) el.style.setProperty("--accent-text", out);
    } catch (e) {}
  }
  function syncAll() {
    sync(document.documentElement);
    var pv = document.getElementById("view-profile");
    if (pv && pv.style.getPropertyValue("--accent")) sync(pv);
  }
  ["applyColors", "applyTheme", "applyScopedColors", "clearScopedColors", "restoreAccent", "applySettings"].forEach(function (name) {
    var f = window[name];
    if (typeof f !== "function") return;
    window[name] = function () {
      var r = f.apply(this, arguments);
      try { syncAll(); } catch (e) {}
      return r;
    };
  });
  try {
    new MutationObserver(function () { syncAll(); }).observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme", "class"] });
  } catch (e) {}
  syncAll();
  setTimeout(syncAll, 800);
  window.__syncAccentText = syncAll;
})();

try { document.documentElement.setAttribute("data-accent-ready", "1"); } catch (e) {}
