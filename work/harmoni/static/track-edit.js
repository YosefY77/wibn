/* تعديل الأغاني (المفضلة + البلاي لستات حقتك): العنوان، الفنان، الصورة، وتقصير الأغنية من البداية/النهاية (على الموجات الصوتية).
   - التعديل يتحفظ في نسختك بس (المفضلة/البلاي لستات) — ما يغير الأغنية عند غيرك
   - لو حطيتها بغرفة: التعديل والقص يمشي على كل اللي بالغرفة (السيرفر يقفز الأجزاء المقصوصة للكل)
   - بالتشغيل الفردي: المشغل يقفز الأجزاء المقصوصة تلقائياً */
(function () {
  "use strict";
  function Lx(ar, en) { return (typeof settings !== "undefined" && settings.lang === "en") ? en : ar; }
  function escx(s) { return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]; }); }
  function $(id) { return document.getElementById(id); }
  function editKey(t) { return t ? (t.web_url || (t.orig && t.orig.title) || t.title || "") : ""; }

  function fmtT(sec) {
    sec = Math.max(0, Math.round(+sec || 0));
    var h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60), s = sec % 60;
    return (h ? h + ":" + String(m).padStart(2, "0") : m) + ":" + String(s).padStart(2, "0");
  }
  function cleanCuts(cuts, dur) {
    var out = [];
    (cuts || []).forEach(function (c) {
      var a = +c[0], b = +c[1];
      if (!isFinite(a) || !isFinite(b)) return;
      if (dur) { a = Math.min(a, dur); b = Math.min(b, dur); }
      if (a < 0 || b - a < 0.5) return;
      out.push([Math.round(a * 100) / 100, Math.round(b * 100) / 100]);
    });
    out.sort(function (x, y) { return x[0] - y[0]; });
    var m = [];
    out.forEach(function (c) { if (m.length && c[0] <= m[m.length - 1][1]) m[m.length - 1][1] = Math.max(m[m.length - 1][1], c[1]); else m.push(c); });
    return m.slice(0, 20);
  }
  function soloTrack() { try { return solo.active ? solo.track : null; } catch (e) { return null; } }
  function currentTrack() { try { return solo.active ? solo.track : (state && state.current) || null; } catch (e) { return null; } }

  function imgToDataUrl(file) {
    if (typeof plImageToDataUrl === "function") return plImageToDataUrl(file);
    return Promise.reject(new Error("no image helper"));
  }

  function applyEdit(target, ed) {
    if (!target.orig) target.orig = { title: target.title, artist: target.artist || "", thumbnail: target.thumbnail || "" };
    target.title = ed.title;
    target.artist = ed.artist;
    target.thumbnail = ed.thumbnail;
    if (ed.cuts.length) target.cuts = ed.cuts; else delete target.cuts;
  }
  function resetEdit(target) {
    if (target.orig) {
      target.title = target.orig.title;
      target.artist = target.orig.artist;
      target.thumbnail = target.orig.thumbnail;
      delete target.orig;
    }
    delete target.cuts;
  }

  /* نطبق على كل نسخ الأغنية في المفضلة والبلاي لستات + الأغنية الشغالة الحين */
  function applyEverywhere(key, fn, trk) {
    var likes = getLikes(), n = 0;
    likes.forEach(function (t) { if (editKey(t) === key) { fn(t); n++; } });
    store("mu_likes", likes);
    var pls = getPlaylists();
    pls.forEach(function (p) { (p.tracks || []).forEach(function (t) { if (editKey(t) === key) { fn(t); n++; } }); });
    savePlaylists(pls);
    if (trk && editKey(trk) === key) fn(trk);
    var cur = soloTrack();
    if (cur && cur !== trk && editKey(cur) === key) fn(cur);
    try { pushUserData(); } catch (e) {}
    try { renderLikes(); renderLikesView(); } catch (e) {}
    try { if ($("view-playlist") && $("view-playlist").classList.contains("active") && typeof renderPlaylistView === "function") renderPlaylistView(); } catch (e) {}
    try { renderNowPlaying(); } catch (e) {}
    try { window.dispatchEvent(new Event("harmoni-library")); window.dispatchEvent(new Event("harmoni-track")); } catch (e) {}
    paintMarks();
    return n;
  }

  /* ===== الموجات الصوتية (تتحسب من صوت الأغنية نفسها وتنحفظ) ===== */
  var WAVES = {};
  function waveCacheGet(key) {
    if (WAVES[key]) return WAVES[key];
    try { var all = JSON.parse(localStorage.getItem("mu_waves") || "{}"); if (all[key]) return (WAVES[key] = all[key]); } catch (e) {}
    return null;
  }
  function waveCachePut(key, w) {
    WAVES[key] = w;
    try {
      var all = JSON.parse(localStorage.getItem("mu_waves") || "{}");
      all[key] = w;
      var ks = Object.keys(all);
      if (ks.length > 80) ks.slice(0, ks.length - 80).forEach(function (k) { delete all[k]; });
      localStorage.setItem("mu_waves", JSON.stringify(all));
    } catch (e) {}
  }
  function loadWave(trk, bars, signal) {
    var key = editKey(trk);
    var hit = waveCacheGet(key);
    if (hit && hit.p && hit.p.length) return Promise.resolve(hit);
    return Promise.resolve(typeof prepareSoloTrack === "function" ? prepareSoloTrack(trk, true) : trk).then(function (t) {
      var su = (t && t.stream_url) || trk.stream_url;
      if (!su || /\.m3u8($|\?)/i.test(su)) throw new Error("no file");
      var url = typeof soloPlayableUrl === "function" ? soloPlayableUrl(su) : su;
      return fetch(url, { signal: signal });
    }).then(function (r) {
      if (!r.ok) throw new Error("http " + r.status);
      return r.arrayBuffer();
    }).then(function (buf) {
      // نفك الصوت بدقة منخفضة (3000Hz) — يكفي للموجات ويخلي الأغاني الطويلة خفيفة على الذاكرة
      var Ctx = window.OfflineAudioContext || window.webkitOfflineAudioContext;
      var ctx = new Ctx(1, 3000, 3000);
      return new Promise(function (res, rej) { var p = ctx.decodeAudioData(buf, res, rej); if (p && p.then) p.then(res, rej); });
    }).then(function (ab) {
      var data = ab.getChannelData(0), n = data.length, block = Math.max(1, Math.floor(n / bars)), peaks = [], max = 0;
      for (var i = 0; i < bars; i++) {
        var s = 0, start = i * block, end = Math.min(n, start + block), step = Math.max(1, Math.floor((end - start) / 400));
        for (var j = start; j < end; j += step) { var v = Math.abs(data[j]); if (v > s) s = v; }
        peaks.push(s); if (s > max) max = s;
      }
      var w = { p: peaks.map(function (v) { return Math.round((max ? v / max : 0) * 100); }), d: Math.round(ab.duration) };
      waveCachePut(key, w);
      return w;
    });
  }

  function trimOf(cuts, dur) {
    var a = 0, b = dur;
    (cuts || []).forEach(function (c) {
      if (c[0] <= 0.5) a = Math.max(a, c[1]);
      else if (dur && c[1] >= dur - 0.5) b = Math.min(b, c[0]);
    });
    return [a, b];
  }

  function editTrackModal(trk) {
    if (!trk) return;
    var key = editKey(trk);
    var dur = +trk.duration || 0;
    var ed = { title: trk.title || "", artist: trk.artist || "", thumbnail: trk.thumbnail || "" };
    var trim = trimOf(trk.cuts, dur);
    var BARS = 160;
    var wave = null;
    var ac = window.AbortController ? new AbortController() : null;
    showModal(Lx("تعديل الأغنية", "Edit song"),
      '<div class="tre">' +
        '<div class="tre-top">' +
          '<div class="tre-cover-wrap"><div class="tre-cover" id="tre-cover"></div>' +
            '<label class="btn tre-small">' + escx(Lx("تغيير الصورة", "Change image")) + '<input type="file" id="tre-file" accept="image/*" hidden></label></div>' +
          '<div class="tre-fields">' +
            '<label class="ple-lab" for="tre-title">' + escx(Lx("العنوان", "Title")) + "</label>" +
            '<input id="tre-title" class="search-input" maxlength="150" autocomplete="off">' +
            '<label class="ple-lab" for="tre-artist">' + escx(Lx("الفنان", "Artist")) + "</label>" +
            '<input id="tre-artist" class="search-input" maxlength="100" autocomplete="off">' +
          "</div></div>" +
        '<div class="tre-cuts">' +
          '<div class="tre-cuts-head"><b>✂ ' + escx(Lx("تقصير الأغنية", "Trim the song")) + "</b>" +
            "<span>" + escx(Lx("اسحب الطرفين عشان تشيل جزء من البداية أو النهاية", "Drag the two edges to trim the start or the end")) + "</span></div>" +
          '<div class="tre-wave" id="tre-wave" dir="' + (getComputedStyle(document.documentElement).direction === "rtl" ? "rtl" : "ltr") + '">' +
            '<canvas id="tre-canvas"></canvas><div class="tre-shade s"></div><div class="tre-shade e"></div>' +
            '<div class="tre-h s" id="tre-hs" role="slider" tabindex="0"><span></span></div><div class="tre-h e" id="tre-he" role="slider" tabindex="0"><span></span></div>' +
            '<div class="tre-wave-msg" id="tre-wave-msg">' + escx(Lx("جاري تحميل الموجات الصوتية...", "Loading the waveform...")) + "</div></div>" +
          '<div class="tre-times" id="tre-times"></div>' +
        "</div>" +
        '<p class="ple-msg" id="tre-msg"></p>' +
        '<div class="ple-actions">' + (trk.orig || (trk.cuts && trk.cuts.length) ? '<button type="button" class="btn tre-reset" id="tre-reset">' + escx(Lx("رجّع الأصل", "Restore original")) + "</button>" : "") +
          '<button type="button" class="btn" id="tre-cancel">' + escx(Lx("إلغاء", "Cancel")) + "</button>" +
          '<button type="button" class="btn btn-primary" id="tre-save">' + escx(Lx("حفظ", "Save")) + "</button></div>" +
      "</div>");
    var mc = document.querySelector("#modal .modal-content");
    if (mc) mc.classList.add("ple-modal", "tre-modal");
    $("tre-title").value = ed.title;
    $("tre-artist").value = ed.artist;
    var box = $("tre-wave"), cv = $("tre-canvas"), rtl = box.getAttribute("dir") === "rtl";

    function paintCover() { $("tre-cover").innerHTML = ed.thumbnail ? '<img src="' + escx(ed.thumbnail) + '" alt="">' : "♪"; }
    function frac(t) { return dur ? Math.max(0, Math.min(1, t / dur)) : 0; }
    function draw() {
      var W = box.clientWidth || 560, H = box.clientHeight || 90, dpr = window.devicePixelRatio || 1;
      cv.width = W * dpr; cv.height = H * dpr; cv.style.width = W + "px"; cv.style.height = H + "px";
      var g = cv.getContext("2d");
      g.setTransform(dpr, 0, 0, dpr, 0, 0);
      g.clearRect(0, 0, W, H);
      var cs = getComputedStyle(box), on = cs.getPropertyValue("--wave-on").trim() || "#1db954", off = cs.getPropertyValue("--wave-off").trim() || "rgba(255,255,255,.22)";
      var peaks = wave ? wave.p : null, n = peaks ? peaks.length : BARS, bw = W / n;
      for (var i = 0; i < n; i++) {
        var v = peaks ? Math.max(4, peaks[i]) / 100 : 0.08;
        var h = Math.max(2, v * (H - 10)), x = rtl ? W - (i + 1) * bw : i * bw;
        var t = (i + 0.5) / n * dur;
        g.fillStyle = (t >= trim[0] && t <= trim[1]) ? on : off;
        g.fillRect(x + bw * 0.18, (H - h) / 2, Math.max(1, bw * 0.64), h);
      }
      var fs = frac(trim[0]) * 100, fe = frac(trim[1]) * 100;
      var side = rtl ? "right" : "left";
      box.querySelector(".tre-shade.s").style.cssText = side + ":0;width:" + fs + "%";
      box.querySelector(".tre-shade.e").style.cssText = side + ":" + fe + "%;width:" + (100 - fe) + "%";
      $("tre-hs").style.cssText = side + ":" + fs + "%";
      $("tre-he").style.cssText = side + ":" + fe + "%";
      $("tre-hs").querySelector("span").textContent = fmtT(trim[0]);
      $("tre-he").querySelector("span").textContent = fmtT(trim[1]);
      var cut = trim[0] + (dur - trim[1]);
      $("tre-times").innerHTML = dur
        ? "<span>" + escx(Lx("المدة الأصلية", "Original")) + " <b>" + fmtT(dur) + "</b></span><span>" + escx(Lx("بعد التقصير", "After trim")) + " <b>" + fmtT(trim[1] - trim[0]) + "</b></span>" +
          (cut >= 0.5 ? "<span class=\"cut\">✂ " + fmtT(cut) + "</span>" : "")
        : "";
    }
    function posToTime(clientX) {
      var r = box.getBoundingClientRect();
      var f = (clientX - r.left) / r.width;
      if (rtl) f = 1 - f;
      return Math.max(0, Math.min(1, f)) * dur;
    }
    var dragging = null;
    function setHandle(which, t) {
      var MIN = Math.min(3, dur / 4);
      if (which === "s") trim[0] = Math.max(0, Math.min(t, trim[1] - MIN));
      else trim[1] = Math.min(dur, Math.max(t, trim[0] + MIN));
      draw();
    }
    box.addEventListener("pointerdown", function (e) {
      if (!dur) return;
      var t = posToTime(e.clientX);
      dragging = Math.abs(t - trim[0]) <= Math.abs(t - trim[1]) ? "s" : "e";
      try { box.setPointerCapture(e.pointerId); } catch (er) {}
      setHandle(dragging, t);
      e.preventDefault();
    });
    box.addEventListener("pointermove", function (e) { if (dragging) setHandle(dragging, posToTime(e.clientX)); });
    box.addEventListener("pointerup", function () { dragging = null; });
    box.addEventListener("pointercancel", function () { dragging = null; });
    ["s", "e"].forEach(function (w) {
      $(w === "s" ? "tre-hs" : "tre-he").addEventListener("keydown", function (e) {
        var step = e.shiftKey ? 5 : 1, d = 0;
        if (e.key === "ArrowLeft") d = rtl ? step : -step;
        if (e.key === "ArrowRight") d = rtl ? -step : step;
        if (!d) return;
        e.preventDefault(); e.stopPropagation();
        setHandle(w, trim[w === "s" ? 0 : 1] + d);
      });
    });

    paintCover();
    draw();
    var msg = $("tre-wave-msg");
    loadWave(trk, BARS, ac && ac.signal).then(function (w) {
      wave = w;
      if (!dur && w.d) { dur = w.d; trim = trimOf(trk.cuts, dur); }
      if (msg) msg.remove();
      draw();
    }).catch(function () {
      if (!dur) { if (msg) msg.textContent = Lx("ما قدرنا نعرف مدة الأغنية — شغّلها مرة وجرب", "Couldn't get the song length — play it once and try again"); return; }
      if (msg) msg.textContent = Lx("ما قدرنا نجيب الموجات — تقدر تقصّر عادي", "Couldn't load the waveform — you can still trim");
      draw();
    });

    $("tre-file").onchange = function (e) {
      var f = e.target.files && e.target.files[0];
      if (!f) return;
      imgToDataUrl(f).then(function (url) { ed.thumbnail = url; paintCover(); })
        .catch(function () { $("tre-msg").textContent = Lx("ما قدرنا نقرا الصورة، جرب صورة ثانية", "Couldn't read that image"); });
    };
    function close() { try { if (ac) ac.abort(); } catch (e) {} hideModal(); }
    $("tre-cancel").onclick = close;
    var rs = $("tre-reset");
    if (rs) rs.onclick = function () {
      applyEverywhere(key, resetEdit, trk);
      close();
      toast(Lx("رجعت الأغنية لأصلها", "Song restored"));
    };
    $("tre-save").onclick = function () {
      var title = $("tre-title").value.trim();
      if (!title) { $("tre-msg").textContent = Lx("اكتب عنوان للأغنية", "Enter a title"); return; }
      var cuts = [];
      if (dur) {
        if (trim[0] >= 0.5) cuts.push([0, trim[0]]);
        if (trim[1] <= dur - 0.5) cuts.push([trim[1], dur]);
      }
      cuts = cleanCuts(cuts, dur);
      var fin = { title: title, artist: $("tre-artist").value.trim(), thumbnail: ed.thumbnail, cuts: cuts };
      applyEverywhere(key, function (t) { applyEdit(t, fin); if (dur && !t.duration) t.duration = Math.round(dur); }, trk);
      close();
      toast(Lx("تم حفظ التعديل", "Song updated") + (cuts.length ? " ✂" : ""));
    };
  }
  window.editTrackModal = editTrackModal;

  /* ===== التشغيل الفردي: نقفز الأجزاء المقصوصة ===== */
  var skipTimer = 0;
  function checkCuts() {
    var t = soloTrack();
    if (!t || !t.cuts || !t.cuts.length) return;
    var a = soloAudio;
    if (!a || a.paused || a.seeking) return;
    var pos = a.currentTime || 0, dur = a.duration || +t.duration || 0;
    for (var i = 0; i < t.cuts.length; i++) {
      var c = t.cuts[i];
      if (pos >= c[0] && pos < c[1] - 0.25) {
        if (dur && c[1] >= dur - 1) { try { a.currentTime = Math.max(0, (a.duration || dur) - 0.05); } catch (e) {} }   // القص لين النهاية = الأغنية اللي بعدها
        else { try { a.currentTime = c[1]; } catch (e) {} }
        return;
      }
    }
  }
  function syncTimer() {
    var t = soloTrack();
    var need = !!(t && t.cuts && t.cuts.length);
    if (need && !skipTimer) skipTimer = setInterval(checkCuts, 120);
    if (!need && skipTimer) { clearInterval(skipTimer); skipTimer = 0; }
  }

  /* ===== علامات الأجزاء المقصوصة على شريط التشغيل ===== */
  function paintMarks() {
    var seek = $("seek");
    if (!seek || !seek.parentElement) return;
    var host = seek.parentElement;
    var box = host.querySelector(".cut-marks");
    var t = currentTrack();
    var dur = +((t && t.duration) || 0) || +seek.max || 0;
    var cuts = (t && t.cuts) || [];
    if (!cuts.length || !dur) { if (box) box.remove(); syncTimer(); return; }
    if (!box) {
      box = document.createElement("div");
      box.className = "cut-marks";
      if (getComputedStyle(host).position === "static") host.style.position = "relative";
      host.appendChild(box);
    }
    box.style.left = seek.offsetLeft + "px";
    box.style.width = seek.offsetWidth + "px";
    box.style.top = (seek.offsetTop + seek.offsetHeight / 2 - 3) + "px";
    var rtl = getComputedStyle(seek).direction === "rtl";
    box.innerHTML = cuts.map(function (c) {
      var a = Math.max(0, c[0]) / dur * 100, w = Math.max(0, Math.min(dur, c[1]) - c[0]) / dur * 100;
      return '<i style="' + (rtl ? "right:" : "left:") + a + "%;width:" + w + '%"></i>';
    }).join("");
    syncTimer();
  }

  function start() {
    try {
      ["loadedmetadata", "play", "emptied"].forEach(function (ev) { soloAudio.addEventListener(ev, function () { setTimeout(paintMarks, 60); }); });
      audio.addEventListener("loadedmetadata", function () { setTimeout(paintMarks, 60); });
    } catch (e) {}
    var np = $("np-title");
    if (np) new MutationObserver(function () { setTimeout(paintMarks, 60); }).observe(np, { childList: true, characterData: true, subtree: true });
    window.addEventListener("resize", paintMarks);
    window.addEventListener("harmoni-track", paintMarks);
    setInterval(paintMarks, 3000);
    try { renderLikesView(); renderLikes(); } catch (e) {}   // تطلع أزرار التعديل على المفضلة اللي انرسمت قبل ما يتحمل هذا الملف
    try { document.documentElement.setAttribute("data-track-edit-ready", "1"); } catch (e) {}
  }
  window.harmoniCuts = { paint: paintMarks, check: checkCuts, clean: cleanCuts };
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start); else start();
})();
