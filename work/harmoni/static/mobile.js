/* نسخة الجوال: شريط تبويبات تحت، مشغل مصغّر يفتح مشغل كامل الشاشة (سحب لتحت يسكره)،
   والضغط على أي أغنية يشغلها. كل الأزرار تستخدم نفس أزرار البرنامج الأصلية (ما فيه منطق تشغيل جديد). */
(function () {
  "use strict";
  var mq = window.matchMedia ? window.matchMedia("(max-width: 820px)") : { matches: false, addEventListener: function () {} };
  var root = document.documentElement;
  function L(ar, en) { return (typeof settings !== "undefined" && settings.lang === "en") ? en : ar; }
  function $(id) { return document.getElementById(id); }
  function isM() { return root.classList.contains("hm-m"); }
  function apply() {
    var was = root.classList.contains("hm-m");
    root.classList.toggle("hm-m", !!mq.matches);
    if (!mq.matches) closeFull();
    if (was !== !!mq.matches) { try { applyTheme(); } catch (e) {} }   // الزجاجي يطفي/يرجع حسب حجم الشاشة
  }
  apply();
  try { mq.addEventListener("change", apply); } catch (e) { try { mq.addListener(apply); } catch (e2) {} }

  var ICON = {
    home: '<path d="M3 10.5 12 3l9 7.5V20a1 1 0 0 1-1 1h-5v-6H9v6H4a1 1 0 0 1-1-1z"/>',
    search: '<circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/>',
    library: '<path d="M4 4v16M9 4v16M14 4l6 16"/>',
    room: '<circle cx="9" cy="8" r="3.2"/><circle cx="17" cy="9" r="2.5"/><path d="M3 20a6 6 0 0 1 12 0M14.5 20a4.5 4.5 0 0 1 7-3.7"/>',
    settings: '<circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"/>',
    down: '<path d="m6 9 6 6 6-6"/>'
  };
  function svg(k) { return '<svg viewBox="0 0 24 24" aria-hidden="true">' + ICON[k] + "</svg>"; }

  var TABS = [
    { id: "home", ar: "الرئيسية", en: "Home" },
    { id: "library", ar: "مكتبتي", en: "Library" },
    { id: "room", ar: "الغرفة", en: "Room" },
    { id: "settings", ar: "الإعدادات", en: "Settings" }
  ];
  function go(id) {
    closeFull();
    try { document.body.classList.remove("m-drawer-open"); } catch (e) {}
    var view = id === "library" ? "likes" : id;
    try { showView(view); } catch (e) {}
    window.scrollTo({ top: 0, behavior: "smooth" });
    mark();
  }
  function buildTabs() {
    if ($("m-tabs")) return;
    var nav = document.createElement("nav");
    nav.id = "m-tabs";
    nav.setAttribute("aria-label", "Harmoni");
    nav.innerHTML = TABS.map(function (t) {
      return '<button type="button" data-tab="' + t.id + '">' + svg(t.id) + "<span></span></button>";
    }).join("");
    document.body.appendChild(nav);
    nav.addEventListener("click", function (e) { var b = e.target.closest("button[data-tab]"); if (b) go(b.dataset.tab); });
    labels();
  }
  function labels() {
    var nav = $("m-tabs");
    if (!nav) return;
    TABS.forEach(function (t) {
      var s = nav.querySelector('[data-tab="' + t.id + '"] span');
      if (s && s.textContent !== L(t.ar, t.en)) s.textContent = L(t.ar, t.en);
    });
  }
  function currentView() {
    var v = document.querySelector(".view.active");
    return v ? v.id.replace(/^view-/, "") : "";
  }
  function mark() {
    var v = currentView();
    if (document.body.dataset.mview !== v) document.body.dataset.mview = v;
    var nav = $("m-tabs");
    if (!nav) return;
    var tab = (v === "likes" || v === "playlist") ? "library" : v;
    nav.querySelectorAll("button").forEach(function (b) { b.classList.toggle("on", b.dataset.tab === tab); });
    if (v === "likes") playlistsStrip();
    if (v !== lastPrefetchView) { lastPrefetchView = v; prefetchView(v); }
  }

  /* ===== مكتبتي (بالجوال): البلاي لستات فوق الأغاني المفضلة ===== */
  function esc(x) { return String(x == null ? "" : x).replace(/[&<>"']/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]; }); }
  var stripKey = "";
  function playlistsStrip() {
    if (!isM()) return;
    var view = $("view-likes");
    if (!view) return;
    var box = $("m-pls");
    if (!box) {
      box = document.createElement("div");
      box.id = "m-pls";
      var head = view.querySelector(".queue-head");
      if (head) head.insertAdjacentElement("beforebegin", box); else view.insertBefore(box, view.firstChild);
      box.addEventListener("click", function (e) {
        var b = e.target.closest("[data-pl]");
        if (b) { try { openPlaylist(b.dataset.pl); } catch (er) {} return; }
        if (e.target.closest(".m-pl-new")) { try { createPlaylistModal(); } catch (er) {} }
      });
    }
    var pls = [];
    try { pls = getPlaylists() || []; } catch (e) {}
    var key = settings.lang + JSON.stringify(pls.map(function (p) { return [p.id, p.name, p.img, (p.tracks || []).length]; }));
    if (key === stripKey && box.childElementCount) return;
    stripKey = key;
    box.innerHTML = '<h2 class="m-lib-title">' + esc(L("مكتبتي", "My library")) + "</h2>" +
      '<div class="m-pls-head"><h3>' + esc(L("البلاي لستات", "Playlists")) + "</h3>" +
      '<button type="button" class="m-pl-new" aria-label="' + esc(L("بلاي لست جديدة", "New playlist")) + '">＋</button></div>' +
      '<div class="m-pls-row">' + (pls.length ? pls.map(function (p) {
        return '<button type="button" class="m-pl" data-pl="' + esc(p.id) + '"><span class="m-pl-cover">' +
          (p.img ? '<img src="' + esc(p.img) + '" alt="" loading="lazy" onerror="this.remove()">' : "") + "♫</span>" +
          '<b>' + esc(p.name) + "</b><small>" + (p.tracks || []).length + " " + esc(L("أغنية", "songs")) + "</small></button>";
      }).join("") : '<p class="m-pls-empty">' + esc(L("ما عندك بلاي لستات للحين — اضغط ＋ وسوّ وحدة", "No playlists yet — tap ＋ to create one")) + "</p>") + "</div>" +
      '<h3 class="m-favs-title">♥ ' + esc(L("الأغاني المفضلة", "Favorites")) + "</h3>";
  }
  window.addEventListener("harmoni-library", function () { stripKey = ""; if (currentView() === "likes") playlistsStrip(); });

  /* نجهز أول أغاني الصفحة بالخلفية (المفضلة/البلاي لست) عشان الضغطة تشتغل على طول */
  var lastPrefetchView = "";
  function prefetchView(v) {
    var list = [];
    try {
      if (v === "likes") list = getLikes().slice(0, 6);
      else if (v === "playlist" && Array.isArray(window.__plPool)) list = window.__plPool.slice(0, 6);
    } catch (e) {}
    list = list.filter(function (t) { return t && t.web_url; }).map(function (t) { return { web_url: t.web_url, source: t.source, title: t.title, artist: t.artist }; });
    if (list.length) fetch("/api/prefetch", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ tracks: list }) }).catch(function () {});
  }

  /* ===== المشغل ===== */
  var bar = null;
  function openFull() {
    if (!isM() || !bar) return;
    bar.classList.add("m-full");
    document.body.classList.add("m-np-open");
    try { history.pushState({ mnp: 1 }, ""); } catch (e) {}
  }
  function closeFull(fromPop) {
    if (!bar || !bar.classList.contains("m-full")) return;
    bar.classList.remove("m-full");
    document.body.classList.remove("m-np-open");
    bar.style.transform = "";
    if (!fromPop) { try { if (history.state && history.state.mnp) history.back(); } catch (e) {} }
  }
  window.addEventListener("popstate", function () { closeFull(true); });
  function buildPlayer() {
    bar = document.querySelector(".player-bar");
    if (!bar || bar.querySelector(".m-np-close")) return;
    var close = document.createElement("button");
    close.type = "button";
    close.className = "m-np-close";
    close.setAttribute("aria-label", L("تصغير", "Minimize"));
    close.innerHTML = svg("down");
    close.onclick = function () { closeFull(); };
    bar.insertBefore(close, bar.firstChild);
    var extra = document.createElement("div");
    extra.className = "m-np-extra";
    extra.innerHTML = '<button type="button" data-x="queue"></button><button type="button" data-x="room"></button>';
    bar.appendChild(extra);
    extra.onclick = function (e) {
      var b = e.target.closest("button[data-x]");
      if (!b) return;
      closeFull();
      try { showView(b.dataset.x); } catch (er) {}
      mark();
    };
    bar.querySelector(".now-playing").addEventListener("click", function (e) {
      if (bar.classList.contains("m-full") || e.target.closest("button")) return;
      var title = ($("np-title") || {}).textContent || "";
      if ($("np-thumb") && $("np-thumb").getAttribute("src") || title.trim()) openFull();
    });
    // سحب المشغل الكامل لتحت = تصغير
    var sy = 0, dy = 0, drag = false;
    bar.addEventListener("touchstart", function (e) {
      if (!bar.classList.contains("m-full") || e.target.closest("input[type=range]")) return;
      drag = bar.scrollTop <= 0; sy = e.touches[0].clientY; dy = 0;
    }, { passive: true });
    bar.addEventListener("touchmove", function (e) {
      if (!drag) return;
      dy = e.touches[0].clientY - sy;
      if (dy > 0) bar.style.transform = "translateY(" + dy + "px)";
    }, { passive: true });
    bar.addEventListener("touchend", function () {
      if (!drag) return;
      drag = false;
      if (dy > 110) closeFull(); else bar.style.transform = "";
    });
    extraLabels();
  }
  function extraLabels() {
    if (!bar) return;
    var q = bar.querySelector('[data-x="queue"]'), r = bar.querySelector('[data-x="room"]');
    if (q) q.textContent = "☰ " + L("قائمة الانتظار", "Queue");
    if (r) r.textContent = "🎧 " + L("غرفة الاستماع", "Listening room");
  }
  function progress() {
    if (!bar || !isM()) return;
    var sk = $("seek");
    var p = sk && +sk.max ? Math.max(0, Math.min(1, (+sk.value || 0) / +sk.max)) : 0;
    bar.style.setProperty("--m-prog", p.toFixed(4));
  }

  /* الضغط على صف الأغنية يشغلها (الأزرار الصغيرة تبقى لشغلها) */
  document.addEventListener("click", function (e) {
    if (!isM()) return;
    var card = e.target.closest(".grid .track-card");
    if (!card || e.target.closest("button, a, input, .artist-link")) return;
    var listen = card.querySelector(".act-listen");
    if (listen && !listen.disabled) listen.click();
  });

  function topSearch() {
    var ms = $("m-search");
    if (!ms || ms.dataset.mReady) return;
    ms.dataset.mReady = "1";
    ms.setAttribute("enterkeyhint", "search");
    ms.removeAttribute("data-i18n-placeholder");
    ms.placeholder = L("ابحث عن أغنية أو فنان…", "Search songs or artists…");
    ms.setAttribute("type", "search");
    // نفس نص البحث يتزامن مع بحث الرئيسية (عشان الفلاتر و«المزيد» تشتغل على نفس الكلمة)
    var mi = $("search-input");
    if (mi) ms.addEventListener("input", function () { mi.value = ms.value; });
  }
  function start() {
    topSearch();
    buildTabs();
    buildPlayer();
    mark();
    var main = document.querySelector(".main") || document.body;
    new MutationObserver(function () { mark(); }).observe(main, { attributes: true, subtree: true, attributeFilter: ["class"] });
    var lib = document.querySelector(".hm-library");
    if (lib) new MutationObserver(mark).observe(lib, { attributes: true, attributeFilter: ["class"] });
    new MutationObserver(function () { labels(); extraLabels(); }).observe(root, { attributes: true, attributeFilter: ["lang"] });
    ["audio", "solo-audio"].forEach(function (id) { var a = $(id); if (a) a.addEventListener("timeupdate", progress); });
    var sk = $("seek"); if (sk) sk.addEventListener("input", progress);
    setInterval(progress, 1000);
    try { root.setAttribute("data-mobile-ready", "1"); } catch (e) {}
  }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start); else setTimeout(start, 0);
})();
