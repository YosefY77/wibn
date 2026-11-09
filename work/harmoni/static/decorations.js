/* الديكوريشن حول الصورة (مثل ديسكورد): تختاره من الإعدادات ← المظهر، ويشوفه الكل حول صورتك
   (البطاقة، صفحة البروفايل، كرتك). المكتبة مشتركة على السيرفر — المؤسس يضيف لها أي صورة/GIF. */
(function () {
  "use strict";
  function L(ar, en) { return (typeof settings !== "undefined" && settings.lang === "en") ? en : ar; }
  function e(s) { return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]; }); }
  function safe(fn, d) { try { return fn(); } catch (x) { return d; } }
  var URL_RE = /^(https:\/\/hub\.harmoni\.click\/media\/deco\/[0-9a-f]{16}\.(gif|png|jpg|webp|avif)|\/static\/decorations\/[A-Za-z0-9._-]{1,80})$/;
  var list = null, founder = false, busy = false;

  // يحط الديكوريشن فوق الصورة (حجمه 120% من الصورة ومتمركز، والصورة بنص الفتحة)
  function applyDeco(host, url) {
    if (!host) return;
    var img = host.querySelector(":scope > .av-deco");
    if (!url || !URL_RE.test(url)) { if (img) img.remove(); host.classList.remove("has-deco"); return; }
    if (!img) { img = document.createElement("img"); img.className = "av-deco"; img.alt = ""; img.draggable = false; host.appendChild(img); }
    if (img.getAttribute("src") !== url && img.dataset.orig !== url) { img.src = url; delete img.dataset.orig; }
    host.classList.add("has-deco");
  }
  window.applyDeco = applyDeco;

  function me() { return safe(function () { return myProfile; }, {}) || {}; }
  function refreshMine() {
    safe(function () { applyDeco(document.querySelector("#me-card .mc-avatar"), me().decoration); });
    safe(function () { if (document.getElementById("view-profile").classList.contains("active")) renderProfile(); });
  }

  function load(force) {
    return fetch("/api/decorations" + (force ? "?force=1" : "")).then(function (r) { return r.json(); })
      .then(function (j) { list = j.decorations || []; paint(); }).catch(function () { list = list || []; paint(); });
  }
  function checkFounder() {
    fetch("/api/badges").then(function (r) { return r.json(); }).then(function (j) {
      var uid = safe(getUid, "");
      founder = !!(uid && j.assign && (j.assign[uid] || []).indexOf("founder") >= 0);
      paint();
    }).catch(function () {});
  }

  function add(f) {
    if (!/^image\//.test(f.type)) { toast(L("لازم يكون صورة (GIF أو PNG أو JPG أو WebP…)", "Pick an image (GIF, PNG, JPG, WebP…)")); return; }
    if (f.size > 20 * 1024 * 1024) { toast(L("الديكوريشن لازم يكون أقل من 20 ميجا", "Decoration must be under 20 MB")); return; }
    busy = true; paint();
    var name = f.name.replace(/\.[^.]+$/, "").slice(0, 60);
    fetch("/api/decorations/add?uid=" + encodeURIComponent(getUid()) + "&name=" + encodeURIComponent(name), {
      method: "POST", headers: { "Content-Type": f.type, "X-Harmoni-Token": safe(getToken, "") || "" }, body: f,
    }).then(function (r) { return r.json(); }).then(function (j) {
      if (j && j.decorations) { list = null; load(); toast(L("انضاف ✓ — يطلع للكل", "Added ✓ — everyone can pick it")); return; }
      var er = (j && j.error) || "";
      toast(er === "founder_only" ? L("إضافة الديكوريشن للمؤسس بس", "Only the founder can add decorations")
        : er === "bad_type" ? L("الصيغة مو مدعومة", "Unsupported format")
        : L("ما قدرنا نضيفه", "Couldn't add it"));
    }).catch(function () { toast(L("ما قدرنا نضيفه", "Couldn't add it")); })
      .then(function () { busy = false; paint(); });
  }
  function remove(d) {
    fetch("/api/decorations/delete", { method: "POST", headers: { "Content-Type": "application/json", "X-Harmoni-Token": safe(getToken, "") || "" },
      body: JSON.stringify({ uid: getUid(), id: d.id }) }).then(function (r) { return r.json(); })
      .then(function (j) { if (j && j.decorations) load(true); }).catch(function () {});
  }

  /* منتقي الديكوريشن (داخل «تعديل البروفايل») */
  var picker = null;   // { host, cur, avatar, name, onPick }
  function avHtml() {
    return picker.avatar ? '<img src="' + e(picker.avatar) + '" alt="">' : "<span>" + e((picker.name || "؟")[0]) + "</span>";
  }
  function paint() {
    if (!picker || !picker.host || !picker.host.isConnected) return;
    var g = picker.host, cur = picker.cur || "", items = list || [];
    g.innerHTML =
      '<div class="deco-grid">' +
        '<button type="button" class="deco-opt' + (!cur ? " on" : "") + '" data-url="" title="' + e(L("بدون", "None")) + '"><div class="deco-av">' + avHtml() + '</div><small>' + e(L("بدون", "None")) + '</small></button>' +
        items.map(function (d) {
          return '<button type="button" class="deco-opt' + (cur === d.url ? " on" : "") + '" data-url="' + e(d.url) + '" title="' + e(d.name) + '">' +
            '<div class="deco-av">' + avHtml() + '</div><small>' + e(String(d.name || "").replace(/[-_ ]?discord[-_ ]?/ig, " ").replace(/[-_]+/g, " ").trim()) + '</small>' +
            (founder && !d.bundled ? '<span class="deco-del" data-id="' + e(d.id) + '" title="' + e(L("حذف من المكتبة", "Remove from library")) + '">✕</span>' : "") + '</button>';
        }).join("") +
        (founder ? '<button type="button" class="deco-opt deco-add"' + (busy ? " disabled" : "") + '><div class="deco-plus">' + (busy ? "…" : "+") + '</div><small>' +
          e(busy ? L("جاري الرفع", "Uploading") : L("أضف للمكتبة", "Add to library")) + '</small></button>' : "") +
      '</div>' +
      (founder ? '<input type="file" class="deco-file" accept="image/*" hidden>' : "") +
      (list === null ? '<p class="sp-link-desc">' + e(L("جاري التحميل…", "Loading…")) + "</p>" : "");
    g.querySelectorAll(".deco-opt[data-url]").forEach(function (b) {
      applyDeco(b.querySelector(".deco-av"), b.dataset.url);
      b.onclick = function (ev) {
        if (ev.target.classList.contains("deco-del")) return;
        picker.cur = b.dataset.url;
        picker.onPick(b.dataset.url);
      };
    });
    g.querySelectorAll(".deco-del").forEach(function (x) {
      x.onclick = function (ev) {
        ev.stopPropagation();
        var d = (list || []).filter(function (y) { return y.id === x.dataset.id; })[0];
        if (d) remove(d);
      };
    });
    var file = g.querySelector(".deco-file"), addBtn = g.querySelector(".deco-add");
    if (addBtn && file) {
      addBtn.onclick = function () { file.click(); };
      file.onchange = function () { var f = file.files && file.files[0]; file.value = ""; if (f) add(f); };
    }
  }
  window.harmoniDecoPicker = function (host, cur, avatar, name, onPick) {
    var fresh = !picker || picker.host !== host;
    picker = { host: host, cur: cur || "", avatar: avatar || "", name: name || "", onPick: onPick };
    if (fresh) { load(); checkFounder(); }
    paint();
  };

  load();
  checkFounder();
  setTimeout(refreshMine, 0);
})();
