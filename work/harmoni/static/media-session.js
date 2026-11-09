/* تحكم ويندوز بالوسائط (Media Session):
   - الأغنية الحالية (الاسم + الفنان + الصورة) تطلع في نافذة الصوت حقت ويندوز وشاشة القفل
   - أزرار الميديا في الكيبورد والسماعات البلوتوث تتحكم بـ Harmoni حتى لو البرنامج مصغّر أو مو قدامك */
(function () {
  "use strict";
  var ms = navigator.mediaSession;
  if (!ms || typeof window.MediaMetadata !== "function") return;
  function $(id) { return document.getElementById(id); }
  function players() { return [$("solo-audio"), $("audio")].filter(Boolean); }
  function active() {
    var list = players();
    for (var i = 0; i < list.length; i++) if (!list[i].paused) return list[i];
    for (i = 0; i < list.length; i++) if (list[i].currentSrc && list[i].currentTime > 0) return list[i];
    return null;
  }
  function cmd(action, value) {
    try { if (window.__harmoniBubble) return window.__harmoniBubble.cmd(action, value); } catch (e) {}
    var b = { play_pause: "btn-play", skip: "btn-skip", prev: "btn-prev" }[action];
    if (b && $(b)) $(b).click();
  }
  function seekTo(sec) {
    var sk = $("seek");
    var max = sk ? (+sk.max || 0) : 0;
    cmd("seek", Math.max(0, max ? Math.min(max, sec) : sec));
  }
  function curPos() { var sk = $("seek"); return sk ? (+sk.value || 0) : 0; }

  var lastKey = "";
  function update() {
    var a = active();
    var title = (($("np-title") || {}).textContent || "").trim();
    var hasTrack = players().some(function (p) { return !!p.currentSrc; });
    if (!hasTrack || !title) {
      if (lastKey) { try { ms.metadata = null; ms.playbackState = "none"; } catch (e) {} lastKey = ""; }
      return;
    }
    var artist = (($("np-artist") || {}).textContent || "").trim();
    var th = $("np-thumb");
    var art = th && th.getAttribute("src") ? th.src : "";
    var key = title + "|" + artist + "|" + art;
    if (key !== lastKey) {
      lastKey = key;
      try {
        ms.metadata = new MediaMetadata({ title: title, artist: artist, album: "Harmoni",
          artwork: art ? [{ src: art, sizes: "512x512" }] : [] });
      } catch (e) {}
    }
    try { ms.playbackState = a && !a.paused ? "playing" : "paused"; } catch (e) {}
    try {
      var sk = $("seek"), dur = sk ? +sk.max || 0 : 0, pos = curPos();
      if (dur > 0 && pos >= 0 && pos <= dur && ms.setPositionState) ms.setPositionState({ duration: dur, position: pos, playbackRate: 1 });
    } catch (e) {}
  }

  function on(name, fn) { try { ms.setActionHandler(name, fn); } catch (e) {} }
  on("play", function () { var a = active(); if (!a || a.paused) cmd("play_pause"); });
  on("pause", function () { var a = active(); if (a && !a.paused) cmd("play_pause"); });
  on("nexttrack", function () { cmd("skip"); });
  on("previoustrack", function () { cmd("prev"); });
  on("seekbackward", function (d) { seekTo(curPos() - ((d && d.seekOffset) || 10)); });
  on("seekforward", function (d) { seekTo(curPos() + ((d && d.seekOffset) || 10)); });
  on("seekto", function (d) { if (d && typeof d.seekTime === "number") seekTo(d.seekTime); });

  function start() {
    players().forEach(function (p) {
      ["play", "pause", "loadedmetadata", "emptied", "ended"].forEach(function (ev) { p.addEventListener(ev, function () { setTimeout(update, 50); }); });
    });
    setInterval(update, 1500);
    update();
    try { document.documentElement.setAttribute("data-media-session-ready", "1"); } catch (e) {}
  }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start); else start();
})();
