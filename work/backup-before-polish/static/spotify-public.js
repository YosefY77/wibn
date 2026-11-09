/* متابعة بلاي لستات سبوتيفاي العامة (بدون تسجيل دخول):
   - المستخدم يلصق روابط بلاي لستاته العامة، وتتحدث عند الفتح وكل 5 دقايق
   - وحدة منها يختارها (⭐) تنضاف أغانيها للأغاني المفضلة، والباقي بلاي لستات عادية
   ملاحظة: سبوتيفاي (2026) ما يسمح بجلب بلاي لستات مستخدم من يوزره بدون ربط، فالروابط هي الطريقة الوحيدة. */
(function () {
  "use strict";
  var KEY = "mu_sp_public";
  var running = false, lastSync = 0;
  function L2(ar, en) { return (typeof settings !== "undefined" && settings.lang === "en") ? en : ar; }
  function esc2(s) { return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]; }); }
  function getState() {
    var s = null;
    try { s = JSON.parse(localStorage.getItem(KEY) || "null"); } catch (e) {}
    if (!s || !Array.isArray(s.lists)) s = { lists: [], fav: "", at: 0 };
    if (!Array.isArray(s.hidden)) s.hidden = [];
    return s;
  }
  function setState(s) { try { localStorage.setItem(KEY, JSON.stringify(s)); } catch (e) {} }
  function post(url, body) {
    return fetch(url, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) })
      .then(function (r) { return r.json().then(function (j) { if (!r.ok && !j.error) j.error = "HTTP " + r.status; return j; }); });
  }
  function note(msg) { if (typeof toast === "function") toast(msg); }

  function sync(manual) {
    var st = getState();
    if ((!st.lists.length && !st.user) || running) return Promise.resolve(null);
    running = true;
    return post("/api/spotify_public/sync", { playlists: st.lists.map(function (p) { return p.id; }), fav: st.fav || "",
                                              user: st.user || "", hidden: st.hidden }).then(function (D) {
      if (D.error) throw new Error(D.error);
      var added = (typeof mergeSpotifyImport === "function") ? mergeSpotifyImport(D, { publicMode: true }) : {};
      lastSync = Date.now();
      var cur = getState();
      cur.at = lastSync;
      (D.info || []).forEach(function (i) {
        var have = cur.lists.filter(function (p) { return p.id === i.id; })[0];
        if (have) { have.name = i.name; have.count = i.count; }
        else if ((D.discovered || []).indexOf(i.id) >= 0 && cur.hidden.indexOf(i.id) < 0) cur.lists.push({ id: i.id, name: i.name, count: i.count, auto: true });
      });
      setState(cur);
      try { renderLikes(); renderLikesView(); } catch (e) {}
      var n = (added.likes || 0) + (added.tracks || 0);
      if (n || added.playlists) {
        note(L2("سبوتيفاي: وصلت ", "Spotify: ") + (n ? n + L2(" أغنية جديدة", " new songs") : "") +
          (n && added.playlists ? L2(" و", " and ") : "") + (added.playlists ? added.playlists + L2(" بلاي لست جديدة", " new playlists") : "") + L2("", " added"));
      } else if (manual) {
        note(L2("سبوتيفاي: كل شي محدّث ✓", "Spotify: everything is up to date ✓"));
      }
      if (manual && D.failed && D.failed.length) note(L2("تعذر قراءة ", "Couldn't read ") + D.failed.length + L2(" بلاي لست — تأكد إنها عامة", " playlist(s) — make sure they're public"));
      paint();
      return D;
    }).catch(function (e) {
      if (manual) note((e && e.message) || L2("تعذر تحديث سبوتيفاي", "Spotify refresh failed"));
      return null;
    }).then(function (r) { running = false; return r; });
  }

  function addLink(url, box) {
    var msg = box.querySelector(".spp-msg");
    var btn = box.querySelector(".spp-add");
    msg.textContent = L2("جاري قراءة البلاي لست...", "Reading the playlist...");
    btn.disabled = true;
    return post("/api/spotify_public/resolve", { url: url }).then(function (j) {
      btn.disabled = false;
      if (j.error) { msg.textContent = j.error; return; }
      var st = getState();
      if (st.lists.some(function (p) { return p.id === j.id; })) { msg.textContent = L2("هذي البلاي لست مضافة من قبل", "This playlist is already added"); return; }
      st.lists.push({ id: j.id, name: j.name, count: j.count });
      if (st.lists.length === 1 && !st.fav) st.fav = "";
      setState(st);
      paint();
      sync(true);
    }).catch(function () { btn.disabled = false; msg.textContent = L2("تعذر الوصول لسبوتيفاي", "Couldn't reach Spotify"); });
  }

  function linkProfile(text, box) {
    var msg = box.querySelector(".spp-pmsg");
    var btn = box.querySelector(".spp-plink");
    msg.textContent = L2("جاري قراءة البروفايل (ثواني)...", "Reading the profile (a few seconds)...");
    btn.disabled = true;
    return post("/api/spotify_public/user", { user: text }).then(function (j) {
      btn.disabled = false;
      if (j.error) { msg.textContent = j.error; return; }
      var st = getState();
      st.user = j.user; st.name = j.name; st.hidden = [];
      (j.playlists || []).forEach(function (p) {
        if (!st.lists.some(function (x) { return x.id === p.id; })) st.lists.push({ id: p.id, name: p.name, count: p.count, auto: true });
      });
      setState(st);
      paint();
      note(L2("لقينا ", "Found ") + (j.playlists || []).length + L2(" بلاي لست عامة — جاري السحب...", " public playlists — pulling them..."));
      sync(true);
    }).catch(function () { btn.disabled = false; msg.textContent = L2("تعذر الوصول لسبوتيفاي", "Couldn't reach Spotify"); });
  }

  function render(box) {
    var st = getState();
    var prof = st.user
      ? '<div class="spp-prof"><span class="spp-badge">✓</span><div class="spp-meta"><b>' + esc2(st.name || st.user) + "</b><span>" +
        esc2(L2("مربوط بالحساب — أي بلاي لست عامة جديدة تنضاف تلقائياً", "Linked — new public playlists are added automatically")) + "</span></div>" +
        '<button type="button" class="btn spp-punlink">' + esc2(L2("إلغاء ربط الحساب", "Unlink account")) + "</button></div>"
      : '<div class="spp-row"><input class="search-input spp-puser" dir="ltr" autocomplete="off" placeholder="' +
        esc2(L2("رابط حسابك في سبوتيفاي (open.spotify.com/user/...)", "Your Spotify profile link (open.spotify.com/user/...)")) + '">' +
        '<button type="button" class="btn btn-primary spp-plink">' + esc2(L2("ربط الحساب", "Link account")) + "</button></div>" +
        '<p class="spp-msg spp-pmsg"></p>';
    var rows = st.lists.map(function (p) {
      var isFav = p.id === st.fav;
      return '<div class="spp-item' + (isFav ? " fav" : "") + '" data-id="' + esc2(p.id) + '">' +
        '<span class="spp-ico">' + (isFav ? "♥" : "♫") + "</span>" +
        '<div class="spp-meta"><b>' + esc2(p.name || p.id) + "</b><span>" +
        (isFav ? esc2(L2("أغانيها تنضاف للمفضلة", "Songs go to Favorites")) : esc2(L2("بلاي لست", "Playlist"))) +
        (p.count != null ? " · " + p.count + L2(" أغنية", " songs") : "") + "</span></div>" +
        '<button type="button" class="btn spp-star" title="' + esc2(L2("أغانيها تروح للمفضلة", "Send its songs to Favorites")) + '">' +
        esc2(isFav ? L2("✓ للمفضلة", "✓ Favorites") : L2("للمفضلة", "To Favorites")) + "</button>" +
        '<button type="button" class="spp-x" title="' + esc2(L2("إزالة", "Remove")) + '">✕</button></div>';
    }).join("");
    var when = st.at ? new Date(st.at).toLocaleTimeString(settings.lang === "en" ? "en" : "ar", { hour: "2-digit", minute: "2-digit" }) : "";
    box.innerHTML =
      '<div class="spp-title">' + esc2(L2("سبوتيفاي بدون تسجيل دخول (البلاي لستات العامة)", "Spotify without sign-in (public playlists)")) + "</div>" +
      '<p class="spp-desc">' + esc2(L2("الصق رابط حسابك في سبوتيفاي، والبرنامج يسحب كل بلاي لستاتك العامة، وأي أغنية تضيفها لها توصل هنا تلقائياً عند الفتح وكل 5 دقايق. اختار وحدة «للمفضلة» عشان أغانيها تنضاف للأغاني المفضلة. ما يحتاج تكون مضاف في تطبيق سبوتيفاي ولا بريميوم.",
        "Paste your Spotify profile link and Harmoni pulls all your public playlists — songs you add arrive automatically on launch and every 5 minutes. Mark one “To Favorites” to send its songs to your Favorites. No allow-list or Premium needed.")) + "</p>" +
      prof +
      (rows ? '<div class="spp-list">' + rows + "</div>" : "") +
      '<div class="spp-sub">' + esc2(L2("أو أضف بلاي لست برابطها:", "Or add a playlist by its link:")) + "</div>" +
      '<div class="spp-row"><input class="search-input spp-url" dir="ltr" autocomplete="off" placeholder="' +
      esc2(L2("الصق رابط بلاي لست عامة من سبوتيفاي", "Paste a public Spotify playlist link")) + '">' +
      '<button type="button" class="btn btn-primary spp-add">' + esc2(L2("إضافة", "Add")) + "</button>" +
      (st.lists.length ? '<button type="button" class="btn spp-sync">' + esc2(L2("مزامنة الحين", "Sync now")) + "</button>" : "") + "</div>" +
      '<p class="spp-msg">' + (when ? esc2(L2("آخر مزامنة: ", "Last sync: ") + when) : "") + "</p>" +
      '<p class="spp-hint">' + esc2(L2("رابط الحساب من سبوتيفاي: البروفايل ← ⋯ ← مشاركة ← نسخ رابط البروفايل. البلاي لستات لازم تكون عامة (Public) وظاهرة في البروفايل. الأغاني المعجب فيها (Liked Songs) خاصة وما تنقرا — حطها في بلاي لست عامة واختارها «للمفضلة».",
        "Profile link in Spotify: Profile → ⋯ → Share → Copy link to profile. Playlists must be public and shown on your profile. Liked Songs are private and can't be read — put them in a public playlist and mark it “To Favorites”.")) + "</p>";
    var pu = box.querySelector(".spp-puser");
    if (pu) {
      var pgo = function () { if (pu.value.trim()) linkProfile(pu.value.trim(), box); };
      box.querySelector(".spp-plink").onclick = pgo;
      pu.onkeydown = function (e) { if (e.key === "Enter") pgo(); };
    }
    var pun = box.querySelector(".spp-punlink");
    if (pun) pun.onclick = function () {
      var cur = getState();
      cur.user = ""; cur.name = ""; cur.hidden = [];
      setState(cur); paint();
      note(L2("تم إلغاء ربط الحساب — البلاي لستات المضافة باقية", "Account unlinked — added playlists stay"));
    };
    var inp = box.querySelector(".spp-url");
    var go = function () { if (inp.value.trim()) addLink(inp.value.trim(), box); };
    box.querySelector(".spp-add").onclick = go;
    inp.onkeydown = function (e) { if (e.key === "Enter") go(); };
    var sb = box.querySelector(".spp-sync");
    if (sb) sb.onclick = function () { sb.disabled = true; sync(true).then(function () { sb.disabled = false; }); };
    box.querySelectorAll(".spp-item").forEach(function (row) {
      var id = row.dataset.id;
      row.querySelector(".spp-star").onclick = function () {
        var cur = getState();
        cur.fav = cur.fav === id ? "" : id;
        if (cur.fav === id) {
          // صارت «للمفضلة»: نشيل نسختها من البلاي لستات عشان ما تتكرر
          try {
            var pls = getPlaylists();
            var left = pls.filter(function (x) { return x.spotify_id !== id; });
            if (left.length !== pls.length) { savePlaylists(left); window.dispatchEvent(new Event("harmoni-library")); }
          } catch (e) {}
        }
        setState(cur); paint(); sync(true);
      };
      row.querySelector(".spp-x").onclick = function () {
        var cur = getState();
        cur.lists = cur.lists.filter(function (p) { return p.id !== id; });
        if (cur.user && cur.hidden.indexOf(id) < 0) cur.hidden.push(id);   // ما نرجّعها من البروفايل
        if (cur.fav === id) cur.fav = "";
        setState(cur); paint();
      };
    });
  }

  function paint() {
    var body = document.getElementById("settings-body");
    if (!body) return;
    var g = body.querySelector('.settings-group[data-key="spotify"]');
    if (!g) return;
    var box = g.querySelector(".sp-pub");
    if (!box) {
      box = document.createElement("div");
      box.className = "sp-pub";
      var after = g.querySelector(".sp-link-desc");
      if (after && after.nextSibling) g.insertBefore(box, after.nextSibling); else g.appendChild(box);
    }
    render(box);
  }

  var orig = window.renderSettings;
  if (typeof orig === "function") {
    window.renderSettings = function () { var r = orig.apply(this, arguments); try { paint(); } catch (e) {} return r; };
  }
  setTimeout(function () { sync(false); }, 5000);
  setInterval(function () { sync(false); }, 5 * 60 * 1000);
  document.addEventListener("visibilitychange", function () {
    if (!document.hidden && Date.now() - lastSync > 5 * 60 * 1000) sync(false);
  });
  window.harmoniSpotifyPublic = { sync: sync, paint: paint };
  try { paint(); document.documentElement.setAttribute("data-sp-public-ready", "1"); } catch (e) {}
})();
