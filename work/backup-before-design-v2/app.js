/* ================= البيانات المحلية ================= */
async function fetchWithTimeout(url, options = {}, timeout = 30000) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeout);
  try {
    return await fetch(url, { ...options, signal: controller.signal });
  } finally {
    clearTimeout(timer);
  }
}
function store(key, val) {
  localStorage.setItem(key, JSON.stringify(val));
  if (key === "mu_playlists" || key === "mu_likes") window.dispatchEvent(new Event("harmoni-library"));
}
function load(key, def) {
  try { const v = JSON.parse(localStorage.getItem(key)); return v === null ? def : v; }
  catch (e) { return def; }
}

function getProfile() {
  const p = load("mu_profile", { name: "", avatar: "", banner: "", bio: "", colors: {} });
  p.colors = p.colors || {};
  if (!p.colors.accent) p.colors.accent = "#1db954";
  if (!p.colors.accent2) p.colors.accent2 = "#0f6bff";
  p.uid = p.uid || getUid();
  return p;
}
function getLikes() { return load("mu_likes", []); }
function getHistory() {
  let h = load("mu_history", []).filter(x => !badTrack(x));
  if (!h.length) {
    // السجل انمسح بالغلط (أو جهاز ثاني رفع سجل فاضي): نرجعه من إحصائيات الاستماع اللي فيها آخر وقت تشغيل لكل أغنية
    try {
      if (localStorage.getItem("mu_history_cleared") === "1") return h;
      const st = JSON.parse(localStorage.getItem("mu_stats") || "null");
      const tr = (st && st.tracks) || {};
      h = Object.keys(tr).map(k => tr[k]).filter(e => e && e.t && e.l)
        .sort((a, b) => b.l - a.l).slice(0, 20)
        .map(e => ({ title: e.t, artist: e.a, thumbnail: e.th, web_url: e.u, source: e.s }));
      if (h.length) localStorage.setItem("mu_history", JSON.stringify(h));
    } catch (e) { h = []; }
  }
  return h;
}

function getFavArtists() { return load("mu_fav_artists", []); }
function isFavArtist(name) {
  const n = (name || "").toLowerCase();
  return getFavArtists().some(a => (a.name || "").toLowerCase() === n);
}
function toggleFavArtist(name, channel) {
  if (!name) return;
  const favs = getFavArtists();
  const i = favs.findIndex(a => (a.name || "").toLowerCase() === name.toLowerCase());
  if (i >= 0) favs.splice(i, 1);
  else favs.unshift({ name, channel: channel || null });
  store("mu_fav_artists", favs);
}

function getPlaylists() { return load("mu_playlists", []); }
function savePlaylists(list) { store("mu_playlists", list); pushUserData(); }
function plId() { return "pl" + Math.random().toString(36).slice(2, 10); }

/* ===== حفظ دائم لكل شيء على السيرفر (لكل حساب) ===== */
let _udTimer = null;
function getToken() { try { return localStorage.getItem("mu_token") || ""; } catch (e) { return ""; } }
let _loginWarned = false;
function warnLoginNeeded() {
  if (_loginWarned) return;
  _loginWarned = true;
  toast(L("لحماية حسابك: سجّل دخولك مرة ثانية عشان تتزامن بياناتك", "To protect your account, please log in again to sync your data"));
}
function pushUserData() {
  clearTimeout(_udTimer);
  _udTimer = setTimeout(async () => {
    try {
      if (!authed()) return;
      const res = await fetch("/api/userdata", {
        method: "POST",
        headers: { "Content-Type": "application/json", "X-Harmoni-Token": getToken() },
        body: JSON.stringify({
          uid: getUid(),
          likes: getLikes(),
          playlists: getPlaylists(),
          history: getHistory(),
          history_clear: localStorage.getItem("mu_history_cleared") === "1",
          following: getFollowing(),
          stats: window.getStatsData ? window.getStatsData() : undefined
        })
      });
      if (res.status === 403) warnLoginNeeded();
    } catch (e) {}
  }, 1200);
}
async function pullUserData() {
  if (!authed()) return;
  try {
    const res = await fetch("/api/userdata?uid=" + encodeURIComponent(getUid()), { headers: { "X-Harmoni-Token": getToken() } });
    if (res.status === 403) { warnLoginNeeded(); return; }
    const d = await res.json();
    if (d.token) { try { localStorage.setItem("mu_token", d.token); } catch (e) {} }
    let changed = false;
    const mergeList = (local, remote, keyFn) => {
      const seen = new Set(local.map(keyFn));
      const out = local.slice();
      remote.forEach(x => {
        const k = keyFn(x);
        if (!seen.has(k)) { seen.add(k); out.push(x); changed = true; }
      });
      return out;
    };
    const nl = mergeList(getLikes(), d.likes || [], trackKey);
    if (changed) { store("mu_likes", nl); }
    const before = JSON.stringify(getPlaylists());
    const np = getPlaylists();
    (d.playlists || []).forEach(sp => {
      const same = p => (p.id && sp.id) ? p.id === sp.id : p.name === sp.name;
      if (!np.some(same)) { np.push(sp); changed = true; }
      else if (!(sp.tracks || []).length) return;
      else {
        const p = np.find(same);
        const keys = new Set((p.tracks || []).map(trackKey));
        (sp.tracks || []).forEach(tk => {
          if (!keys.has(trackKey(tk))) { (p.tracks = p.tracks || []).push(tk); changed = true; }
        });
      }
    });
    const nh = mergeList(getHistory(), d.history || [], trackKey)
      .sort((a, b) => 0);
    if (JSON.stringify(getHistory()) !== JSON.stringify(nh.slice(0, 20)) && (d.history || []).length >= getHistory().length) {
      store("mu_history", nh.slice(0, 20)); changed = true;
    }
    try { if (window.mergeRemoteStats && window.mergeRemoteStats(d.stats)) changed = true; } catch (e) {}
    const nf = Array.from(new Set([...getFollowing(), ...(d.following || [])]));
    if (nf.length !== getFollowing().length) { saveFollowing(nf); changed = true; }
    if (before !== JSON.stringify(np)) store("mu_playlists", np);
    if (changed) {
      renderLikes(); renderHistory(); renderLikesView(); renderQueue();
      loadSuggestions();
      pushUserData();
    }
  } catch (e) {}
}

function getUid() {
  let u = localStorage.getItem("mu_uid");
  if (!u) {
    u = "u" + Math.random().toString(36).slice(2) + Date.now().toString(36);
    localStorage.setItem("mu_uid", u);
  }
  return u;
}
function getFollowing() { return load("mu_following", []); }
function saveFollowing(f) { store("mu_following", f); pushUserData(); }
function isFollowing(m) { return !!m && !!m.uid && getFollowing().includes(m.uid); }
function isMyProfile(p) {
  if (!p) return true;
  if (p === myProfile) return true;
  if (p.id && p.id === socket.id) return true;
  return !!p.uid && p.uid === getUid();
}
function profileMap() {
  const map = {};
  (state.members || []).forEach(m => {
    if (m.uid) map[m.uid] = { uid: m.uid, name: m.name, avatar: m.avatar, id: m.id, following: (m.following || []).slice(), bio: m.bio || "", banner: m.banner || "", colors: m.colors || {}, likes: m.likes || [], online: true };
  });
  profileRegistry.forEach(pr => {
    if (!pr.uid) return;
    if (map[pr.uid]) {
      if (!map[pr.uid].following.length && pr.following) map[pr.uid].following = pr.following.slice();
      if (!map[pr.uid].bio && pr.bio) map[pr.uid].bio = pr.bio;
      if (!map[pr.uid].banner && pr.banner) map[pr.uid].banner = pr.banner;
      if (!map[pr.uid].likes.length && pr.likes) map[pr.uid].likes = pr.likes.slice();
    } else {
      map[pr.uid] = { uid: pr.uid, name: pr.name, avatar: pr.avatar, id: null, following: (pr.following || []).slice(), bio: pr.bio || "", banner: pr.banner || "", colors: pr.colors || {}, likes: pr.likes || [], online: false };
    }
  });
  return map;
}
function followerCount(m) {
  const uid = m && m.uid;
  if (!uid) return 0;
  const pm = profileMap();
  let n = 0;
  for (const k in pm) if (k !== uid && pm[k].following.includes(uid)) n++;
  return n;
}
function followingCount(m) {
  return profileFollowing(m).length;
}
function profileFollowing(m) {
  if (isMyProfile(m)) return getFollowing();
  return (m && m.following) || [];
}
function followerMembers(p) {
  const uid = p && p.uid;
  if (!uid) return [];
  const pm = profileMap();
  const out = [];
  for (const k in pm) if (k !== uid && pm[k].following.includes(uid)) out.push(pm[k]);
  return out;
}
function followingMembers(p) {
  const fl = profileFollowing(p);
  if (!fl.length) return [];
  const pm = profileMap();
  return fl.map(uid => pm[uid]).filter(Boolean);
}
function avatarHtml(m, cls) {
  const img = m.avatar
    ? '<img class="' + cls + '" src="' + m.avatar + '">'
    : '<div class="' + cls + ' f-fallback">' + esc((m.name || "؟")[0]) + '</div>';
  return '<div class="avatar-wrap">' + img + '<span class="presence-dot ' + (m.online ? "on" : "off") + '" title="' + (m.online ? t("status.online") : t("status.offline")) + '"></span></div>';
}

function openProfile(person) {
  if (!person) return;
  hideModal();
  if (isMyProfile(person)) { showView("profile"); return; }
  viewingProfile = person;
  showView("profile");
}

function showFollowModal(title, list, kind, owner) {
  const canManage = !!kind && !!owner && isMyProfile(owner);
  let body;
  if (!list.length) {
    body = '<div class="msg">' + t("follows.empty") + '</div>';
  } else {
    body = '<div id="follow-list">' + list.map(m => {
      let btn = "";
      if (canManage && m.uid) {
        if (kind === "following") btn = '<button class="f-act" data-act="unfollow">' + esc(t("unfollow.btn")) + '</button>';
        else btn = '<button class="f-act danger" data-act="remove">' + esc(t("follows.remove")) + '</button>';
      }
      return '<div class="f-item" data-uid="' + esc(m.uid || "") + '">' +
        avatarHtml(m, "f-avatar") +
        '<span class="f-name">' + esc(m.name || t("me.default")) + '</span>' + btn + '</div>';
    }).join('') + '</div>';
  }
  showModal(title, body);
  document.querySelectorAll("#follow-list .f-item").forEach(el => {
    el.onclick = () => {
      const person = profileMap()[el.dataset.uid];
      if (person) openProfile(person);
    };
    const b = el.querySelector(".f-act");
    if (b) b.onclick = e => {
      e.stopPropagation();
      const uid = el.dataset.uid;
      if (b.dataset.act === "unfollow") {
        const person = profileMap()[uid];
        if (person) toggleFollow(person);
        el.remove();
        refreshProfileStats();
      } else {
        fetch("/api/remove_follower", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ owner: getUid(), target: uid }) })
          .then(r => r.json())
          .then(() => {
            el.remove();
            setTimeout(() => { loadProfiles().then(() => { refreshProfileStats(); renderMe(); }); }, 300);
          }).catch(() => {});
      }
    };
  });
}
async function openFollowList(kind, p) {
  await loadProfiles();
  await new Promise(r => setTimeout(r, 350));
  await loadProfiles();
  const list = kind === "followers" ? followerMembers(p) : followingMembers(p);
  showFollowModal(kind === "followers" ? t("follows.followers") : t("follows.following"), list, kind, p);
}

function toggleFollow(m) {
  if (!m || !m.uid || isMyProfile(m)) return;
  const f = getFollowing();
  const i = f.indexOf(m.uid);
  if (i >= 0) f.splice(i, 1); else f.push(m.uid);
  saveFollowing(f);
  emitJoin();
  setTimeout(loadProfiles, 600);
  if (viewingProfile === m) renderProfile();
  if (popupPerson === m) showProfilePopup(popupPerson, popupAnchor);
  renderMembers();
}

function trackKey(t) { return t.web_url || t.title; }

function badTrack(t) {
  const title = String((t && t.title) || "").trim();
  return !title || title === "بدون عنوان" || title === "Untitled";
}
function addHistory(t) {
  if (badTrack(t)) return;
  try { localStorage.removeItem("mu_history_cleared"); } catch (e) {}
  const h = getHistory().filter(x => trackKey(x) !== trackKey(t));
  h.unshift({ title: t.title, artist: t.artist, duration: t.duration, thumbnail: t.thumbnail, web_url: t.web_url, source: t.source });
  store("mu_history", h.slice(0, 20));
  try { if (window.recordPlay) window.recordPlay(t); } catch (e) {}
  pushUserData();
  renderHistory();
  loadSuggestions();
}

function toggleLike(t) {
  let likes = getLikes();
  const i = likes.findIndex(x => trackKey(x) === trackKey(t));
  if (i >= 0) likes.splice(i, 1);
  else { likes.unshift({ title: t.title, artist: t.artist, duration: t.duration, thumbnail: t.thumbnail, web_url: t.web_url, source: t.source }); cacheAudioOnDisk([t]); }
  store("mu_likes", likes);
  pushUserData();
  renderLikes();
  renderHistory();
  renderLikesView();
}

function isLiked(t) { return getLikes().some(x => trackKey(x) === trackKey(t)); }

/* ================= الألوان ================= */
function lighten(hex, amt) {
  try {
    let c = String(hex).replace("#", "");
    if (c.length === 3) c = c.split("").map(x => x + x).join("");
    const r = parseInt(c.slice(0, 2), 16), g = parseInt(c.slice(2, 4), 16), b = parseInt(c.slice(4, 6), 16);
    const f = v => Math.min(255, Math.round(v + (255 - v) * amt));
    return "#" + [f(r), f(g), f(b)].map(v => v.toString(16).padStart(2, "0")).join("");
  } catch (e) { return hex; }
}

function contrastText(hex) {
  try {
    let c = String(hex).replace("#", "");
    if (c.length === 3) c = c.split("").map(x => x + x).join("");
    const r = parseInt(c.slice(0, 2), 16), g = parseInt(c.slice(2, 4), 16), b = parseInt(c.slice(4, 6), 16);
    const lum = (0.299 * r + 0.587 * g + 0.114 * b) / 255;
    return lum > 0.45 ? "#000" : "#fff";
  } catch (e) { return "#fff"; }
}

function applyColors(c) {
  c = c || {};
  const root = document.documentElement;
  root.style.setProperty("--accent", c.accent || "#1db954");
  root.style.setProperty("--accent-hover", lighten(c.accent || "#1db954", 0.15));
  root.style.setProperty("--accent2", c.accent2 || "#0f6bff");
  root.style.setProperty("--on-accent", contrastText(c.accent || "#1db954"));
}

function applyScopedColors(el, c) {
  if (!el) return;
  c = c || {};
  el.style.setProperty("--accent", c.accent || "#1db954");
  el.style.setProperty("--accent-hover", lighten(c.accent || "#1db954", 0.15));
  el.style.setProperty("--accent2", c.accent2 || "#0f6bff");
  el.style.setProperty("--on-accent", contrastText(c.accent || "#1db954"));
}

function clearScopedColors(el) {
  if (!el) return;
  ["--accent", "--accent-hover", "--accent2", "--on-accent"].forEach(v => el.style.removeProperty(v));
}

const profileViewEl = () => document.getElementById("view-profile");

/* ================= الإعدادات واللغة والمظهر ================= */
function getSettings() {
  const s = load("mu_settings", {});
  if (s.lang !== "ar" && s.lang !== "en") s.lang = "ar";
  if (!s.theme) s.theme = "dark";
  if (!s.accent) s.accent = "#1db954";
  return s;
}
let settings = getSettings();
function saveSettings() { store("mu_settings", settings); }

const I18N = {
  ar: {
    "app.title": "موسيقى جماعية",
    "nav.home": "الرئيسية",
    "nav.queue": "قائمة الانتظار",
    "nav.room": "غرفة الاستماع",
    "room.add.title": "أضف إلى قائمة التشغيل",
    "room.queue.title": "قائمة التشغيل",
    "room.from.likes": "من مفضلتك",
    "room.status.in": "أنت داخل غرفة:",
    "room.status.out": "لسه ما دخلت أي غرفة",
    "room.code.share": "شارك هذا الكود مع أصحابك — يدخلون غرفتك بكبسولته",
    "room.code.copied": "تم نسخ كود الغرفة",
    "settings.sp.cid": "Spotify Client ID",
    "settings.sp.sec": "Spotify Client Secret",
    "settings.sp.save": "حفظ بيانات سبوتيفاي",
    "settings.sp.saved_ph": "محفوظة ✓ (اتركها فاضية للاحتفاظ)",
    "settings.sp.need_cid": "اكتب الـ Client ID أولاً",
    "toast.sp.saved": "تم حفظ بيانات سبوتيفاي ✓",
    "nav.likes": "الأغاني المفضلة",
    "nav.settings": "الإعدادات",
    "room.title": "الغرفة الجماعية",
    "room.available": "الغرف المتاحة",
    "room.create": "إنشاء غرفة استماع",
    "room.join": "الانضمام",
    "room.leave": "الخروج من الغرفة",
    "room.name": "اسم الغرفة",
    "room.not_found": "الغرفة غير موجودة",
    "rooms.empty": "لا توجد غرف متاحة بعد.",
    "rooms.locked": "هذه الغرفة محمية بكلمة سر",
    "room.crown": "صاحب الغرفة",
    "room.kick": "طرد",
    "room.kicked": "تم طردك من الغرفة",
    "room.owner_only": "المالك فقط يضيف الأغاني",
    "room.join_first": "انضم إلى غرفة أولاً لإضافة الأغاني",
    "room.auto_name": "غرفة",
    "track.addfail": "تعذر إضافة الأغنية",
    "room.settings.title": "إعدادات الغرفة",
    "room.max": "أقصى عدد أعضاء",
    "room.max.unlimited": "غير محدود",
    "room.pass": "كلمة سر الغرفة (اختياري)",
    "room.canadd": "الجميع يقدرون إضافة الأغاني",
    "room.join.title": "الانضمام إلى غرفة",
    "room.join.link": "ألصق رابط الغرفة",
    "room.join.submit": "دخول",
    "room.join.badlink": "رابط غير صالح",
    "room.password.title": "كلمة سر الغرفة",
    "room.password.label": "أدخل كلمة السر",
    "room.password.submit": "دخول",
    "room.wrong_password": "كلمة السر خاطئة",
    "room.full": "الغرفة ممتلئة",
    "follows.followers": "المتابِعون",
    "follows.following": "الذين يتابعهم",
    "follows.empty": "لا يوجد أحد بعد.",
    "status.online": "متصل الآن",
    "status.offline": "غير متصل",
    "likes.count": "أغنية مفضلة",
    "share.btn": "مشاركة رابط الاستماع",
    "me.default": "مستمع مجهول",
    "profile.btn": "عرض البروفايل",
    "footer.host": "تعمل على جهاز المستضيف",
    "home.title": "استمع إلى الموسيقى مع أصدقائك",
    "source.youtube": "يوتيوب",
    "source.soundcloud": "ساوند كلاود",
    "source.spotify": "سبوتيفاي",
    "search.placeholder": "ابحث عن أغنية أو الصق رابط (يوتيوب / سبوتيفاي / ساوند كلاود)...",
    "search.btn": "بحث",
    "history.title": "استمعت مؤخراً",
    "history.clear": "مسح السجل",
    "queue.title": "قائمة الانتظار",
    "shuffle.btn": "خلط عشوائي",
    "likes.title": "الأغاني التي أعجبتني",
    "likes.clear": "مسح المفضلة",
    "likes.other": "الأغاني التي أعجبته",
    "np.nothing": "لا شيء قيد التشغيل",
    "np.preparing": "⏳ جارٍ تجهيز الصوت… (الشبكة بطيئة، سيبدأ تلقائياً)",
    "edit.profile": "تعديل البروفايل",
    "crop.title": "قص الصورة",
    "crop.avatar": "قص الأفاتار",
    "crop.banner": "قص البنر",
    "crop.hint": "حرّك الصورة وقرّبها بزر الفأرة لاختيار المكان المناسب",
    "btn.cancel": "إلغاء",
    "btn.confirm": "تأكيد",
    "popup.full": "عرض البروفايل كامل",
    "toast.play.join": "اضغط زر التشغيل للانضمام إلى الموسيقى",
    "toast.play.failed": "تعذّر تشغيل الأغنية. جرّب مرة أخرى أو اختر مصدرًا آخر.",
    "queue.empty": "قائمة الانتظار فارغة — ابحث عن أغنية وأضفها.",
    "queue.remove": "إزالة",
    "added.by": "أضافها: ",
    "members.empty": "لا يوجد مستمعون بعد",
    "track.add": "إضافة",
    "track.listen": "استماع",
    "track.replay": "استمع مجدداً",
    "searching": "جارٍ البحث ...",
    "error.prefix": "خطأ: ",
    "search.fail": "تعذّر البحث",
    "toast.added": "تمت إضافة الأغنية",
    "share.title": "مشاركة رابط الاستماع",
    "share.body": "أرسل هذا الرابط لأصدقائك — من يفتحه ينضم للغرفة ويقدر يضيف أغاني.",
    "share.copy": "نسخ",
    "share.tip": "الرابط العام يعمل من أي شبكة. إن لم يظهر رابط عام، تأكد من تثبيت cloudflared.",
    "toast.copied": "تم نسخ الرابط",
    "form.user": "اسم المستخدم (@)",
    "user.checking": "جارٍ التحقق من اليوزر…",
    "user.taken": "هذا اليوزر محجوز، اختر يوزر آخر",
    "artist.fav": "تفضيل الفنان",
    "artist.faved": "الفنان من مفضلتك",
    "artist.channel": "أغاني قناة الفنان",
    "artist.local": "من سجلّك ومفضلتك",
    "artist.loading": "جارٍ جلب أغاني القناة…",
    "artist.none": "لم يتم العثور على أغاني لهذا الفنان.",
    "form.name": "الاسم",
    "form.bio": "البايو",
    "form.avatar": "صورة الأفاتار",
    "form.banner": "صورة البنر",
    "form.color1": "اللون الرئيسي",
    "form.presets": "تدرجات جاهزة",
    "form.anim": "بانر متحرك",
    "form.color2": "اللون الثانوي",
    "btn.save": "حفظ",
    "toast.saved": "تم حفظ البروفايل",
    "follow.btn": "متابعة",
    "unfollow.btn": "إلغاء المتابعة",
    "follows.remove": "إزالة",
    "confirm.clearLikes": "هل تريد حقًا مسح كل المفضلة؟",
    "confirm.yes": "نعم، امسح",
    "confirm.no": "إلغاء",
    "stats.followers": "متابع",
    "stats.following": "يتابع",
    "stats.likes": "إعجاب",
    "likes.mine.empty": "لم تضف أي أغنية إلى المفضلة بعد.",
    "likes.other.empty": "لا توجد أغانٍ مفضلة.",
    "likes.empty": "لا توجد أغانٍ مفضلة بعد.",
    "history.empty": "لا يوجد سجل استماع بعد.",
    "now.label": "يستمع الآن",
    "suggest.title": "مقترحة لك",
    "suggest.empty": "لا توجد اقتراحات الآن.",
    "artists.title": "الفنانين",
    "artists.empty": "لا يوجد فنانين بعد.",
    "artists.click": "افتح أغاني هذا الفنان",
    "artists.songs": "أغاني الفنان",
    "artists.notracks": "لا توجد أغاني مسجلة لهذا الفنان في سجلك أو مفضلتك.",
    "settings.title": "الإعدادات",
    "settings.language": "اللغة",
    "settings.appearance": "المظهر",
    "settings.miniplayer": "النافذة المصغرة",
    "settings.hotkey.press": "اضغط أي مفتاح… (Esc للإلغاء)",
    "settings.desktop": "النافذة المكتبية (خارج المتصفح)",
    "settings.desktop.open": "فتح النافذة المكتبية",
    "settings.desktop.hint": "تظهر فوق كل البرامج وتُدار بـ Alt+M حتى لو كان المتصفح مصغّراً",
    "settings.light": "الوضع الفاتح",
    "settings.dark": "الوضع الداكن",
    "settings.custom": "الوضع المخصص",
    "settings.custom.color": "اللون",
    "btn.shuffle.tip": "خلط عشوائي",
    "btn.play.tip": "تشغيل / إيقاف مؤقت",
    "btn.skip.tip": "تخطي",
    "btn.prev.tip": "السابق",
    "btn.like.tip": "أضف إلى المفضلة",
    "btn.loop.tip": "تكرار",
    "btn.pl.tip": "إضافة إلى بلاي لست",
    "btn.volume.tip": "كتم",
    "pl.title": "البلاي لستات",
    "pl.new": "+ بلاي لست جديد",
    "pl.create_title": "بلاي لست جديد",
    "pl.name": "اسم البلاي لست",
    "pl.image": "صورة البلاي لست",
    "pl.need_name": "اكتب اسم للبلاي لست",
    "pl.empty": "لا توجد بلاي لستات بعد.",
    "pl.play": "تشغيل البلاي لست",
    "pl.addto": "أضف إلى بلاي لست",
    "pl.added": "أُضيفت الأغنية إلى البلاي لست",
    "pl.created": "تم إنشاء البلاي لست",
    "pl.count": "أغنية",
    "pl.remove": "إزالة",
    "pl.delete": "حذف البلاي لست",
    "pl.deleted": "تم حذف البلاي لست",
    "pl.empty_list": "البلاي لست فاضي — أضف أغاني من المفضلة.",
    "artists.favs": "الفنانين المفضلين",
    "settings.spotify": "سبوتيفاي",
    "spotify.link": "ربط حساب سبوتيفاي",
    "spotify.desc": "استورد بلاي لست من سبوتيفاي بلصق رابطه، أو اربط حسابك لاستيراد المفضلة والبلاي لستات تلقائياً.",
    "spotify.import.ph": "ألصق رابط بلاي لست أو ألبوم من سبوتيفاي...",
    "spotify.import.btn": "استيراد",
    "spotify.import.ok": "تم استيراد البلاي لست",
    "spotify.import.fail": "تعذر الاستيراد",
    "spotify.timeout": "انتهت مهلة الاستيراد، حاول مرة أخرى",
    "spotify.linked": "حسابك مرتبط بسبوتيفاي",
    "spotify.unlink": "إلغاء الربط",
    "spotify.unlinked": "تم إلغاء الربط",
    "spotify.imported": "استوردنا من سبوتيفاي ",
    "spotify.ru.label": "أضف هذا الرابط بالضبط في Spotify Dashboard ← Redirect URIs ثم اضغط عليه لنسخه:",
    "account.restore": "استعادة حساب",
    "account.restore.desc": "اكتب يوزرك وكلمة المرور لاسترجاع بروفايلك وأغانيك من أي جهاز أو رابط.",
    "account.restore.ph": "@username",
    "account.restore.btn": "استعادة",
    "account.restore.ok": "تم استعادة الحساب ✓",
    "account.restore.fail": "يوزر أو كلمة سر خطأ",
    "auth.login": "دخول",
    "auth.register": "حساب جديد",
    "auth.pass": "كلمة المرور",
    "auth.pass2": "تأكيد كلمة المرور",
    "auth.create": "إنشاء حساب",
    "auth.logout": "تسجيل خروج",
    "auth.pass.mismatch": "كلمتا السر غير متطابقتين",
    "artist.back": "رجوع",
    "friends.ph": "@يوزر صديقك...",
    "friends.none": "لا توجد نتائج"
  },
  en: {
    "app.title": "Group Music",
    "nav.home": "Home",
    "nav.queue": "Queue",
    "nav.room": "Listening Room",
    "room.add.title": "Add to queue",
    "room.queue.title": "Playlist",
    "room.from.likes": "From your favorites",
    "room.status.in": "You're in room:",
    "room.status.out": "You haven't joined a room yet",
    "room.code.share": "Share this code — friends join your room by entering it",
    "room.code.copied": "Room code copied",
    "settings.sp.cid": "Spotify Client ID",
    "settings.sp.sec": "Spotify Client Secret",
    "settings.sp.save": "Save Spotify credentials",
    "settings.sp.saved_ph": "saved ✓ (leave empty to keep)",
    "settings.sp.need_cid": "Enter the Client ID first",
    "toast.sp.saved": "Spotify credentials saved ✓",
    "nav.likes": "Favorites",
    "nav.settings": "Settings",
    "room.title": "Listening Room",
    "room.available": "Available rooms",
    "room.create": "Create listening room",
    "room.join": "Join",
    "room.leave": "Leave room",
    "room.name": "Room name",
    "room.not_found": "Room not found",
    "rooms.empty": "No rooms available yet.",
    "rooms.locked": "This room requires a password",
    "room.crown": "Room owner",
    "room.kick": "Kick",
    "room.kicked": "You were kicked from the room",
    "room.owner_only": "Only the owner can add songs",
    "room.join_first": "Join a room first to add songs",
    "room.auto_name": "Room",
    "track.addfail": "Couldn't add the song",
    "room.settings.title": "Room settings",
    "room.max": "Max members",
    "room.max.unlimited": "Unlimited",
    "room.pass": "Room password (optional)",
    "room.canadd": "Everyone can add songs",
    "room.join.title": "Join a room",
    "room.join.link": "Paste the room link",
    "room.join.submit": "Enter",
    "room.join.badlink": "Invalid link",
    "room.password.title": "Room password",
    "room.password.label": "Enter the password",
    "room.password.submit": "Enter",
    "room.wrong_password": "Wrong password",
    "room.full": "The room is full",
    "follows.followers": "Followers",
    "follows.following": "Following",
    "follows.empty": "No one yet.",
    "status.online": "Online",
    "status.offline": "Offline",
    "likes.count": "liked songs",
    "share.btn": "Share listening link",
    "me.default": "Anonymous Listener",
    "profile.btn": "View Profile",
    "footer.host": "Runs on the host device",
    "home.title": "Listen to music with your friends",
    "source.youtube": "YouTube",
    "source.soundcloud": "SoundCloud",
    "source.spotify": "Spotify",
    "search.placeholder": "Search for a song or paste a link (YouTube / Spotify / SoundCloud)...",
    "search.btn": "Search",
    "history.title": "Recently Played",
    "history.clear": "Clear History",
    "queue.title": "Queue",
    "shuffle.btn": "Shuffle",
    "likes.title": "Songs I Liked",
    "likes.clear": "Clear favorites",
    "likes.other": "Songs They Liked",
    "np.nothing": "Nothing playing",
    "np.preparing": "⏳ Preparing audio… (slow network, will start automatically)",
    "edit.profile": "Edit Profile",
    "crop.title": "Crop Image",
    "crop.avatar": "Crop Avatar",
    "crop.banner": "Crop Banner",
    "crop.hint": "Move the image and zoom with the mouse to pick the right spot",
    "btn.cancel": "Cancel",
    "btn.confirm": "Confirm",
    "popup.full": "View Full Profile",
    "toast.play.join": "Press play to join the music",
    "toast.play.failed": "This track could not be played. Try again or choose another source.",
    "queue.empty": "Queue is empty — search for a song and add it.",
    "queue.remove": "Remove",
    "added.by": "Added by: ",
    "members.empty": "No listeners yet",
    "track.add": "Add",
    "track.listen": "Listen",
    "track.replay": "Listen again",
    "searching": "Searching...",
    "error.prefix": "Error: ",
    "search.fail": "Search failed",
    "toast.added": "Song added",
    "share.title": "Share listening link",
    "share.body": "Send this link to your friends — whoever opens it joins the room and can add songs.",
    "share.copy": "Copy",
    "share.tip": "The public link works from any network. If no public link shows, make sure cloudflared is installed.",
    "toast.copied": "Link copied",
    "form.user": "Username (@)",
    "user.checking": "Checking username…",
    "user.taken": "This username is already taken",
    "artist.fav": "Favorite artist",
    "artist.faved": "In your favorites",
    "artist.channel": "Songs from the artist's channel",
    "artist.local": "From your history & likes",
    "artist.loading": "Loading channel songs…",
    "artist.none": "No songs found for this artist.",
    "form.name": "Name",
    "form.bio": "Bio",
    "form.avatar": "Avatar image",
    "form.banner": "Banner image",
    "form.color1": "Main color",
    "form.presets": "Gradient presets",
    "form.anim": "Animated banner",
    "form.color2": "Secondary color",
    "btn.save": "Save",
    "toast.saved": "Profile saved",
    "follow.btn": "Follow",
    "unfollow.btn": "Unfollow",
    "follows.remove": "Remove",
    "confirm.clearLikes": "Clear all favorites?",
    "confirm.yes": "Yes, clear",
    "confirm.no": "Cancel",
    "stats.followers": "Followers",
    "stats.following": "Following",
    "stats.likes": "Likes",
    "likes.mine.empty": "You haven't added any favorite songs yet.",
    "likes.other.empty": "No favorite songs.",
    "likes.empty": "No favorites yet.",
    "history.empty": "No listening history yet.",
    "now.label": "Listening now",
    "suggest.title": "Suggested for you",
    "suggest.empty": "No suggestions right now.",
    "artists.title": "Artists",
    "artists.empty": "No artists yet.",
    "artists.click": "Open this artist's songs",
    "artists.songs": "Artist songs",
    "artists.notracks": "No songs for this artist in your history or favorites.",
    "settings.title": "Settings",
    "settings.language": "Language",
    "settings.appearance": "Appearance",
    "settings.miniplayer": "Mini player",
    "settings.hotkey.press": "Press any key… (Esc to cancel)",
    "settings.desktop": "Desktop window (outside browser)",
    "settings.desktop.open": "Open desktop window",
    "settings.desktop.hint": "Floats above all apps, toggle with Alt+M even when the browser is minimized",
    "settings.light": "Light Mode",
    "settings.dark": "Dark Mode",
    "settings.custom": "Custom Mode",
    "settings.custom.color": "Color",
    "btn.shuffle.tip": "Shuffle",
    "btn.play.tip": "Play / Pause",
    "btn.skip.tip": "Skip",
    "btn.prev.tip": "Previous",
    "btn.like.tip": "Add to favorites",
    "btn.loop.tip": "Loop",
    "btn.pl.tip": "Add to playlist",
    "btn.volume.tip": "Mute",
    "pl.title": "Playlists",
    "pl.new": "+ New playlist",
    "pl.create_title": "New playlist",
    "pl.name": "Playlist name",
    "pl.image": "Playlist image",
    "pl.need_name": "Enter a playlist name",
    "pl.empty": "No playlists yet.",
    "pl.play": "Play playlist",
    "pl.addto": "Add to playlist",
    "pl.added": "Song added to playlist",
    "pl.created": "Playlist created",
    "pl.count": "songs",
    "pl.remove": "Remove",
    "pl.delete": "Delete playlist",
    "pl.deleted": "Playlist deleted",
    "pl.empty_list": "This playlist is empty — add songs from your favorites.",
    "artists.favs": "Favorite artists",
    "settings.spotify": "Spotify",
    "spotify.link": "Link Spotify account",
    "spotify.desc": "Import a Spotify playlist by pasting its link, or link your account to import liked songs and playlists automatically.",
    "spotify.import.ph": "Paste a Spotify playlist or album link...",
    "spotify.import.btn": "Import",
    "spotify.import.ok": "Playlist imported",
    "spotify.import.fail": "Import failed",
    "spotify.timeout": "Import timed out, please try again",
    "spotify.linked": "Spotify account linked",
    "spotify.unlink": "Unlink",
    "spotify.unlinked": "Unlinked",
    "spotify.imported": "Imported from Spotify ",
    "spotify.ru.label": "Add exactly this URL in Spotify Dashboard → Redirect URIs (click to copy):",
    "account.restore": "Restore account",
    "account.restore.desc": "Enter your username and password to recover your profile and liked songs on any device.",
    "account.restore.ph": "@username",
    "account.restore.btn": "Restore",
    "account.restore.ok": "Account restored ✓",
    "account.restore.fail": "Wrong username or password",
    "auth.login": "Log in",
    "auth.register": "Create account",
    "auth.pass": "Password",
    "auth.pass2": "Confirm password",
    "auth.create": "Create account",
    "auth.logout": "Log out",
    "auth.pass.mismatch": "Passwords don't match",
    "artist.back": "Back",
    "friends.ph": "@friend username...",
    "friends.none": "No results"
  }
};

function t(key) {
  return (I18N[settings.lang] && I18N[settings.lang][key]) ||
         I18N.ar[key] || I18N.en[key] || key;
}

function customPalette(color) {
  const hex = /^#[0-9a-f]{6}$/i.test(color || "") ? color : "#1db954";
  const rgb = [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16));
  const tint = (base, amount) => "#" + rgb.map(c => Math.round(base * (1 - amount) + c * amount).toString(16).padStart(2, "0")).join("");
  return {"--bg":tint(13,.09), "--bg2":tint(21,.12), "--bg3":tint(31,.16), "--bg4":tint(43,.20), "--border":tint(72,.25), "--fg":"#f5f5f5", "--muted":"#ccd0d4"};
}

function applyTheme() {
  const root = document.documentElement;
  root.setAttribute("data-theme", settings.theme);
  const palette = customPalette(settings.accent);
  Object.keys(palette).forEach(key => {
    if (settings.theme === "custom") root.style.setProperty(key, palette[key]);
    else root.style.removeProperty(key);
  });
  if (settings.theme === "glass") {
    // الوضع الزجاجي: لون تحكم خاص فيه (مثل المخصص) بس الخلفية تبقى زجاج
    const g = settings.glassAccent || "#8ab4ff";
    root.style.setProperty("--accent", g);
    root.style.setProperty("--accent-hover", lighten(g, 0.15));
    root.style.setProperty("--accent2", g);
    root.style.setProperty("--on-accent", contrastText(g));
    root.style.setProperty("--glass-tint", g);
    // اللون يلوّن التطبيق كامل — الخلفية نفسها بنفس لون المستخدم بالضبط
    const rgb = [1, 3, 5].map(i => parseInt((/^#[0-9a-f]{6}$/i.test(g) ? g : "#8ab4ff").slice(i, i + 2), 16));
    const mix = (base, amt) => rgb.map(c => Math.round(base * (1 - amt) + c * amt));
    const rgba = (c, a) => "rgba(" + c.join(",") + "," + Math.max(0, Math.min(1, a)).toFixed(3) + ")";
    // الشفافية (0..100): 0 = اللون صلب بالضبط، 100 = شفاف جداً بس اللون يبقى (30%)
    const trans = 100 - Math.max(5, Math.min(95, +settings.glassOpacity || 35));
    const tt = trans / 100;
    const winAlpha = 1 - 0.7 * tt;
    const blur = Math.max(0, Math.min(100, settings.glassBlur == null ? 75 : +settings.glassBlur));
    const grain = Math.max(0, Math.min(100, settings.glassGrain == null ? 20 : +settings.glassGrain));
    const lum = (0.299 * rgb[0] + 0.587 * rgb[1] + 0.114 * rgb[2]) / 255;
    const lightColor = lum > 0.62 && tt < 0.5;          // لون فاتح ومعتم → نص غامق عشان يبين
    root.style.setProperty("--fg", lightColor ? "#111418" : "#f4f6f8");
    root.style.setProperty("--muted", lightColor ? "#3b4249" : "#c3cad1");
    const shade = lightColor ? 0 : 0;                   // الألواح أغمق شوي من الخلفية عشان تنفصل عنها
    root.style.setProperty("--glass-color", rgba(rgb, 1));
    root.style.setProperty("--glass-back", rgba(rgb, winAlpha));
    root.style.setProperty("--glass-panel", rgba(mix(shade, 0.78), 0.10 + 0.40 * (1 - tt)));
    root.style.setProperty("--glass-card", rgba(mix(shade, 0.72), 0.12 + 0.40 * (1 - tt)));
    root.style.setProperty("--glass-deep", rgba(mix(shade, 0.70), 0.92));
    root.style.setProperty("--glass-op", String(winAlpha));
    root.style.setProperty("--glass-blur", (blur * 0.8).toFixed(1) + "px");      // 100% = 80px
    root.style.setProperty("--glass-panel-blur", (blur * 0.6).toFixed(1) + "px");
    root.style.setProperty("--glass-grain", (grain / 100).toFixed(2));
    root.style.setProperty("--bg2", rgba(mix(lightColor ? 255 : 0, 0.80), 0.45));
    root.style.setProperty("--bg3", rgba(mix(lightColor ? 255 : 30, 0.70), 0.55));
    root.style.setProperty("--bg4", rgba(mix(lightColor ? 255 : 50, 0.65), 0.6));
    root.style.setProperty("--border", lightColor ? "rgba(0,0,0,.14)" : "rgba(255,255,255,.12)");
    // نرسل اللون والشفافية والضبابية لنافذة ويندوز (glass-theme.js)
    try { window.dispatchEvent(new CustomEvent("harmoni-glass", { detail: { color: g, alpha: winAlpha, blur: blur } })); } catch (e) {}
  } else {
    ["--glass-tint", "--glass-color", "--glass-panel", "--glass-card", "--glass-deep", "--glass-op", "--glass-back", "--glass-blur", "--glass-panel-blur", "--glass-grain"].forEach(k => root.style.removeProperty(k));
    if (settings.theme !== "custom") ["--fg", "--muted"].forEach(k => root.style.removeProperty(k));
    if (settings.theme !== "custom") ["--bg2", "--bg3", "--bg4", "--border"].forEach(k => root.style.removeProperty(k));
  }
  if (settings.theme === "custom") {
    const a = settings.accent || "#1db954";
    root.style.setProperty("--accent", a);
    root.style.setProperty("--accent-hover", lighten(a, 0.15));
    root.style.setProperty("--accent2", a);
    root.style.setProperty("--on-accent", contrastText(a));
  }
}

function restoreAccent() {
  applyTheme();
  if (settings.theme !== "custom" && settings.theme !== "glass") applyColors(myProfile.colors);
}

function renderSettings() {
  const body = document.getElementById("settings-body");
  body.innerHTML = "";

  if (window.harmoniDiscordSettings) window.harmoniDiscordSettings(body);

  const langGroup = document.createElement("div");
  langGroup.className = "settings-group";
  langGroup.dataset.key = "lang";
  const langHead = document.createElement("div");
  langHead.className = "settings-head";
  langHead.textContent = t("settings.language");
  langGroup.appendChild(langHead);
  const langOpts = document.createElement("div");
  langOpts.className = "settings-options";
  [["ar", "العربية"], ["en", "English"]].forEach(([val, label]) => {
    const b = document.createElement("button");
    b.className = "opt-btn" + (settings.lang === val ? " active" : "");
    b.dataset.langOpt = val;
    b.textContent = label;
    b.onclick = () => { settings.lang = val; saveSettings(); applyLang(); };
    langOpts.appendChild(b);
  });
  langGroup.appendChild(langOpts);
  body.appendChild(langGroup);

  const themeGroup = document.createElement("div");
  themeGroup.className = "settings-group";
  themeGroup.dataset.key = "theme";
  const themeHead = document.createElement("div");
  themeHead.className = "settings-head";
  themeHead.textContent = t("settings.appearance");
  themeGroup.appendChild(themeHead);
  const themeOpts = document.createElement("div");
  themeOpts.className = "settings-options";
  [["light", t("settings.light")], ["dark", t("settings.dark")], ["glass", L("الوضع الزجاجي", "Glass Mode")], ["custom", t("settings.custom")]].forEach(([val, label]) => {
    const b = document.createElement("button");
    b.className = "opt-btn" + (settings.theme === val ? " active" : "");
    b.dataset.themeOpt = val;
    b.textContent = label;
    b.onclick = () => {
      settings.theme = val;
      saveSettings();
      restoreAccent();
      renderSettings();
    };
    themeOpts.appendChild(b);
  });
  themeGroup.appendChild(themeOpts);

  const colorRow = document.createElement("div");
  const glassMode = settings.theme === "glass";
  colorRow.className = "custom-color-row" + (settings.theme === "custom" || glassMode ? "" : " hidden");
  const cl = document.createElement("label");
  cl.textContent = glassMode ? L("لون الوضع الزجاجي", "Glass mode color") : t("settings.custom.color");
  const ci = document.createElement("input");
  ci.type = "color";
  ci.id = "custom-accent";
  ci.value = glassMode ? (settings.glassAccent || "#8ab4ff") : (settings.accent || "#1db954");
  ci.oninput = () => {
    if (glassMode) settings.glassAccent = ci.value; else settings.accent = ci.value;
    saveSettings();
    applyTheme();
  };
  colorRow.appendChild(cl);
  colorRow.appendChild(ci);
  themeGroup.appendChild(colorRow);
  if (glassMode) {
    // أشرطة الوضع الزجاجي: الشفافية + الضبابية + الـ Grain
    const slider = (label, get, set, fmtv) => {
      const row = document.createElement("div");
      row.className = "glass-op-row";
      const lab = document.createElement("label");
      lab.textContent = label;
      const val = document.createElement("b");
      const rng = document.createElement("input");
      rng.type = "range"; rng.min = "0"; rng.max = "100"; rng.step = "1";
      rng.value = String(get());
      const show = () => { val.textContent = (fmtv ? fmtv(+rng.value) : rng.value) + "%"; rng.style.setProperty("--p", rng.value + "%"); };
      rng.oninput = () => { set(+rng.value); show(); applyTheme(); };
      rng.onchange = () => saveSettings();
      show();
      row.appendChild(lab); row.appendChild(rng); row.appendChild(val);
      themeGroup.appendChild(row);
    };
    // الشفافية: يمين = أشفّ (القيمة المحفوظة = عتامة الألواح 5..95)
    slider(L("الشفافية", "Transparency"),
      () => 100 - Math.max(5, Math.min(95, +settings.glassOpacity || 35)),
      v => { settings.glassOpacity = Math.max(5, Math.min(95, 100 - v)); });
    slider(L("الضبابية", "Blur"),
      () => (settings.glassBlur == null ? 75 : +settings.glassBlur),
      v => { settings.glassBlur = v; });
    slider("Grain",
      () => (settings.glassGrain == null ? 20 : +settings.glassGrain),
      v => { settings.glassGrain = v; });
  }

  body.appendChild(themeGroup);

  const hkGroup = document.createElement("div");
  hkGroup.className = "settings-group";
  hkGroup.dataset.key = "hotkey";
  const hkHead = document.createElement("div");
  hkHead.className = "settings-head";
  hkHead.textContent = t("settings.miniplayer");
  hkGroup.appendChild(hkHead);
  const hkOpts = document.createElement("div");
  hkOpts.className = "settings-options";
  const hkBtn = document.createElement("button");
  hkBtn.className = "opt-btn" + (mpCaptureNext ? " active" : "");
  hkBtn.textContent = mpCaptureNext ? t("settings.hotkey.press") : hotkeyLabel(getHotkey());
  hkBtn.onclick = () => { mpCaptureNext = true; renderSettings(); };
  hkOpts.appendChild(hkBtn);
  hkGroup.appendChild(hkOpts);
  body.appendChild(hkGroup);

  const dtGroup = document.createElement("div");
  dtGroup.className = "settings-group";
  dtGroup.dataset.key = "desktop";
  const dtHead = document.createElement("div");
  dtHead.className = "settings-head";
  dtHead.textContent = t("settings.desktop");
  dtGroup.appendChild(dtHead);
  const dtHint = document.createElement("div");
  dtHint.className = "sp-link-desc";
  dtHint.textContent = t("settings.desktop.hint");
  dtGroup.appendChild(dtHint);
  const dtOpts = document.createElement("div");
  dtOpts.className = "settings-options";
  const dtBtn = document.createElement("button");
  dtBtn.className = "opt-btn";
  dtBtn.textContent = t("settings.desktop.open");
  dtBtn.onclick = async () => {
    try {
      await fetch("/api/open_mini", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ uid: getUid() }),
      });
      toast(t("settings.desktop.open") + " ✓");
    } catch (e) {}
  };
  dtOpts.appendChild(dtBtn);
  dtGroup.appendChild(dtOpts);
  body.appendChild(dtGroup);

  const accGroup = document.createElement("div");
  accGroup.className = "settings-group";
  accGroup.dataset.key = "account";
  const accHead = document.createElement("div");
  accHead.className = "settings-head";
  accHead.textContent = t("account.restore");
  accGroup.appendChild(accHead);
  const accDesc = document.createElement("div");
  accDesc.className = "sp-link-desc";
  accDesc.textContent = t("account.restore.desc");
  accGroup.appendChild(accDesc);
  const accRow = document.createElement("div");
  accRow.className = "sp-import-row";
  accRow.style.flexDirection = "column";
  accRow.style.alignItems = "stretch";
  const accInput = document.createElement("input");
  accInput.className = "search-input";
  accInput.id = "acc-restore";
  accInput.dir = "ltr";
  accInput.placeholder = t("account.restore.ph");
  accInput.style.marginBottom = "8px";
  const accPass = document.createElement("input");
  accPass.type = "password";
  accPass.className = "search-input";
  accPass.id = "acc-restore-pass";
  accPass.dir = "ltr";
  accPass.placeholder = t("auth.pass");
  accPass.style.marginBottom = "0";
  const accBtn = document.createElement("button");
  accBtn.className = "btn btn-primary";
  accBtn.textContent = t("account.restore.btn");
  accBtn.onclick = () => loginAccount(accInput.value, accPass.value, null);
  accRow.appendChild(accInput);
  accRow.appendChild(accPass);
  accRow.appendChild(accBtn);
  accGroup.appendChild(accRow);
  const logoutBtn = document.createElement("button");
  logoutBtn.className = "btn btn-block";
  logoutBtn.style.marginTop = "10px";
  logoutBtn.textContent = t("auth.logout");
  logoutBtn.onclick = () => {
    ["mu_uid", "mu_profile", "mu_likes", "mu_following", "mu_sp_linked"].forEach(k => {
      localStorage.removeItem(k);
      try { localStorage.removeItem("mu_" + k); } catch (e) {}
    });
    try { localStorage.removeItem("mu_authed"); } catch (e) {}
    location.reload();
  };
  accGroup.appendChild(logoutBtn);
  body.appendChild(accGroup);

  const spGroup = document.createElement("div");
  spGroup.className = "settings-group";
  spGroup.dataset.key = "spotify";
  const spHead = document.createElement("div");
  spHead.className = "settings-head";
  spHead.textContent = t("settings.spotify");
  spGroup.appendChild(spHead);
  const spDesc = document.createElement("div");
  spDesc.className = "sp-link-desc";
  spDesc.textContent = t("spotify.desc") + L(" يتحدث الحساب المرتبط تلقائيًا كل خمس دقائق أثناء فتح التطبيق.", " Linked accounts refresh every five minutes while the app is open.");
  spGroup.appendChild(spDesc);

  const ruRow = document.createElement("div");
  ruRow.className = "sp-ru-row";
  const ruLabel = document.createElement("div");
  ruLabel.className = "sp-ru-label";
  ruLabel.textContent = t("spotify.ru.label");
  const ruBox = document.createElement("div");
  ruBox.className = "sp-ru-box";
  ruBox.dir = "ltr";
  ruBox.textContent = "...";
  ruBox.title = t("toast.copied");
  ruBox.onclick = () => {
    navigator.clipboard.writeText(ruBox.textContent).then(() => toast(t("toast.copied")));
  };
  ruRow.appendChild(ruLabel);
  ruRow.appendChild(ruBox);
  spGroup.appendChild(ruRow);
  fetch("/api/spotify_redirect_uri").then(r => r.json()).then(j => {
    if (j.redirect_uri) ruBox.textContent = j.redirect_uri;
  }).catch(() => {});

  const credRow = document.createElement("div");
  credRow.className = "sp-cred-row";
  credRow.innerHTML =
    '<div class="sp-cred-grid">' +
    '<label>' + esc(t("settings.sp.cid")) + '</label><input id="sp-cid" class="search-input" dir="ltr" autocomplete="off">' +
    '<label>' + esc(t("settings.sp.sec")) + '</label><input id="sp-sec" class="search-input" dir="ltr" type="password" autocomplete="new-password" placeholder="••••••••">' +
    '</div>';
  spGroup.appendChild(credRow);
  const cidInput = credRow.querySelector("#sp-cid");
  const secInput = credRow.querySelector("#sp-sec");
  let _spCredTimer = null;
  const spCredAuto = () => {
    clearTimeout(_spCredTimer);
    _spCredTimer = setTimeout(async () => {
      const cid = cidInput.value.trim();
      const sec = secInput.value.trim();
      if (!cid) return;
      try {
        const res = await fetch("/api/spotify_config", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ client_id: cid, client_secret: sec })
        });
        const j = await res.json();
        if (j.ok) {
          secInput.value = "";
          secInput.placeholder = t("settings.sp.saved_ph");
          toast(t("toast.sp.saved"));
        } else toast(j.error || t("search.fail"));
      } catch (e) {}
    }, 800);
  };
  cidInput.addEventListener("input", spCredAuto);
  secInput.addEventListener("input", spCredAuto);
  fetch("/api/spotify_config").then(r => r.json()).then(j => {
    if (j.client_id && cidInput) cidInput.value = j.client_id;
    if (j.has_secret && secInput) secInput.placeholder = t("settings.sp.saved_ph");
  }).catch(() => {});

  const impRow = document.createElement("div");
  impRow.className = "sp-import-row";
  const impInput = document.createElement("input");
  impInput.className = "search-input";
  impInput.id = "sp-import-url";
  impInput.dir = "ltr";
  impInput.placeholder = t("spotify.import.ph");
  const impBtn = document.createElement("button");
  impBtn.className = "btn btn-primary";
  impBtn.textContent = t("spotify.import.btn");
  impBtn.onclick = async () => {
    const url = impInput.value.trim();
    if (!url) return;
    impBtn.disabled = true;
    try {
      const res = await fetch("/api/spotify_import", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ url })
      });
      const j = await res.json();
      if (j.error || !j.name) {
        toast((t("spotify.import.fail") + ": " + (j.error || "")) || t("spotify.import.fail"));
      } else {
        const pls = getPlaylists();
        pls.unshift({ id: plId(), name: j.name, img: j.img || "", tracks: j.tracks || [], created: Date.now() });
        savePlaylists(pls);
        toast(t("spotify.import.ok") + " — " + j.name + " (" + (j.tracks || []).length + ")");
        if (j.note) setTimeout(() => toast(j.note), 2400);
        renderProfileSections(true);
      }
    } catch (e) {
      toast(t("spotify.import.fail"));
    }
    impBtn.disabled = false;
  };
  impRow.appendChild(impInput);
  impRow.appendChild(impBtn);
  spGroup.appendChild(impRow);

  let spLinked = null;
  try { spLinked = JSON.parse(localStorage.getItem("mu_sp_linked") || "null"); } catch (e) {}
  const spOpts = document.createElement("div");
  spOpts.className = "settings-options";
  if (spLinked) {
    const linkedBadge = document.createElement("div");
    linkedBadge.className = "sp-linked-badge";
    linkedBadge.textContent = "✓ " + t("spotify.linked") + (spLinked.user ? " (" + spLinked.user + ")" : "");
    if (!spLinked.sync_key) linkedBadge.textContent += L(" — ألغِ الربط ثم اربطه مرة أخرى لتفعيل التحديث التلقائي", " — Unlink and reconnect once to enable automatic refresh");
    spOpts.appendChild(linkedBadge);
    const unlinkBtn = document.createElement("button");
    unlinkBtn.className = "opt-btn";
    unlinkBtn.textContent = t("spotify.unlink");
    unlinkBtn.onclick = async () => {
      if (spLinked.sync_key) {
        unlinkBtn.disabled = true;
        try {
          const response = await fetchWithTimeout("/api/spotify_unlink", {method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({sync_key:spLinked.sync_key})});
          if (!response.ok) throw new Error();
        } catch (e) { unlinkBtn.disabled = false; toast(L("تعذر إلغاء الربط، حاول مرة أخرى", "Could not unlink. Please retry.")); return; }
      }
      localStorage.removeItem("mu_sp_linked");
      renderSettings();
      toast(t("spotify.unlinked"));
    };
    spOpts.appendChild(unlinkBtn);
  } else {
    const spBtn = document.createElement("button");
    spBtn.className = "opt-btn spotify-btn";
    spBtn.textContent = t("spotify.link");
    spBtn.onclick = () => {
      location.href = "/api/spotify/auth?uid=" + encodeURIComponent(getUid()) +
        "&origin=" + encodeURIComponent(location.origin);
    };
    spOpts.appendChild(spBtn);
  }
  spGroup.appendChild(spOpts);
  body.appendChild(spGroup);
}

function applyLang() {
  document.documentElement.lang = settings.lang;
  document.documentElement.dir = settings.lang === "ar" ? "rtl" : "ltr";
  document.querySelectorAll("[data-i18n]").forEach(el => {
    el.textContent = t(el.getAttribute("data-i18n"));
  });
  document.querySelectorAll("[data-i18n-placeholder]").forEach(el => {
    el.setAttribute("placeholder", t(el.getAttribute("data-i18n-placeholder")));
  });
  document.querySelectorAll("[data-i18n-title]").forEach(el => {
    el.setAttribute("title", t(el.getAttribute("data-i18n-title")));
  });
  renderSettings();
  renderQueue();
  renderMembers();
  renderMe();
  renderNowPlaying();
  renderHistory();
  renderLikes();
  renderSuggestions();
  try { syncSearchFilters(); renderOpenedPlaylistBar(); } catch (e) {}
  if (document.getElementById("view-profile").classList.contains("active")) renderProfile();
}

/* ================= الحالة والاتصال ================= */
let state = { queue: [], current: null, playing: false, position: 0, updated_at: 0, shuffle: false, members: [] };
let currentTrackId = null;
let hls = null;
let seekDragging = false;
let autoplayBlocked = false;
let viewingProfile = null;
let popupPerson = null;
let popupAnchor = null;
let myProfile = getProfile();
let myRoomId = (new URLSearchParams(location.search)).get("room") || null;
let inRoom = false;
let roomPassword = "";
let profileRegistry = [];

const socket = io();
const audio = document.getElementById("audio");
const soloAudio = document.getElementById("solo-audio");
let solo = { active: false, track: null, hls: null };
let _soloRepTimer = null;
function reportSoloState() {
  try {
    const body = (solo.active && solo.track)
      ? { uid: getUid(), track: { title: solo.track.title, artist: solo.track.artist, thumbnail: solo.track.thumbnail, duration: solo.track.duration }, playing: !soloAudio.paused, position: soloAudio.currentTime || 0 }
      : { uid: getUid(), track: null };
    fetch("/api/solo_state", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) }).catch(() => {});
  } catch (e) {}
}
function startSoloReporting() {
  clearInterval(_soloRepTimer);
  reportSoloState();
  _soloRepTimer = setInterval(reportSoloState, 4000);
}
function stopSoloReporting() {
  clearInterval(_soloRepTimer);
  _soloRepTimer = null;
  reportSoloState();
}
function destroySoloHls() { if (solo.hls) { try { solo.hls.destroy(); } catch (e) {} solo.hls = null; } }
let soloRequestSeq = 0;
function loadSoloTrack(track) {
  const usePrimedAudio = !solo.active;
  solo.active = true;
  solo.track = track;
  destroySoloHls();
  try { audio.pause(); } catch (e) {}
  soloAudio.pause();
  const url = track && track.stream_url;
  if (!url) { soloAudio.removeAttribute("src"); renderNowPlaying(); return; }
  // نفرّغ المشغل تماماً قبل الأغنية الجديدة عشان ما يطلع صوت من الأغنية اللي قبلها
  const nextSrc = /\.m3u8($|\?)/i.test(url) ? "" : soloPlayableUrl(url);
  if (!(usePrimedAudio && nextSrc && soloAudio.getAttribute("src") === nextSrc)) {
    soloAudio.removeAttribute("src");
    try { soloAudio.load(); } catch (e) {}
  }
  if (/\.m3u8($|\?)/i.test(url) && window.Hls && Hls.isSupported()) {
    solo.hls = new Hls({ xhrSetup: (x, u) => { try { x.open("GET", soloPlayableUrl(u), true); } catch (e) {} } });
    solo.hls.loadSource(url);
    solo.hls.attachMedia(soloAudio);
  } else {
    const playable = soloPlayableUrl(url);
    if (!usePrimedAudio || soloAudio.getAttribute("src") !== playable) soloAudio.src = playable;
  }
  soloAudio.volume = audio.volume;
  soloAudio.muted = audio.muted;
  soloAudio.play().then(() => {}).catch(() => toast(t("toast.play.join")));
  startSoloReporting();
  renderNowPlaying();
  setTimeout(() => { if (solo.track === track) prefetchSoloNeighbor(track); }, 2000);
}
function prefetchSoloNeighbor(cur) {
  try {
    const pool = soloPool().filter(x => x && x.web_url);
    if (!pool.length) return;
    let i = cur ? pool.findIndex(x => (x.id && cur.id && x.id === cur.id) || x.title === cur.title) : -1;
    const nx = pool[(i + 1 + pool.length) % pool.length];
    if (nx) prepareSoloTrack(nx).catch(() => {});
  } catch (e) {}
}
function exitSolo() {
  ++soloRequestSeq;
  solo.active = false;
  destroySoloHls();
  try { soloAudio.pause(); } catch (e) {}
  soloAudio.removeAttribute("src");
  solo.track = null;
  stopSoloReporting();
  if (inRoom && state.current) { loadTrack(state.current); syncPlayback(); }
  renderNowPlaying();
}
function soloPlayableUrl(u) {
  if (!u) return u;
  if (/\.m3u8($|\?)/i.test(u)) return u;
  if (u.startsWith("/")) return u;
  return "/api/audio_proxy?url=" + encodeURIComponent(u);
}
function streamFresh(t) {
  return !!(t && t.stream_url && t.stream_ts && (Date.now() - t.stream_ts < 18 * 60 * 1000));
}
const preparedStreams = new Map();
const pendingStreams = new Map();
function showPendingTrack(trk) {
  // ردة فعل فورية: تظهر الأغنية بالمشغل مع مؤشر تحميل لين يجهز الصوت
  try {
    const th = document.getElementById("np-thumb");
    if (th) { if (trk.thumbnail) th.src = trk.thumbnail; else th.removeAttribute("src"); }
    const ti = document.getElementById("np-title"); if (ti) ti.textContent = trk.title || "";
    const ar = document.getElementById("np-artist"); if (ar) ar.textContent = trk.artist || "";
    document.body.classList.add("np-loading");
  } catch (e) {}
}
function preparedTrack(trk) {
  if (streamFresh(trk)) return trk;
  const hit = preparedStreams.get(trackKey(trk));
  if (!streamFresh(hit)) return null;
  Object.assign(trk, hit);
  return trk;
}
function prepareSoloTrack(trk, priority = false) {
  if (preparedTrack(trk)) return Promise.resolve(trk);
  const key = trackKey(trk);
  let pending = pendingStreams.get(key);
  if (!pending) {
    if (!priority && pendingStreams.size >= 3) return Promise.resolve(null);
    pending = fetchWithTimeout("/api/resolve_stream", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ web_url: trk.web_url, source: trk.source, title: trk.title, artist: trk.artist })
    }).then(r => r.json()).then(j => {
      if (!j.stream_url) { const err = new Error("Stream unavailable"); err.ytdlp = j.hint === "ytdlp"; throw err; }
      const ready = { stream_url: j.stream_url, stream_ts: Date.now() };
      preparedStreams.set(key, ready);
      while (preparedStreams.size > 128) preparedStreams.delete(preparedStreams.keys().next().value);
      return ready;
    }).finally(() => pendingStreams.delete(key));
    pendingStreams.set(key, pending);
  }
  return pending.then(ready => { Object.assign(trk, ready); return trk; });
}
function primeSoloAudio(trk) {
  if (!trk || solo.active || inRoom || !streamFresh(trk)) return;
  // Buffer only one likely selection, without autoplay or touching active music.
  if (/\.m3u8($|\?)/i.test(trk.stream_url)) return;
  const url = soloPlayableUrl(trk.stream_url);
  if (soloAudio.getAttribute("src") === url) return;
  soloAudio.preload = "auto";
  soloAudio.src = url;
  soloAudio.load();
}
function playSolo(trk) {
  if (!trk) return;
  const seq = ++soloRequestSeq;
  if (preparedTrack(trk)) { loadSoloTrack(trk); addHistory(trk); return; }
  // نوقف الأغنية الحالية فوراً لين تجهز الجديدة (ما تكمل القديمة وتتداخل)
  const sa = typeof soloAudio !== "undefined" ? soloAudio : null;
  const ra = typeof audio !== "undefined" ? audio : null;
  const resumeOld = (solo.active && sa && !sa.paused) ? sa : (ra && !ra.paused ? ra : null);
  try { if (sa) sa.pause(); if (ra) ra.pause(); } catch (e) {}
  const npLoading = on => { try { document.body.classList.toggle("np-loading", on); } catch (e) {} };
  try { if (typeof showPendingTrack === "function") showPendingTrack(trk); } catch (e) {}
  prepareSoloTrack(trk, true).then(() => {
    if (seq !== soloRequestSeq) return;
    npLoading(false);
    loadSoloTrack(trk); addHistory(trk);
  }).catch(err => {
    if (seq !== soloRequestSeq) return;
    npLoading(false);
    if (resumeOld) resumeOld.play().catch(() => {});
    try { renderNowPlaying(); } catch (e) {}
    toast(err && err.ytdlp ? L("يوتيوب غيّر شي في موقعه — شغّل ملف update-ytdlp.bat عشان يرجع التشغيل", "YouTube changed something — run update-ytdlp.bat to fix playback") : t("toast.play.failed"));
  });
}
function prefetchOnServer(list, n) {
  try {
    const tracks = (list || []).filter(x => x && x.web_url && x.kind !== "playlist").slice(0, n || 8)
      .map(x => ({ web_url: x.web_url, source: x.source, title: x.title, artist: x.artist }));
    if (!tracks.length) return;
    fetch("/api/prefetch", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ tracks }) }).catch(() => {});
  } catch (e) {}
}
function cacheAudioOnDisk(list) {
  try {
    const tracks = (list || []).filter(x => x && x.web_url).slice(0, 60).map(x => ({ web_url: x.web_url }));
    if (!tracks.length) return;
    fetch("/api/cache_audio", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ tracks }) }).catch(() => {});
  } catch (e) {}
}
function preResolve(results) {
  prefetchOnServer(results, 8);
  (results || []).slice(0, 2).forEach((trk, index) => {
    prepareSoloTrack(trk).then(ready => {
      if (index === 0) primeSoloAudio(ready);
    }).catch(() => {});
  });
}
setInterval(() => {
  if (!solo.active) return;
  const u = localStorage.getItem("mu_uid") || "";
  if (!u) return;
  fetch("/api/solo_cmd?uid=" + encodeURIComponent(u))
    .then(r => r.json())
    .then(j => {
      if (!j || !j.cmd) return;
      const a = soloAudio;
      if (j.cmd === "play_pause") { a.paused ? a.play().catch(() => {}) : a.pause(); }
      else if (j.cmd === "skip") soloSkip(1);
      else if (j.cmd === "prev") soloSkip(-1);
      else if (j.cmd === "seek" && typeof j.pos === "number") { try { a.currentTime = j.pos; } catch (e) {} }
    }).catch(() => {});
}, 900);

soloAudio.addEventListener("ended", () => {
  if (!solo.active) return;
  const plPool = (window.__plPlaying && Array.isArray(window.__plPool) && solo.track &&
    window.__plPool.some(x => trackKey(x) === trackKey(solo.track))) ? window.__plPool : null;
  const pool = plPool || ((Array.isArray(lastResults) && lastResults.length &&
    lastResults.some(x => solo.track && ((x.web_url && x.web_url === solo.track.web_url) || (x.title && x.title === solo.track.title))))
    ? lastResults : soloPool());
  const cur = solo.track;
  let i = cur ? pool.findIndex(x => (x.web_url && cur.web_url && x.web_url === cur.web_url) || (x.id && cur.id && x.id === cur.id) || x.title === cur.title) : -1;
  if (i === -1) { exitSolo(); return; }
  playSolo(pool[(i + 1 + pool.length) % pool.length]);
});
soloAudio.addEventListener("play", () => { if (solo.active) reportSoloState(); });
soloAudio.addEventListener("pause", () => { if (solo.active) reportSoloState(); });

async function loadProfiles() {
  try {
    const r = await fetch("/api/profiles");
    profileRegistry = ((await r.json()).profiles) || [];
  } catch (e) {
    profileRegistry = [];
  }
}

async function loadRooms() {
  try {
    const r = await fetch("/api/rooms");
    renderRoomList(((await r.json()).rooms) || []);
  } catch (e) {}
}

function renderRoomList(rooms) {
  const el = document.getElementById("room-list");
  if (!el) return;
  if (!rooms.length) {
    el.innerHTML = '<div class="room-empty">' + t("rooms.empty") + '</div>';
    return;
  }
  el.innerHTML = rooms.map(r => {
    const active = r.id === myRoomId;
    return '<div class="room-item' + (active ? " active" : "") + '" data-rid="' + esc(r.id) + '">' +
      '<span class="room-item-name">' + esc(r.name) + '</span>' +
      (r.has_password ? '<span class="room-lock" data-i18n-title="rooms.locked">🔒</span>' : "") +
      '<span class="room-item-meta">' + r.members + "/" + (r.max_members || "∞") + '</span>' +
      "</div>";
  }).join("");
  el.querySelectorAll(".room-item").forEach(item => {
    item.onclick = () => joinRoom(item.dataset.rid);
  });
}

function renderRoomArea() {
  const members = document.getElementById("members");
  const leaveBtn = document.getElementById("leave-room-btn");
  if (leaveBtn) leaveBtn.classList.toggle("hidden", !inRoom);
  const mbox = document.querySelector(".room-members-box");
  if (mbox) mbox.classList.toggle("hidden", !inRoom);
  const qbox = document.querySelector(".room-queue-box");
  if (qbox) qbox.classList.toggle("hidden", !inRoom);
  const empty = document.getElementById("members-empty");
  if (empty) empty.classList.toggle("hidden", inRoom);
  const rs = document.getElementById("room-status");
  if (rs) {
    const rname = ((document.getElementById("room-name-title") || {}).textContent || "").trim();
    rs.textContent = inRoom ? ("🟢 " + t("room.status.in") + (rname ? " «" + rname + "»" : "")) : ("⚪ " + t("room.status.out"));
  }
  const cb = document.getElementById("room-code-badge");
  if (cb) {
    cb.classList.toggle("hidden", !inRoom);
    if (inRoom && state.rid) {
      cb.textContent = "🔑 الكود: " + state.rid;
      cb.onclick = () => {
        navigator.clipboard.writeText(state.rid).then(() => toast(t("toast.copied")));
      };
    } else {
      cb.onclick = null;
    }
  }
  loadRooms();
}

function joinRoom(rid) {
  myRoomId = rid;
  roomPassword = "";
  emitJoin();
}

function leaveRoom() {
  socket.emit("leave");
  myRoomId = null;
  roomPassword = "";
  inRoom = false;
  state = { queue: [], current: null, playing: false, position: 0, updated_at: 0, shuffle: false, members: [] };
  renderQueue();
  renderMembers();
  renderMe();
  renderNowPlaying();
  renderRoomArea();
  loadRooms();
}

function profilePayload() {
  return {
    uid: getUid(),
    name: myProfile.name,
    username: myProfile.username || "",
    avatar: myProfile.avatar,
    banner: myProfile.banner,
    bio: myProfile.bio,
    colors: myProfile.colors,
    likes: getLikes(),
    following: getFollowing(),
    token: getToken(),
  };
}

function emitJoin() {
  const payload = profilePayload();
  payload.room = myRoomId || "";
  payload.password = roomPassword;
  socket.emit("join", payload);
}

function autoCreateRoom() {
  const payload = profilePayload();
  payload.room_name = t("room.auto_name") + " " + (myProfile.name || t("me.default"));
  payload.max_members = 0;
  payload.password = "";
  payload.can_add = true;
  payload.auto = true;
  socket.emit("create_room", payload);
}

socket.on("connect", () => {
  loadProfiles();
  if (myRoomId && !(state && state.personal)) emitJoin();
  else autoCreateRoom();
  renderRoomArea();
});
socket.on("state", s => applyState(s));
socket.on("join_error", d => onJoinError((d || {}).reason));
socket.on("add_error", d => toast((d || {}).error || t("track.addfail")));
socket.on("add_ok", () => toast(t("toast.added")));
socket.on("leave_ok", () => {
  myRoomId = null;
  inRoom = false;
  loadRooms();
  renderRoomArea();
});
socket.on("kicked", () => {
  socket.io.opts.reconnection = false;
  myRoomId = null;
  inRoom = false;
  showModal(t("room.kicked"), '<div class="msg">' + t("room.kicked") + '</div>');
});

function onJoinError(reason) {
  if (reason === "need_password" || reason === "wrong_password") {
    showModal(t("room.password.title"),
      '<div class="form-row"><label>' + t("room.password.label") + '</label><input id="join-pass" type="text" value="' + esc(roomPassword) + '"></div>' +
      (reason === "wrong_password" ? '<div class="msg">' + t("room.wrong_password") + '</div>' : "") +
      '<button id="join-pass-submit" class="btn btn-primary btn-block">' + t("room.password.submit") + '</button>');
    document.getElementById("join-pass-submit").onclick = () => {
      roomPassword = document.getElementById("join-pass").value;
      hideModal();
      emitJoin();
    };
  } else if (reason === "full") {
    showModal(t("room.full"), '<div class="msg">' + t("room.full") + '</div>');
  } else if (reason === "not_found") {
    myRoomId = null;
    inRoom = false;
    toast(t("room.not_found"));
    renderRoomArea();
  }
}

function openCreateRoom() {
  const maxVals = [0, 2, 3, 5, 8, 10, 15, 20, 30, 50];
  const opts = maxVals.map(v => '<option value="' + v + '">' + (v === 0 ? t("room.max.unlimited") : v) + '</option>').join("");
  showModal(t("room.settings.title"),
    '<div class="form-row"><label>' + t("room.name") + '</label><input id="rm-name" type="text" value="' + esc(myProfile.name || t("me.default")) + '"></div>' +
    '<div class="form-row"><label>' + t("room.max") + '</label><select id="rm-max">' + opts + '</select></div>' +
    '<div class="form-row"><label>' + t("room.pass") + '</label><input id="rm-pass" type="text" value=""></div>' +
    '<div class="form-row form-inline"><label>' + t("room.canadd") + ' <input id="rm-canadd" type="checkbox" checked></label></div>' +
    '<button id="rm-create" class="btn btn-primary btn-block">' + t("room.create") + '</button>');
  document.getElementById("rm-create").onclick = () => {
    const payload = profilePayload();
    payload.room_name = document.getElementById("rm-name").value;
    payload.max_members = parseInt(document.getElementById("rm-max").value, 10) || 0;
    payload.password = document.getElementById("rm-pass").value;
    payload.can_add = document.getElementById("rm-canadd").checked;
    socket.emit("create_room", payload);
    hideModal();
    setTimeout(shareLink, 400);
  };
}

function openJoinRoom() {
  showModal(t("room.join.title"),
    '<div class="code-boxes" dir="ltr">' +
    [0, 1, 2, 3, 4, 5].map(i => '<input class="code-cell" id="cc' + i + '" maxlength="1" inputmode="numeric" autocomplete="one-time-code">').join("") +
    '</div>' +
    '<button id="join-submit" class="btn btn-primary btn-block">' + t("room.join.submit") + '</button>');
  const cells = [...document.querySelectorAll(".code-cell")];
  const val = () => cells.map(c => c.value).join("");
  const submit = () => {
    const v = val();
    if (/^\d{6}$/.test(v)) { hideModal(); joinRoom(v); }
    else toast(t("room.join.badlink"));
  };
  document.getElementById("join-submit").onclick = submit;
  cells.forEach((c, i) => {
    c.addEventListener("input", () => {
      c.value = c.value.replace(/\D/g, "").slice(-1);
      if (c.value && i < 5) cells[i + 1].focus();
      if (/^\d{6}$/.test(val())) submit();
    });
    c.addEventListener("keydown", e => {
      if (e.key === "Backspace" && !c.value && i > 0) cells[i - 1].focus();
      if (e.key === "Enter") submit();
      if (e.key === "ArrowLeft" && i > 0) cells[i - 1].focus();
      if (e.key === "ArrowRight" && i < 5) cells[i + 1].focus();
    });
    c.addEventListener("paste", e => {
      e.preventDefault();
      const digits = ((e.clipboardData || window.clipboardData).getData("text").match(/\d/g) || []).slice(0, 6);
      digits.forEach((d, j) => { if (cells[j]) cells[j].value = d; });
      const v = val();
      if (/^\d{6}$/.test(v)) { submit(); return; }
      cells[Math.min(digits.length, 5)].focus();
    });
  });
  setTimeout(() => cells[0] && cells[0].focus(), 60);
}

function applyState(s) {
  state = s;
  inRoom = !!s.rid;
  if (typeof s.server_time === "number") {
    srvDelta = s.server_time - Date.now() / 1000;
  }
  if (s.rid) myRoomId = s.rid;
  if (viewingProfile) {
    const fresh = s.members.find(m => m.id === viewingProfile.id);
    if (fresh) viewingProfile = fresh;
  }
  renderQueue();
  renderMembers();
  renderMe();
  refreshProfileStats();
  renderShuffle();
  renderNowPlaying();
  updatePopupNow();
  renderRoomArea();
  if (!solo.active) {
    const cur = s.current;
    const urlChanged = cur && cur.id === currentTrackId && cur.stream_url && cur.stream_url !== loadedUrl &&
      (audio.error || (state.playing && audio.paused) || !loadedHasStream);
    if (cur && (cur.id !== currentTrackId || (cur.stream_url && !loadedHasStream) || urlChanged)) {
      loadTrack(cur);
    }
    syncPlayback();
  }
}

let srvDelta = 0;
function expectedPos() {
  if (!state.current) return 0;
  let p = state.position || 0;
  if (state.playing) p += (Date.now() / 1000 - srvDelta) - (state.updated_at || Date.now() / 1000);
  return Math.max(0, p);
}

let loadedHasStream = false;
let loadedUrl = "";
let lastSeekAt = 0;
let loadedAt = 0;
let lastHistId = "";
function loadTrack(t) {
  currentTrackId = t.id;
  destroyHls();
  audio.pause();
  const url = t.stream_url;
  if (!url) {
    loadedHasStream = false;
    loadedUrl = "";
    audio.removeAttribute("src");
    try { audio.load(); } catch (e) {}
    return;
  }
  loadedHasStream = true;
  loadedUrl = url;
  loadedAt = Date.now();
  if (/\.m3u8($|\?)|m3u8/i.test(url) && window.Hls && Hls.isSupported()) {
    hls = new Hls({ xhrSetup: (x, u) => { try { x.open("GET", "/api/audio_proxy?url=" + encodeURIComponent(u), true); } catch (e) {} } });
    hls.loadSource(url);
    hls.attachMedia(audio);
    hls.on(Hls.Events.ERROR, (e, d) => { if (d.fatal) nextOnFail(); });
  } else {
    // عبر السيرفر: روابط يوتيوب مربوطة بجهاز صاحب الغرفة، فأصحابك ما يقدرون يشغلونها مباشرة
    audio.src = soloPlayableUrl(url);
  }
  if (lastHistId !== t.id) { lastHistId = t.id; addHistory(t); }
}

function nextOnFail() {
  // ما نتخطى الأغنية على الكل — نطلب رابط جديد للأغنية نفسها
  if (state.current && state.current.id === currentTrackId) socket.emit("stream_failed", { id: currentTrackId });
}
audio.addEventListener("error", () => { if (!solo.active && audio.getAttribute("src")) nextOnFail(); });
socket.on("track_failed", d => toast("⚠ " + L("ما قدرنا نشغّل «", "Couldn't play “") + ((d && d.title) || "") + L("» — انتقلنا للي بعدها", "” — moved to the next one")));

function destroyHls() { if (hls) { try { hls.destroy(); } catch (e) {} hls = null; } }

function syncPlayback() {
  const cur = state.current;
  if (!cur) {
    audio.pause();
    audio.removeAttribute("src");
    currentTrackId = null;
    return;
  }
  const pos = expectedPos();
  const dur = cur.duration || 0;
  if (state.playing) {
    if (audio.paused && !audio.ended && loadedHasStream) {
      audio.play().then(() => { autoplayBlocked = false; }).catch(() => {
        autoplayBlocked = true;
        toast(t("toast.play.join"));
      });
    }
    if (dur && pos > dur + 3) { socket.emit("ended", { id: cur.id }); return; }
    // لا نقفز والصوت لسا يحمّل — كان يسبب تقطيع وإعادة تحميل بلا نهاية
    if (Math.abs(audio.currentTime - pos) > 3 && audio.readyState >= (lastSeekAt < loadedAt ? 1 : 3) && !audio.seeking && Date.now() - lastSeekAt > 4000) {
      lastSeekAt = Date.now();
      try { audio.currentTime = pos; } catch (e) {}
    }
  } else {
    if (!audio.paused) audio.pause();
    if (Math.abs(audio.currentTime - pos) > 0.5) {
      try { audio.currentTime = pos; } catch (e) {}
    }
  }
}

setInterval(() => {
  if (!solo.active && !document.body.classList.contains("np-loading")) syncPlayback();
  updateSeek();
  if (!document.getElementById("profile-popup").classList.contains("hidden")) updatePopupNow();
}, 1500);
setInterval(updateSeek, 300);

/* ================= عرض الواجهة ================= */
function renderQueue() {
  const el = document.getElementById("queue-list");
  const count = document.getElementById("queue-count");
  count.textContent = state.queue.length;
  const items = [];
  if (state.current) items.push({ tr: state.current, cur: true });
  state.queue.forEach(t => items.push({ tr: t, cur: false }));

  if (items.length === 0) {
    el.innerHTML = '<div class="msg">' + t("queue.empty") + '</div>';
    return;
  }
  el.innerHTML = "";
  items.forEach((it, idx) => {
    const track = it.tr;
    const div = document.createElement("div");
    div.className = "queue-item" + (it.cur ? " current" : "");
    div.innerHTML =
      '<div class="q-index">' + (it.cur ? "▶" : idx) + '</div>' +
      '<img class="q-thumb" src="' + (track.thumbnail || "") + '" alt="">' +
      '<div class="q-meta"><div class="q-title">' + esc(track.title) + '</div>' +
      '<div class="q-artist">' + esc(track.artist || "") + '</div>' +
      (track.added_by ? '<div class="q-added">' + t("added.by") + esc(track.added_by) + '</div>' : "") +
      '</div>' +
      '<div class="q-actions">' +
      (it.cur ? "" : '<button class="icon-btn play-now" title="' + t("btn.play.tip") + '">' + svgPlay() + "</button>") +
      '<button class="icon-btn remove-item" title="' + t("queue.remove") + '">' + svgX() + "</button>" +
      "</div>";
    div.querySelector(".remove-item").onclick = () => socket.emit("remove", { id: track.id });
    if (!it.cur) div.querySelector(".play-now").onclick = () => socket.emit("play_now", { id: track.id });
    el.appendChild(div);
  });
  renderRoomQueue();
}

function renderRoomQueue() {
  const el = document.getElementById("room-queue-list");
  if (!el) return;
  const canIRemove = !!(state.can_add !== false || (state.owner_sid && typeof socket !== "undefined" && state.owner_sid === socket.id));
  const items = [];
  if (state.current) items.push({ tr: state.current, cur: true });
  state.queue.forEach(t => items.push({ tr: t, cur: false }));
  if (!items.length) {
    el.innerHTML = '<div class="msg">' + t("queue.empty") + '</div>';
    return;
  }
  el.innerHTML = "";
  items.forEach((it, idx) => {
    const track = it.tr;
    const div = document.createElement("div");
    div.className = "queue-item" + (it.cur ? " current" : "");
    div.innerHTML =
      '<div class="q-index">' + (it.cur ? "▶" : idx) + '</div>' +
      '<img class="q-thumb" src="' + (track.thumbnail || "") + '" alt="">' +
      '<div class="q-meta"><div class="q-title">' + esc(track.title) + '</div>' +
      '<div class="q-artist">' + esc(track.artist || "") + '</div></div>' +
      '<div class="q-actions">' +
      (it.cur ? "" : '<button class="icon-btn play-now" title="' + t("btn.play.tip") + '">' + svgPlay() + "</button>") +
      (canIRemove ? '<button class="icon-btn rq-remove" title="' + esc(L("حذف من القائمة", "Remove from queue")) + '">' + svgX() + "</button>" : "") +
      "</div>";
    if (!it.cur) div.querySelector(".play-now").onclick = () => socket.emit("play_now", { id: track.id });
    const rm = div.querySelector(".rq-remove");
    if (rm) rm.onclick = e => {
      e.stopPropagation();
      div.classList.add("rq-removing");
      socket.emit("remove", { id: track.id });
      toast(it.cur ? L("تم حذفها — شغّلنا اللي بعدها", "Removed — playing the next one") : L("تم حذف الأغنية من القائمة", "Removed from the queue"));
    };
    el.appendChild(div);
  });
}

function renderMembers() {
  const el = document.getElementById("members");
  if (!el) return;
  el.innerHTML = "";
  const titleEl = document.getElementById("room-name-title");
  if (titleEl) {
    if (inRoom && state.name) {
      titleEl.textContent = state.name;
      titleEl.classList.remove("hidden");
    } else {
      titleEl.classList.add("hidden");
    }
  }
  const sorted = [...state.members].sort((a, b) => ((b.id === state.owner_sid) ? 1 : 0) - ((a.id === state.owner_sid) ? 1 : 0));
  if (!sorted.length) {
    el.innerHTML = '<div class="msg" style="font-size:12px">' + t("members.empty") + '</div>';
    return;
  }
  const iAmOwner = state.owner_sid === socket.id;
  sorted.forEach(m => {
    const div = document.createElement("div");
    div.className = "member";
    div.title = t("profile.btn");
    const isOwner = m.id === state.owner_sid;
    const canKick = iAmOwner && m.id !== socket.id && !isOwner;
    div.innerHTML = '<div class="m-avatar">' + (m.avatar ? '<img src="' + m.avatar + '">' : '<span>' + (m.name ? m.name[0] : "؟") + '</span>') + '<span class="presence-dot on"></span></div>' +
      '<div class="m-name">' + esc(m.name) + (isOwner ? ' <span class="crown" data-i18n-title="room.crown">👑</span>' : '') + '</div>' +
      (canKick ? '<button class="kick-btn" data-i18n="room.kick">طرد</button>' : '');
    const kb = div.querySelector(".kick-btn");
    if (kb) kb.onclick = e => { e.stopPropagation(); socket.emit("kick", { sid: m.id }); };
    div.onclick = () => showProfilePopup(m, div.querySelector(".m-name") || div);
    el.appendChild(div);
  });
}

function renderMe() {
  const card = document.getElementById("me-card");
  if (!card) return;
  const letter = esc((myProfile.name || "?")[0]);
  const avatarInner = myProfile.avatar
    ? '<img src="' + myProfile.avatar + '" alt="">'
    : '<span class="me-letter">' + letter + '</span>';
  let lk = 0;
  try { lk = (getLikes() || []).length; } catch (e) { lk = (myProfile.likes || []).length; }
  const fw = followerCount(myProfile);
  const fg = getFollowing().length;
  const name = myProfile.name || t("me.default");
  card.title = name + (myProfile.username ? " (@" + myProfile.username + ")" : "");
  card.innerHTML =
    '<div class="mc-banner">' + (myProfile.banner ? '<img src="' + myProfile.banner + '" alt="">' : '') +
      '<button class="mc-edit" id="mc-edit" title="' + esc(t("edit.profile")) + '">✎</button>' +
    '</div>' +
    '<div class="mc2-head">' +
      '<div id="me-avatar" class="m-avatar mc-avatar"><span class="av-clip">' + avatarInner + '</span><span class="presence-dot on"></span></div>' +
      '<div class="mc2-names">' +
        '<div class="mc2-name" id="me-name" dir="auto">' + esc(name) + '</div>' +
        (myProfile.username ? '<div class="mc2-user" id="me-user" dir="ltr">@' + esc(myProfile.username) + '</div>' : '<div class="mc2-user" id="me-user"></div>') +
      '</div>' +
    '</div>' +
    '<div class="mc2-stats">' +
      '<button type="button" class="mc2-stat" id="mc-followers"><b>' + fw + '</b><span>' + esc(t("stats.followers")) + '</span></button>' +
      '<button type="button" class="mc2-stat" id="mc-following"><b>' + fg + '</b><span>' + esc(t("stats.following")) + '</span></button>' +
      '<div class="mc2-stat"><b>' + lk + '</b><span>' + esc(t("stats.likes")) + '</span></div>' +
    '</div>';
  const eb = card.querySelector("#mc-edit");
  if (eb) eb.onclick = (e) => { e.stopPropagation(); editProfile(); };
  const fwe = card.querySelector("#mc-followers");
  if (fwe) fwe.onclick = (e) => { e.stopPropagation(); openFollowList("followers", myProfile); };
  const fge = card.querySelector("#mc-following");
  if (fge) fge.onclick = (e) => { e.stopPropagation(); openFollowList("following", myProfile); };
  card.onclick = () => { viewingProfile = null; showView("profile"); };
  clearScopedColors(card);
  applyScopedColors(card, myProfile.colors);
  fitMeCard();
}

function renderShuffle() {
  document.getElementById("shuffle-btn").classList.toggle("active", state.shuffle);
  document.getElementById("btn-shuffle").classList.toggle("on", state.shuffle);
}

function renderNowPlaying() {
  window.dispatchEvent(new Event("harmoni-track"));
  const soloBadge = document.getElementById("solo-badge");
  if (solo.active && solo.track) {
    const trk = solo.track;
    const thumb = document.getElementById("np-thumb");
    const title = document.getElementById("np-title");
    const artist = document.getElementById("np-artist");
    const likeBtn = document.getElementById("np-like");
    thumb.src = trk.thumbnail || "";
    title.textContent = trk.title;
    artist.textContent = trk.artist || "";
    artist.classList.toggle("clickable", !!trk.artist);
    artist.onclick = trk.artist ? () => openArtist(trk.artist) : null;
    if (likeBtn) likeBtn.classList.toggle("liked", isLiked(trk));
    if (soloBadge) soloBadge.classList.remove("hidden");
    setPlayIcon(!soloAudio.paused);
    return;
  }
  const trk = state.current;
  const thumb = document.getElementById("np-thumb");
  const title = document.getElementById("np-title");
  const artist = document.getElementById("np-artist");
  const likeBtn = document.getElementById("np-like");
  if (soloBadge) soloBadge.classList.add("hidden");
  if (!trk) {
    thumb.removeAttribute("src");
    title.textContent = t("np.nothing");
    artist.textContent = "";
    if (likeBtn) likeBtn.classList.remove("liked", "active");
    return;
  }
  thumb.src = trk.thumbnail || "";
  title.textContent = trk.title;
  artist.textContent = trk.artist || "";
  artist.classList.toggle("clickable", !!trk.artist);
  artist.onclick = trk.artist ? () => openArtist(trk.artist) : null;
  if (likeBtn) likeBtn.classList.toggle("liked", isLiked(trk));
}

function fmt(s) {
  if (!s || isNaN(s) || s < 0) s = 0;
  s = Math.floor(s);
  const m = Math.floor(s / 60);
  const sec = s % 60;
  return m + ":" + (sec < 10 ? "0" : "") + sec;
}

function formatSubs(n) {
  n = Number(n) || 0;
  if (n >= 1000000) return (n / 1000000).toFixed(1).replace(/\.0$/, "") + "M " + (settings.lang === "ar" ? "مشترك" : "subscribers");
  if (n >= 1000) return Math.round(n / 1000) + "K " + (settings.lang === "ar" ? "مشترك" : "subscribers");
  return n + " " + (settings.lang === "ar" ? "مشترك" : "subscribers");
}

function updateSeek() {
  const seek = document.getElementById("seek");
  if (solo.active) {
    const dur = (solo.track && solo.track.duration) || soloAudio.duration || 0;
    const pos = soloAudio.currentTime || 0;
    document.getElementById("dur-time").textContent = fmt(dur || soloAudio.duration || 0);
    if (!seekDragging) document.getElementById("cur-time").textContent = fmt(pos);
    const maxd = dur || soloAudio.duration || 0;
    if (maxd) { seek.max = maxd; if (!seekDragging) seek.value = pos; }
    const pct = maxd ? (pos / maxd) * 100 : 0;
    seek.style.setProperty("--seek-pct", pct + "%");
    setPlayIcon(!soloAudio.paused);
    return;
  }
  const cur = state.current;
  const dur = cur ? (cur.duration || 0) : 0;
  document.getElementById("dur-time").textContent = fmt(dur);
  if (seekDragging) return;
  const pos = state.current ? expectedPos() : 0;
  document.getElementById("cur-time").textContent = fmt(pos);
  if (dur) { seek.max = dur; seek.value = pos; }
  const pct = dur ? (pos / dur) * 100 : 0;
  seek.style.setProperty("--seek-pct", pct + "%");
  setPlayIcon();
}

function setPlayIcon(force) {
  let playing;
  if (typeof force === "boolean") playing = force;
  else if (solo.active) playing = !soloAudio.paused;
  else playing = !!(state.playing && state.current);
  document.getElementById("icon-play").style.display = playing ? "none" : "block";
  document.getElementById("icon-pause").style.display = playing ? "block" : "none";
}

function esc(s) {
  return String(s == null ? "" : s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

function svgPlay() { return '<svg viewBox="0 0 24 24"><path d="M8 5v14l11-7z"/></svg>'; }
function svgX() { return '<svg viewBox="0 0 24 24"><path d="M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z"/></svg>'; }

/* ================= البحث ================= */
const searchCache = new Map();
let searchSeq = 0;
let searchPage = -1;
let searchIdentity = "";
let searchBusy = false;
let searchHasMore = false;
const SEARCH_PAGE_SIZE = 49;
let searchSort = "";
try { searchSort = localStorage.getItem("mu_search_sort") || ""; } catch (e) {}
if (["", "popular", "oldest", "playlists"].indexOf(searchSort) < 0) searchSort = "";
function searchPageData(query, source, page, sort, limit = SEARCH_PAGE_SIZE) {
  sort = sort || "";
  const key = JSON.stringify([query.toLowerCase(), source, page, sort, limit]);
  if (searchCache.has(key)) return searchCache.get(key);
  const body = {query, source, limit, offset:page * SEARCH_PAGE_SIZE};
  if (sort) body.sort = sort;
  const pending = fetchWithTimeout("/api/search", {
    method: "POST", headers: {"Content-Type":"application/json"},
    body: JSON.stringify(body)
  }, 50000).then(async response => {
    const data = await response.json();
    if (!response.ok || data.error) throw new Error(data.error || t("search.fail"));
    if (data.notice && page === 0) { try { toast(data.notice); } catch (e) {} }
    return data;
  }).catch(error => { searchCache.delete(key); throw error; });
  searchCache.set(key, pending);
  while(searchCache.size > 30) searchCache.delete(searchCache.keys().next().value);
  return pending;
}
function updateMoreResults() {
  const button = document.getElementById("more-results");
  button.classList.toggle("hidden", !searchHasMore);
  button.disabled = searchBusy;
  button.setAttribute("aria-busy", String(searchBusy));
  button.textContent = searchBusy ? t("searching") : L("عرض المزيد", "Show more");
}
async function doSearch(more) {
  const query = document.getElementById("search-input").value.trim();
  const source = document.getElementById("source-select").value;
  if (!query) return;
  const sort = searchSort;
  closeOpenedPlaylist(true);
  const identity = JSON.stringify([query.toLowerCase(), source, sort]);
  if (more && (searchBusy || !searchHasMore)) return;
  if (identity !== searchIdentity) more = false;
  const page = more ? searchPage + 1 : 0;
  const seq = ++searchSeq;
  searchIdentity = identity;
  if (!more) { searchPage = -1; searchHasMore = false; }
  searchBusy = true;
  updateMoreResults();
  const message = document.getElementById("search-msg");
  message.textContent = t("searching");
  try {
    // Show playable results after the first upstream batch; fill the 49-card
    // page using the same server continuation without repeating that search.
    if (!more && source === "youtube" && !sort && !/^https?:/i.test(query)) {
      const preview = await searchPageData(query, source, 0, sort, 14);
      if (seq !== searchSeq) return;
      renderResults(preview.results || [], false);
      preResolve((preview.results || []).filter(r => r.kind !== "playlist"));
      message.textContent = "";
    }
    const data = await searchPageData(query, source, page, sort);
    if (seq !== searchSeq) return;
    searchPage = page;
    searchHasMore = !!data.has_more;
    renderResults(data.results || [], !!more);
    preResolve((data.results || []).filter(r => r.kind !== "playlist"));
    message.textContent = "";
    // Prepare one next page. Clicking More reuses this exact request.
    if(searchHasMore) searchPageData(query, source, page + 1, sort).catch(() => {});
    if(!more && !(data.results || []).length) message.textContent = L("لا توجد نتائج لهذا الفلتر", "No results for this filter");
  } catch(error) {
    if(seq === searchSeq) message.textContent = error.message || t("search.fail");
  } finally {
    if(seq === searchSeq) { searchBusy = false; updateMoreResults(); }
  }
}

function canIAdd() {
  return state.can_add !== false || state.owner_sid === socket.id;
}

function plOf(trk) {
  const k = trackKey(trk);
  return getPlaylists().find(pl => (pl.tracks || []).some(x => trackKey(x) === k)) || null;
}
function L(ar, en) { return (settings.lang === "ar") ? ar : en; }
function refreshTrackCards() {
  try { if (Array.isArray(lastResults)) renderResults(lastResults); } catch (e) {}
  try { renderSuggestions(); } catch (e) {}
  try { renderHistory(); } catch (e) {}
  try { if (typeof renderLikesView === "function") renderLikesView(); } catch (e) {}
}
function trackCard(trk, showLike, opts) {
  if (showLike === undefined) showLike = true;
  opts = opts || {};
  const div = document.createElement("div");
  div.className = "track-card";
  const liked = isLiked(trk);
  const addDisabled = !inRoom || !canIAdd();
  const icon = path => '<svg viewBox="0 0 24 24" aria-hidden="true">' + path + '</svg>';
  const playIcon = icon('<path d="M8.5 5.2v13.6L19 12z" fill="currentColor" stroke="none"/>');   // مثلث متوازن بالمنتصف
  const heartIcon = icon('<path d="M20.8 4.6a5.5 5.5 0 0 0-7.8 0L12 5.7l-1.1-1.1a5.5 5.5 0 0 0-7.8 7.8L12 21l8.8-8.6a5.5 5.5 0 0 0 0-7.8Z"/>');
  const playlistIcon = icon('<path d="M4 6h13M4 11h9M4 16h7M18 12v8M14 16h8"/>');
  const queueIcon = icon('<path d="M4 5h12M4 10h12M4 15h6m5 0 5 3-5 3Z"/>');
  const removeIcon = icon('<path d="M4 6h13M4 11h9M4 16h7m4-3 6 6m0-6-6 6"/>');
  const button = (cls, title, content, extra = '') => '<button type="button" class="btn ' + cls + '" title="' + esc(title) + '" aria-label="' + esc(title) + '" ' + extra + '>' + content + '</button>';
  const addBtn = inRoom ? button('act-add sq-btn', t("track.add"), queueIcon, addDisabled ? 'disabled' : '') : '';
  const listenBtn = button('act-listen listen-icon', t("track.listen"), playIcon + '<span>' + esc(t("track.listen")) + '</span>');
  const removeFromList = !!(opts.plBtn && plOf(trk));
  const playlistBtn = button(removeFromList ? 'act-pl-del' : 'act-pl', removeFromList ? L("حذف من القائمة", "Remove from playlist") : t("pl.addto"), removeFromList ? removeIcon : playlistIcon);
  const likeTitle = liked ? L("إلغاء الإعجاب", "Unlike") : L("إعجاب", "Like");
  div.innerHTML =
    (trk.thumbnail
      ? '<img class="track-thumb" src="' + esc(trk.thumbnail) + '" alt="" loading="lazy" onerror="this.onerror=null;this.classList.add(\'no-art\');this.src=\'data:image/gif;base64,R0lGODlhAQABAAAAACw=\'">'
      : '<div class="track-thumb no-art"></div>') +
    '<div class="track-title">' + esc(trk.title) + '</div>' +
    '<div class="track-artist">' + (trk.artist ? '<span class="artist-link">' + esc(trk.artist) + '</span>' : "") + (trk.duration ? " • " + fmt(trk.duration) : "") + '</div>' +
    '<div class="track-actions">' +
    listenBtn +
    addBtn +
    playlistBtn +
    (showLike ? button('btn-like act-like' + (liked ? ' active' : ''), likeTitle, heartIcon, 'aria-pressed="' + liked + '"') : '') +
    "</div>";
  div.querySelector(".act-listen").onclick = () => { window.__plPlaying = false; playSolo(trk); };
  const prepare = () => prepareSoloTrack(trk).then(primeSoloAudio).catch(() => {});
  div.addEventListener("pointerenter", prepare);
  div.querySelector(".act-listen").addEventListener("focus", prepare);
  if (inRoom) div.querySelector(".act-add").onclick = () => {
    if (!inRoom) { toast(t("room.join_first")); return; }
    if (!canIAdd()) { toast(t("room.owner_only")); return; }
    addToQueue(trk);
  };
  if (removeFromList) div.querySelector(".act-pl-del").onclick = () => removeFromPlaylistModal(trk);
  else div.querySelector(".act-pl").onclick = () => addToPlaylistModal(trk);
  if (showLike) div.querySelector(".act-like").onclick = () => {
    toggleLike(trk);
    const b = div.querySelector(".act-like");
    const nowLiked = isLiked(trk);
    b.classList.toggle("active", nowLiked);
    b.setAttribute("aria-pressed", String(nowLiked));
    const title = nowLiked ? L("إلغاء الإعجاب", "Unlike") : L("إعجاب", "Like");
    b.title = title;
    b.setAttribute("aria-label", title);
  };
  div.querySelector(".track-thumb").onclick = () => { if (inRoom && canIAdd()) addToQueue(trk); };
  const al = div.querySelector(".artist-link");
  if (al && trk.artist) al.onclick = e => { e.stopPropagation(); openArtist(trk.artist); };
  return div;
}

function renderResults(results, appendMode) {
  if (appendMode) {
    const existing = new Set((lastResults || []).map(trackKey));
    results = results.filter(track => { const key = trackKey(track); if(existing.has(key)) return false; existing.add(key); return true; });
  }
  if (appendMode && Array.isArray(lastResults)) lastResults = lastResults.concat(results);
  else lastResults = results;
  const el = document.getElementById("results");
  if (!appendMode) el.innerHTML = "";
  results.forEach(t => el.appendChild(t.kind === "playlist" ? playlistResultCard(t) : trackCard(t)));

}
if (!window.__moreSugWired) {
  window.__moreSugWired = true;
  document.addEventListener("DOMContentLoaded", () => {
    const msl = document.getElementById("more-suggestions");
    if (msl) msl.onclick = () => { sugShown += 36; renderSuggestions(); };
  });
}
if (!window.__moreWired) {
  window.__moreWired = true;
  document.addEventListener("DOMContentLoaded", () => {
    const mr = document.getElementById("more-results");
    if (mr) mr.onclick = () => doSearch(true);
  });
}

/* فلاتر البحث ونتائج البلاي لست: static/search-filters.js */

function renderHistory() {
  const el = document.getElementById("history");
  el.innerHTML = "";
  const h = getHistory();
  if (!h.length) {
    el.innerHTML = '<div class="msg">' + t("history.empty") + '</div>';
    return;
  }
  h.forEach(trk => {
    const div = trackCard(trk);
    const addBtn = div.querySelector(".act-add");
    if (addBtn) addBtn.title = t("track.replay");
    el.appendChild(div);
  });
  renderArtists();
}

function renderLikes() {
  const el = document.getElementById("likes");
  el.innerHTML = "";
  const isMine = !viewingProfile;
  const likes = isMine ? getLikes() : (viewingProfile.likes || []);
  if (!likes.length) {
    el.innerHTML = '<div class="msg">' + (isMine ? t("likes.mine.empty") : t("likes.other.empty")) + '</div>';
    return;
  }
  likes.forEach(t => el.appendChild(trackCard(t, isMine)));
}

function renderLikesView() {
  const el = document.getElementById("likes-list");
  if (!el) return;
  el.innerHTML = "";
  const likes = getLikes();
  const cnt = document.getElementById("likes-count");
  if (cnt) cnt.textContent = likes.length;
  if (!likes.length) {
    el.innerHTML = '<div class="msg">' + t("likes.mine.empty") + '</div>';
    return;
  }
  likes.forEach(t => el.appendChild(trackCard(t, true, { plBtn: true })));
}

/* ================= الأغاني المقترحة والفنانين ================= */
let lastSuggestions = null;
let suggestionSeq = 0;
let suggestionAbort = null;
async function loadSuggestions() {
  const seq = ++suggestionSeq;
  if(suggestionAbort) suggestionAbort.abort();
  let h = getHistory();
  if (!h.length) h = getLikes().slice(0, 12);   // ما فيه سجل؟ نقترح على أساس المفضلة
  if (!h.length) { lastSuggestions = []; renderSuggestions(); return; }
  const artists = [];
  const seen = {};
  const played = [];
  h.forEach(x => {
    const a = (x.artist || "").trim();
    if (a && !seen[a.toLowerCase()]) { seen[a.toLowerCase()] = 1; artists.push(a); }
    if (x.title) played.push(x.title);
  });
  try {
    const ctrl = new AbortController();
    suggestionAbort = ctrl;
    const tm = setTimeout(() => ctrl.abort(), 20000);
    const res = await fetch("/api/suggest", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ artists: artists.slice(0, 6), current: h[0].artist || "", played: played, recent: h.slice(0, 12).map(track => ({title:track.title, artist:track.artist})) }),
      signal: ctrl.signal
    });
    clearTimeout(tm);
    const j = await res.json();
    if(seq !== suggestionSeq) return;
    lastSuggestions = j.results || [];
  } catch (e) {
    lastSuggestions = lastSuggestions || [];
  }
  if(seq !== suggestionSeq) return;
  sugShown = 36;
  renderSuggestions();
}
let sugShown = 36;

function renderArtists() {
  const el = document.getElementById("artists");
  if (!el) return;
  el.innerHTML = "";
  const counts = {};
  const bump = (a) => { const k = (a || "").trim(); if (k) counts[k.toLowerCase()] = k; };
  getHistory().forEach(x => bump(x.artist));
  getLikes().forEach(x => bump(x.artist));
  const favs = getFavArtists();
  const derived = Object.keys(counts);
  const list = [];
  const added = {};
  favs.forEach(f => {
    const k = f.name.toLowerCase();
    if (!added[k]) { added[k] = 1; list.push({ name: f.name, fav: true }); }
  });
  derived.forEach(k => {
    if (!added[k]) { added[k] = 1; list.push({ name: counts[k], fav: false }); }
  });
  if (!list.length) {
    el.innerHTML = '<div class="msg">' + t("artists.empty") + '</div>';
    return;
  }
  list.forEach(a => {
    const div = document.createElement("div");
    div.className = "artist-chip" + (a.fav ? " fav" : "");
    div.title = t("artists.click");
    div.innerHTML = '<span class="artist-avatar">' + esc(a.name[0]) + '</span><span class="artist-name">' + esc(a.name) + '</span>' + (a.fav ? '<span class="artist-star">★</span>' : '');
    div.onclick = () => openArtist(a.name);
    el.appendChild(div);
  });
  updateArtistArrow();
}

function updateArtistArrow() {
  const el = document.getElementById("artists");
  const fwd = document.getElementById("artist-arrow");
  const back = document.getElementById("artist-arrow-back");
  if (!el) return;
  const scrollable = el.scrollWidth > el.clientWidth + 8;
  const maxScroll = el.scrollWidth - el.clientWidth;
  const pos = Math.abs(el.scrollLeft);
  if (fwd) fwd.classList.toggle("hidden", !scrollable || pos >= maxScroll - 8);
  if (back) back.classList.toggle("hidden", !scrollable || pos < 8);
}
if (!window.__artWired) {
  window.__artWired = true;
  document.addEventListener("DOMContentLoaded", () => {
    const el = document.getElementById("artists");
    const fwd = document.getElementById("artist-arrow");
    const back = document.getElementById("artist-arrow-back");
    if (!el || !fwd || !back) return;
    fwd.onclick = () => el.scrollBy({ left: -260, behavior: "smooth" });
    back.onclick = () => el.scrollBy({ left: 260, behavior: "smooth" });
    el.addEventListener("scroll", updateArtistArrow, { passive: true });
    window.addEventListener("resize", updateArtistArrow);
  });
}

let artistSeq = 0;
let artistBackView = "home";
function openArtist(artist) {
  if (!artist) return;
  const activeView = document.querySelector(".view.active");
  if (activeView && activeView.id !== "view-artist") artistBackView = activeView.id.replace("view-", "");
  showView("artist");

  const normName = (s) => (s || "").toLowerCase().trim();
  const target = normName(artist);
  const seen = {};
  const tracks = [];
  const add = (t) => {
    const k = trackKey(t);
    if (seen[k]) return;
    seen[k] = 1;
    const ta = normName(t.artist);
    if (ta && (ta === target || ta.includes(target) || target.includes(ta))) tracks.push(t);
  };
  (getHistory() || []).forEach(add);
  (getLikes() || []).forEach(add);

  const isFav = isFavArtist(artist);
  const mySeq = ++artistSeq;

  const av = document.getElementById("ap-avatar");
  av.innerHTML = esc((artist || "?")[0]);
  const banner = document.getElementById("ap-banner");
  if (banner) { banner.style.backgroundImage = "none"; banner.classList.add("noimg"); }
  document.getElementById("ap-name").textContent = artist;
  document.getElementById("ap-subs").textContent = "";
  const fb = document.getElementById("ap-fav");
  fb.classList.toggle("active", isFav);
  fb.textContent = isFav ? ("★ " + t("artist.faved")) : ("☆ " + t("artist.fav"));
  fb.onclick = () => {
    toggleFavArtist(artist);
    const nowFav = isFavArtist(artist);
    fb.classList.toggle("active", nowFav);
    fb.textContent = nowFav ? ("★ " + t("artist.faved")) : ("☆ " + t("artist.fav"));
    renderArtists();
  };

  const lw = document.getElementById("ap-local-wrap");
  if (lw) lw.classList.add("hidden");

  const box = document.getElementById("ap-channel");
  box.innerHTML = '<div class="msg spinner-row"><span class="spinner"></span> ' + t("artist.loading") + '</div>';

  fetch("/api/artist_songs", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ artist })
  }).then(r => r.json()).then(j => {
    if (mySeq !== artistSeq) return;
    if (j.channel) {
      if (j.channel.avatar) av.innerHTML = '<img src="' + esc(j.channel.avatar) + '" alt="">';
      const bn = document.getElementById("ap-banner");
      if (bn && j.channel.avatar) { bn.style.backgroundImage = 'url("' + j.channel.avatar + '")'; bn.classList.remove("noimg"); }
      const subs = document.getElementById("ap-subs");
      if (subs && j.channel.subscribers) subs.textContent = formatSubs(j.channel.subscribers);
    }
    box.innerHTML = "";
    const merged = [];
    const seen2 = {};
    const pushM = (t) => {
      const k = trackKey(t);
      if (!k || seen2[k]) return;
      const ta = normName(t.artist);
      if (ta && (ta === target || ta.includes(target) || target.includes(ta))) {
        seen2[k] = 1;
        merged.push(t);
      }
    };
    (getLikes() || []).forEach(pushM);
    (j.tracks || []).forEach(pushM);
    if (!merged.length) {
      box.innerHTML = '<div class="msg">' + t("artist.none") + '</div>';
      return;
    }
    const grid = document.createElement("div");
    grid.className = "grid artist-grid";
    merged.forEach(tr => grid.appendChild(trackCard(tr, true)));
    box.appendChild(grid);
  }).catch(() => {
    if (mySeq !== artistSeq) return;
    box.innerHTML = '<div class="msg">' + t("search.fail") + '</div>';
  });
}

function renderSuggestions() {
  const el = document.getElementById("suggestions");
  if (!el) return;
  el.innerHTML = "";
  if (lastSuggestions === null) return;
  if (!lastSuggestions.length) {
    el.innerHTML = '<div class="msg">' + t("suggest.empty") + '</div>';
    return;
  }
  lastSuggestions.slice(0, sugShown).forEach(s => el.appendChild(trackCard(s, false)));
  const msb = document.getElementById("more-suggestions");
  if (msb) msb.classList.toggle("hidden", lastSuggestions.length <= sugShown);
}

function addToQueue(trk) {
  if (!inRoom) { toast(t("room.join_first")); return; }
  socket.emit("add", { track: { title: trk.title, artist: trk.artist, duration: trk.duration, thumbnail: trk.thumbnail, web_url: trk.web_url, source: trk.source, added_by: myProfile.name || "" } });
}

/* ================= البلاي لستات ================= */
function playlistCoverHtml(p) {
  return '<div class="playlist-cover">' + (p.img ? '<img src="' + p.img + '" alt="">' : "♫") + '</div>';
}

function playlistCard(p) {
  const div = document.createElement("div");
  div.className = "playlist-card";
  div.innerHTML =
    playlistCoverHtml(p) +
    '<button class="pl-play-btn" title="' + esc(t("pl.play")) + '">' + svgPlay() + '</button>' +
    '<div class="playlist-name">' + esc(p.name) + '</div>' +
    '<div class="playlist-meta">' + (p.tracks || []).length + " " + t("pl.count") + '</div>';
  div.onclick = () => openPlaylist(p.id);
  div.querySelector(".pl-play-btn").onclick = e => {
    e.stopPropagation();
    playPlaylist(p);
  };
  return div;
}

function renderProfileSections(isMine) {
  const box = document.getElementById("pl-sections");
  if (!box) return;
  if (!isMine) {
    box.innerHTML = "";
    const other = viewingProfile;
    if (other && Array.isArray(other.playlists) && other.playlists.length) {
      let h = '<div class="section-head"><h3>' + esc(t("pl.title")) + '</h3></div><div class="grid pl-grid"></div>';
      box.innerHTML = h;
      const pg = box.querySelector(".pl-grid");
      other.playlists.forEach(p => {
        const card = document.createElement("div");
        card.className = "playlist-card";
        card.innerHTML = playlistCoverHtml(p) +
          '<div class="playlist-name">' + esc(p.name) + '</div>' +
          '<div class="playlist-meta">' + ((p.tracks || []).length) + " " + t("pl.count") + '</div>';
        pg.appendChild(card);
      });
    }
    return;
  }
  const favs = getFavArtists().filter(f => f && f.name);
  let html = "";
  if (favs.length) {
    html += '<div class="section-head"><h3>' + esc(t("artists.favs")) + '</h3></div><div id="fav-artists-row" class="artists-row"></div>';
  }
  html += '<div class="section-head"><h3>' + esc(t("pl.title")) + '</h3><button id="pl-new-btn" class="link-btn">' + esc(t("pl.new")) + '</button></div>';
  const pls = getPlaylists();
  html += pls.length ? '<div id="playlists-grid" class="grid pl-grid"></div>' : '<div class="msg">' + esc(t("pl.empty")) + '</div>';
  box.innerHTML = html;
  const far = document.getElementById("fav-artists-row");
  if (far) {
    favs.forEach(f => {
      const chip = document.createElement("div");
      chip.className = "artist-chip fav";
      chip.title = t("artists.click");
      chip.innerHTML = '<span class="artist-avatar">' + esc((f.name || "?")[0]) + '</span><span class="artist-name">' + esc(f.name) + '</span><span class="artist-star">★</span>';
      chip.onclick = () => openArtist(f.name);
      far.appendChild(chip);
    });
  }
  const pg = document.getElementById("playlists-grid");
  if (pg) pls.forEach(p => pg.appendChild(playlistCard(p)));
  document.getElementById("pl-new-btn").onclick = createPlaylistModal;
}

function createPlaylistModal(thenAdd) {
  let newImg = "";
  showModal(t("pl.create_title"),
    '<div class="form-row"><label>' + t("pl.name") + '</label><input id="npl-name" type="text"></div>' +
    '<div class="form-row"><label>' + t("pl.image") + '</label><input id="npl-img" type="file" accept="image/*"></div>' +
    '<button id="npl-create" class="btn btn-primary btn-block">' + t("pl.create_title") + '</button>');
  document.getElementById("npl-img").onchange = e => pickCrop(e.target, 300, 300, t("pl.image"), d => { newImg = d; });
  document.getElementById("npl-create").onclick = () => {
    const name = document.getElementById("npl-name").value.trim();
    if (!name) { toast(t("pl.need_name")); return; }
    const pls = getPlaylists();
    const pl = { id: plId(), name, img: newImg, tracks: [], created: Date.now() };
    if (thenAdd) {
      pl.tracks.push({ title: thenAdd.title, artist: thenAdd.artist, duration: thenAdd.duration, thumbnail: thenAdd.thumbnail, web_url: thenAdd.web_url, source: thenAdd.source });
    }
    pls.unshift(pl);
    savePlaylists(pls);
    hideModal();
    toast(t("pl.created"));
    renderProfileSections(true);
  };
}

/* صفحة البلاي لست: static/playlist-page.js */
function playPlaylist(p) {
  if (!inRoom) { toast(t("room.join_first")); return; }
  const tracks = (p && p.tracks) || [];
  if (!tracks.length) { toast(t("pl.empty_list")); return; }
  tracks.forEach((trk, i) => setTimeout(() => addToQueue(trk), i * 400));
  toast("▶ " + p.name);
}

function addToPlaylistModal(trk) {
  const pls = getPlaylists();
  let body = "";
  if (pls.length) {
    body += pls.map(p =>
      '<div class="pl-choice" data-pid="' + esc(p.id) + '"><span>♫ ' + esc(p.name) + '</span><small>' + (p.tracks || []).length + " " + t("pl.count") + '</small></div>'
    ).join("");
  } else {
    body += '<div class="msg">' + esc(t("pl.empty")) + '</div>';
  }
  body += '<button id="apl-new" class="btn btn-primary btn-block">' + esc(t("pl.new")) + '</button>';
  showModal(t("pl.addto"), body);
  document.querySelectorAll(".pl-choice").forEach(el => {
    el.onclick = () => {
      const list = getPlaylists();
      const p = list.find(x => x.id === el.dataset.pid);
      if (!p) return;
      p.tracks = p.tracks || [];
      const k = trackKey(trk);
      if (!p.tracks.some(x => trackKey(x) === k)) p.tracks.push({ title: trk.title, artist: trk.artist, duration: trk.duration, thumbnail: trk.thumbnail, web_url: trk.web_url, source: trk.source });
      savePlaylists(list);
      hideModal();
      toast(t("pl.added"));
      renderProfileSections(true);
      refreshTrackCards();
    };
  });
  document.getElementById("apl-new").onclick = () => createPlaylistModal(trk);
}

function removeFromPlaylistModal(trk) {
  const k = trackKey(trk);
  const holders = getPlaylists().filter(pl => (pl.tracks || []).some(x => trackKey(x) === k));
  let body = holders.map(pl =>
    '<div class="pl-choice pl-remove" data-pid="' + esc(pl.id) + '"><span>♪ ' + esc(pl.name) + '</span><small>' + L("اضغط للإزالة", "Tap to remove") + '</small></div>'
  ).join("");
  showModal(L("إزالة من قائمة", "Remove from playlist"), body);
  document.querySelectorAll(".pl-remove").forEach(el => {
    el.onclick = () => {
      const list = getPlaylists();
      const pl = list.find(x => x.id === el.dataset.pid);
      if (!pl) return;
      pl.tracks = (pl.tracks || []).filter(x => trackKey(x) !== k);
      savePlaylists(list);
      hideModal();
      toast(L("تم الحذف من القائمة", "Removed from playlist"));
      renderProfileSections(true);
      refreshTrackCards();
    };
  });
}

/* ===== اختصارات لوحة المفاتيح داخل التطبيق =====
   مسافة: تشغيل/إيقاف · ←/→: ترجيع/تقديم 5 ثواني · Shift+←/→ (أو Ctrl): الأغنية السابقة/التالية · ↑/↓: الصوت
   تشتغل حتى لو كان آخر شي ضغطته زر أو شريط (كانت تتعطل لأن المسافة تضغط الزر نفسه والأسهم تحرك الشريط) */
(function initAppHotkeys() {
  const TEXT_TYPES = /^(text|search|password|email|number|url|tel)$/i;
  function typing(el) {
    if (!el) return false;
    if (el.isContentEditable) return true;
    const tag = (el.tagName || "").toUpperCase();
    if (tag === "TEXTAREA" || tag === "SELECT") return true;
    return tag === "INPUT" && TEXT_TYPES.test(el.type || "text");
  }
  function ctl(action, value) {
    try { if (window.__harmoniBubble) return window.__harmoniBubble.cmd(action, value); } catch (e) {}
    const b = { play_pause: "btn-play", skip: "btn-skip", prev: "btn-prev" }[action];
    if (b && document.getElementById(b)) document.getElementById(b).click();
  }
  function seekBy(sec) {
    const sk = document.getElementById("seek");
    if (!sk) return;
    const max = +sk.max || 0;
    const v = Math.max(0, Math.min(max || Infinity, (+sk.value || 0) + sec));
    ctl("seek", v);
  }
  function volBy(step) {
    const vol = document.getElementById("volume");
    if (!vol) return;
    vol.value = Math.max(0, Math.min(100, (+vol.value || 0) + step));
    vol.dispatchEvent(new Event("input", { bubbles: true }));
    vol.dispatchEvent(new Event("change", { bubbles: true }));
  }
  document.addEventListener("keydown", e => {
    if (e.defaultPrevented || e.altKey || e.metaKey || typeof mpCaptureNext !== "undefined" && mpCaptureNext) return;
    if (typing(e.target)) return;
    if (document.querySelector(".modal:not(.hidden) input:focus")) return;
    let handled = true;
    const jump = e.shiftKey || e.ctrlKey;
    switch (e.key) {
      case " ": case "Spacebar":
        if (e.ctrlKey || e.shiftKey) { handled = false; break; }
        if (!e.repeat) ctl("play_pause");
        break;
      case "ArrowRight":
        if (jump) ctl("skip"); else seekBy(5);
        break;
      case "ArrowLeft":
        if (jump) ctl("prev"); else seekBy(-5);
        break;
      case "ArrowUp":
        if (e.ctrlKey) { handled = false; break; }
        volBy(5);
        break;
      case "ArrowDown":
        if (e.ctrlKey) { handled = false; break; }
        volBy(-5);
        break;
      // أزرار الميديا يتكفل فيها media-session.js (تشتغل حتى لو البرنامج مو قدامك) — هنا بس لو المتصفح ما يدعمها
      case "MediaPlayPause": if (navigator.mediaSession) handled = false; else ctl("play_pause"); break;
      case "MediaTrackNext": if (navigator.mediaSession) handled = false; else ctl("skip"); break;
      case "MediaTrackPrevious": if (navigator.mediaSession) handled = false; else ctl("prev"); break;
      default: handled = false;
    }
    if (!handled) return;
    e.preventDefault();
    e.stopPropagation();
    // الزر/الشريط اللي عليه التركيز ما ياخذ الضغطة مرة ثانية (المسافة كانت توقف وتشغل بنفس اللحظة)
    const a = document.activeElement;
    if (a && a !== document.body && (a.tagName === "BUTTON" || (a.tagName === "INPUT" && !TEXT_TYPES.test(a.type || "")))) a.blur();
  }, true);
  // نمنع المسافة تضغط الزر لما ترتفع (keyup) بعد ما عالجناها
  document.addEventListener("keyup", e => {
    if ((e.key === " " || e.key === "Spacebar") && !typing(e.target)) e.preventDefault();
  }, true);
})();



/* ================= مشاركة كود الغرفة ================= */
async function shareLink() {
  if (myRoomId && /^\d{6}$/.test(myRoomId)) {
    showModal(t("share.title"),
      '<p style="margin-bottom:12px">' + t("room.code.share") + '</p>' +
      '<div class="share-code" id="share-code">' + esc(myRoomId) + '</div>' +
      '<button id="copy-btn" class="btn btn-primary btn-block">' + t("share.copy") + '</button>');
    document.getElementById("copy-btn").onclick = () => {
      navigator.clipboard.writeText(myRoomId).then(() => toast(t("toast.copied")));
    };
    return;
  }
  const base = location.origin + location.pathname;
  let url = base;
  try {
    const res = await fetch("/api/info");
    const info = await res.json();
    if (info.tunnel) {
      url = info.tunnel;
    } else if (location.hostname === "localhost" || location.hostname === "127.0.0.1") {
      url = "http://" + info.ip + ":" + info.port;
    }
  } catch (e) {}
  showModal(t("share.title"),
    '<p style="margin-bottom:12px">' + t("share.body") + '</p>' +
    '<div style="display:flex;gap:8px"><input id="share-url" class="search-input" readonly value="' + esc(url) + '">' +
    '<button id="copy-btn" class="btn btn-primary">' + t("share.copy") + '</button></div>');
  document.getElementById("copy-btn").onclick = () => {
    const inp = document.getElementById("share-url");
    inp.select();
    navigator.clipboard.writeText(inp.value).then(() => toast(t("toast.copied")));
  };
}

/* ================= البروفايل ================= */
function authed() {
  try { return !!getUid() && localStorage.getItem("mu_authed") === "1"; }
  catch (e) { return false; }
}

function applyAuth(j) {
  localStorage.setItem("mu_uid", j.uid);
  try { if (j.token) localStorage.setItem("mu_token", j.token); } catch (e) {}
  try { localStorage.setItem("mu_authed", "1"); } catch (e) {}
  const p = j.profile || {};
  const d = j.data || {};
  store("mu_profile", {
    uid: j.uid,
    name: p.name || "",
    avatar: p.avatar || "",
    banner: p.banner || "",
    bio: p.bio || "",
    colors: p.colors || {},
    username: p.username || "",
  });
  store("mu_likes", d.likes || []);
  store("mu_following", d.following || []);
  store("mu_playlists", d.playlists || []);
  if ((d.history || []).length || !getHistory().length) store("mu_history", d.history || []);
  toast(t("account.restore.ok"));
  setTimeout(() => location.reload(), 700);
}

async function loginAccount(uname, pass, msgEl) {
  uname = (uname || "").replace(/^@+/, "").trim();
  try {
    const res = await fetch("/api/login", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username: uname, password: pass })
    });
    const j = await res.json();
    if (j.error) {
      if (msgEl) msgEl.textContent = j.error;
      else toast(j.error);
      return false;
    }
    applyAuth(j);
    return true;
  } catch (e) {
    if (msgEl) msgEl.textContent = t("account.restore.fail");
    return false;
  }
}

function initAuthGate() {
  const ov = document.getElementById("auth-overlay");
  if (!ov) return;
  ov.classList.toggle("hidden", authed());
  const tabL = document.getElementById("auth-tab-login");
  const tabR = document.getElementById("auth-tab-reg");
  const formL = document.getElementById("auth-login-form");
  const formR = document.getElementById("auth-reg-form");
  const msg = document.getElementById("auth-msg");
  const setTab = (reg) => {
    tabL.classList.toggle("active", !reg);
    tabR.classList.toggle("active", reg);
    formL.classList.toggle("hidden", reg);
    formR.classList.toggle("hidden", !reg);
    msg.textContent = "";
  };
  tabL.onclick = () => setTab(false);
  tabR.onclick = () => setTab(true);
  const doLogin = () => loginAccount(document.getElementById("al-user").value, document.getElementById("al-pass").value, msg);
  document.getElementById("al-submit").onclick = doLogin;
  formL.addEventListener("keydown", e => { if (e.key === "Enter") doLogin(); });
  const doRegister = async () => {
    const u = document.getElementById("ar-user").value;
    const p1 = document.getElementById("ar-pass").value;
    const p2 = document.getElementById("ar-pass2").value;
    if (p1 !== p2) { msg.textContent = t("auth.pass.mismatch"); return; }
    try {
      const res = await fetch("/api/register", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ username: u, password: p1 })
      });
      const j = await res.json();
      if (j.error) { msg.textContent = j.error; return; }
      applyAuth(j);
    } catch (e) {
      msg.textContent = "خطأ في الاتصال";
    }
  };
  document.getElementById("ar-submit").onclick = doRegister;
  formR.addEventListener("keydown", e => { if (e.key === "Enter") doRegister(); });
}

function currentProfile() { return viewingProfile || myProfile; }

function renderProfile() {
  const p = currentProfile();
  const isMine = isMyProfile(viewingProfile);
  const avatar = document.getElementById("avatar");
  const banner = document.getElementById("banner");
  const pOnline = isMine || (state.members || []).some(x => x.uid === p.uid);
  avatar.innerHTML = (p.avatar ? '<img src="' + p.avatar + '">' : '<span id="avatar-fallback">' + (p.name ? esc(p.name[0]) : "؟") + '</span>');
  const dot = document.getElementById("presence-full");
  if (dot) {
    dot.className = "presence-dot big " + (pOnline ? "on" : "off");
    dot.title = pOnline ? t("status.online") : t("status.offline");
  }
  banner.innerHTML = p.banner ? '<img class="banner-img" src="' + p.banner + '">' : '<div class="banner-fallback"></div>';
  document.getElementById("profile-name").textContent = p.name || t("me.default");
  document.getElementById("profile-user").textContent = p.username ? ("@" + p.username) : "";
  document.getElementById("profile-bio").textContent = p.bio || "";
  document.getElementById("edit-profile-btn").classList.toggle("hidden", !isMine);
  const fb = document.getElementById("follow-btn");
  fb.classList.toggle("hidden", isMine);
  if (!isMine) {
    fb.textContent = isFollowing(p) ? t("unfollow.btn") : t("follow.btn");
    fb.classList.toggle("following", isFollowing(p));
  }
  refreshProfileStats();
  document.getElementById("likes-title").textContent = isMine ? t("likes.title") : t("likes.other");
  const vp = profileViewEl();
  if (isMine) { clearScopedColors(vp); restoreAccent(); }
  else applyScopedColors(vp, p.colors);
  renderLikes();
  renderProfileSections(isMine);
  try { if (window.renderMyStats) window.renderMyStats(isMine); } catch (e) {}
}

function refreshProfileStats() {
  const stats = document.getElementById("profile-stats");
  if (!stats) return;
  const p = currentProfile();
  const isMine = isMyProfile(viewingProfile);
  const fw = isMine ? followerCount(myProfile) : followerCount(p);
  const fg = isMine ? getFollowing().length : followingCount(p);
  stats.innerHTML = '<span class="stat" id="stat-fw">' + fw + ' ' + t("stats.followers") + '</span>' +
    '<span class="stat-sep"> • </span>' +
    '<span class="stat" id="stat-fg">' + fg + ' ' + t("stats.following") + '</span>';
  document.getElementById("stat-fw").onclick = () => openFollowList("followers", p);
  document.getElementById("stat-fg").onclick = () => openFollowList("following", p);
}

function viewMemberProfile(m) {
  if (isMyProfile(m)) return;
  viewingProfile = m;
  showView("profile");
}

function showProfilePopup(person, anchorEl) {
  popupPerson = person;
  popupAnchor = anchorEl || null;
  applyScopedColors(document.querySelector(".popup-card"), person.colors);
  document.getElementById("popup-banner").innerHTML = person.banner ? '<img src="' + person.banner + '">' : '';
  document.getElementById("popup-avatar").innerHTML = (person.avatar ? '<img src="' + person.avatar + '">' : '<span>' + (person.name ? esc(person.name[0]) : "؟") + '</span>') + '<span class="presence-dot on"></span>';
  document.getElementById("popup-name").textContent = person.name || t("me.default");
  const pu = document.getElementById("popup-user");
  if (pu) pu.textContent = person.username ? ("@" + person.username) : "";
  document.getElementById("popup-bio").textContent = person.bio || "";
  const ps = document.getElementById("popup-stats");
  if (ps) ps.textContent = followerCount(person) + " " + t("stats.followers") + " • " + followingCount(person) + " " + t("stats.following");
  document.getElementById("popup-crown").classList.toggle("hidden", person.id !== state.owner_sid);
  const pk = document.getElementById("popup-kick");
  const canKick = state.owner_sid === socket.id && person.id !== socket.id && !isMyProfile(person);
  pk.classList.toggle("hidden", !canKick);
  pk.onclick = () => {
    socket.emit("kick", { sid: person.id });
    closeProfilePopup();
  };
  const fb = document.getElementById("popup-follow");
  if (isMyProfile(person)) {
    fb.classList.add("hidden");
  } else {
    fb.classList.remove("hidden");
    fb.textContent = isFollowing(person) ? t("unfollow.btn") : t("follow.btn");
    fb.classList.toggle("following", isFollowing(person));
  }
  updatePopupNow();
  document.getElementById("profile-popup").classList.remove("hidden");
  positionPopup();
}

function updatePopupNow() {
  const row = document.getElementById("popup-now");
  if (!row) return;
  const person = popupPerson;
  if (!person) { row.classList.add("hidden"); return; }
  const isMe = person.uid && myProfile && person.uid === myProfile.uid;
  if (isMe) {
    let cur, playing, pos;
    if (solo.active && solo.track) {
      cur = solo.track;
      playing = !soloAudio.paused;
      pos = soloAudio.currentTime || 0;
    } else {
      cur = state.current;
      playing = !!state.playing;
      pos = expectedPos();
    }
    if (!cur) { row.classList.add("hidden"); return; }
    row.classList.remove("hidden");
    const dot = row.querySelector(".popup-now-dot");
    dot.style.background = playing ? "#4cffa0" : "#9a9a9a";
    dot.style.boxShadow = playing ? "0 0 6px #4cffa0" : "none";
    dot.style.animation = playing ? "" : "none";
    document.getElementById("popup-now-track").textContent = cur.title + (cur.artist ? " — " + cur.artist : "");
    document.getElementById("popup-now-time").textContent = fmt(pos) + " / " + fmt(cur.duration || 0);
    return;
  }
  fetch("/api/state_snapshot?uid=" + encodeURIComponent(person.uid))
    .then(r => r.json())
    .then(s => {
      const cur = s.current;
      const age = s.updated_at ? Date.now() / 1000 - s.updated_at : 999;
      const fresh = age < 45;
      if (!cur || (!fresh && !s.playing)) {
        if (!cur || age > 120) { row.classList.add("hidden"); return; }
      }
      if (!cur) { row.classList.add("hidden"); return; }
      row.classList.remove("hidden");
      const dot = row.querySelector(".popup-now-dot");
      const playing = !!s.playing && fresh;
      dot.style.background = playing ? "#4cffa0" : "#9a9a9a";
      dot.style.boxShadow = playing ? "0 0 6px #4cffa0" : "none";
      dot.style.animation = playing ? "" : "none";
      document.getElementById("popup-now-track").textContent = cur.title + (cur.artist ? " — " + cur.artist : "");
      const pos = (s.position || 0) + (playing ? (Date.now() / 1000) - (s.updated_at || Date.now() / 1000) : 0);
      document.getElementById("popup-now-time").textContent = fmt(Math.max(0, pos)) + " / " + fmt(cur.duration || 0);
    })
    .catch(() => row.classList.add("hidden"));
}

function positionPopup() {
  const btn = popupAnchor || document.getElementById("profile-btn") || document.getElementById("me-avatar");
  if (!btn) return;
  const card = document.querySelector(".popup-card");
  let b;
  try {
    const rg = document.createRange();
    rg.selectNodeContents(btn);
    b = rg.getBoundingClientRect();
    if (!b || (!b.width && !b.height)) b = btn.getBoundingClientRect();
  } catch (e) { b = btn.getBoundingClientRect(); }
  const w = card.offsetWidth || 320;
  const h = card.offsetHeight || 380;
  const gap = 8;
  let left = b.left - w - gap;
  if (left < 8) left = Math.min(window.innerWidth - w - 8, b.right + gap);
  if (left < 8) left = 8;
  let top = b.bottom - h;
  if (top < 8) top = 8;
  if (top + h > window.innerHeight - 8) top = window.innerHeight - h - 8;
  card.style.left = left + "px";
  card.style.top = top + "px";
  card.style.right = "auto";
  card.style.bottom = "auto";
}

function closeProfilePopup() {
  document.getElementById("profile-popup").classList.add("hidden");
  popupPerson = null;
  clearScopedColors(document.querySelector(".popup-card"));
}

function editProfile() {
  const p = myProfile;
  newAvatar = undefined;
  newBanner = undefined;
  const c1 = (p.colors && p.colors.accent) || "#1db954";
  const c2 = (p.colors && p.colors.accent2) || "#0f6bff";
  const cam = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 8h3l2-3h6l2 3h3v11H4z"/><circle cx="12" cy="13" r="3.5"/></svg>';
  const body =
    '<div class="pe">' +
      '<div class="pe-card">' +
        '<button type="button" class="pe-banner" id="pe-banner" title="' + esc(L("تغيير الغلاف", "Change banner")) + '">' +
          '<span class="pe-banner-img"></span><span class="pe-overlay">' + cam + '<b>' + esc(L("تغيير الغلاف", "Change banner")) + '</b></span>' +
        '</button>' +
        '<div class="pe-id">' +
          '<button type="button" class="pe-avatar" id="pe-avatar" title="' + esc(L("تغيير الصورة", "Change photo")) + '">' +
            '<span class="pe-avatar-img"></span><span class="pe-overlay">' + cam + '</span></button>' +
          '<div class="pe-names"><div class="pe-name"></div><div class="pe-user" dir="ltr"></div></div>' +
        '</div>' +
        '<div class="pe-bio-preview"></div>' +
      '</div>' +
      '<div class="pe-fields">' +
        '<label class="pe-field"><span>' + esc(t("form.name")) + '</span><input id="pf-name" maxlength="32" value="' + esc(p.name || "") + '"></label>' +
        '<label class="pe-field"><span>' + esc(L("اسم المستخدم", "Username")) + '</span><div class="pe-at"><i>@</i><input id="pf-user" dir="ltr" maxlength="24" placeholder="username" value="' + esc(p.username || "") + '"></div><em id="pf-user-msg" class="user-msg"></em></label>' +
        '<label class="pe-field pe-wide"><span>' + esc(t("form.bio")) + ' <small id="pe-bio-count"></small></span><textarea id="pf-bio" maxlength="160" rows="3" placeholder="' + esc(L("اكتب شي عنك…", "Say something about you…")) + '">' + esc(p.bio || "") + '</textarea></label>' +
      '</div>' +
      '<div class="pe-section">' + esc(L("ألوان البروفايل", "Profile colors")) + '</div>' +
      '<div class="pe-colors">' +
        '<label class="pe-color"><input id="pf-accent" type="color" value="' + esc(c1) + '"><span class="pe-color-dot"></span><span class="pe-color-t"><b>' + esc(t("form.color1")) + '</b><small dir="ltr"></small></span></label>' +
        '<label class="pe-color"><input id="pf-accent2" type="color" value="' + esc(c2) + '"><span class="pe-color-dot"></span><span class="pe-color-t"><b>' + esc(t("form.color2")) + '</b><small dir="ltr"></small></span></label>' +
      '</div>' +
      '<div class="pe-actions">' +
        (p.avatar || p.banner ? '<button type="button" class="btn pe-reset" id="pe-reset">' + esc(L("إزالة الصور", "Remove photos")) + '</button>' : '<span></span>') +
        '<div><button type="button" class="btn" id="pe-cancel">' + esc(t("btn.cancel")) + '</button>' +
        '<button type="button" id="pf-save" class="btn btn-primary">' + esc(t("btn.save")) + '</button></div>' +
      '</div>' +
      '<input id="pf-avatar" type="file" accept="image/*" hidden><input id="pf-banner" type="file" accept="image/*" hidden>' +
    '</div>';
  showModal(t("edit.profile"), body);
  const mc = document.querySelector("#modal .modal-content");
  if (mc) mc.classList.add("pe-modal");
  const $ = id => document.getElementById(id);
  let avatarNow = p.avatar || "", bannerNow = p.banner || "";

  const refresh = () => {
    const a = $("pf-accent").value, b = $("pf-accent2").value;
    const card = document.querySelector(".pe-card");
    card.style.setProperty("--pe-a", a);
    card.style.setProperty("--pe-b", b);
    const bi = document.querySelector(".pe-banner-img");
    bi.style.backgroundImage = bannerNow ? 'url("' + bannerNow + '")' : "";
    const ai = document.querySelector(".pe-avatar-img");
    const nm = $("pf-name").value.trim() || t("me.default");
    ai.style.backgroundImage = avatarNow ? 'url("' + avatarNow + '")' : "";
    ai.textContent = avatarNow ? "" : nm[0];
    document.querySelector(".pe-name").textContent = nm;
    const un = $("pf-user").value.trim().replace(/^@+/, "");
    document.querySelector(".pe-user").textContent = un ? "@" + un : "";
    const bio = $("pf-bio").value;
    document.querySelector(".pe-bio-preview").textContent = bio.trim();
    $("pe-bio-count").textContent = bio.length + "/160";
    document.querySelectorAll(".pe-color").forEach(l => {
      const v = l.querySelector("input").value;
      l.querySelector(".pe-color-dot").style.background = v;
      l.querySelector("small").textContent = v.toUpperCase();
    });
  };
  ["pf-name", "pf-user", "pf-bio", "pf-accent", "pf-accent2"].forEach(id => $(id).addEventListener("input", refresh));
  $("pe-avatar").onclick = () => $("pf-avatar").click();
  $("pe-banner").onclick = () => $("pf-banner").click();
  $("pf-avatar").onchange = e => pickCrop(e.target, 256, 256, t("crop.avatar"), d => { newAvatar = d; avatarNow = d; refresh(); });
  $("pf-banner").onchange = e => pickCrop(e.target, 900, 220, t("crop.banner"), d => { newBanner = d; bannerNow = d; refresh(); });
  const rs = $("pe-reset");
  if (rs) rs.onclick = () => { newAvatar = ""; newBanner = ""; avatarNow = ""; bannerNow = ""; rs.remove(); refresh(); };
  $("pe-cancel").onclick = hideModal;

  let userTimer = null;
  const checkUser = async () => {
    const umsg = $("pf-user-msg");
    const uname = $("pf-user").value.trim().replace(/^@+/, "");
    if (!uname || norm(uname) === norm(myProfile.username)) { umsg.textContent = ""; umsg.className = "user-msg"; return true; }
    umsg.textContent = t("user.checking"); umsg.className = "user-msg";
    try {
      const j = await (await fetch("/api/check_username?uid=" + encodeURIComponent(getUid()) + "&username=" + encodeURIComponent(uname))).json();
      if (norm($("pf-user").value) !== norm(uname)) return true;
      umsg.textContent = j.available ? "✓ " + L("متاح", "available") : t("user.taken");
      umsg.className = "user-msg " + (j.available ? "ok" : "err");
      return !!j.available;
    } catch (e) { umsg.textContent = ""; return true; }
  };
  $("pf-user").addEventListener("input", () => { clearTimeout(userTimer); userTimer = setTimeout(checkUser, 450); });
  refresh();

  $("pf-save").onclick = async () => {
    const saveBtn = $("pf-save");
    saveBtn.disabled = true;
    const okUser = await checkUser();
    if (!okUser) { saveBtn.disabled = false; $("pf-user").focus(); return; }
    myProfile.username = $("pf-user").value.trim().replace(/^@+/, "");
    myProfile.name = $("pf-name").value.trim();
    myProfile.bio = $("pf-bio").value.trim();
    myProfile.colors = { accent: $("pf-accent").value, accent2: $("pf-accent2").value };
    if (newAvatar !== undefined) myProfile.avatar = newAvatar;
    if (newBanner !== undefined) myProfile.banner = newBanner;
    store("mu_profile", myProfile);
    viewingProfile = null;
    restoreAccent();
    renderProfile();
    renderMe();
    emitJoin();
    setTimeout(loadProfiles, 600);
    hideModal();
    toast(t("toast.saved"));
  };
}

function norm(s) { return (s || "").trim().replace(/^@+/, "").toLowerCase(); }

/* قص وتقريب الصور */
let cropper = null;
let cropTarget = null;

function pickCrop(input, w, h, title, onDone) {
  const f = input.files && input.files[0];
  if (!f) return;
  const reader = new FileReader();
  reader.onload = () => {
    const img = document.getElementById("crop-img");
    document.getElementById("crop-title").textContent = title;
    document.getElementById("crop-modal").classList.remove("hidden");
    cropTarget = { w, h, onDone };
    img.onload = () => {
      if (cropper) { cropper.destroy(); cropper = null; }
      cropper = new Cropper(img, { aspectRatio: w / h, viewMode: 1, autoCropArea: 1, dragMode: "move", background: false });
    };
    img.src = reader.result;
  };
  reader.readAsDataURL(f);
}

function closeCrop() {
  if (cropper) { cropper.destroy(); cropper = null; }
  cropTarget = null;
  document.getElementById("crop-modal").classList.add("hidden");
  document.getElementById("crop-img").removeAttribute("src");
}

/* ================= المودال والتنبيه ================= */
function showModal(title, html) {
  const mc0 = document.querySelector("#modal .modal-content");
  if (mc0) mc0.classList.remove("pe-modal", "ple-modal");
  document.getElementById("modal-title").textContent = title;
  document.getElementById("modal-body").innerHTML = html;
  document.getElementById("modal").classList.remove("hidden");
}
function hideModal() {
  document.getElementById("modal").classList.add("hidden");
  const mc = document.querySelector("#modal .modal-content");
  if (mc) mc.classList.remove("pe-modal", "ple-modal");
}

let toastTimer = null;
function toast(text) {
  const el = document.getElementById("toast");
  el.textContent = text;
  el.classList.remove("hidden");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.classList.add("hidden"), 2200);
}

/* ================= التنقل ================= */
function showView(name) {
  document.querySelectorAll(".nav-btn").forEach(b => b.classList.toggle("active", b.dataset.view === name));
  document.querySelectorAll(".view").forEach(v => v.classList.remove("active"));
  document.getElementById("view-" + name).classList.add("active");
  if (name === "profile") {
    renderProfile();
  } else {
    viewingProfile = null;
    clearScopedColors(profileViewEl());
    restoreAccent();
  }
  if (name === "home") renderHistory();
  if (name === "likes") renderLikesView();
  if (name === "room") {
    loadRooms();
    renderMembers();
    renderRoomLikes();
  }
}

/* ================= ربط الأحداث ================= */
document.getElementById("search-btn").onclick = doSearch;
document.getElementById("search-input").addEventListener("keydown", e => { if (e.key === "Enter") doSearch(); });
document.getElementById("clear-history").onclick = () => { store("mu_history", []); try { localStorage.setItem("mu_history_cleared", "1"); } catch (e) {} pushUserData(); renderHistory(); };
document.getElementById("clear-likes").onclick = () => {
  showModal(t("likes.clear"),
    '<div class="msg">' + esc(t("confirm.clearLikes")) + '</div>' +
    '<div class="confirm-row"><button id="cl-no" class="btn">' + esc(t("confirm.no")) + '</button>' +
    '<button id="cl-yes" class="btn danger-btn">' + esc(t("confirm.yes")) + '</button></div>');
  document.getElementById("cl-no").onclick = hideModal;
  document.getElementById("cl-yes").onclick = () => {
    store("mu_likes", []);
    pushUserData();
    renderLikes();
    renderHistory();
    renderLikesView();
    hideModal();
  };
};
document.getElementById("edit-profile-btn").onclick = editProfile;
const meCard = document.getElementById("me-card");
if (meCard) meCard.onclick = () => showProfilePopup(myProfile);
document.getElementById("follow-btn").onclick = () => { if (viewingProfile) toggleFollow(viewingProfile); };
document.getElementById("popup-follow").onclick = () => { if (popupPerson) toggleFollow(popupPerson); };
document.getElementById("modal-close").onclick = hideModal;
document.getElementById("modal").onclick = e => { if (e.target.id === "modal") hideModal(); };
document.getElementById("artist-back").onclick = () => showView(artistBackView || "home");
document.getElementById("playlist-back").onclick = () => showView(playlistBackView || "home");

/* ================= بحث إضافة أصدقاء ================= */
let friendSearchSeq = 0;
async function doFriendSearch() {
  const input = document.getElementById("friend-search");
  const panel = document.getElementById("friend-results");
  if (!input || !panel) return;
  const q = norm(input.value);
  const mySeq = ++friendSearchSeq;
  if (!q) { panel.classList.add("hidden"); return; }
  let all = [];
  try {
    const j = await fetch("/api/profiles").then(r => r.json());
    all = j.profiles || [];
  } catch (e) { return; }
  if (mySeq !== friendSearchSeq) return;
  const myUid = getUid();
  const matches = all.filter(p =>
    !isMyProfile(p) &&
    (norm(p.username).includes(q) || (p.name || "").toLowerCase().includes(q))
  ).slice(0, 8);
  panel.innerHTML = "";
  if (!matches.length) {
    panel.innerHTML = '<div class="friend-empty">' + t("friends.none") + '</div>';
  } else {
    matches.forEach(p => {
      const row = document.createElement("div");
      row.className = "friend-row";
      const isFollowing = (getFollowing() || []).includes(p.uid);
      row.innerHTML =
        avatarHtml(p, "f-avatar") +
        '<div class="friend-main">' +
        '<div class="friend-name">' + esc(p.name || "مستمع") + '</div>' +
        '<div class="friend-user">@' + esc(p.username || "") + '</div>' +
        '</div>';
      const btn = document.createElement("button");
      btn.className = "friend-follow" + (isFollowing ? " following" : "");
      btn.textContent = isFollowing ? "✓" : "+";
      btn.onclick = (e) => {
        e.stopPropagation();
        toggleFollow(p);
        const now = (getFollowing() || []).includes(p.uid);
        btn.classList.toggle("following", now);
        btn.textContent = now ? "✓" : "+";
      };
      row.appendChild(btn);
      row.onclick = () => {
        panel.classList.add("hidden");
        input.value = "";
        showProfilePopup(p, row.querySelector(".friend-name") || row);
      };
      panel.appendChild(row);
    });
  }
  panel.classList.remove("hidden");
}
(function initFriendSearch() {
  const input = document.getElementById("friend-search");
  if (!input) return;
  input.addEventListener("input", doFriendSearch);
  input.addEventListener("keydown", e => { if (e.key === "Enter") doFriendSearch(); });
  document.addEventListener("click", e => {
    const panel = document.getElementById("friend-results");
    if (!panel) return;
    if (!panel.contains(e.target) && e.target !== input) panel.classList.add("hidden");
  });
})();

/* ================= بحث وإضافة داخل غرفة الاستماع ================= */
let roomSearchSeq = 0;
function raddRow(trk) {
  const row = document.createElement("div");
  row.className = "radd-item";
  row.innerHTML =
    '<img class="radd-thumb" src="' + (trk.thumbnail || "") + '" alt="">' +
    '<div class="radd-meta"><div class="radd-t">' + esc(trk.title) + '</div>' +
    '<div class="radd-a">' + esc(trk.artist || "") + (trk.duration ? " • " + fmt(trk.duration) : "") + '</div></div>';
  const add = document.createElement("button");
  add.className = "btn btn-primary radd-add";
  add.textContent = t("track.add");
  add.onclick = e => {
    e.stopPropagation();
    if (!inRoom) { toast(t("room.join_first")); return; }
    if (!canIAdd()) { toast(t("room.owner_only")); return; }
    addToQueue(trk);
    toast(t("toast.added"));
  };
  row.appendChild(add);
  return row;
}

function renderRoomLikes() {
  const panel = document.getElementById("room-search-results");
  if (!panel) return;
  const likes = getLikes();
  panel.innerHTML = "";
  if (!likes.length) {
    panel.innerHTML = '<div class="msg">' + t("likes.empty") + '</div>';
    return;
  }
  const hint = document.createElement("div");
  hint.className = "radd-hint";
  hint.textContent = "♥ " + t("room.from.likes");
  panel.appendChild(hint);
  likes.forEach(trk => panel.appendChild(raddRow(trk)));
}

async function doRoomSearch() {
  const input = document.getElementById("room-search-input");
  const panel = document.getElementById("room-search-results");
  if (!input || !panel || !input.value.trim()) { renderRoomLikes(); return; }
  const q = input.value.trim();
  const src = (document.getElementById("source-select") || {}).value || "youtube";
  const mySeq = ++roomSearchSeq;
  panel.innerHTML = '<div class="msg spinner-row"><span class="spinner"></span> ' + t("searching") + '</div>';
  try {
    const res = await fetch("/api/search", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ query: q, source: src })
    });
    const j = await res.json();
    if (mySeq !== roomSearchSeq) return;
    const results = j.results || [];
    panel.innerHTML = "";
    if (!results.length) {
      panel.innerHTML = '<div class="msg">' + t("search.fail") + '</div>';
      return;
    }
    results.forEach(trk => panel.appendChild(raddRow(trk)));
  } catch (e) {
    if (mySeq === roomSearchSeq) panel.innerHTML = '<div class="msg">' + t("search.fail") + '</div>';
  }
}
document.getElementById("room-search-btn").onclick = doRoomSearch;
document.getElementById("room-search-input").addEventListener("keydown", e => { if (e.key === "Enter") doRoomSearch(); });
document.getElementById("room-search-input").addEventListener("input", e => {
  if (!e.target.value.trim()) renderRoomLikes();
});

document.getElementById("popup-full").onclick = () => {
  const person = popupPerson;
  popupPerson = null;
  document.getElementById("profile-popup").classList.add("hidden");
  viewingProfile = isMyProfile(person) ? null : person;
  showView("profile");
};
document.getElementById("profile-popup").onclick = e => { if (e.target.id === "profile-popup") closeProfilePopup(); };
window.addEventListener("resize", () => {
  if (!document.getElementById("profile-popup").classList.contains("hidden")) positionPopup();
});

document.getElementById("crop-close").onclick = closeCrop;
document.getElementById("crop-cancel").onclick = closeCrop;
document.getElementById("crop-modal").onclick = e => { if (e.target.id === "crop-modal") closeCrop(); };
document.getElementById("crop-confirm").onclick = () => {
  if (!cropper || !cropTarget) return;
  const canvas = cropper.getCroppedCanvas({ width: cropTarget.w, height: cropTarget.h });
  const dataUrl = canvas.toDataURL("image/jpeg", 0.85);
  cropTarget.onDone(dataUrl);
  closeCrop();
};

document.getElementById("btn-play").onclick = () => {
  if (solo.active) {
    if (soloAudio.paused) soloAudio.play().catch(() => {});
    else soloAudio.pause();
    setPlayIcon(!soloAudio.paused);
    return;
  }
  if (autoplayBlocked || (state.playing && audio.paused)) {
    audio.play().then(() => { autoplayBlocked = false; }).catch(() => {});
    return;
  }
  socket.emit("play_pause");
};
document.getElementById("btn-skip").onclick = () => { if (solo.active) soloSkip(1); else socket.emit("skip", { id: state.current ? state.current.id : null }); };
document.getElementById("btn-prev").onclick = () => { if (solo.active) soloSkip(-1); else socket.emit("prev"); };
const _sb = document.getElementById("solo-badge");
if (_sb) _sb.onclick = () => exitSolo();
const npLikeBtn = document.getElementById("np-like");
npLikeBtn.onclick = () => {
  const trk = solo.active ? solo.track : state.current;
  if (!trk) return;
  toggleLike(trk);
  npLikeBtn.classList.toggle("liked", isLiked(trk));
  renderLikesView();
};
const npPlBtn = document.getElementById("np-pl");
if (npPlBtn) npPlBtn.onclick = () => {
  const trk = solo.active ? solo.track : state.current;
  if (!trk) { toast(t("np.nothing")); return; }
  addToPlaylistModal(trk);
};
const btnLoop = document.getElementById("btn-loop");
if (btnLoop) btnLoop.onclick = () => {
  state.loop = !state.loop;
  try { audio.loop = state.loop; } catch (e) {}
  try { soloAudio.loop = state.loop; } catch (e) {}
  btnLoop.classList.toggle("on", state.loop);
};
function soloPool() {
  const plPool = typeof window !== "undefined" ? window.__plPool : null;
  if (plPool && solo.track && plPool.some(x => trackKey(x) === trackKey(solo.track))) return plPool;
  if (solo.track && Array.isArray(lastResults) && lastResults.some(x => trackKey(x) === trackKey(solo.track))) return lastResults;
  const a = getLikes();
  if (a.length) return a;
  return (typeof lastSuggestions !== "undefined" && lastSuggestions) ? lastSuggestions : [];
}
function soloSkip(dir) {
  const pool = soloPool().filter(t => t && (t.stream_url || t.web_url));
  if (!pool.length) { toast("\u0645\u0627 \u0641\u064a\u0647 \u0623\u063a\u0627\u0646\u064a \u0644\u0644\u062a\u0642\u062f\u064a\u0645"); return; }
  const cur = solo.track;
  let i = cur ? pool.findIndex(t => (t.id && cur.id && t.id === cur.id) || (cur.web_url && t.web_url === cur.web_url) || t.title === cur.title) : -1;
  i = i === -1 ? 0 : (i + dir + pool.length) % pool.length;
  playSolo(pool[i]);
}
document.getElementById("btn-shuffle").onclick = () => socket.emit("shuffle", { on: !state.shuffle });
document.getElementById("shuffle-btn").onclick = () => socket.emit("shuffle", { on: !state.shuffle });

const seek = document.getElementById("seek");
seek.addEventListener("input", () => { seekDragging = true; document.getElementById("cur-time").textContent = fmt(parseFloat(seek.value)); });
seek.addEventListener("change", () => {
  seekDragging = false;
  if (solo.active) {
    const wasPlaying = !soloAudio.paused;
    soloAudio.currentTime = parseFloat(seek.value) || 0;
    if (wasPlaying) soloAudio.play().catch(() => {});
    return;
  }
  socket.emit("seek", { pos: parseFloat(seek.value) });
});

const volume = document.getElementById("volume");
function savedVolume() {
  const value = load("mu_volume", null);
  return typeof value === "number" && Number.isFinite(value) ? Math.max(0, Math.min(100, value)) : 25;
}
function rememberVolume() {
  const value = Math.round(audio.volume * 100);
  soloAudio.volume = audio.volume;
  volume.value = value;
  volume.style.setProperty("--vol-pct", value + "%");
  const mini = document.getElementById("mp-volume");
  if (mini) { mini.value = value; mini.style.setProperty("--vol-pct", value + "%"); }
  try { store("mu_volume", value); } catch (e) {}
}
audio.volume = savedVolume() / 100;
rememberVolume();
audio.addEventListener("volumechange", rememberVolume);
volume.addEventListener("input", () => { audio.volume = volume.value / 100; soloAudio.volume = audio.volume; volume.style.setProperty("--vol-pct", volume.value + "%"); });
document.getElementById("btn-volume").onclick = () => { audio.muted = !audio.muted; soloAudio.muted = audio.muted; };

/* ================= النافذة المصغرة ================= */
const miniPlayerEl = document.getElementById("mini-player");
const mpSeek = document.getElementById("mp-seek");
let mpSeekDragging = false;
let mpCaptureNext = false;

function getHotkey() {
  return settings.miniHotkey || { key: "m", ctrl: false, alt: true, shift: false };
}
function hotkeyLabel(hk) {
  const parts = [];
  if (hk.ctrl) parts.push("Ctrl");
  if (hk.alt) parts.push("Alt");
  if (hk.shift) parts.push("Shift");
  parts.push((hk.key || "").toUpperCase());
  return parts.join(" + ");
}
document.addEventListener("keydown", e => {
  const el = e.target;
  const tag = (el.tagName || "").toLowerCase();
  if (mpCaptureNext) {
    e.preventDefault();
    if (e.key !== "Escape") {
      settings.miniHotkey = { key: (e.key || "").toLowerCase(), ctrl: e.ctrlKey, alt: e.altKey, shift: e.shiftKey };
      saveSettings();
    }
    mpCaptureNext = false;
    renderSettings();
    return;
  }
  if (tag === "input" || tag === "textarea" || tag === "select" || el.isContentEditable) return;
  const hk = getHotkey();
  if ((e.key || "").toLowerCase() !== hk.key) return;
  if (e.ctrlKey !== !!hk.ctrl || e.altKey !== !!hk.alt || e.shiftKey !== !!hk.shift) return;
  e.preventDefault();
  miniPlayerEl.classList.toggle("hidden");
});
document.getElementById("mp-close").onclick = () => miniPlayerEl.classList.add("hidden");
document.getElementById("mp-play").onclick = () => {
  if (solo.active) {
    if (soloAudio.paused) soloAudio.play().catch(() => {}); else soloAudio.pause();
    return;
  }
  socket.emit("play_pause");
};
document.getElementById("mp-skip").onclick = () => { if (solo.active) soloSkip(1); else socket.emit("skip", { id: state.current ? state.current.id : null }); };
mpSeek.addEventListener("input", () => { mpSeekDragging = true; });
mpSeek.addEventListener("change", () => {
  mpSeekDragging = false;
  if (solo.active) {
    const maxd = (solo.track && solo.track.duration) || soloAudio.duration || 0;
    if (maxd) soloAudio.currentTime = (parseFloat(mpSeek.value) / 100) * maxd;
    return;
  }
  const cur = state.current;
  if (cur && cur.duration) socket.emit("seek", { pos: (parseFloat(mpSeek.value) / 100) * cur.duration });
});
document.getElementById("mp-volume").addEventListener("input", function () {
  audio.volume = this.value / 100;
  audio.muted = false;
  soloAudio.volume = audio.volume;
  soloAudio.muted = false;
  this.style.setProperty("--vol-pct", this.value + "%");
});
setInterval(() => {
  if (miniPlayerEl.classList.contains("hidden")) return;
  const cur = solo.active ? solo.track : state.current;
  const playingEl = solo.active ? !soloAudio.paused : !!(state.playing && state.current && !audio.paused);
  const mpTitle = document.getElementById("mp-title");
  mpTitle.textContent = cur ? (cur.title || "—") : "—";
  document.getElementById("mp-thumb").src = (cur && cur.thumbnail) || "";
  document.getElementById("mp-artist").textContent = cur ? (cur.artist || "") : "";
  mpMarquee();
  document.getElementById("mp-icon-play").style.display = playingEl ? "none" : "";
  document.getElementById("mp-icon-pause").style.display = playingEl ? "" : "none";
  const mpVol = document.getElementById("mp-volume");
  mpVol.value = Math.round((audio.muted ? 0 : audio.volume) * 100);
  mpVol.style.setProperty("--vol-pct", mpVol.value + "%");
  if (!mpSeekDragging) {
    let pct = 0;
    if (solo.active) {
      const maxd = (solo.track && solo.track.duration) || soloAudio.duration || 0;
      if (maxd) pct = ((soloAudio.currentTime || 0) / maxd) * 100;
    } else if (cur && cur.duration) {
      pct = (Math.min(expectedPos(), cur.duration) / cur.duration) * 100;
    }
    mpSeek.value = pct;
    mpSeek.style.setProperty("--mp-seek-pct", pct + "%");
  }
}, 400);

function mpMarquee() {
  const wrapEl = document.getElementById("mp-title-wrap");
  const spanEl = document.getElementById("mp-title");
  if (!wrapEl || !spanEl) return;
  const over = spanEl.scrollWidth - wrapEl.clientWidth;
  if (over > 4) {
    wrapEl.classList.add("scroll");
    const rtl = getComputedStyle(wrapEl).direction === "rtl";
    wrapEl.style.setProperty("--shift", (rtl ? over : -over) + "px");
    wrapEl.style.setProperty("--dur", Math.max(4, over / 22) + "s");
  } else {
    wrapEl.classList.remove("scroll");
  }
}

try {
  fetch("/api/mini_uid", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ uid: getUid() }),
  }).catch(() => {});
} catch (e) {}

audio.addEventListener("ended", () => {
  if (state.current && state.current.id === currentTrackId) socket.emit("ended", { id: currentTrackId });
});

document.querySelectorAll(".nav-btn").forEach(btn => {
  btn.onclick = () => showView(btn.dataset.view);
});

/* ================= تشغيل أولي ================= */
let lastResults = [];
let newAvatar, newBanner;
function mergeSpotifyImport(D, opts) {
  function key(t) { return ((t.title || "") + "|" + (t.artist || "")).toLowerCase(); }
  function nid() { return "sp" + Math.random().toString(36).slice(2, 10); }
  const likes = getLikes();
  let nl = 0, nt = 0;
  // سبوتيفاي يرجّع الأحدث أول — الأغاني الجديدة تنحط فوق بنفس ترتيبها
  const have = new Set(likes.map(key));
  const fresh = [];
  (D.likes || []).forEach(t => {
    const k = key(t);
    if (!have.has(k)) { have.add(k); t.id = nid(); fresh.push(t); nl++; }
  });
  store("mu_likes", fresh.concat(likes));
  pushUserData();
  const pls = getPlaylists();
  let np = 0;
  (D.playlists || []).forEach(p => {
    if (!p || !p.name) return;
    const existing = pls.find(x => (p.spotify_id && x.spotify_id === p.spotify_id) || (!x.spotify_id && (x.name || "").toLowerCase() === p.name.toLowerCase()));
    const trs = [];
    (p.tracks || []).forEach(t => {
      if (!trs.some(x => key(x) === key(t))) { t.id = nid(); trs.push(t); }
    });
    if (existing) {
      existing.spotify_id = p.spotify_id || existing.spotify_id;
      if (!existing.custom_name) existing.name = p.name;          // لو المستخدم غيّر الاسم/الصورة بنفسه نحترمه
      if (!existing.custom_img) existing.img = p.img || existing.img;
      existing.tracks = existing.tracks || [];
      trs.forEach(track => { if (!existing.tracks.some(x => key(x) === key(track))) { existing.tracks.push(track); nt++; } });
    } else {
      pls.push({ id: plId(), spotify_id: p.spotify_id, name: p.name, img: p.img || "", tracks: trs, created: Date.now() });
      np++;
    }
  });
  savePlaylists(pls);
  if (!(opts && opts.publicMode)) {   // الربط باليوزر (العام) ما يعتبر ربط حساب
    try { localStorage.setItem("mu_sp_linked", JSON.stringify({ user: D.user || "", at: Date.now(), sync_key: D.sync_key || (load("mu_sp_linked", {}) || {}).sync_key })); } catch (e) {}
  }
  try { window.dispatchEvent(new Event("harmoni-library")); } catch (e) {}
  return { likes: nl, playlists: np, tracks: nt };
}
async function handleSpotifyDone() {
  const params = new URLSearchParams(location.search);
  const ticket = params.get("t");
  if (!ticket) { history.replaceState({}, "", "/"); return; }
  let D = null;
  try {
    for (let i = 0; i < 80; i++) {
      const res = await fetch("/api/spotify_ticket?t=" + encodeURIComponent(ticket));
      const j = await res.json();
      if (j.status === "pending") { await new Promise(r => setTimeout(r, 1500)); continue; }
      D = j;
      break;
    }
    if (!D) throw new Error("timeout");
    if (D.error) throw new Error(D.error);
    const r = mergeSpotifyImport(D);
    renderSettings();
    setTimeout(() => toast(t("spotify.imported") + r.likes + " ♪ / " + r.playlists + " ♫"), 800);
    if (D.note) setTimeout(() => toast(D.note), 2600);
    renderLikesView();
    renderHistory();
    renderArtists();
    renderProfileSections(isMyProfile(viewingProfile));
  } catch (e) {
    const message = e.message === "timeout" ? t("spotify.timeout") : (e.message || t("spotify.import.fail"));
    setTimeout(() => {
      showView("settings");
      showModal(L("تعذر ربط سبوتيفاي", "Spotify connection failed"), '<p style="line-height:1.9;white-space:normal">' + esc(message) + '</p>');
    }, 800);
  }
  history.replaceState({}, "", "/");
}
(function initSpotifyDone() {
  if (location.pathname === "/spotify_done") handleSpotifyDone();
})();
(function checkSpotifyImport() {
  try {
    const im = load("mu_sp_import", null);
    if (im && (im.likes || im.playlists)) {
      localStorage.removeItem("mu_sp_import");
      setTimeout(() => toast(t("spotify.imported") + im.likes + " ♪ / " + im.playlists + " ♫"), 800);
    } else if (im) {
      localStorage.removeItem("mu_sp_import");
    }
  } catch (e) {}
})();
restoreAccent();
applyLang();
initAuthGate();
renderHistory();
renderProfile();
renderQueue();
renderMembers();
renderMe();
setPlayIcon();
renderLikesView();
loadProfiles();
loadRooms();
setInterval(() => { if (!document.hidden || inRoom) loadRooms(); }, 5000);   // البرنامج مصغّر وما أنت بغرفة؟ نوقف التحديث
document.addEventListener("visibilitychange", () => { if (!document.hidden) loadRooms(); });
loadSuggestions();
setTimeout(pullUserData, 2500);
setInterval(pushUserData, 30000);

/* ===== نسخة الجوال: قائمة جانبية + بحث علوي ===== */
(function initMobileShell() {
  const btn = document.getElementById("m-menu-btn");
  if (!btn) return;
  const setOpen = v => document.body.classList.toggle("m-drawer-open", !!v);
  btn.addEventListener("click", () => setOpen(!document.body.classList.contains("m-drawer-open")));
  const ov = document.getElementById("m-overlay");
  if (ov) ov.addEventListener("click", () => setOpen(false));
  const ms = document.getElementById("m-search");
  if (ms) ms.addEventListener("keydown", e => {
    if (e.key !== "Enter") return;
    const q = ms.value.trim();
    const mi = document.getElementById("search-input");
    if (mi) mi.value = q;
    showView("home");
    doSearch();
    setOpen(false);
  });

  /* اختيار أي خيار من القائمة يسكرها */
  document.querySelectorAll(".sidebar [data-view]").forEach(b =>
    b.addEventListener("click", () => setOpen(false)));

  /* سحب بالإصبع من أي مكان: يساراً = فتح، يميناً = إغلاق */
  const sb = document.querySelector(".sidebar");
  const ov2 = document.getElementById("m-overlay");
  let sx = 0, sy = 0, gActive = false, gStartOpen = false;
  const drawerW = () => (sb ? sb.offsetWidth : 320) || 320;
  const scrollableX = el => {
    for (let n = el; n && n !== document.body; n = n.parentElement) {
      const cs = getComputedStyle(n);
      if (/(auto|scroll)/.test(cs.overflowX) && n.scrollWidth > n.clientWidth + 10) return true;
    }
    return false;
  };
  document.addEventListener("touchstart", e => {
    const t = e.touches[0];
    const tg = e.target;
    sx = t.clientX; sy = t.clientY;
    const bad = tg.closest && tg.closest("input,textarea,select,.seek-row,.player-bar");
    const npOv = document.getElementById("np-overlay");
    const ovOpen = npOv && !npOv.classList.contains("hidden");
    gActive = !bad && !ovOpen && !scrollableX(tg);
    gStartOpen = document.body.classList.contains("m-drawer-open");
  }, { passive: true });
  document.addEventListener("touchmove", e => {
    if (!gActive || !sb) return;
    const t = e.touches[0];
    const dyy = Math.abs(t.clientY - sy);
    const dx = t.clientX - sx;
    if (!gStartOpen && dyy > 26 && Math.abs(dx) < dyy * 1.4) { gActive = false; return; }
    if (dyy > 90) { gActive = false; return; }
    let p = (gStartOpen ? 1 : 0) - dx / drawerW();
    p = Math.max(0, Math.min(1, p));
    sb.dataset.p = String(p);
    sb.style.transition = "none";
    sb.style.transform = "translateX(" + ((1 - p) * 105).toFixed(2) + "%)";
    if (ov2) { ov2.style.transition = "none"; ov2.style.opacity = (p * 0.55).toFixed(2); }
  }, { passive: true });
  const gEnd = () => {
    if (!gActive) return;
    gActive = false;
    if (!sb) return;
    const p = parseFloat(sb.dataset.p || (gStartOpen ? "1" : "0"));
    delete sb.dataset.p;
    sb.style.transition = "";
    sb.style.transform = "";
    if (ov2) { ov2.style.transition = ""; ov2.style.opacity = ""; }
    setOpen(p >= 0.5);
  };
  document.addEventListener("touchend", gEnd, { passive: true });
  document.addEventListener("touchcancel", gEnd, { passive: true });
})();

/* ===== صفحة الأغنية المكبرة (Now Playing) ===== */
(function initNpOverlay() {
  const ov = document.getElementById("np-overlay");
  if (!ov) return;
  const thumb = document.getElementById("np-thumb");
  const img = document.getElementById("np-ov-img");
  const ttl = document.getElementById("np-ov-title");
  const art = document.getElementById("np-ov-artist");
  const slotC = ov.querySelector(".np-ov-controls-slot");
  const slotS = ov.querySelector(".np-ov-seek-slot");
  const slotV = ov.querySelector(".np-ov-vol-slot");
  const controls = document.querySelector(".player-bar .player-controls");
  const seekRow = document.querySelector(".player-bar .seek-row");
  const centerHome = document.querySelector(".player-center");
  const prRight = document.querySelector(".player-right");
  const volBtn = document.getElementById("btn-volume");
  const volSlider = document.getElementById("volume");
  let artistName = "";
  function sync(force) {
    if (!force && ov.classList.contains("hidden")) return;
    img.src = thumb ? (thumb.src || "") : "";
    ttl.textContent = document.getElementById("np-title").textContent;
    artistName = document.getElementById("np-artist").textContent || "";
    art.textContent = artistName;
    art.classList.toggle("clickable", !!artistName);
  }
  window.syncNpOverlay = sync;
  const _rnp = renderNowPlaying;
  renderNowPlaying = function () { _rnp(); sync(); };
  function open() {
    if (slotC && controls) slotC.appendChild(controls);
    if (slotS && seekRow) slotS.appendChild(seekRow);
    if (slotV && volBtn && volSlider) { slotV.appendChild(volBtn); slotV.appendChild(volSlider); }
    ov.classList.remove("hidden");
    sync(true);
  }
  function close() {
    if (centerHome && controls) centerHome.appendChild(controls);
    if (centerHome && seekRow) centerHome.appendChild(seekRow);
    if (prRight && volBtn) prRight.appendChild(volBtn);
    if (prRight && volSlider) prRight.appendChild(volSlider);
    ov.classList.add("hidden");
  }
  if (thumb) thumb.addEventListener("click", open);
  const xbtn = document.getElementById("np-ov-close");
  if (xbtn) xbtn.addEventListener("click", close);
  if (art) art.addEventListener("click", () => {
    if (artistName) { close(); openArtist(artistName); }
  });
})();

/* ===== استعادة البث بعد التقديم (HLS-aware) ===== */
(function initStallRecovery() {
  const sa = document.getElementById("solo-audio");
  if (!sa) return;
  let tmr = null, rt = null, busy = false;
  let retryTrack = null, retryCount = 0;
  function rebuildSoloAt(pos) {
    if (busy || !solo.active) return;
    busy = true;
    const trk = solo.track;
    const seq = soloRequestSeq;
    if (!trk || !trk.web_url) { busy = false; return; }
    if (retryTrack !== trk) { retryTrack = trk; retryCount = 0; }
    if (++retryCount > 3) { busy = false; toast(t("toast.play.failed")); return; }
    fetch("/api/resolve_stream", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ web_url: trk.web_url, source: trk.source, title: trk.title, artist: trk.artist, refresh: true })
    }).then(r => r.json()).then(j => {
      if (!solo.active || solo.track !== trk || seq !== soloRequestSeq) return;
      if (!j || !j.stream_url) return;
      destroySoloHls();
      trk.stream_url = j.stream_url;
      trk.stream_ts = Date.now();
      const url = j.stream_url;
      sa.pause();
      if (/\.m3u8($|\?)/i.test(url) && window.Hls && Hls.isSupported()) {
        solo.hls = new Hls({ startPosition: pos || 0, xhrSetup: (x, u) => { try { x.open("GET", soloPlayableUrl(u), true); } catch (e) {} } });
        solo.hls.loadSource(url);
        solo.hls.attachMedia(sa);
        solo.hls.on(Hls.Events.MANIFEST_PARSED, () => {
          if (solo.active && solo.track === trk && seq === soloRequestSeq) sa.play().catch(() => {});
        });
      } else {
        sa.src = soloPlayableUrl(url);
        const once = () => {
          sa.removeEventListener("loadedmetadata", once);
          if (!solo.active || solo.track !== trk || seq !== soloRequestSeq) return;
          try { sa.currentTime = pos || 0; } catch (e) {}
          sa.play().catch(() => {});
        };
        sa.addEventListener("loadedmetadata", once);
      }
    }).catch(() => {}).finally(() => { busy = false; });
  }
  const sk = document.getElementById("seek");
  if (sk) sk.addEventListener("change", () => {
    if (!solo.active) return;
    clearTimeout(rt);
    const pos = parseFloat(sk.value) || sa.currentTime || 0;
    rt = setTimeout(() => {
      if (sa.readyState < 2) rebuildSoloAt(pos);
    }, 1200);
  });
  sa.addEventListener("error", () => {
    clearTimeout(tmr); clearTimeout(rt);
    if (solo.active) rebuildSoloAt(sa.currentTime || 0);
  });
  sa.addEventListener("waiting", () => {
    clearTimeout(tmr);
    tmr = setTimeout(() => {
      if (!sa.paused && sa.readyState < 2) rebuildSoloAt(sa.currentTime);
    }, 1500);
  });
  ["playing", "canplay", "pause", "ended"].forEach(ev =>
    sa.addEventListener(ev, () => { clearTimeout(tmr); clearTimeout(rt); }));
})();

/* ===== ربط بطاقة البروفايل بمستطيل القائمة الجانبية + تطلع من تحت شريط التشغيل ===== */
function fitMeCard() {
  const card = document.getElementById("me-card");
  const sb = document.querySelector(".sidebar");
  if (!card || !sb) return;
  const r = sb.getBoundingClientRect();
  if (!r || r.width < 40) return;
  card.style.left = "auto";
  card.style.right = Math.round(Math.max(0, window.innerWidth - r.right)) + "px";
  card.style.width = Math.round(r.width) + "px";
}
fitMeCard();
window.addEventListener("resize", fitMeCard);
if (window.ResizeObserver) {
  try {
    new ResizeObserver(fitMeCard).observe(document.querySelector(".sidebar"));
    setTimeout(fitMeCard, 400);
    setTimeout(fitMeCard, 1500);
  } catch (e) {}
}

/* ===== DEBUG TEMP: تقرير موقع البطاقة (بدون إطار أحمر) ===== */
setTimeout(function () {
  try {
    var card = document.getElementById("me-card");
    if (!card) { fetch("/api/client_debug", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ info: "NO CARD" }) }).catch(function () {}); return; }
    var r = card.getBoundingClientRect();
    var np = document.querySelector(".player-bar .now-playing");
    var nr = np ? np.getBoundingClientRect() : null;
    var av = document.getElementById("me-avatar");
    var img = av ? av.querySelector("img") : null;
    var info =
      "card=[" + Math.round(r.left) + ".." + Math.round(r.right) + "]x[" + Math.round(r.top) + ".." + Math.round(r.bottom) + "]" +
      " | np=" + (nr ? ("[" + Math.round(nr.left) + ".." + Math.round(nr.right) + "]x[" + Math.round(nr.top) + ".." + Math.round(nr.bottom) + "]") : "none") +
      " | img=" + (img ? (img.complete + "/" + img.naturalWidth) : "none");
    console.log("[MECARD-DEBUG]", info);
    fetch("/api/client_debug", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        info: info,
        vw: window.innerWidth, vh: window.innerHeight,
        card_class: card.className,
        ua: navigator.userAgent.slice(0, 120)
      })
    }).catch(function () {});
  } catch (e) {}
}, 2000);

/* Reuse the existing import merge; never interrupt playback or replace local songs. */
let spotifySyncRunning = false;
async function syncSpotifyLibrary() {
  const linked = load('mu_sp_linked', null);
  if (spotifySyncRunning || !linked || !linked.sync_key || !authed()) return;
  const uid = getUid();
  const stillLinked = () => getUid() === uid && (load('mu_sp_linked', {}) || {}).sync_key === linked.sync_key;
  spotifySyncRunning = true;
  try {
    const response = await fetchWithTimeout('/api/spotify_sync', {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({sync_key:linked.sync_key})});
    if (response.status === 429) return;
    if (!response.ok) throw new Error(L('تعذر تحديث سبوتيفاي؛ أعد ربط الحساب إذا استمرت المشكلة.','Spotify refresh failed; reconnect if the problem continues.'));
    const started = await response.json();
    for(let attempt=0; attempt<100; attempt++) {
      if(!stillLinked()) return;
      const result = await fetchWithTimeout('/api/spotify_ticket?t='+encodeURIComponent(started.ticket));
      if(!result.ok) throw new Error(L('تعذر تحديث سبوتيفاي','Spotify refresh failed'));
      const data = await result.json();
      if(data.status === 'pending') { await new Promise(resolve=>setTimeout(resolve,1500)); continue; }
      if(data.error) throw new Error(data.error);
      if(!stillLinked()) return;
      const added = mergeSpotifyImport(data);
      spotifyLastSync = Date.now();
      renderLikes(); renderLikesView();
      try { if (typeof renderPlaylistView === "function" && document.getElementById("view-playlist") && document.getElementById("view-playlist").classList.contains("active")) renderPlaylistView(); } catch (e) {}
      const n = (added.likes || 0) + (added.tracks || 0);
      if (n || added.playlists) toast(L("سبوتيفاي: وصلت ", "Spotify: ") + (n ? n + L(" أغنية جديدة", " new songs") : "") + (n && added.playlists ? L(" و", " and ") : "") + (added.playlists ? added.playlists + L(" بلاي لست جديدة", " new playlists") : "") + (L("", " added")));
      if(document.getElementById('view-profile').classList.contains('active')) renderProfileSections(isMyProfile(viewingProfile));
      return;
    }
  } catch (error) {
    if(stillLinked()) toast(error.message || L('تعذر تحديث سبوتيفاي','Spotify refresh failed'));
  } finally { spotifySyncRunning = false; }
}
let spotifyLastSync = 0;
setInterval(syncSpotifyLibrary, 5 * 60 * 1000);
setTimeout(syncSpotifyLibrary, 3000);
// لو الجهاز نام أو البرنامج كان مخفي وفاتت الخمس دقايق: نحدّث أول ما يرجع
document.addEventListener("visibilitychange", () => {
  if (!document.hidden && Date.now() - spotifyLastSync > 5 * 60 * 1000) syncSpotifyLibrary();
});

try { document.documentElement.setAttribute("data-app-ready", "1"); } catch (e) {}
