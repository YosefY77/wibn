/* ملصقات البروفايل (تجربة): صور (GIF/PNG/JPG/WebP) فوق صفحة البروفايل وفوق بطاقة البروفايل.
   - كل مستخدم له مساحة 1GB على السيرفر المشترك (تشمل الصورة والغلاف والملصقات) — يضيف قد ما يبي لين تخلص.
   - صاحب البروفايل يحرك الملصق ويكبره ويدوره من أي زاوية (Shift = بخطوات ثابتة)، ويختار شكله.
   - لكل ملصق مكان بالصفحة ومكان بالبطاقة (كل وحدة تتعدل لحالها). */
(function () {
  "use strict";
  var OUT = 70;   // كم بكسل يقدر ملصق البطاقة يطلع برا حدودها
  var MAXB = 95 * 1024 * 1024, MAXN = 50, TYPES = ["image/gif", "image/png", "image/jpeg", "image/webp"];
  var SHAPES = [
    { id: "none", icon: "▢", ar: "الشكل الأصلي", en: "Original" },
    { id: "square", icon: "■", ar: "مربع", en: "Square" },
    { id: "circle", icon: "●", ar: "دائرة", en: "Circle" },
    { id: "star", icon: "★", ar: "نجمة", en: "Star" },
    { id: "triangle", icon: "▲", ar: "مثلث", en: "Triangle" },
  ];
  function L(ar, en) { return (typeof settings !== "undefined" && settings.lang === "en") ? en : ar; }
  function e(s) { return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]; }); }
  function safe(fn, d) { try { return fn(); } catch (x) { return d; } }
  function clamp(v, a, b) { return Math.min(b, Math.max(a, v)); }
  function mb(n) {
    if (n >= 1073741824) return (n / 1073741824).toFixed(2) + " GB";
    if (n >= 1048576) return (n / 1048576).toFixed(1) + " MB";
    return Math.max(0, Math.round(n / 1024)) + " KB";
  }
  function keyOf(url) { var m = /\/media\/([^?#]+)$/.exec(url || ""); return m ? m[1] : ""; }

  // الأماكن اللي تنعرض فيها الملصقات
  var SURF = {
    page: { root: function () { return document.querySelector("#view-profile .profile-card"); } },
    card: { root: function () { return document.querySelector("#profile-popup .popup-card"); } },
  };
  // كل ملصق له مكانه الخاص (page أو card). المكان بالبكسل من أقرب حافة يمين/يسار + من فوق،
  // فلو صغّرت النافذة ما يتغير حجمه ولا يبعد عن الحافة اللي هو جنبها.
  function where(s) { return s.where === "card" ? "card" : "page"; }
  function toPx(s, root) {
    if (s.u === "px") return;
    var r = root.getBoundingClientRect(), cx = s.x / 100 * r.width;
    s.a = cx > r.width / 2 ? "r" : "l";
    s.x = s.a === "r" ? r.width - cx : cx;
    s.y = s.y / 100 * r.height;
    s.w = s.w / 100 * r.width;
    s.u = "px";
  }

  var ed = null;   // { surf, list, uploaded: [keys], sel, usage }

  function mine(surf) {
    if (surf === "card") return safe(function () { return popupPerson && isMyProfile(popupPerson); }, false);
    return safe(function () { return isMyProfile(viewingProfile); }, false);
  }
  function allFor(surf) {
    if (mine(surf)) return safe(function () { return myProfile.stickers; }, null) || [];
    var p = surf === "card" ? safe(function () { return popupPerson; }, null) : safe(function () { return currentProfile(); }, null);
    var f = p && safe(function () { return fullPerson(p); }, p);
    return (f && f.stickers) || (p && p.stickers) || [];
  }
  function listFor(surf) {
    if (ed && ed.surf === surf) return ed.list;
    return allFor(surf).filter(function (s) { return where(s) === surf; });
  }
  function layer(surf) {
    var c = SURF[surf].root();
    if (!c) return null;
    var l = c.querySelector(":scope > .stk-layer");
    if (!l) { l = document.createElement("div"); l.className = "stk-layer"; c.appendChild(l); }
    return l;
  }
  function place(el, p) {
    if (p.u === "px") {
      el.style.left = p.a === "r" ? "calc(100% - " + p.x + "px)" : p.x + "px";
      el.style.top = p.y + "px";
      el.style.width = p.w + "px";
    } else {
      el.style.left = p.x + "%";
      el.style.top = p.y + "%";
      el.style.width = p.w + "%";
    }
    el.style.transform = "translate(-50%,-50%) rotate(" + p.rot + "deg)";
  }

  function paint(surf) {
    var l = layer(surf);
    if (!l) return;
    var editing = !!(ed && ed.surf === surf);
    var list = listFor(surf);
    l.classList.toggle("editing", editing);
    l.innerHTML = "";
    list.forEach(function (s, i) {
      var el = document.createElement("div");
      el.className = "stk shape-" + (s.shape || "none") + (editing && ed.sel === i ? " sel" : "");
      place(el, s);
      el.innerHTML = '<img src="' + e(s.url) + '" draggable="false" alt="">' +
        (editing ? '<button type="button" class="stk-del" title="' + e(L("حذف", "Remove")) + '">✕</button>' +
          ["tl", "tr", "bl", "br"].map(function (c) { return '<span class="stk-h stk-' + c + '" title="' + e(L("كبّر/صغّر ودوّر (Shift = بخطوات ثابتة)", "Resize & rotate (Shift = fixed steps)")) + '"></span>'; }).join("") : "");
      if (editing) bind(el, s, i, surf);
      l.appendChild(el);
    });
    controls(surf);
  }

  function bind(el, s, i, surf) {
    el.querySelector(".stk-del").onclick = function (ev) { ev.stopPropagation(); ed.list.splice(i, 1); ed.sel = -1; paint(surf); };
    el.addEventListener("pointerdown", function (ev) {
      if (ev.target.classList.contains("stk-h") || ev.target.classList.contains("stk-del")) return;
      ev.preventDefault(); ev.stopPropagation();
      if (ed.sel !== i) { ed.sel = i; paint(surf); return; }
      var p = s, r = SURF[surf].root().getBoundingClientRect(), sx = ev.clientX, sy = ev.clientY;
      var ocx = p.a === "r" ? r.width - p.x : p.x, oy = p.y;
      el.setPointerCapture(ev.pointerId);
      el.classList.add("active");
      var M = surf === "card" ? OUT : 0;                 // بالبطاقة يقدر يطلع برا شوي
      function mv(m) {
        var cx = clamp(ocx + (m.clientX - sx), -M, r.width + M);
        p.a = cx > r.width / 2 ? "r" : "l";            // يتعلق بأقرب حافة
        p.x = p.a === "r" ? r.width - cx : cx;
        p.y = clamp(oy + (m.clientY - sy), -M, r.height + M);
        place(el, p);
      }
      function up() { el.removeEventListener("pointermove", mv); el.removeEventListener("pointerup", up); el.classList.remove("active"); }
      el.addEventListener("pointermove", mv);
      el.addEventListener("pointerup", up);
    });
    el.querySelectorAll(".stk-h").forEach(function (h) {
      h.addEventListener("pointerdown", function (ev) {
        ev.preventDefault(); ev.stopPropagation();
        var p = s, b = el.getBoundingClientRect(), cx = b.left + b.width / 2, cy = b.top + b.height / 2;
        var rw = SURF[surf].root().getBoundingClientRect().width;
        var d0 = Math.hypot(ev.clientX - cx, ev.clientY - cy) || 1, a0 = Math.atan2(ev.clientY - cy, ev.clientX - cx);
        var w0 = p.w, r0 = p.rot;
        h.setPointerCapture(ev.pointerId);
        el.classList.add("active");
        function mv(m) {
          var d = Math.hypot(m.clientX - cx, m.clientY - cy), a = Math.atan2(m.clientY - cy, m.clientX - cx);
          var w = w0 * d / d0, rot = r0 + (a - a0) * 180 / Math.PI;
          if (m.shiftKey) { w = Math.round(w / 10) * 10; rot = Math.round(rot / 15) * 15; }
          p.w = clamp(w, 16, Math.max(40, rw * 1.5));
          p.rot = ((rot % 360) + 360) % 360;
          place(el, p);
        }
        function up() { h.removeEventListener("pointermove", mv); h.removeEventListener("pointerup", up); el.classList.remove("active"); }
        h.addEventListener("pointermove", mv);
        h.addEventListener("pointerup", up);
      });
    });
  }

  function editButton(surf) {
    var host = surf === "card" ? document.querySelector("#profile-popup .popup-actions") : document.querySelector("#view-profile .edit-row");
    if (!host) return;
    var id = "stk-btn-" + surf, btn = document.getElementById(id);
    if (!btn) {
      btn = document.createElement("button");
      btn.type = "button";
      btn.id = id;
      btn.className = "btn stk-btn" + (surf === "card" ? " popup-full-btn" : "");
      btn.onclick = function (ev) { ev.stopPropagation(); start(surf); };
      host.insertBefore(btn, host.firstChild);
    }
    btn.textContent = surf === "card" ? "🖼️" : "🖼️ " + L("ملصقات", "Stickers");
    btn.title = L("ملصقات البطاقة", "Card stickers");
    btn.classList.toggle("hidden", !mine(surf) || !!ed);
  }

  function controls(surf) {
    editButton(surf);
    var c = SURF[surf].root();
    var bar = document.getElementById("stk-bar-" + surf);
    if (!ed || ed.surf !== surf) {
      if (bar) { bar.remove(); if (surf === "card") setTimeout(function () { safe(function () { positionPopup(); }); }, 0); }
      return;
    }
    if (!bar && c) {
      // شريط التعديل تحت البطاقة (مو فوقها) عشان ما يغطي عليها
      bar = document.createElement("div");
      bar.id = "stk-bar-" + surf;
      bar.className = "stk-bar under" + (surf === "card" ? " compact" : "");
      if (surf === "card") c.appendChild(bar);                     // آخر شي بالبطاقة، تحت محتواها
      else c.parentNode.insertBefore(bar, c.nextSibling);            // تحت بطاقة البروفايل
      if (surf === "card") setTimeout(function () { safe(function () { positionPopup(); }); }, 0);
    }
    if (!bar) return;
    var u = ed.usage, full = u && u.used >= u.limit;
    var sel = ed.sel >= 0 ? ed.list[ed.sel] : null;
    bar.innerHTML =
      '<div class="stk-row">' +
        '<button type="button" class="btn stk-add"' + (ed.uploading || full || ed.list.length + (ed.others || 0) >= MAXN ? " disabled" : "") + '>' +
          (ed.uploading ? e(L("جاري الرفع…", "Uploading…")) : "+ " + e(L("أضف ملصق", "Add sticker"))) + '</button>' +
        '<span class="stk-usage">' + (u ? e(mb(u.used) + " / " + mb(u.limit)) : "") + '</span>' +
        '<button type="button" class="btn stk-cancel">' + e(L("إلغاء", "Cancel")) + '</button>' +
        '<button type="button" class="btn btn-primary stk-save">' + e(L("حفظ", "Save")) + '</button>' +
      '</div>' +
      (sel ? '<div class="stk-shapes">' + SHAPES.map(function (sh) {
        return '<button type="button" class="stk-shape' + ((sel.shape || "none") === sh.id ? " on" : "") + '" data-shape="' + sh.id + '" title="' + e(L(sh.ar, sh.en)) + '">' + sh.icon + '</button>';
      }).join("") + '</div>' : '<div class="stk-hint">' + e(L("اضغط ملصق لتختار شكله · اسحب لتحريك · اسحب أي زاوية للتكبير والتدوير · Shift = بخطوات ثابتة",
        "Tap a sticker to pick its shape · drag to move · drag a corner to resize & rotate · Shift = fixed steps")) + '</div>') +
      '<input type="file" class="stk-file" accept="image/gif,image/png,image/jpeg,image/webp" hidden>';
    bar.onclick = function (ev) { ev.stopPropagation(); };
    var file = bar.querySelector(".stk-file");
    bar.querySelector(".stk-add").onclick = function () { file.click(); };
    file.onchange = function () { var f = file.files && file.files[0]; file.value = ""; if (f) add(f); };
    bar.querySelector(".stk-cancel").onclick = cancel;
    bar.querySelector(".stk-save").onclick = save;
    bar.querySelectorAll(".stk-shape").forEach(function (b) {
      b.onclick = function () { if (sel) { sel.shape = b.dataset.shape; paint(surf); } };
    });
  }

  function api(path, opts) {
    opts = opts || {};
    opts.headers = Object.assign({ "X-Harmoni-Token": safe(getToken, "") || "" }, opts.headers || {});
    return fetch(path, opts).then(function (r) { return r.json(); });
  }
  function refreshUsage() {
    api("/api/media_usage?uid=" + encodeURIComponent(getUid())).then(function (j) {
      if (ed && j && typeof j.used === "number") {
        ed.usage = { used: j.used, limit: j.limit };
        ed.files = Array.isArray(j.stickers) ? j.stickers : [];
        controls(ed.surf);
      }
    }).catch(function () {});
  }

  function start(surf) {
    if (ed) return;
    var root = SURF[surf].root();
    ed = { surf: surf, sel: -1, uploading: 0, uploaded: [], usage: null,
      list: (safe(function () { return myProfile.stickers; }, null) || []).filter(function (s) { return where(s) === surf; })
        .map(function (s) { var c = Object.assign({}, s); delete c.card; c.where = surf; if (root) toPx(c, root); return c; }) };
    ed.others = (safe(function () { return myProfile.stickers; }, null) || []).length - ed.list.length;
    paint(surf);
    refreshUsage();
  }

  function add(f) {
    if (TYPES.indexOf(f.type) < 0) { toast(L("الصيغ المسموحة: GIF و PNG و JPG و WebP", "Allowed: GIF, PNG, JPG, WebP")); return; }
    if (f.size > MAXB) { toast(L("الملصق الواحد لازم يكون أقل من 95 ميجا", "One sticker must be under 95 MB")); return; }
    if (ed.usage && ed.usage.used + f.size > ed.usage.limit) { toast(L("ما بقى لك مساحة كافية (1 جيجا لكل مستخدم) — احذف ملصقات قديمة", "Not enough space left (1 GB per user) — remove some stickers")); return; }
    var surf = ed.surf;
    ed.uploading++;
    controls(surf);
    api("/api/media_upload?uid=" + encodeURIComponent(getUid()) + "&kind=sticker", { method: "POST", headers: { "Content-Type": f.type }, body: f })
      .then(function (j) {
        if (j && typeof j.used === "number" && ed) ed.usage = { used: j.used, limit: j.limit };
        if (!j || !j.url) {
          var er = (j && j.error) || "";
          toast(er === "quota" ? L("خلصت مساحتك (1 جيجا) — احذف ملصقات قديمة عشان تضيف", "You're out of space (1 GB) — remove some stickers first")
            : er === "media_not_configured" ? L("رفع الصور مو مفعّل بالسيرفر — شغّل deploy-hub.bat", "Uploads aren't enabled on the server yet")
            : er === "too_big" ? L("الملصق كبير", "Sticker too large")
            : L("ما قدرنا نرفع الملصق", "Couldn't upload the sticker"));
          return;
        }
        if (!ed) { delKeys([keyOf(j.url)]); return; }
        ed.uploaded.push(keyOf(j.url));
        var rr = SURF[ed.surf].root().getBoundingClientRect();
        var s = { url: j.url, where: ed.surf, shape: "none", u: "px", a: "l", x: Math.round(rr.width / 2), y: Math.round(Math.min(rr.height / 2, 160)),
          w: Math.round(Math.min(rr.width * 0.3, 220)), rot: 0 };
        ed.list.push(s);
        ed.sel = ed.list.length - 1;
      }).catch(function () { toast(L("ما قدرنا نرفع الملصق", "Couldn't upload the sticker")); })
      .then(function () { if (ed) { ed.uploading--; paint(ed.surf); refreshUsage(); } });
  }

  function delKeys(keys) {
    keys = keys.filter(Boolean);
    if (!keys.length) return;
    api("/api/media_delete", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ uid: getUid(), keys: keys }) })
      .then(function () { if (ed) refreshUsage(); }).catch(function () {});
  }

  function finish() {
    var surf = ed.surf;
    ed = null;
    paint(surf);
    paint(surf === "card" ? "page" : "card");
  }
  function cancel() {
    if (!ed || ed.uploading) return;
    delKeys(ed.uploaded);   // ملصقات انرفعت وما انحفظت → نرجع مساحتها
    finish();
  }
  function save() {
    if (!ed || ed.uploading) return;
    var all = safe(function () { return myProfile.stickers; }, null) || [];
    var before = all.map(function (s) { return keyOf(s.url); });
    var r1 = function (n) { return Math.round(n * 10) / 10; };
    var surf = ed.surf;
    var others = all.filter(function (s) { return where(s) !== surf; });   // ملصقات المكان الثاني ما تتغير
    myProfile.stickers = others.concat(ed.list.map(function (s) {
      return { url: s.url, where: surf, shape: s.shape || "none", u: "px", a: s.a === "r" ? "r" : "l", x: r1(s.x), y: r1(s.y), w: r1(s.w), rot: r1(s.rot) };
    }));
    if (myProfile.stickers.length) delete myProfile.stickersCleared; else myProfile.stickersCleared = Date.now();
    var now = myProfile.stickers.map(function (s) { return keyOf(s.url); });
    delKeys(before.concat(ed.uploaded).filter(function (k) { return now.indexOf(k) < 0; }));   // المحذوفة ترجع مساحتها
    safe(function () { store("mu_profile", myProfile); });
    safe(function () { emitJoin(); });
    safe(function () { setTimeout(loadProfiles, 600); });
    finish();
    toast(L("تم حفظ الملصقات ✓", "Stickers saved ✓"));
  }

  /* GIF يبدأ من أوله كل مرة تنفتح البطاقة/الصفحة: المتصفح يكمل الحركة لو نفس الرابط،
     فنعرض الصورة من نسخة بالذاكرة (blob) برابط جديد كل مرة — بدون ما ننزلها من جديد. */
  var blobs = {}, live = { page: [], card: [] };
  function replay(root, surf) {
    if (!root) return;
    (live[surf] || []).forEach(function (u) { try { URL.revokeObjectURL(u); } catch (x) {} });
    live[surf] = [];
    root.querySelectorAll("img").forEach(function (img) {
      var src = img.dataset.orig || img.getAttribute("src") || "";
      var ok = /^https:\/\/hub\.harmoni\.click\/media\//.test(src) || /^data:image\/gif/.test(src);
      if (!ok) return;
      img.dataset.orig = src;
      var p = blobs[src] || (blobs[src] = fetch(src).then(function (r) { if (!r.ok) throw new Error(r.status); return r.blob(); })
        .catch(function () { delete blobs[src]; return null; }));
      p.then(function (b) {
        if (!b || !img.isConnected || img.dataset.orig !== src) return;
        if (b.type && b.type !== "image/gif") return;          // الصور الثابتة ما تحتاج
        var u = URL.createObjectURL(b);
        live[surf].push(u);
        img.src = u;
      });
    });
  }

  /* أمان: لو فيه ملفات ملصقات بمساحتك وما هي بالبروفايل (يعني انشالت بالغلط، مو أنت اللي حذفتها —
     الحذف يمسح الملف نفسه) نرجعها للبروفايل تلقائياً. */
  function heal() {
    var uid = safe(getUid, "");
    if (!uid || !safe(function () { return localStorage.getItem("mu_authed"); }, null)) return;
    api("/api/media_usage?uid=" + encodeURIComponent(uid)).then(function (j) {
      var files = (j && Array.isArray(j.stickers)) ? j.stickers : [];
      var mineList = safe(function () { return myProfile.stickers; }, null) || [];
      var used = mineList.map(function (s) { return keyOf(s.url); });
      var lost = files.filter(function (k) { return used.indexOf(k) < 0; });
      if (!lost.length || ed) return;
      myProfile.stickers = mineList.concat(lost.map(function (k, i) {
        return { url: "https://hub.harmoni.click/media/" + k, where: "page", shape: "none", u: "px", a: "l",
          x: 80 + (i % 6) * 80, y: 70 + Math.floor(i / 6) * 80, w: 100, rot: 0 };
      }));
      delete myProfile.stickersCleared;
      safe(function () { store("mu_profile", myProfile); });
      safe(function () { emitJoin(); });
      safe(function () { paint("page"); });
    }).catch(function () {});
  }
  setTimeout(heal, 4000);

  var origProfile = window.renderProfile;
  if (typeof origProfile === "function") {
    window.renderProfile = function () {
      var r = origProfile.apply(this, arguments);
      if (ed && ed.surf === "page" && !mine("page")) { delKeys(ed.uploaded); ed = null; }
      safe(function () { paint("page"); });
      safe(function () { replay(SURF.page.root(), "page"); });
      return r;
    };
  }
  var origPopup = window.showProfilePopup;
  if (typeof origPopup === "function") {
    window.showProfilePopup = function () {
      if (ed && ed.surf === "card") { delKeys(ed.uploaded); ed = null; }
      var r = origPopup.apply(this, arguments);
      safe(function () { paint("card"); });
      safe(function () { replay(SURF.card.root(), "card"); });
      return r;
    };
  }
  var origPos = window.positionPopup;
  if (typeof origPos === "function") {
    window.positionPopup = function () {
      var r = origPos.apply(this, arguments);
      var bar = document.getElementById("stk-bar-card"), card = SURF.card.root();
      if (bar && card && !document.documentElement.classList.contains("hm-m")) {
        var over = bar.getBoundingClientRect().bottom - (window.innerHeight - 8);
        if (over > 0) card.style.top = Math.max(8, (parseFloat(card.style.top) || 0) - over) + "px";
      }
      return r;
    };
  }
  var origClose = window.closeProfilePopup;
  if (typeof origClose === "function") {
    window.closeProfilePopup = function () {
      if (ed && ed.surf === "card") { if (ed.uploading) return; delKeys(ed.uploaded); ed = null; }
      return origClose.apply(this, arguments);
    };
  }
})();
