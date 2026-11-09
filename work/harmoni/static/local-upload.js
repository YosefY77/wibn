/* إضافة أغاني من جهازك: أي ملف صوت + صورة + عنوان + اسم المغني → للمفضلة أو لبلاي لست.
   الملف ينحفظ في مجلد البرنامج (uploads) ويشتغل مثل أي أغنية (وبالغرفة لأصحابك بعد). */
(function () {
  "use strict";
  function Lx(ar, en) { return (typeof settings !== "undefined" && settings.lang === "en") ? en : ar; }
  function escx(s) { return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]; }); }
  function $(id) { return document.getElementById(id); }
  var AUDIO_EXT = /\.(mp3|m4a|aac|wav|flac|ogg|oga|opus|webm|weba|mp4|3gp|amr|aiff?|wma)$/i;

  function fmtSize(b) { return b > 1048576 ? (b / 1048576).toFixed(1) + " MB" : Math.round(b / 1024) + " KB"; }
  function titleFromName(name) {
    var t = String(name || "").replace(/\.[^.]+$/, "").replace(/[_]+/g, " ").replace(/\s+/g, " ").trim();
    var m = t.match(/^(.{1,60}?)\s+[-–—]\s+(.+)$/);   // «الفنان - الأغنية»
    return m ? { artist: m[1].trim(), title: m[2].trim() } : { artist: "", title: t };
  }
  function probeDuration(file) {
    return new Promise(function (res) {
      var url = URL.createObjectURL(file), a = new Audio();
      var done = function (d) { URL.revokeObjectURL(url); res(d); };
      a.preload = "metadata";
      a.onloadedmetadata = function () { done(isFinite(a.duration) ? Math.round(a.duration) : 0); };
      a.onerror = function () { done(-1); };   // -1 = المتصفح ما يقدر يشغل هالنوع
      setTimeout(function () { done(0); }, 8000);
      a.src = url;
    });
  }

  function openUpload(opts) {
    opts = opts || {};
    var file = null, dur = 0, cover = "";
    var pls = (typeof getPlaylists === "function" ? getPlaylists() : []) || [];
    var dest = opts.playlistId ? "pl:" + opts.playlistId : "likes";
    showModal(Lx("أضف أغنية من جهازك", "Add a song from your device"),
      '<div class="lup">' +
        '<label class="lup-drop" id="lup-drop"><input type="file" id="lup-file" accept="audio/*,.mp3,.m4a,.aac,.wav,.flac,.ogg,.opus,.webm,.wma,.aiff" hidden>' +
          '<span class="lup-ico">🎵</span><b id="lup-drop-t">' + escx(Lx("اختار ملف صوت أو اسحبه هنا", "Choose an audio file or drop it here")) + "</b>" +
          '<small id="lup-drop-s">MP3, M4A, WAV, FLAC, OGG, OPUS…</small></label>' +
        '<div class="tre-top lup-meta">' +
          '<div class="tre-cover-wrap"><div class="tre-cover" id="lup-cover">♪</div>' +
            '<label class="btn tre-small">' + escx(Lx("اختار صورة", "Choose image")) + '<input type="file" id="lup-img" accept="image/*" hidden></label></div>' +
          '<div class="tre-fields">' +
            '<label class="ple-lab" for="lup-title">' + escx(Lx("العنوان", "Title")) + "</label>" +
            '<input id="lup-title" class="search-input" maxlength="150" autocomplete="off">' +
            '<label class="ple-lab" for="lup-artist">' + escx(Lx("اسم المغني", "Artist")) + "</label>" +
            '<input id="lup-artist" class="search-input" maxlength="100" autocomplete="off">' +
            '<label class="ple-lab" for="lup-dest">' + escx(Lx("وين تنضاف؟", "Add to")) + "</label>" +
            '<select id="lup-dest" class="select lup-dest"><option value="likes">♥ ' + escx(Lx("الأغاني المفضلة", "Favorites")) + "</option>" +
              pls.map(function (p) { return '<option value="pl:' + escx(p.id) + '">♫ ' + escx(p.name) + "</option>"; }).join("") + "</select>" +
          "</div></div>" +
        '<div class="lup-bar hidden" id="lup-bar"><i></i></div>' +
        '<p class="ple-msg" id="lup-msg"></p>' +
        '<div class="ple-actions"><button type="button" class="btn" id="lup-cancel">' + escx(Lx("إلغاء", "Cancel")) + "</button>" +
          '<button type="button" class="btn btn-primary" id="lup-save" disabled>' + escx(Lx("إضافة", "Add")) + "</button></div>" +
      "</div>");
    var mc = document.querySelector("#modal .modal-content");
    if (mc) mc.classList.add("ple-modal", "tre-modal");
    $("lup-dest").value = dest;
    var xhr = null;

    function setFile(f) {
      if (!f) return;
      var okType = (f.type || "").indexOf("audio/") === 0 || AUDIO_EXT.test(f.name);
      if (!okType) { $("lup-msg").textContent = Lx("هذا مو ملف صوت", "That's not an audio file"); return; }
      file = f;
      $("lup-msg").textContent = "";
      $("lup-drop").classList.add("has");
      $("lup-drop-t").textContent = f.name;
      $("lup-drop-s").textContent = fmtSize(f.size) + " · " + Lx("جاري قراءة الملف...", "Reading the file...");
      var guess = titleFromName(f.name);
      if (!$("lup-title").value.trim()) $("lup-title").value = guess.title;
      if (!$("lup-artist").value.trim() && guess.artist) $("lup-artist").value = guess.artist;
      probeDuration(f).then(function (d) {
        if (file !== f) return;
        if (d === -1) {
          $("lup-drop-s").textContent = fmtSize(f.size);
          $("lup-msg").textContent = Lx("البرنامج ما يقدر يشغّل هالنوع من الملفات — حوّله لـ MP3 وجرب", "Harmoni can't play this format — convert it to MP3 and try again");
          $("lup-save").disabled = true;
          return;
        }
        dur = d;
        $("lup-drop-s").textContent = fmtSize(f.size) + (d ? " · " + fmt(d) : "");
        $("lup-save").disabled = false;
      });
    }
    $("lup-file").onchange = function (e) { setFile(e.target.files && e.target.files[0]); };
    var drop = $("lup-drop");
    ["dragenter", "dragover"].forEach(function (ev) { drop.addEventListener(ev, function (e) { e.preventDefault(); drop.classList.add("over"); }); });
    ["dragleave", "drop"].forEach(function (ev) { drop.addEventListener(ev, function (e) { e.preventDefault(); drop.classList.remove("over"); }); });
    drop.addEventListener("drop", function (e) { var f = e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files[0]; setFile(f); });
    $("lup-img").onchange = function (e) {
      var f = e.target.files && e.target.files[0];
      if (!f || typeof plImageToDataUrl !== "function") return;
      plImageToDataUrl(f).then(function (url) { cover = url; $("lup-cover").innerHTML = '<img src="' + escx(url) + '" alt="">'; })
        .catch(function () { $("lup-msg").textContent = Lx("ما قدرنا نقرا الصورة", "Couldn't read the image"); });
    };
    $("lup-cancel").onclick = function () { try { if (xhr) xhr.abort(); } catch (e) {} hideModal(); };
    $("lup-save").onclick = function () {
      var title = $("lup-title").value.trim();
      if (!file) return;
      if (!title) { $("lup-msg").textContent = Lx("اكتب عنوان للأغنية", "Enter a title"); return; }
      var btn = $("lup-save");
      btn.disabled = true;
      $("lup-bar").classList.remove("hidden");
      $("lup-msg").textContent = "";
      var fd = new FormData();
      fd.append("file", file, file.name);
      xhr = new XMLHttpRequest();
      xhr.open("POST", "/api/upload_track");
      xhr.upload.onprogress = function (e) { if (e.lengthComputable) $("lup-bar").querySelector("i").style.width = Math.round(e.loaded / e.total * 100) + "%"; };
      xhr.onerror = function () { btn.disabled = false; $("lup-msg").textContent = Lx("انقطع الرفع — جرب مرة ثانية", "Upload failed — try again"); };
      xhr.onload = function () {
        var j = {};
        try { j = JSON.parse(xhr.responseText || "{}"); } catch (e) {}
        if (xhr.status !== 200 || !j.web_url) { btn.disabled = false; $("lup-msg").textContent = j.error || Lx("ما قدرنا نرفع الملف", "Upload failed"); return; }
        var trk = { id: "loc" + j.id.slice(0, 10), title: title, artist: $("lup-artist").value.trim(), thumbnail: cover, duration: dur || 0,
                    web_url: j.web_url, source: "local", stream_url: j.stream_url, stream_ts: Date.now() };
        var where = $("lup-dest").value;
        if (where === "likes") {
          var likes = getLikes();
          likes.unshift(trk);
          store("mu_likes", likes);
          try { pushUserData(); renderLikes(); renderLikesView(); } catch (e) {}
        } else {
          var list = getPlaylists(), pl = list.filter(function (p) { return "pl:" + p.id === where; })[0];
          if (pl) { (pl.tracks = pl.tracks || []).push(trk); savePlaylists(list); }
          try { if ($("view-playlist").classList.contains("active") && typeof renderPlaylistView === "function") renderPlaylistView(); } catch (e) {}
        }
        try { window.dispatchEvent(new Event("harmoni-library")); } catch (e) {}
        hideModal();
        toast(Lx("تمت إضافة الأغنية", "Song added") + " — " + title);
      };
      xhr.send(fd);
    };
  }
  window.harmoniUploadTrack = openUpload;

  /* أزرار «أضف من جهازك»: صفحة المفضلة + صفحة البلاي لست */
  function likesButton() {
    var head = document.querySelector("#view-likes .queue-head");
    if (!head || head.querySelector(".lup-btn")) return;
    var box = head.querySelector(".likes-actions");
    if (!box) { box = document.createElement("div"); box.className = "likes-actions"; head.appendChild(box); }
    var b = document.createElement("button");
    b.type = "button";
    b.className = "btn lup-btn likes-act";
    box.insertBefore(b, box.firstChild);
    b.onclick = function () { openUpload({}); };
    paintLabels();
  }
  function paintLabels() {
    document.querySelectorAll(".lup-btn").forEach(function (b) { b.textContent = "＋ " + Lx("أضف من جهازك", "Add from device"); });
  }
  function playlistButton() {
    var acts = $("plv-actions");
    if (!acts || acts.querySelector(".lup-btn") || !acts.querySelector(".plv-edit")) return;   // بلاي لست محفوظة (حقتك) بس
    var b = document.createElement("button");
    b.type = "button";
    b.className = "btn lup-btn";
    acts.insertBefore(b, acts.querySelector(".plv-edit"));
    b.onclick = function () { openUpload({ playlistId: (typeof plvCurrent !== "undefined" && plvCurrent && plvCurrent.id) || "" }); };
    paintLabels();
  }
  function start() {
    likesButton();
    var acts = $("plv-actions");
    if (acts) new MutationObserver(playlistButton).observe(acts, { childList: true });
    new MutationObserver(paintLabels).observe(document.documentElement, { attributes: true, attributeFilter: ["lang"] });
    try { document.documentElement.setAttribute("data-local-upload-ready", "1"); } catch (e) {}
  }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start); else start();
})();
