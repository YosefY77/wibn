/* تحسينات الشكل (v2) — عرض بس، ما تغير أي منطق تشغيل:
   1) الصفحة الرئيسية: ترحيب صغير + «كمّل من حيث وقفت» + صف «بلاي لستاتك»
   2) اللوحة اليسار وقت ما فيه أغنية: كرت صغير بدل الـ CD الكبير + زر «شغّل عشوائي من المفضلة»
   3) القائمة اليمين: «تتابعهم» بدل الفراغ
   (المفضلة المكررة في المكتبة ونص «تعمل على جهاز المستضيف» انشالت من CSS) */
(function () {
  "use strict";
  function $(id) { return document.getElementById(id); }
  function Lx(ar, en) { return (typeof settings !== "undefined" && settings.lang === "en") ? en : ar; }
  function escx(s) { return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]; }); }
  function safe(fn) { try { return fn(); } catch (e) { return null; } }
  function nowTrack() {
    return safe(function () {
      if (solo.active && solo.track) return solo.track;
      return state && state.current ? state.current : null;
    });
  }
  function isPlaying() {
    return !!safe(function () {
      var m = solo.active ? soloAudio : audio;
      return m && !m.paused && !m.ended;
    });
  }
  function myName() {
    var p = safe(function () { return getProfile(); }) || {};
    var n = String(p.name || "").trim();
    return n && n !== "مستمع مجهول" && n !== "Anonymous listener" ? n : "";
  }
  function greeting() {
    var h = new Date().getHours();
    if (h >= 5 && h < 12) return Lx("صباح الخير", "Good morning");
    if (h >= 12 && h < 17) return Lx("مساء الخير", "Good afternoon");
    return Lx("مساء الخير", "Good evening");
  }
  function playFrom(list, i) {
    safe(function () { window.__plPool = list.slice(); window.__plPlaying = true; playSolo(list[i]); });
  }
  function thumbHtml(src, cls) {
    return src ? '<img class="' + cls + '" src="' + escx(src) + '" alt="" loading="lazy" onerror="this.onerror=null;this.classList.add(\'no-art\');this.src=\'data:image/gif;base64,R0lGODlhAQABAAAAACw=\'">'
               : '<span class="' + cls + ' no-art"></span>';
  }

  /* ===== 1) الترحيب + كمّل من حيث وقفت ===== */
  function paintHero() {
    var hero = document.querySelector(".hm-hero");
    if (!hero) return;
    var box = hero.querySelector(".hm2-hello");
    if (!box) {
      box = document.createElement("div");
      box.className = "hm2-hello";
      box.innerHTML = '<span class="hm2-greet"></span><h2 class="hm2-title"></h2>';
      hero.insertBefore(box, hero.firstChild);
      hero.classList.add("hm2");
    }
    var n = myName();
    box.querySelector(".hm2-greet").textContent = greeting() + (n ? Lx("، ", ", ") + n : "") + " 👋";
    box.querySelector(".hm2-title").textContent = Lx("وش تبي تسمع اليوم؟", "What do you want to hear today?");
    var res = hero.querySelector(".hm2-resume");
    var hist = safe(function () { return getHistory(); }) || [];
    var cur = nowTrack();
    var show = !cur && hist.length;
    if (!show) { if (res) res.remove(); return; }
    var t = hist[0];
    if (!res) { res = document.createElement("button"); res.type = "button"; res.className = "hm2-resume"; hero.appendChild(res); }
    var key = (t.web_url || t.title) + "|" + (settings && settings.lang);
    if (res.dataset.key === key) return;
    res.dataset.key = key;
    res.innerHTML = thumbHtml(t.thumbnail, "hm2-resume-art") +
      '<span class="hm2-resume-meta"><small>' + escx(Lx("كمّل من حيث وقفت", "Pick up where you left off")) + "</small><b>" + escx(t.title) + "</b><span>" + escx(t.artist || "") + "</span></span>" +
      '<span class="hm2-resume-play" aria-hidden="true"><svg viewBox="0 0 24 24"><path d="M8.5 5.2v13.6L19 12z"/></svg></span>';
    res.title = Lx("تشغيل", "Play");
    res.onclick = function () { playFrom(safe(function () { return getHistory(); }) || [t], 0); };
  }

  /* ===== 1b) صف «بلاي لستاتك» في الرئيسية ===== */
  function paintPlaylistsRow() {
    var home = $("view-home");
    var sugHead = $("suggestions") && $("suggestions").previousElementSibling;
    if (!home || !sugHead) return;
    var sec = $("hm2-pls");
    if (!sec) {
      sec = document.createElement("section");
      sec.id = "hm2-pls";
      sec.className = "hm2-pls";
      sugHead.parentNode.insertBefore(sec, sugHead);
    }
    var pls = safe(function () { return getPlaylists(); }) || [];
    var html = '<div class="section-head"><h3>' + escx(Lx("بلاي لستاتك", "Your playlists")) + "</h3></div><div class=\"hm2-pl-row\">";
    pls.slice(0, 12).forEach(function (p) {
      var tr = p.tracks || [];
      var img = p.img || ((tr.find ? tr.find(function (x) { return x.thumbnail; }) : null) || {}).thumbnail || "";
      html += '<button type="button" class="hm2-pl" data-id="' + escx(p.id) + '">' + thumbHtml(img, "hm2-pl-art") +
        '<b>' + escx(p.name) + "</b><small>" + tr.length + " " + escx(Lx("أغنية", "songs")) + "</small></button>";
    });
    html += '<button type="button" class="hm2-pl hm2-pl-new"><span class="hm2-pl-art hm2-plus">+</span><b>' + escx(Lx("بلاي لست جديدة", "New playlist")) + "</b><small>&nbsp;</small></button></div>";
    if (sec.dataset.html !== html) {
      sec.dataset.html = html;
      sec.innerHTML = html;
      sec.querySelectorAll(".hm2-pl[data-id]").forEach(function (b) { b.onclick = function () { safe(function () { openPlaylist(b.dataset.id); }); }; });
      sec.querySelector(".hm2-pl-new").onclick = function () { safe(function () { createPlaylistModal(); }); };
    }
  }

  /* ===== 2) اللوحة اليسار وقت ما فيه أغنية ===== */
  function paintIdle() {
    var panel = document.querySelector(".hm-disc-panel");
    if (!panel) return;
    var idle = !nowTrack();
    panel.classList.toggle("hm2-idle", idle);
    var btn = panel.querySelector(".hm2-shuffle");
    var likes = safe(function () { return getLikes(); }) || [];
    if (idle && likes.length) {
      if (!btn) {
        btn = document.createElement("button");
        btn.type = "button";
        btn.className = "btn btn-primary hm2-shuffle";
        panel.appendChild(btn);
        btn.onclick = function () {
          var l = (safe(function () { return getLikes(); }) || []).slice();
          for (var i = l.length - 1; i > 0; i--) { var j = Math.floor(Math.random() * (i + 1)); var x = l[i]; l[i] = l[j]; l[j] = x; }
          if (l.length) playFrom(l, 0);
        };
      }
      btn.textContent = "🔀 " + Lx("شغّل عشوائي من المفضلة", "Shuffle your favorites");
      btn.hidden = false;
    } else if (btn) btn.hidden = true;
    document.body.classList.toggle("hm2-nothing", idle);
  }

  /* ===== 3) «تتابعهم» في القائمة اليمين ===== */
  function paintFollowing() {
    var side = document.querySelector(".sidebar");
    var me = $("me-card");
    if (!side || !me) return;
    var box = $("hm2-follow");
    if (!box) {
      box = document.createElement("section");
      box.id = "hm2-follow";
      box.className = "hm2-follow";
      side.insertBefore(box, me);
    }
    var ids = safe(function () { return getFollowing(); }) || [];
    var reg = safe(function () { return profileRegistry; }) || [];
    var people = ids.map(function (u) { return reg.filter(function (p) { return p.uid === u; })[0]; }).filter(Boolean);
    var html = '<h4>' + escx(Lx("تتابعهم", "Following")) + (people.length ? " <small>" + people.length + "</small>" : "") + "</h4>";
    if (!people.length) {
      html += '<p class="hm2-follow-empty">' + escx(Lx("ابحث عن صديقك بيوزره فوق وتابعه عشان يطلع هنا.", "Search a friend by username above and follow them to see them here.")) + "</p>";
    } else {
      html += '<div class="hm2-follow-list">' + people.slice(0, 6).map(function (p) {
        var av = p.avatar ? '<img src="' + escx(p.avatar) + '" alt="">' : "<span>" + escx((p.name || "؟").charAt(0)) + "</span>";
        return '<button type="button" class="hm2-friend" data-uid="' + escx(p.uid) + '"><span class="hm2-av">' + av + "</span><span class=\"hm2-fmeta\"><b>" +
          escx(p.name || "") + "</b>" + (p.username ? '<small dir="ltr">@' + escx(p.username) + "</small>" : "") + "</span></button>";
      }).join("") + "</div>";
    }
    if (box.dataset.html !== html) {
      box.dataset.html = html;
      box.innerHTML = html;
      box.querySelectorAll(".hm2-friend").forEach(function (b) {
        b.onclick = function () {
          var p = (safe(function () { return profileRegistry; }) || []).filter(function (x) { return x.uid === b.dataset.uid; })[0];
          if (p) safe(function () { openProfile(p, b); });
        };
      });
    }
  }

  function paintAll() { paintHero(); paintPlaylistsRow(); paintIdle(); paintFollowing(); }
  var t = 0;
  function soon() { clearTimeout(t); t = setTimeout(paintAll, 120); }
  function start() {
    // أي تعديل على البلاي لستات (من أي مكان) يحدّث صف «بلاي لستاتك»
    safe(function () {
      var orig = window.savePlaylists;
      if (typeof orig === "function") window.savePlaylists = function () { var r = orig.apply(this, arguments); soon(); return r; };
      var ol = window.loadProfiles;
      if (typeof ol === "function") window.loadProfiles = function () { var r = ol.apply(this, arguments); Promise.resolve(r).then(soon); return r; };
    });
    paintAll();
    ["harmoni-library", "harmoni-track", "storage"].forEach(function (ev) { window.addEventListener(ev, soon); });
    safe(function () {
      [audio, soloAudio].forEach(function (m) { ["play", "pause", "emptied", "loadeddata", "ended"].forEach(function (ev) { m.addEventListener(ev, soon); }); });
    });
    new MutationObserver(soon).observe(document.documentElement, { attributes: true, attributeFilter: ["lang"] });
    var np = $("np-title");
    if (np) new MutationObserver(soon).observe(np, { childList: true, characterData: true, subtree: true });
    [1200, 3000, 6000].forEach(function (ms) { setTimeout(paintAll, ms); });   // البروفايلات والسجل توصل بعد التشغيل بشوي
    setInterval(paintAll, 15000);   // الترحيب يتغير مع الوقت + البروفايلات توصل متأخرة
    try { document.documentElement.setAttribute("data-design-v2-ready", "1"); } catch (e) {}
  }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start); else start();
})();
