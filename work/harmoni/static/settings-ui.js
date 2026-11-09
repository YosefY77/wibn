/* صفحة الإعدادات الجديدة: تبويبات (عام / المظهر / الربط / الحساب / التحديثات) + أيقونات + معاينة الثيمات + مفاتيح تشغيل بدل المربعات.
   ما تغيّر منطق أي إعداد — ترتّب وتزيّن المجموعات اللي تبنيها app.js وdiscord.js وpanel-settings.js وupdater.js. */
(function () {
  "use strict";
  var TAB_KEY = "harmoni_settings_tab";
  function L(ar, en) { return (typeof settings !== "undefined" && settings.lang === "en") ? en : ar; }
  function svg(d) {
    return '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' + d + "</svg>";
  }
  var IC = {
    general: svg('<circle cx="12" cy="12" r="3.5"/><path d="M12 2.5v3M12 18.5v3M4.6 4.6l2.1 2.1M17.3 17.3l2.1 2.1M2.5 12h3M18.5 12h3M4.6 19.4l2.1-2.1M17.3 6.7l2.1-2.1"/>'),
    appearance: svg('<circle cx="12" cy="12" r="9"/><path d="M12 3a9 9 0 0 0 0 18z" fill="currentColor"/>'),
    connections: svg('<path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1"/><path d="M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1"/>'),
    account: svg('<circle cx="12" cy="8" r="4"/><path d="M4 21a8 8 0 0 1 16 0"/>'),
    about: svg('<circle cx="12" cy="12" r="9"/><path d="M12 16V8M8.5 11.5L12 8l3.5 3.5"/>'),
    lang: svg('<circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3a14 14 0 0 1 0 18M12 3a14 14 0 0 0 0 18"/>'),
    hotkey: svg('<rect x="2.5" y="6" width="19" height="12" rx="2.5"/><path d="M6.5 10h.01M10 10h.01M14 10h.01M17.5 10h.01M7.5 14h9"/>'),
    desktop: svg('<rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4"/>'),
    panel: svg('<rect x="3" y="4" width="18" height="16" rx="2.5"/><path d="M15 4v16"/>'),
    spotify: svg('<circle cx="12" cy="12" r="9"/><path d="M7.5 9.8c3.2-1 6.6-.7 9.2.9M8 13c2.6-.8 5.3-.5 7.4.8M8.6 16c2-.5 4-.3 5.6.6"/>'),
    discord: svg('<path d="M21 12a8.5 8.5 0 0 1-12.3 7.6L3.5 21l1.4-4.9A8.5 8.5 0 1 1 21 12z"/>')
  };
  IC.taste = svg('<path d="M9 18V5l12-2v13"/><circle cx="6" cy="18" r="3"/><circle cx="18" cy="16" r="3"/>');
  IC.decoration = svg('<circle cx="12" cy="13" r="6"/><path d="M5 8l2.5-4 2.5 3 2-3.5 2 3.5 2.5-3L19 8"/>');
  IC.storage = svg('<ellipse cx="12" cy="6" rx="8" ry="3"/><path d="M4 6v12c0 1.7 3.6 3 8 3s8-1.3 8-3V6M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3"/>');
  IC.ducking = svg('<path d="M3 9v6h4l5 5V4L7 9H3z"/><path d="M16 9.5c.8.8 1.2 1.6 1.2 2.5s-.4 1.7-1.2 2.5M19 7c1.5 1.4 2.3 3.1 2.3 5s-.8 3.6-2.3 5"/>');
  IC.bubble = svg('<rect x="3" y="4" width="18" height="7" rx="3.5"/><path d="M9 7.5h6M5 15h14M5 19h9"/>');
  IC.connect = svg('<rect x="7" y="2.5" width="10" height="19" rx="2.5"/><path d="M11 18.5h2"/>');
  IC.theme = IC.appearance; IC.account2 = IC.account; IC.updates = IC.about;

  var CATS = [
    { id: "general", ar: "عام", en: "General" },
    { id: "appearance", ar: "المظهر", en: "Appearance" },
    { id: "connections", ar: "الربط", en: "Connections" },
    { id: "account", ar: "الحساب", en: "Account" },
    { id: "about", ar: "التحديثات", en: "Updates" }
  ];
  // الترتيب داخل كل تبويب + وصف قصير للمجموعات اللي ما لها وصف
  var KEYS = {
    lang:    { cat: "general", ar: "لغة واجهة البرنامج.", en: "The language of the app interface." },
    taste:   { cat: "general" },
    hotkey:  { cat: "general", ar: "اختصار يفتح المشغّل المصغّر — اضغط الزر وبعدين الاختصار الجديد.", en: "Shortcut that opens the mini player — click it, then press the new keys." },
    desktop: { cat: "general" },
    panel:   { cat: "general" },
    ducking: { cat: "general" },
    storage: { cat: "general" },
    decoration: { cat: "appearance" },
    bubble:  { cat: "general" },
    connect: { cat: "connections" },
    theme:   { cat: "appearance", ar: "اختار شكل البرنامج. الزجاجي يخلي النافذة شفافة فوق سطح المكتب وتقدر تتحكم بلونه.", en: "Choose how Harmoni looks. Glass makes the window see-through with your own tint." },
    spotify: { cat: "connections" },
    discord: { cat: "connections" },
    account: { cat: "account" },
    updates: { cat: "about" },
    other:   { cat: "general" }
  };
  var ORDER = ["lang", "taste", "hotkey", "desktop", "panel", "bubble", "ducking", "storage", "theme", "decoration", "spotify", "discord", "connect", "account", "updates", "other"];

  function keyOf(g) {
    if (g.dataset.key) return g.dataset.key;
    if (g.id === "panel-settings") return "panel";
    if (g.id === "update-settings") return "updates";
    if (g.querySelector("#discord-client-id")) return "discord";
    return "other";
  }
  function getTab() { try { return localStorage.getItem(TAB_KEY) || "general"; } catch (e) { return "general"; } }
  function setTab(id) { try { localStorage.setItem(TAB_KEY, id); } catch (e) {} }

  var busy = false, obs = null, raf = 0;

  function decorate(g, key) {
    var head = g.querySelector(".settings-head");
    if (head && !head.querySelector(".st-ico")) {
      var txt = head.textContent;
      head.innerHTML = '<span class="st-ico">' + (IC[key] || IC.general) + '</span><span class="st-title"></span>';
      head.querySelector(".st-title").textContent = txt;
    }
    var meta = KEYS[key] || {};
    if (head && meta.ar && !g.querySelector(".st-sub")) {
      var sub = document.createElement("p");
      sub.className = "st-sub";
      head.insertAdjacentElement("afterend", sub);
    }
    var subEl = g.querySelector(".st-sub");
    if (subEl && meta.ar) { var t = L(meta.ar, meta.en); if (subEl.textContent !== t) subEl.textContent = t; }
    if (key === "theme") {
      var box = g.querySelector(".settings-options");
      if (box && !box.classList.contains("theme-tiles")) box.classList.add("theme-tiles");
      g.querySelectorAll(".opt-btn[data-theme-opt]").forEach(function (b) {
        if (b.classList.contains("theme-tile")) return;
        var name = b.textContent;
        b.classList.add("theme-tile");
        b.innerHTML = '<span class="tt-prev tt-' + b.dataset.themeOpt + '"><i></i><i></i><i></i></span><span class="tt-name"></span>';
        b.querySelector(".tt-name").textContent = name;
      });
    }
    if (key === "lang") {
      var lo = g.querySelector(".settings-options");
      if (lo && !lo.classList.contains("st-seg")) lo.classList.add("st-seg");
    }
  }

  function tabs(view, present, active) {
    var nav = view.querySelector(".st-tabs");
    if (!nav) {
      nav = document.createElement("nav");
      nav.className = "st-tabs";
      nav.setAttribute("role", "tablist");
      var title = view.querySelector(".view-title");
      if (title) title.insertAdjacentElement("afterend", nav); else view.insertBefore(nav, view.firstChild);
      nav.addEventListener("click", function (e) {
        var b = e.target.closest(".st-tab");
        if (!b) return;
        setTab(b.dataset.cat);
        organize();
      });
    }
    var html = CATS.filter(function (c) { return present[c.id]; }).map(function (c) {
      return '<button type="button" role="tab" class="st-tab' + (c.id === active ? " active" : "") + '" data-cat="' + c.id + '" aria-selected="' + (c.id === active) + '">' +
        IC[c.id] + "<span>" + L(c.ar, c.en) + "</span></button>";
    }).join("");
    if (nav.innerHTML !== html) nav.innerHTML = html;
  }

  function organize() {
    var body = document.getElementById("settings-body");
    var view = document.getElementById("view-settings");
    if (!body || !view || busy) return;
    busy = true;
    try {
      body.classList.add("st-v2");
      var groups = Array.prototype.filter.call(body.children, function (el) { return el.classList.contains("settings-group"); });
      var present = {};
      groups.forEach(function (g) {
        var key = keyOf(g);
        var cat = (KEYS[key] || KEYS.other).cat;
        if (g.dataset.key !== key) g.dataset.key = key;
        if (g.dataset.cat !== cat) g.dataset.cat = cat;
        present[cat] = true;
        decorate(g, key);
      });
      var active = getTab();
      if (!present[active]) active = present.general ? "general" : (CATS.filter(function (c) { return present[c.id]; })[0] || { id: "general" }).id;
      // ترتيب ثابت — نحرك العناصر بس لو الترتيب غلط (عشان ما نسبب دوران مع المراقب)
      var sorted = groups.slice().sort(function (a, b) { return ORDER.indexOf(a.dataset.key) - ORDER.indexOf(b.dataset.key); });
      if (sorted.some(function (g, i) { return g !== groups[i]; })) sorted.forEach(function (g) { body.appendChild(g); });
      groups.forEach(function (g) { g.classList.toggle("st-show", g.dataset.cat === active); });
      if (body.dataset.cat !== active) body.dataset.cat = active;
      tabs(view, present, active);
    } finally {
      if (obs) obs.takeRecords();
      busy = false;
    }
  }

  function schedule() { cancelAnimationFrame(raf); raf = requestAnimationFrame(organize); }
  function start() {
    var body = document.getElementById("settings-body");
    if (!body) return;
    obs = new MutationObserver(function () { if (!busy) schedule(); });
    obs.observe(body, { childList: true, subtree: true });
    organize();
    try { document.documentElement.setAttribute("data-settings-ui-ready", "1"); } catch (e) {}
  }
  window.harmoniSettingsTab = function (id) { setTab(id); organize(); };
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start); else start();
})();
