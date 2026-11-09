/* غرفة الاستماع — نسخة مبسّطة:
   - زر واحد «ابدأ غرفة»: الأغنية اللي تسمعها تكمل للكل بدون أي نوافذ إعدادات
   - دخول بالكود من نفس الصفحة، أو ضغطة على غرفة مفتوحة
   - داخل الغرفة: «استمع» على أي أغنية تشغّلها للكل، والإعدادات للمالك وهي شغالة
   - الخروج يرجّعك لوضعك العادي بدون ما توقف الأغنية */
(function () {
  "use strict";
  const $ = id => document.getElementById(id);
  const shared = () => !!(inRoom && state && state.rid && !state.personal);
  const iOwn = () => !!(state && state.owner_sid && state.owner_sid === socket.id);

  function carryTrack() {
    if (!(solo && solo.active && solo.track)) return null;
    const tr = solo.track;
    return { title: tr.title, artist: tr.artist, duration: tr.duration, thumbnail: tr.thumbnail,
             web_url: tr.web_url, source: tr.source,
             added_by: myProfile.name || "", position: soloAudio.currentTime || 0, playing: !soloAudio.paused };
  }

  function personalRoom(carry) {
    const payload = profilePayload();
    payload.room_name = t("room.auto_name") + " " + (myProfile.name || t("me.default"));
    payload.auto = true;
    payload.can_add = true;
    payload.carry = !!carry;
    socket.emit("create_room", payload);
  }
  autoCreateRoom = function () { personalRoom(false); };

  let justStarted = false;
  function startRoom() {
    const payload = profilePayload();
    payload.room_name = L("غرفة ", "") + (myProfile.name || t("me.default")) + L("", "'s room");
    payload.can_add = true;
    payload.max_members = 0;
    payload.password = "";
    payload.carry = true;
    payload.carry_track = carryTrack();
    justStarted = true;
    socket.emit("create_room", payload);
  }

  // دخول طلبه المستخدم بنفسه (كود/رابط/ضغطة على غرفة) — رسائل الخطأ تطلع بس لهذا،
  // مو لمحاولات الدخول التلقائية (رجوع الاتصال، حفظ البروفايل…)
  let explicitJoinAt = new URLSearchParams(location.search).get("room") ? Date.now() : 0;   // فتح رابط دعوة = طلب دخول
  function joinByCode(code) {
    explicitJoinAt = Date.now();
    code = String(code || "").replace(/\D/g, "");
    if (!/^\d{6}$/.test(code)) { toast(L("الكود 6 أرقام", "The code is 6 digits")); return; }
    myRoomId = code;
    roomPassword = "";
    emitJoin();
  }
  joinRoom = joinByCode;
  openJoinRoom = () => { showView("room"); setTimeout(() => $("rm-code") && $("rm-code").focus(), 50); };
  openCreateRoom = startRoom;

  leaveRoom = function () {
    myRoomId = null;
    roomPassword = "";
    personalRoom(true);
    toast(L("طلعت من الغرفة — الأغنية مكملة عندك", "You left the room — the song keeps playing"));
  };

  /* «استمع» على أي أغنية داخل غرفة مشتركة = تشتغل للكل */
  const origPlaySolo = playSolo;
  playSolo = function (trk) {
    if (!shared() || !trk) return origPlaySolo.apply(this, arguments);
    if (!state.can_add && !iOwn()) { toast(t("room.owner_only")); return; }
    socket.emit("add", { play_now: true, track: { title: trk.title, artist: trk.artist, duration: trk.duration,
      thumbnail: trk.thumbnail, web_url: trk.web_url, source: trk.source, cuts: trk.cuts || undefined, added_by: myProfile.name || "" } });
    toast("🎧 " + L("تشتغل الآن للكل", "Playing for everyone"));
  };

  const origApply = applyState;
  applyState = function (s) {
    origApply.apply(this, arguments);
    if (shared() && solo.active) exitSolo();
    if (justStarted && shared()) {
      justStarted = false;
      copyInvite(true);
    }
  };

  socket.off("join_error");
  socket.on("join_error", d => {
    const reason = (d || {}).reason;
    if (state && state.rid) myRoomId = state.rid;   // ما زلت في غرفتك الحالية
    const explicit = Date.now() - explicitJoinAt < 20000;
    explicitJoinAt = 0;
    if (!explicit && reason !== "kicked") {
      // محاولة تلقائية فشلت (مثلاً الغرفة انقفلت): نرجع لوضعك العادي بهدوء بدون رسائل
      if (!state || !state.rid) personalRoom(false);
      return;
    }
    if (reason === "need_password" || reason === "wrong_password") {
      const code = String((d && d.room) || $("rm-code").value || "").replace(/\D/g, "");
      showModal(t("room.password.title"),
        '<div class="form-row"><label>' + t("room.password.label") + '</label><input id="join-pass" type="text"></div>' +
        (reason === "wrong_password" ? '<div class="msg">' + t("room.wrong_password") + '</div>' : "") +
        '<button id="join-pass-submit" class="btn btn-primary btn-block">' + t("room.password.submit") + '</button>');
      const go = () => { roomPassword = $("join-pass").value; hideModal(); myRoomId = pendingCode || code; explicitJoinAt = Date.now(); origEmitJoin(); };
      $("join-pass-submit").onclick = go;
      $("join-pass").addEventListener("keydown", e => { if (e.key === "Enter") go(); });
      setTimeout(() => $("join-pass") && $("join-pass").focus(), 50);
    } else if (reason === "full") {
      toast(t("room.full"));
    } else if (reason === "kicked") {
      toast(L("صاحب الغرفة طلّعك منها", "The owner removed you from this room"));
    } else {
      if (/^(localhost|127\.0\.0\.1)$/.test(location.hostname)) {
        roomToast(L("ما لقينا الغرفة — إذا الغرفة على جهاز صاحبك، اطلب منه «رابط الدعوة» والصقه هنا (الكود لحاله يشتغل بس لغرف جهازك)",
                    "Room not found — if it's on your friend's PC, ask for the invite link and paste it here (a code alone only works for rooms on your own PC)"));
      } else toast(L("ما لقينا غرفة بهذا الكود", "No room with that code"));
      if (!state || !state.rid) personalRoom(false);
    }
  });
  let pendingCode = "";
  const origEmitJoin = emitJoin;
  emitJoin = function () { pendingCode = myRoomId || ""; return origEmitJoin.apply(this, arguments); };

  // الاتصال بالغرفة المشتركة انقطع وما رجع: نرجع لوضعك العادي والأغنية تكمل عندك
  socket.on("room_lost", () => {
    myRoomId = null;
    toast(L("انقطع الاتصال بالغرفة — رجعناك لوضعك العادي", "Lost connection to the room — you're back to solo listening"));
    personalRoom(true);
  });

  socket.off("kicked");
  socket.on("kicked", () => {
    myRoomId = null;
    toast(L("صاحب الغرفة طلّعك منها", "The owner removed you from the room"));
    personalRoom(true);
  });

  /* رابط عام للغرفة: لو البرنامج ما عنده نفق، يشغّل واحد مجاني (Cloudflare) — عشان أصحابك يدخلون من أي مكان */
  async function publicBase() {
    const local = /^(localhost|127\.0\.0\.1)$/.test(location.hostname);
    if (!local) return location.origin;
    let info = {};
    try { info = await (await fetch("/api/info")).json(); } catch (e) {}
    if (info.tunnel) return info.tunnel.replace(/\/+$/, "");
    let shown = false;
    try {
      let st = await (await fetch("/api/room_tunnel", { method: "POST" })).json();
      for (let i = 0; i < 240 && st.state !== "ready" && st.state !== "error"; i++) {
        const msg = st.state === "downloading"
          ? L("نجهّز رابط الدعوة لأول مرة (تحميل أداة Cloudflare المجانية) ", "Preparing the invite link for the first time (downloading Cloudflare's free tool) ") + (st.progress || 0) + "%"
          : L("نجهّز رابط الدعوة…", "Preparing the invite link…");
        if (!shown || st.state === "downloading") { toast(msg); shown = true; }
        await new Promise(r => setTimeout(r, 1000));
        st = await (await fetch("/api/room_tunnel")).json();
      }
      if (st.state === "ready" && st.url) return st.url.replace(/\/+$/, "");
    } catch (e) {}
    roomToast(L("ما قدرنا نسوي رابط عام — الدعوة بتشتغل بس لأصحابك على نفس شبكة الواي فاي", "Couldn't make a public link — the invite only works on the same Wi-Fi"));
    return "http://" + info.ip + ":" + info.port;
  }
  async function inviteLink(code) {
    // الغرف المشتركة على السيرفر: رابط ثابت يشتغل دايماً (ما يحتاج جهازك يكون شغال)
    if (state && state.cloud) return "https://hub.harmoni.click/r/" + code;
    let base = location.origin;
    try { base = await publicBase(); } catch (e) {}
    return base + "/?room=" + code;
  }
  function roomToast(text) {
    toast(text);
    try { clearTimeout(toastTimer); toastTimer = setTimeout(() => $("toast").classList.add("hidden"), 7000); } catch (e) {}
  }

  /* الدخول برابط دعوة: لو الغرفة على جهاز ثاني نفتحها في نافذة منفصلة */
  const LINK_RE = /https?:\/\/[^\s"'<>]+?[?&]room=\d{6}[^\s"'<>]*/i;
  const HUB_LINK_RE = /https:\/\/hub\.harmoni\.click\/r\/(\d{6})/i;
  async function joinByLink(text) {
    const hm = String(text || "").match(HUB_LINK_RE);
    if (hm) { joinByCode(hm[1]); return true; }
    const m = String(text || "").match(LINK_RE);
    if (!m) return false;
    let u;
    try { u = new URL(m[0]); } catch (e) { return false; }
    const code = u.searchParams.get("room");
    let mine = u.origin === location.origin;
    if (!mine && /^(localhost|127\.0\.0\.1)$/.test(location.hostname)) {
      // غرفة مشتركة على السيرفر؟ ندخلها من هنا مباشرة بدل ما نفتح جهاز صاحبك
      try { const j = await (await fetch("/api/rooms")).json(); mine = (j.rooms || []).some(r => r.id === code && r.cloud); } catch (e) {}
    }
    if (!mine && /^(localhost|127\.0\.0\.1)$/.test(location.hostname)) {
      try { const info = await (await fetch("/api/info")).json(); mine = !!info.tunnel && new URL(info.tunnel).host === u.host; } catch (e) {}
    }
    if (mine) { joinByCode(code); return true; }
    if (u.protocol !== "https:") {
      roomToast(L("هذا رابط شبكة منزلية — يفتح بس لو أنتم على نفس الواي فاي. اطلب من صاحبك يحدّث البرنامج ويرسل الدعوة من جديد",
                  "That's a home-network link — it only opens on the same Wi-Fi. Ask your friend to update Harmoni and resend the invite"));
      return true;
    }
    if (!/^(localhost|127\.0\.0\.1)$/.test(location.hostname)) { location.href = u.href; return true; }
    u.searchParams.set("guest", (myProfile.name || "").slice(0, 30));
    try {
      const r = await fetch("/api/open_room", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ url: u.href }) });
      const j = await r.json();
      if (j.ok) { toast(j.window ? L("فتحنا غرفة صاحبك في نافذة جديدة 🎧", "Opened your friend's room in a new window 🎧") : L("فتحنا غرفة صاحبك في المتصفح 🎧", "Opened your friend's room in the browser 🎧")); return true; }
    } catch (e) {}
    roomToast(L("ما قدرنا نفتح الرابط", "Couldn't open the link"));
    return true;
  }
  window.harmoniJoinByLink = joinByLink;
  async function copyInvite(auto) {
    if (!shared()) return;
    const code = state.rid;
    const link = await inviteLink(code);
    const text = L("تعال اسمع معي في Harmoni 🎧\nالكود: ", "Listen with me on Harmoni 🎧\nCode: ") + code + "\n" + link;
    try { await navigator.clipboard.writeText(text); } catch (e) {}
    toast(auto ? L("الغرفة جاهزة ✓ نسخنا لك الدعوة، أرسلها لأصحابك", "Room is live ✓ invite copied — send it to your friends")
               : L("تم نسخ الدعوة", "Invite copied"));
  }

  function renderSettings() {
    const box = $("rm-settings");
    if (!box || box.classList.contains("hidden")) return;
    if (box.dataset.rid === state.rid) return;     // لا نمسح اللي يكتبه المستخدم
    box.dataset.rid = state.rid;
    const maxVals = [0, 2, 3, 5, 8, 10, 15, 20, 30, 50];
    box.innerHTML =
      '<label class="rm-field"><span>' + L("اسم الغرفة", "Room name") + '</span><input id="rms-name" maxlength="40" value="' + esc(state.name || "") + '"></label>' +
      '<label class="rm-field"><span>' + L("كلمة سر (اختياري)", "Password (optional)") + '</span><input id="rms-pass" maxlength="32" placeholder="' +
        esc(state.has_password ? L("محمية — اكتب جديدة أو امسحها", "Locked — type a new one or clear") : L("بدون", "None")) + '"></label>' +
      '<label class="rm-field"><span>' + L("أقصى عدد", "Max members") + '</span><select id="rms-max">' +
        maxVals.map(v => '<option value="' + v + '"' + (v === (state.max_members || 0) ? " selected" : "") + '>' + (v ? v : L("مفتوح", "Unlimited")) + '</option>').join("") +
      '</select></label>' +
      '<label class="rm-check"><input id="rms-add" type="checkbox"' + (state.can_add ? " checked" : "") + '> ' + L("الكل يقدر يضيف ويشغّل أغاني", "Everyone can add & play songs") + '</label>' +
      '<div class="rm-set-actions"><button id="rms-save" class="btn btn-primary">' + L("حفظ", "Save") + '</button>' +
      (state.has_password ? '<button id="rms-unlock" class="btn">' + L("شيل كلمة السر", "Remove password") + '</button>' : "") + '</div>';
    $("rms-save").onclick = () => {
      const data = { name: $("rms-name").value, can_add: $("rms-add").checked, max_members: parseInt($("rms-max").value, 10) || 0 };
      const pass = $("rms-pass").value.trim();
      if (pass) data.password = pass;
      socket.emit("room_settings", data);
      box.classList.add("hidden");
      toast(L("تم حفظ إعدادات الغرفة", "Room settings saved"));
    };
    const un = $("rms-unlock");
    if (un) un.onclick = () => { socket.emit("room_settings", { password: "" }); box.classList.add("hidden"); toast(L("الغرفة صارت مفتوحة", "Password removed")); };
  }

  let lastRoomsFetch = 0;
  renderRoomArea = function () {
    const live = shared();
    const lobby = $("rm-lobby"), liveEl = $("rm-live");
    if (!lobby || !liveEl) return;
    lobby.classList.toggle("hidden", live);
    liveEl.classList.toggle("hidden", !live);
    document.documentElement.toggleAttribute("data-in-shared-room", live);
    if (!live) {
      $("rm-settings").classList.add("hidden");
      if (Date.now() - lastRoomsFetch > 2500) { lastRoomsFetch = Date.now(); loadRooms(); }
      return;
    }
    const n = (state.members || []).length;
    const owner = (state.members || []).find(m => m.id === state.owner_sid);
    $("room-name-title").textContent = state.name || "";
    $("room-name-title").classList.remove("hidden");
    $("rm-sub").textContent = n + " " + L(n === 1 ? "مستمع" : "مستمعين", n === 1 ? "listener" : "listeners") +
      (owner ? " · 👑 " + owner.name : "") + (state.has_password ? " · 🔒" : "") +
      (state.can_add ? "" : " · " + L("المالك بس يضيف", "owner adds only"));
    $("room-code-badge").textContent = state.rid;
    $("rm-settings-btn").classList.toggle("hidden", !iOwn());
    if (!iOwn()) $("rm-settings").classList.add("hidden");
    renderSettings();
  };

  renderRoomList = function (rooms) {
    const el = $("room-list");
    if (!el) return;
    if (!rooms.length) {
      el.innerHTML = '<div class="room-empty">' + L("ما فيه غرف مفتوحة الحين — ابدأ وحدة وادعُ أصحابك", "No open rooms right now — start one and invite friends") + '</div>';
      return;
    }
    el.innerHTML = rooms.map(r =>
      '<button class="rm-card" data-rid="' + esc(r.id) + '">' +
        '<span class="rm-card-icon">🎧</span>' +
        '<span class="rm-card-meta"><span class="rm-card-name">' + esc(r.name) + (r.has_password ? " 🔒" : "") + '</span>' +
        '<span class="rm-card-sub">' + r.members + (r.max_members ? "/" + r.max_members : "") + " " + L("مستمع", "listening") + '</span></span>' +
        '<span class="rm-card-go">' + L("دخول", "Join") + '</span>' +
      '</button>').join("");
    el.querySelectorAll(".rm-card").forEach(b => { b.onclick = () => joinByCode(b.dataset.rid); });
  };

  function i18nStatic() {
    const set = (sel, ar, en) => { const e = document.querySelector(sel); if (e) e.textContent = L(ar, en); };
    set(".rm-hero-title", "اسمعوا مع بعض", "Listen together");
    set(".rm-hero-text", "كلكم تسمعون نفس الأغنية بنفس اللحظة، وأي أحد يقدر يضيف أغاني.", "Everyone hears the same song at the same moment, and anyone can add songs.");
    set("#rm-start", "ابدأ غرفة", "Start a room");
    set(".rm-or span", "أو ادخل غرفة صاحبك بالكود", "or join a friend's room with a code");
    const li = $("rm-link"); if (li) li.placeholder = L("…أو الصق رابط الدعوة هنا", "…or paste the invite link here");
    set("#rm-link-go", "فتح", "Open");
    set("#rm-join", "دخول", "Join");
    set(".rm-public .room-title", "غرف مفتوحة الآن", "Open rooms");
    set("#rm-invite", "🔗 نسخ الدعوة", "🔗 Copy invite");
    set("#leave-room-btn", "خروج", "Leave");
    set(".room-queue-box .room-title", "يشتغل الآن والتالي", "Now playing & up next");
    set(".room-add-box .room-title", "أضف أغنية", "Add a song");
  }

  function wire() {
    i18nStatic();
    $("rm-start").onclick = startRoom;
    $("rm-join").onclick = () => joinByCode($("rm-code").value);
    const code = $("rm-code");
    code.addEventListener("input", () => {
      code.value = code.value.replace(/\D/g, "").slice(0, 6);
      if (code.value.length === 6) joinByCode(code.value);
    });
    code.addEventListener("keydown", e => { if (e.key === "Enter") joinByCode(code.value); });
    code.addEventListener("paste", e => {
      const txt = (e.clipboardData || window.clipboardData).getData("text") || "";
      if (LINK_RE.test(txt)) { e.preventDefault(); code.value = ""; joinByLink(txt); return; }
      const v = (txt.match(/\d{6}/) || [])[0];
      if (v) { e.preventDefault(); code.value = v; joinByCode(v); }
    });
    const linkIn = $("rm-link");
    if (linkIn) {
      const go = () => { const v = linkIn.value; if (!LINK_RE.test(v)) { toast(L("الصق رابط الدعوة كامل", "Paste the full invite link")); return; } linkIn.value = ""; joinByLink(v); };
      $("rm-link-go").onclick = go;
      linkIn.addEventListener("keydown", e => { if (e.key === "Enter") go(); });
      linkIn.addEventListener("paste", () => setTimeout(() => { if (LINK_RE.test(linkIn.value)) go(); }, 0));
    }
    $("rm-invite").onclick = () => copyInvite(false);
    $("room-code-badge").onclick = () => {
      navigator.clipboard.writeText(state.rid || "").then(() => toast(t("room.code.copied"))).catch(() => {});
    };
    $("rm-settings-btn").onclick = () => {
      const box = $("rm-settings");
      box.dataset.rid = "";
      box.classList.toggle("hidden");
      renderSettings();
    };
    $("leave-room-btn").onclick = () => leaveRoom();
    renderRoomArea();
    try { document.documentElement.setAttribute("data-room-ready", "1"); } catch (e) {}
  }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", wire); else wire();
})();
