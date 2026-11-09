/* الشارات: تظهر جنب اسم الشخص في كل مكان (البروفايل، كرتك، الغرفة، المتابَعين، البحث).
   إدارة الشارات (إعطاء/سحب/إنشاء) تظهر للمؤسس بس، من جهازه وبحسابه — والسيرفر يتأكد من هذا بنفسه. */
(function () {
  "use strict";
  function L(ar, en) { return (typeof settings !== "undefined" && settings.lang === "en") ? en : ar; }
  function esc(s) { return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]; }); }
  function safe(fn) { try { return fn(); } catch (e) { return null; } }
  var B = { defs: {}, order: [], assign: {} }, admin = false, raf = 0, busy = false, reqCount = 0;

  function myUid() { return safe(function () { return getUid(); }) || ""; }
  function token() { return safe(function () { return getToken(); }) || ""; }
  function nameOf(id) { var d = B.defs[id] || {}; return L(d.ar || id, d.en || d.ar || id); }

  function html(uid) {
    var list = (uid && B.assign[uid]) || [];
    return list.filter(function (b) { return B.defs[b]; }).map(function (b) {
      var d = B.defs[b];
      return '<span class="hb-badge hb-' + esc(b) + '" title="' + esc(nameOf(b)) + '" style="--hb:' + esc(d.color || "#9aa4ff") + '">' + esc(d.emoji) + "</span>";
    }).join("");
  }
  window.harmoniBadgesHtml = html;

  /* الشارات تطلع في سطر لحالها تحت الاسم/اليوزر.
     after = بعد العنصر مباشرة (سطر جديد)، inside = آخر شي داخل العنصر */
  function put(el, uid, mode) {
    if (!el) return;
    var want = html(uid);
    var span = mode === "after"
      ? (el.nextElementSibling && el.nextElementSibling.classList.contains("hb-badges") ? el.nextElementSibling : null)
      : el.querySelector(":scope > .hb-badges");
    if (!want) { if (span) span.remove(); return; }
    if (!span) {
      span = document.createElement("div");
      span.className = "hb-badges";
      if (mode === "after") el.insertAdjacentElement("afterend", span); else el.appendChild(span);
    }
    if (span.dataset.k !== uid + want) { span.dataset.k = uid + want; span.innerHTML = want; }
  }

  function decorate() {
    if (busy) return;
    busy = true;
    try {
      var cp = safe(function () { return currentProfile(); });
      var cpUid = cp && (cp.uid || (safe(function () { return isMyProfile(cp); }) ? myUid() : ""));
      put(document.querySelector(".profile-name-row"), cpUid);
      var meHead = document.querySelector(".me-card .mc2-head");
      var old = meHead && meHead.nextElementSibling;
      if (old && old.classList.contains("hb-badges")) old.remove();   // مكانها القديم (تحت) — صارت جنب اليوزر
      var meUser = document.querySelector(".me-card .mc2-user");
      if (meUser) put(meUser, myUid(), "after");
      // بطاقة البروفايل المنبثقة ترسم شاراتها بنفسها (fillProfileCard)
      document.querySelectorAll(".member[data-uid] .m-name").forEach(function (el) { put(el, el.closest(".member").dataset.uid); });
      document.querySelectorAll(".f-item[data-uid] .f-name").forEach(function (el) { put(el, el.closest(".f-item").dataset.uid); });
      document.querySelectorAll(".friend-row[data-uid] .friend-main").forEach(function (el) { put(el, el.closest(".friend-row").dataset.uid); });
      document.querySelectorAll(".hm2-friend[data-uid] .hm2-fmeta").forEach(function (el) { put(el, el.closest(".hm2-friend").dataset.uid); });
      adminButton(cp);
    } finally {
      busy = false;
    }
  }
  function soon() { cancelAnimationFrame(raf); raf = requestAnimationFrame(decorate); }

  function load() {
    return fetch("/api/badges").then(function (r) { return r.json(); }).then(function (j) {
      B = { defs: j.defs || {}, order: j.order || [], assign: j.assign || {} };
      soon();
    }).catch(function () {});
  }
  function checkAdmin() {
    if (!/^(localhost|127\.0\.0\.1)$/.test(location.hostname)) return;   // الإدارة من جهاز المؤسس بس
    var u = myUid(), t = token();
    if (!u || !t) return;
    fetch("/api/badges/admin?uid=" + encodeURIComponent(u) + "&token=" + encodeURIComponent(t))
      .then(function (r) { return r.json(); }).then(function (j) {
        admin = !!j.admin; soon();
        if (admin) api({ action: "reset_requests" }).then(function (k) { reqCount = (k.requests || []).length; soon(); }).catch(function () {});
      }).catch(function () {});
  }

  /* ===== إدارة الشارات (المؤسس بس) ===== */
  function adminButton(cp) {
    var row = document.querySelector(".profile-name-row");
    var btn = document.getElementById("hb-manage");
    if (row && btn && btn.parentNode !== row.parentNode) btn.remove(), btn = null;
    if (!admin || !row || !cp) { if (btn) btn.remove(); return; }
    if (!btn) {
      btn = document.createElement("button");
      btn.type = "button";
      btn.id = "hb-manage";
      btn.className = "btn hb-manage";
      row.insertAdjacentElement("afterend", btn);
    }
    var label = "🏅 " + L("الشارات", "Badges") + (reqCount ? " · 🔑 " + reqCount : "");
    if (btn.textContent !== label) btn.textContent = label;
    btn.onclick = function () { manage(cp); };
  }

  function api(body) {
    body.uid = myUid(); body.token = token();
    return fetch("/api/badges/admin", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) })
      .then(function (r) { return r.json().then(function (j) { if (!r.ok) throw new Error(j.error || "failed"); return j; }); });
  }
  function errText(code) {
    return ({ keep_founder: L("ما تقدر تشيل التاج عن نفسك", "You can't remove the crown from yourself"),
              bad_def: L("اكتب إيموجي واسم للشارة", "Enter an emoji and a name"),
              forbidden: L("هالخاصية للمؤسس بس", "Founder only"),
              no_user: L("هذا الحساب مو موجود على السيرفر", "That account isn't on the server"),
              short_password: L("كلمة السر لازم 4 أحرف على الأقل", "Password must be at least 4 characters"),
              not_found: L("سيرفر هارموني يحتاج تحديث — شغّل deploy-hub.bat", "The Harmoni server needs an update — run deploy-hub.bat"),
              hub_down: L("سيرفر هارموني ما يرد الحين، جرب بعد شوي", "Harmoni server isn't responding, try later") })[code] || L("ما قدرنا نحفظ", "Couldn't save");
  }

  function manage(person) {
    var uid = person.uid || (safe(function () { return isMyProfile(person); }) ? myUid() : "");
    if (!uid) return;
    function render() {
      var have = B.assign[uid] || [];
      var rows = B.order.filter(function (b) { return B.defs[b]; }).map(function (b) {
        var d = B.defs[b], on = have.indexOf(b) >= 0, auto = !!d.auto;
        return '<label class="hb-row' + (auto ? " auto" : "") + '"><span class="hb-badge" style="--hb:' + esc(d.color || "#9aa4ff") + '">' + esc(d.emoji) + "</span>" +
          '<span class="hb-row-name">' + esc(nameOf(b)) + (auto ? ' <small>' + esc(L("تلقائية — تنعطى لأكثر واحد استماعاً", "Automatic — goes to the top listener")) + "</small>" : "") + "</span>" +
          (d.custom ? '<button type="button" class="hb-del" data-b="' + esc(b) + '" title="' + esc(L("حذف الشارة نهائياً", "Delete badge")) + '">🗑</button>' : "") +
          '<input type="checkbox" data-b="' + esc(b) + '"' + (on ? " checked" : "") + (auto ? " disabled" : "") + "></label>";
      }).join("");
      var body = '<div class="hb-manage-box"><p class="hb-who">' + esc(L("شارات ", "Badges for ")) + "<b>" + esc(person.name || "") + "</b>" +
        (person.username ? ' <span dir="ltr">@' + esc(person.username) + "</span>" : "") + "</p>" +
        '<div class="hb-list">' + rows + "</div>" +
        '<details class="sp-adv hb-new"><summary>' + esc(L("＋ شارة جديدة", "＋ New badge")) + "</summary>" +
          '<div class="hb-new-row"><input id="hb-emoji" class="search-input" maxlength="8" placeholder="🎤"><input id="hb-ar" class="search-input" maxlength="30" placeholder="' + esc(L("اسم الشارة", "Badge name")) + '"></div>' +
          '<button type="button" class="btn btn-primary" id="hb-create">' + esc(L("إنشاء", "Create")) + "</button></details>" +
        '<div class="hb-reset"><h4>🔑 ' + esc(L("كلمة سر جديدة لهالحساب", "New password for this account")) + "</h4>" +
          '<div class="hb-reset-row"><input id="hb-pw" class="search-input" dir="ltr" maxlength="64" autocomplete="off" placeholder="' + esc(L("كلمة السر الجديدة", "New password")) + '">' +
          '<button type="button" class="btn" id="hb-pw-go">' + esc(L("تعيين", "Set")) + "</button></div>" +
          '<div id="hb-reqs"></div></div>' +
        '<p class="hb-msg" id="hb-msg"></p><p class="hb-note">🔒 ' + esc(L("هالقائمة تظهر لك بس — محد غيرك يقدر يعطي أو يشيل شارات.", "Only you see this — nobody else can give or remove badges.")) + "</p></div>";
      showModal("🏅 " + L("الشارات", "Badges"), body);
      var box = document.querySelector(".hb-manage-box");
      box.querySelectorAll('input[type=checkbox][data-b]').forEach(function (cb) {
        cb.onchange = function () {
          cb.disabled = true;
          api({ action: cb.checked ? "grant" : "revoke", target: uid, badge: cb.dataset.b })
            .then(function (j) { B = { defs: j.defs, order: j.order, assign: j.assign }; soon(); refreshRegistry(); render(); })
            .catch(function (e) { cb.checked = !cb.checked; cb.disabled = false; document.getElementById("hb-msg").textContent = errText(e.message); });
        };
      });
      box.querySelectorAll(".hb-del").forEach(function (b) {
        b.onclick = function (e) {
          e.preventDefault();
          api({ action: "delete", badge: b.dataset.b }).then(function (j) { B = { defs: j.defs, order: j.order, assign: j.assign }; soon(); render(); })
            .catch(function (er) { document.getElementById("hb-msg").textContent = errText(er.message); });
        };
      });
      function setPw(target, input, label) {
        var pw = input.value;
        var m = document.getElementById("hb-msg");
        if (pw.length < 4) { m.textContent = errText("short_password"); return; }
        api({ action: "reset_password", target: target, password: pw }).then(function () {
          input.value = "";
          m.textContent = L("✓ تغيرت كلمة سر ", "✓ Password changed for ") + label + L(" — قوله عليها", " — let them know");
          loadReqs();
        }).catch(function (er) { m.textContent = errText(er.message); });
      }
      document.getElementById("hb-pw-go").onclick = function () {
        setPw(uid, document.getElementById("hb-pw"), "@" + (person.username || person.name || ""));
      };
      function loadReqs() {
        api({ action: "reset_requests" }).then(function (j) {
          var box = document.getElementById("hb-reqs");
          if (!box) return;
          var list = j.requests || [];
          reqCount = list.length; soon();
          if (!list.length) { box.innerHTML = ""; return; }
          box.innerHTML = '<h4 style="margin-top:12px">' + esc(L("طلبات «نسيت كلمة السر»", "“Forgot password” requests")) + " (" + list.length + ")</h4>" +
            list.map(function (r, i) {
              return '<div class="hb-reset-row"><b dir="ltr">@' + esc(r.username) + '</b><input class="search-input" dir="ltr" data-i="' + i + '" maxlength="64" autocomplete="off" placeholder="' +
                esc(L("كلمة سر جديدة", "New password")) + '"><button type="button" class="btn" data-i="' + i + '">' + esc(L("تعيين", "Set")) + "</button></div>";
            }).join("");
          box.querySelectorAll("button[data-i]").forEach(function (b) {
            var r = list[+b.dataset.i];
            b.onclick = function () { setPw(r.uid, box.querySelector('input[data-i="' + b.dataset.i + '"]'), "@" + r.username); };
          });
        }).catch(function () {});
      }
      loadReqs();
      document.getElementById("hb-create").onclick = function () {
        var em = document.getElementById("hb-emoji").value.trim(), ar = document.getElementById("hb-ar").value.trim();
        api({ action: "create", emoji: em, ar: ar }).then(function (j) {
          B = { defs: j.defs, order: j.order, assign: j.assign };
          return api({ action: "grant", target: uid, badge: j.badge });
        }).then(function (j) { B = { defs: j.defs, order: j.order, assign: j.assign }; soon(); render(); })
          .catch(function (er) { document.getElementById("hb-msg").textContent = errText(er.message); });
      };
    }
    render();
  }
  function refreshRegistry() { safe(function () { loadProfiles(); }); }

  function start() {
    load(); checkAdmin();
    setInterval(load, 60000);
    new MutationObserver(function () { if (!busy) soon(); }).observe(document.body, { childList: true, subtree: true });
    new MutationObserver(soon).observe(document.documentElement, { attributes: true, attributeFilter: ["lang"] });
    try { document.documentElement.setAttribute("data-badges-ready", "1"); } catch (e) {}
  }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start); else start();
})();
