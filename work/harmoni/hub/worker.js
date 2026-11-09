// Harmoni Hub — Cloudflare Worker + D1 (binding name: DB; secret HUB_SECRET optional — auto-generated in D1 otherwise)
// كل نسخ هارموني (كل جهاز) تكلم هذا السيرفر عشان الحسابات والبحث والمتابعة والشارات تكون مشتركة.
// كلمات السر ما توصل هنا أبداً: البرنامج يحسب PBKDF2 على الجهاز ويرسل الناتج، والسيرفر يخزن sha256 له بس.

const USERNAME_RE = /^[a-zA-Z0-9_.؀-ۿ-]+$/;
const PUBLIC = ["name", "avatar", "banner", "bio", "colors", "likes", "following", "stickers", "decoration"];
const PRIVATE = ["playlists", "history", "stats", "taste"];
const BUILTIN = {
  founder:      { emoji: "👑", ar: "مؤسس هارموني", en: "Harmoni founder", color: "#f5c542" },
  top_listener: { emoji: "🔥", ar: "الأكثر استماعاً", en: "Top listener", color: "#ff7a3d", auto: true },
  first_user:   { emoji: "🥇", ar: "أول مستخدم", en: "First user", color: "#e0b04a" },
  vip:          { emoji: "⭐", ar: "مميز", en: "VIP", color: "#ffd166" },
  supporter:    { emoji: "💎", ar: "داعم", en: "Supporter", color: "#5cc8ff" },
};
const ORDER = ["founder", "first_user", "top_listener", "vip", "supporter"];
const SEED = { founder: "hiro", first_user: "md5n" };
const MAX_FIELD = 1500000;

const json = (o, status = 200) =>
  new Response(JSON.stringify(o), { status, headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" } });
const err = (code, status = 400) => json({ error: code }, status);
const norm = (s) => String(s || "").trim().replace(/^@+/, "").toLowerCase();
const hex = (buf) => [...new Uint8Array(buf)].map((b) => b.toString(16).padStart(2, "0")).join("");
const parse = (s, d) => { try { const v = JSON.parse(s); return v == null ? d : v; } catch (e) { return d; } };
const now = () => Math.floor(Date.now() / 1000);

async function sha256(s) {
  return hex(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(String(s))));
}
// الجداول تنشأ تلقائياً أول طلب (نفس hub/schema.sql)
let SCHEMA_OK = false;
async function ensureSchema(env) {
  if (SCHEMA_OK) return;
  await env.DB.batch([
    env.DB.prepare("CREATE TABLE IF NOT EXISTS accounts (uid TEXT PRIMARY KEY, username TEXT NOT NULL UNIQUE, salt TEXT NOT NULL, iter INTEGER NOT NULL, vh TEXT NOT NULL, profile TEXT NOT NULL, data TEXT NOT NULL, score REAL NOT NULL DEFAULT 0, updated INTEGER NOT NULL)"),
    env.DB.prepare("CREATE INDEX IF NOT EXISTS accounts_score ON accounts(score)"),
    env.DB.prepare("CREATE TABLE IF NOT EXISTS kv (k TEXT PRIMARY KEY, v TEXT NOT NULL)"),
    env.DB.prepare("CREATE TABLE IF NOT EXISTS rooms (id TEXT PRIMARY KEY, name TEXT NOT NULL, owner TEXT NOT NULL, members INTEGER NOT NULL, max_members INTEGER NOT NULL, has_password INTEGER NOT NULL, can_add INTEGER NOT NULL, updated INTEGER NOT NULL)"),
  ]);
  SCHEMA_OK = true;
}

// السر: من HUB_SECRET لو موجود، وإلا السيرفر يولّده بنفسه أول مرة ويحفظه بقاعدة البيانات
let SECRET = "";
async function secretOf(env) {
  if (env.HUB_SECRET && env.HUB_SECRET.length >= 32) return env.HUB_SECRET;
  if (SECRET) return SECRET;
  const row = await env.DB.prepare("SELECT v FROM kv WHERE k = 'secret'").first();
  if (row && row.v && row.v.length >= 32) return (SECRET = row.v);
  const fresh = hex(crypto.getRandomValues(new Uint8Array(32)));
  await env.DB.prepare("INSERT OR IGNORE INTO kv (k, v) VALUES ('secret', ?)").bind(fresh).run();
  const again = await env.DB.prepare("SELECT v FROM kv WHERE k = 'secret'").first();
  return (SECRET = again.v);
}
async function tokenFor(env, uid) {
  const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(await secretOf(env)), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  return hex(await crypto.subtle.sign("HMAC", key, new TextEncoder().encode("uid:" + uid))).slice(0, 40);
}
function same(a, b) {
  a = String(a || ""); b = String(b || "");
  if (!a || a.length !== b.length) return false;
  let x = 0;
  for (let i = 0; i < a.length; i++) x |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return x === 0;
}
async function authed(env, uid, token) {
  return !!uid && !!token && same(token, await tokenFor(env, uid));
}

// ديكوريشن الصورة (مثل ديسكورد): من مكتبة الديكوريشن المشتركة بالسيرفر، أو من اللي داخل البرنامج
const DECO_URL = /^(https:\/\/hub\.harmoni\.click\/media\/deco\/[0-9a-f]{16}\.(gif|png|jpg|webp|avif)|\/static\/decorations\/[A-Za-z0-9._-]{1,80})$/;

// ملصقات البروفايل: روابط صور من هذا السيرفر بس، 50 بالكثير، ولكل ملصق مكان بصفحة البروفايل ومكان بالبطاقة
const STICKER_URL = /^https:\/\/hub\.harmoni\.click\/media\/[A-Za-z0-9_-]{4,40}\/sticker[0-2]?-[0-9a-f]{16}$/;
const STICKER_SHAPES = ["none", "square", "circle", "star", "triangle"];
function stickerPos(s) {
  const num = (x, d, lo, hi) => { x = Number(x); if (!isFinite(x)) x = d; return Math.min(hi, Math.max(lo, x)); };
  const rot = ((num(s.rot, 0, -1e6, 1e6) % 360) + 360) % 360;
  if (s.u === "px") return { u: "px", a: s.a === "r" ? "r" : "l", x: num(s.x, 100, -200, 5000), y: num(s.y, 100, -200, 5000), w: num(s.w, 120, 8, 3000), rot };
  return { x: num(s.x, 50, 0, 100), y: num(s.y, 50, 0, 100), w: num(s.w, 25, 4, 100), rot };
}
function cleanStickers(v) {
  return (Array.isArray(v) ? v : []).slice(0, 200).filter((s) => s && STICKER_URL.test(String(s.url || ""))).slice(0, 50).map((s) =>
    Object.assign({ url: String(s.url), where: s.where === "card" ? "card" : "page", shape: STICKER_SHAPES.includes(s.shape) ? s.shape : "none" }, stickerPos(s)));
}

function cleanPublic(src, base) {
  const out = Object.assign({}, base || {});
  for (const k of PUBLIC) {
    if (!(k in (src || {}))) continue;
    let v = src[k];
    if (k === "stickers") v = cleanStickers(v);
    else if (k === "decoration") v = DECO_URL.test(String(v || "")) ? String(v) : "";
    else if (k === "likes" || k === "following") v = Array.isArray(v) ? v.slice(0, 2000) : [];
    else if (k === "colors") v = v && typeof v === "object" && !Array.isArray(v) ? v : {};
    else v = String(v == null ? "" : v);
    if (JSON.stringify(v).length > MAX_FIELD) continue;
    out[k] = v;
  }
  if (Array.isArray(out.following)) out.following = [...new Set(out.following.filter((x) => typeof x === "string"))];
  return out;
}
function cleanPrivate(src, base) {
  const out = Object.assign({}, base || {});
  for (const k of PRIVATE) {
    if (!(k in (src || {}))) continue;
    let v = src[k];
    if (k === "stats" || k === "taste") v = v && typeof v === "object" && !Array.isArray(v) ? v : {};
    else v = Array.isArray(v) ? v.slice(0, 2000) : [];
    if (k === "history" && !v.length && (out.history || []).length && !src.history_clear) continue;
    if (JSON.stringify(v).length > MAX_FIELD) continue;
    out[k] = v;
  }
  return out;
}
function scoreOf(data) {
  const st = (data && data.stats) || {};
  return (Number(st.secs) || 0) + Math.min(Number(st.total) || 0, 999999) / 1e6;
}
function userdata(row) {
  const p = parse(row.profile, {}), d = parse(row.data, {});
  return {
    likes: p.likes || [], following: p.following || [], playlists: d.playlists || [],
    history: d.history || [], stats: d.stats || {}, taste: d.taste || {},
  };
}
function publicOf(row, assign) {
  const p = parse(row.profile, {});
  return {
    uid: row.uid, name: p.name || "مستمع", avatar: p.avatar || "", banner: p.banner || "", bio: p.bio || "",
    colors: p.colors || {}, likes: p.likes || [], following: p.following || [], stickers: p.stickers || [], decoration: p.decoration || "", username: row.username,
    badges: (assign && assign[row.uid]) || [], updated: row.updated,
  };
}
async function loginPayload(env, row, assign) {
  return { uid: row.uid, token: await tokenFor(env, row.uid), profile: publicOf(row, assign), data: userdata(row) };
}
const byUsername = (env, un) => env.DB.prepare("SELECT * FROM accounts WHERE username = ?").bind(un).first();
const byUid = (env, uid) => env.DB.prepare("SELECT * FROM accounts WHERE uid = ?").bind(uid).first();

// ===== الشارات =====
async function loadBadges(env) {
  const row = await env.DB.prepare("SELECT v FROM kv WHERE k = 'badges'").first();
  const b = Object.assign({ defs: {}, assign: {}, seeded: false }, parse(row && row.v, {}));
  if (!b.seeded) {
    for (const [badge, un] of Object.entries(SEED)) {
      const acc = await byUsername(env, un);
      if (acc) {
        const l = (b.assign[acc.uid] = b.assign[acc.uid] || []);
        if (!l.includes(badge)) l.push(badge);
      }
    }
    if (Object.values(b.assign).some((l) => (l || []).includes("founder"))) {
      b.seeded = true;
      await saveBadges(env, b);
    }
  }
  return b;
}
const saveBadges = (env, b) =>
  env.DB.prepare("INSERT INTO kv (k, v) VALUES ('badges', ?) ON CONFLICT(k) DO UPDATE SET v = excluded.v").bind(JSON.stringify(b)).run();
function badgeDefs(b) {
  const out = {};
  for (const [k, v] of Object.entries(BUILTIN)) out[k] = Object.assign({}, v);
  for (const [k, v] of Object.entries(b.defs || {})) if (!out[k]) out[k] = Object.assign({}, v, { custom: true });
  return out;
}
const badgeOrder = (defs) => ORDER.concat(Object.keys(defs).filter((k) => !ORDER.includes(k)).sort());
async function badgeView(env, b) {
  b = b || (await loadBadges(env));
  const defs = badgeDefs(b), order = badgeOrder(defs), assign = {};
  for (const [u, l] of Object.entries(b.assign || {})) {
    const ok = (l || []).filter((x) => defs[x] && !defs[x].auto);
    if (ok.length) assign[u] = ok;
  }
  const top = await env.DB.prepare("SELECT uid FROM accounts WHERE score > 0 ORDER BY score DESC LIMIT 1").first();
  if (top) assign[top.uid] = (assign[top.uid] || []).concat("top_listener");
  for (const u of Object.keys(assign)) assign[u] = [...new Set(assign[u])].sort((a, c) => order.indexOf(a) - order.indexOf(c));
  return { defs, order, assign };
}

async function isFounder(env, body) {
  const uid = String((body && body.uid) || "");
  if (!(await authed(env, uid, body.token))) return false;
  const b = await loadBadges(env);
  return (b.assign[uid] || []).includes("founder");
}

// ===== المسارات =====
const routes = {
  // ===== غرف الاستماع المشتركة (كل غرفة Durable Object) =====
  "GET /rooms": async (env) => {
    await env.DB.prepare("DELETE FROM rooms WHERE (members = 0 AND updated < ?) OR updated < ?").bind(now() - 120, now() - 3 * 86400).run();
    const { results } = await env.DB.prepare("SELECT * FROM rooms WHERE members > 0 ORDER BY name LIMIT 200").all();
    return json({ rooms: (results || []).map((r) => ({ id: r.id, name: r.name, owner: r.owner, members: r.members,
      max_members: r.max_members, has_password: !!r.has_password, can_add: !!r.can_add, cloud: true })) });
  },
  "POST /rooms/new": async (env) => {
    for (let i = 0; i < 40; i++) {
      const id = String(100000 + (crypto.getRandomValues(new Uint32Array(1))[0] % 900000));
      const r = await env.DB.prepare("INSERT OR IGNORE INTO rooms (id, name, owner, members, max_members, has_password, can_add, updated) VALUES (?, '', '', 0, 0, 0, 1, ?)")
        .bind(id, now()).run();
      if (r && r.meta && r.meta.changes === 1) return json({ id });
    }
    return err("busy", 503);
  },

  "GET /health": async (env) => {
    const r = await env.DB.prepare("SELECT COUNT(*) AS n FROM accounts").first();
    return json({ ok: true, accounts: (r && r.n) || 0 });
  },

  "GET /salt": async (env, q) => {
    const row = await byUsername(env, norm(q.get("username")));
    return row ? json({ salt: row.salt, iter: row.iter }) : err("no_user", 404);
  },

  "GET /check_username": async (env, q) => {
    const un = norm(q.get("username"));
    if (!un) return json({ available: false });
    const row = await byUsername(env, un);
    return json({ available: !row || row.uid === (q.get("uid") || "") });
  },

  "POST /register": async (env, q, body) => {
    const un = norm(body.username);
    if (un.length < 3) return err("short_username");
    if (!USERNAME_RE.test(un)) return err("bad_username");
    if (!/^[0-9a-f]{16,128}$/.test(body.salt || "") || !/^[0-9a-f]{32,256}$/.test(body.hash || "")) return err("bad_auth");
    if (await byUsername(env, un)) return err("taken", 409);
    const uid = "u" + hex(crypto.getRandomValues(new Uint8Array(8))).slice(0, 15);
    const profile = cleanPublic({ name: String(body.name || un).slice(0, 60) });
    await env.DB.prepare("INSERT INTO accounts (uid, username, salt, iter, vh, profile, data, score, updated) VALUES (?, ?, ?, ?, ?, ?, '{}', 0, ?)")
      .bind(uid, un, body.salt, Number(body.iter) || 60000, await sha256(body.hash), JSON.stringify(profile), now()).run();
    return json(await loginPayload(env, await byUid(env, uid), (await badgeView(env)).assign));
  },

  "POST /login": async (env, q, body) => {
    const row = await byUsername(env, norm(body.username));
    if (!row) return err("no_user", 404);
    if (!same(await sha256(body.hash || ""), row.vh)) return err("bad_password", 401);
    return json(await loginPayload(env, row, (await badgeView(env)).assign));
  },

  // حساب قديم موجود بجهاز واحد بس → نرفعه للسيرفر (لو اليوزر فاضي)، أو نثبت إنه نفس الحساب (نفس الهاش)
  "POST /migrate": async (env, q, body) => {
    const uid = String(body.uid || ""), un = norm(body.username);
    if (!/^[A-Za-z0-9_-]{4,40}$/.test(uid) || un.length < 2 || !USERNAME_RE.test(un)) return err("bad_account");
    if (!/^[0-9a-f]{16,128}$/.test(body.salt || "") || !/^[0-9a-f]{32,256}$/.test(body.hash || "")) return err("bad_auth");
    const vh = await sha256(body.hash);
    const existing = await byUid(env, uid);
    if (existing) {
      if (!same(vh, existing.vh)) return err("conflict", 409);
      return json(await loginPayload(env, existing, (await badgeView(env)).assign));
    }
    if (await byUsername(env, un)) return err("taken", 409);
    const profile = cleanPublic(body.profile || {});
    const data = cleanPrivate(body.data || {});
    await env.DB.prepare("INSERT INTO accounts (uid, username, salt, iter, vh, profile, data, score, updated) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)")
      .bind(uid, un, body.salt, Number(body.iter) || 60000, vh, JSON.stringify(profile), JSON.stringify(data), scoreOf(data), now()).run();
    return json(await loginPayload(env, await byUid(env, uid), (await badgeView(env)).assign));
  },

  "GET /profiles": async (env, q) => {
    const meta = await env.DB.prepare("SELECT COUNT(*) AS n, MAX(updated) AS m FROM accounts").first();
    const b = await badgeView(env);
    const ver = (meta.n || 0) + "." + (meta.m || 0) + "." + await sha256(JSON.stringify(b.assign) + JSON.stringify(b.defs));
    if (q.get("v") === ver) return json({ same: true, ver, badges: b });
    const { results } = await env.DB.prepare("SELECT uid, username, profile, updated FROM accounts").all();
    return json({ ver, profiles: (results || []).map((r) => publicOf(r, b.assign)), badges: b });
  },

  // تحديث البروفايل و/أو بيانات المستخدم (توكن مطلوب)
  "POST /update": async (env, q, body) => {
    const uid = String(body.uid || "");
    if (!(await authed(env, uid, body.token))) return err("login_required", 403);
    const row = await byUid(env, uid);
    if (!row) return err("no_user", 404);
    let username = row.username;
    if (body.username && norm(body.username) !== row.username) {
      const un = norm(body.username);
      if (un.length >= 3 && USERNAME_RE.test(un) && !(await byUsername(env, un))) username = un;
    }
    const profile = cleanPublic(body.profile || {}, parse(row.profile, {}));
    const data = cleanPrivate(Object.assign({}, body.data || {}, { history_clear: body.history_clear }), parse(row.data, {}));
    await env.DB.prepare("UPDATE accounts SET username = ?, profile = ?, data = ?, score = ?, updated = ? WHERE uid = ?")
      .bind(username, JSON.stringify(profile), JSON.stringify(data), scoreOf(data), now(), uid).run();
    return json({ ok: true, username });
  },

  "GET /userdata": async (env, q) => {
    const uid = q.get("uid") || "";
    if (!(await authed(env, uid, q.get("token")))) return err("login_required", 403);
    const row = await byUid(env, uid);
    return row ? json(userdata(row)) : err("no_user", 404);
  },

  // أشيل متابع من عندي: أحذف نفسي (owner) من قائمة «أتابعهم» حقته (target)
  "POST /remove_follower": async (env, q, body) => {
    const owner = String(body.owner || ""), target = String(body.target || "");
    if (!(await authed(env, owner, body.token))) return err("login_required", 403);
    const row = await byUid(env, target);
    if (!row) return json({ ok: true });
    const p = parse(row.profile, {});
    if (Array.isArray(p.following) && p.following.includes(owner)) {
      p.following = p.following.filter((x) => x !== owner);
      await env.DB.prepare("UPDATE accounts SET profile = ?, updated = ? WHERE uid = ?").bind(JSON.stringify(p), now(), target).run();
    }
    return json({ ok: true });
  },

  // «نسيت كلمة السر»: طلب يوصل للمؤسس، وهو يحط كلمة سر جديدة من البرنامج
  "POST /reset_request": async (env, q, body) => {
    const un = norm(body.username);
    const row = un && (await byUsername(env, un));
    if (row) {
      const cur = await env.DB.prepare("SELECT v FROM kv WHERE k = 'resets'").first();
      let list = parse(cur && cur.v, []);
      if (!Array.isArray(list)) list = [];
      if (!list.some((x) => x.uid === row.uid)) {
        list.push({ uid: row.uid, username: row.username, at: now() });
        list = list.slice(-50);
        await env.DB.prepare("INSERT INTO kv (k, v) VALUES ('resets', ?) ON CONFLICT(k) DO UPDATE SET v = excluded.v").bind(JSON.stringify(list)).run();
      }
    }
    return json({ ok: true });   // نفس الرد دايماً (ما نكشف إذا اليوزر موجود)
  },

  "POST /admin/reset_requests": async (env, q, body) => {
    if (!(await isFounder(env, body))) return err("forbidden", 403);
    const cur = await env.DB.prepare("SELECT v FROM kv WHERE k = 'resets'").first();
    const list = parse(cur && cur.v, []);
    return json({ requests: Array.isArray(list) ? list : [] });
  },

  "POST /admin/reset_password": async (env, q, body) => {
    if (!(await isFounder(env, body))) return err("forbidden", 403);
    const target = String(body.target || "");
    if (!/^[0-9a-f]{16,128}$/.test(body.salt || "") || !/^[0-9a-f]{32,256}$/.test(body.hash || "")) return err("bad_auth");
    const row = await byUid(env, target);
    if (!row) return err("no_user", 404);
    await env.DB.prepare("UPDATE accounts SET salt = ?, iter = ?, vh = ?, updated = ? WHERE uid = ?")
      .bind(body.salt, Number(body.iter) || 60000, await sha256(body.hash), now(), target).run();
    const cur = await env.DB.prepare("SELECT v FROM kv WHERE k = 'resets'").first();
    const list = parse(cur && cur.v, []);
    if (Array.isArray(list) && list.some((x) => x.uid === target)) {
      await env.DB.prepare("UPDATE kv SET v = ? WHERE k = 'resets'").bind(JSON.stringify(list.filter((x) => x.uid !== target))).run();
    }
    return json({ ok: true, username: row.username });
  },

  "GET /badges": async (env) => json(await badgeView(env)),

  "POST /badges/admin": async (env, q, body) => {
    const uid = String(body.uid || "");
    if (!(await authed(env, uid, body.token))) return err("forbidden", 403);
    const b = await loadBadges(env);
    if (!((b.assign[uid] || []).includes("founder"))) return err("forbidden", 403);
    const action = body.action, target = String(body.target || "");
    let badge = String(body.badge || "");
    const defs = badgeDefs(b);
    if (action === "check") return json({ admin: true });
    if (action === "grant" || action === "revoke") {
      if (!(await byUid(env, target))) return err("no_user", 404);
      if (!defs[badge] || defs[badge].auto) return err("bad_badge");
      const l = b.assign[target] || [];
      if (action === "grant") { if (!l.includes(badge)) l.push(badge); b.assign[target] = l; }
      else {
        if (badge === "founder" && target === uid) return err("keep_founder");
        b.assign[target] = l.filter((x) => x !== badge);
        if (!b.assign[target].length) delete b.assign[target];
      }
    } else if (action === "create") {
      const emoji = String(body.emoji || "").trim(), ar = String(body.ar || "").trim().slice(0, 30);
      const en = String(body.en || "").trim().slice(0, 30);
      if (!emoji || !/^\S{1,8}$/u.test(emoji) || !ar) return err("bad_def");
      badge = "c" + Date.now().toString(16);
      b.defs[badge] = { emoji, ar, en: en || ar, color: "#9aa4ff" };
    } else if (action === "delete") {
      if (!(b.defs || {})[badge]) return err("builtin");
      delete b.defs[badge];
      for (const u of Object.keys(b.assign)) {
        b.assign[u] = b.assign[u].filter((x) => x !== badge);
        if (!b.assign[u].length) delete b.assign[u];
      }
    } else if (action === "import") {
      // مرة وحدة: نجيب الشارات اللي سواها المؤسس على جهازه قبل السيرفر
      for (const [k, v] of Object.entries(body.defs || {})) {
        if (!BUILTIN[k] && !b.defs[k] && v && v.emoji && v.ar) b.defs[k] = { emoji: String(v.emoji).slice(0, 8), ar: String(v.ar).slice(0, 30), en: String(v.en || v.ar).slice(0, 30), color: v.color || "#9aa4ff" };
      }
      const all = badgeDefs(b);
      for (const [u, l] of Object.entries(body.assign || {})) {
        if (!Array.isArray(l) || !(await byUid(env, u))) continue;
        const cur = b.assign[u] || [];
        for (const x of l) if (all[x] && !all[x].auto && !cur.includes(x)) cur.push(x);
        if (cur.length) b.assign[u] = cur;
      }
    } else return err("bad_action");
    await saveBadges(env, b);
    return json(Object.assign({ ok: true, badge }, await badgeView(env, b)));
  },
};

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const path = url.pathname.replace(/\/+$/, "") || "/";
    // صور البروفايل الكبيرة (GIF لين 20MB) على Cloudflare R2 — تنرفع وتنعرض بدون ما تمر بقاعدة البيانات
    if (path === "/decorations" && request.method === "GET") return decoList(env);
    if (path === "/decorations/add" && request.method === "POST") return decoAdd(env, request, url);
    if (path === "/decorations/delete" && request.method === "POST") return decoDelete(env, request);
    const dm = /^\/media\/(deco\/[0-9a-f]{16}\.(?:gif|png|jpg|webp|avif))$/.exec(path);
    if (dm && request.method === "GET") return mediaGet(env, dm[1]);
    const med = /^\/media\/([A-Za-z0-9_-]{4,40}\/(?:avatar|banner|sticker[0-2]?)-[0-9a-f]{16})$/.exec(path);
    if (med && request.method === "GET") return mediaGet(env, med[1]);
    if (path === "/media" && request.method === "POST") return mediaPut(env, request, url);
    if (path === "/media/delete" && request.method === "POST") return mediaDelete(env, request, url);
    if (path === "/media/usage" && request.method === "GET") return mediaUsageRoute(env, request, url);
    if (path === "/media" && request.method === "OPTIONS") return new Response(null, { status: 204 });
    const inv = /^\/r\/(\d{6})$/.exec(path);
    if (inv) return invitePage(inv[1]);
    const wsm = /^\/room\/(\d{6})\/ws$/.exec(path);
    if (wsm) {
      if (!env.ROOMS) return err("rooms_not_configured", 500);
      if (request.headers.get("Upgrade") !== "websocket") return err("expected_websocket", 426);
      return env.ROOMS.get(env.ROOMS.idFromName(wsm[1])).fetch(request);
    }
    const fn = routes[request.method + " " + path];
    if (!fn) return path === "/" ? json({ harmoni: "hub", ok: true }) : err("not_found", 404);
    if (!env.DB) return err("hub_not_configured", 500);
    await ensureSchema(env);
    let body = {};
    if (request.method === "POST") {
      const len = Number(request.headers.get("content-length") || 0);
      if (len > 8000000) return err("too_big", 413);
      body = (await request.json().catch(() => null)) || {};
    }
    try {
      return await fn(env, url.searchParams, body);
    } catch (e) {
      const msg = String((e && e.message) || e);
      if (/UNIQUE/i.test(msg)) return err("taken", 409);
      return json({ error: "server", detail: msg.slice(0, 200) }, 500);
    }
  },
};


const MEDIA_MAX = 20 * 1024 * 1024;          // الصورة والغلاف
const STICKER_MAX = 95 * 1024 * 1024;        // الملصق الواحد (حد كلاودفلير للطلب 100MB)
const QUOTA = 1024 * 1024 * 1024;            // مساحة كل مستخدم: 1GB (تشمل الصورة والغلاف والملصقات)
const MEDIA_TYPES = ["image/gif", "image/png", "image/jpeg", "image/webp"];
const MEDIA_KINDS = ["avatar", "banner", "sticker", "sticker0", "sticker1", "sticker2"];
async function mediaAuth(env, url, body) {
  if (!env.MEDIA) return [null, err("media_not_configured", 501)];
  if (!env.DB) return [null, err("hub_not_configured", 500)];
  await ensureSchema(env);
  const uid = (body && body.uid) || url.searchParams.get("uid") || "";
  const token = (body && body.token) || url.searchParams.get("token") || "";
  if (!/^[A-Za-z0-9_-]{4,40}$/.test(uid) || !(await authed(env, uid, token))) return [null, err("login_required", 403)];
  return [uid, null];
}
async function mediaUsage(env, uid) {
  let used = 0, cursor;
  const sizes = {};
  do {
    const r = await env.MEDIA.list({ prefix: uid + "/", cursor });
    for (const o of r.objects || []) { used += o.size || 0; sizes[o.key] = o.size || 0; }
    cursor = r.truncated ? r.cursor : undefined;
  } while (cursor);
  return { used, sizes };
}
const quotaOf = (env) => Number(env.MEDIA_QUOTA) || QUOTA;
async function mediaPut(env, request, url) {
  const [uid, bad] = await mediaAuth(env, url);
  if (bad) return bad;
  const kind = url.searchParams.get("kind") || "";
  if (!MEDIA_KINDS.includes(kind)) return err("bad_request");
  const type = (request.headers.get("content-type") || "").split(";")[0].trim().toLowerCase();
  if (!MEDIA_TYPES.includes(type)) return err("bad_type");
  const len = Number(request.headers.get("content-length") || 0);
  if (!len) return err("length_required", 411);
  if (len > (kind.startsWith("sticker") ? STICKER_MAX : MEDIA_MAX)) return err("too_big", 413);
  // الصورة/الغلاف القديم ينحذف بعد الرفع — فما ينحسب من المساحة
  const k = "media:" + uid + ":" + kind;
  const old = kind === "sticker" ? null : await env.DB.prepare("SELECT v FROM kv WHERE k = ?").bind(k).first();
  const { used, sizes } = await mediaUsage(env, uid);
  const freed = old && old.v ? (sizes[old.v] || 0) : 0;
  if (used - freed + len > quotaOf(env)) return json({ error: "quota", used, limit: quotaOf(env) }, 413);
  const key = uid + "/" + kind + "-" + hex(crypto.getRandomValues(new Uint8Array(8)));
  await env.MEDIA.put(key, request.body, { httpMetadata: { contentType: type, cacheControl: "public, max-age=31536000, immutable" } });
  if (kind !== "sticker") {
    await env.DB.prepare("INSERT OR REPLACE INTO kv (k, v) VALUES (?, ?)").bind(k, key).run();
    if (old && old.v && old.v !== key) { try { await env.MEDIA.delete(old.v); } catch (e) {} }
  }
  return json({ ok: true, url: "https://hub.harmoni.click/media/" + key, key, used: used - freed + len, limit: quotaOf(env) });
}
async function mediaDelete(env, request, url) {
  const body = (await request.json().catch(() => null)) || {};
  const [uid, bad] = await mediaAuth(env, url, body);
  if (bad) return bad;
  const keys = (Array.isArray(body.keys) ? body.keys : [body.key]).filter((x) => typeof x === "string" && x.startsWith(uid + "/")).slice(0, 200);
  for (const key of keys) { try { await env.MEDIA.delete(key); } catch (e) {} }
  const { used } = await mediaUsage(env, uid);
  return json({ ok: true, deleted: keys.length, used, limit: quotaOf(env) });
}
async function mediaUsageRoute(env, request, url) {
  const [uid, bad] = await mediaAuth(env, url);
  if (bad) return bad;
  const { used, sizes } = await mediaUsage(env, uid);
  // ملفات الملصقات اللي عنده (عشان يقدر يسترجع ملصقات انشالت من بروفايله بالغلط)
  const stickers = Object.keys(sizes).filter((k) => /\/sticker[0-2]?-[0-9a-f]{16}$/.test(k));
  return json({ used, limit: quotaOf(env), stickers });
}
// ===== مكتبة الديكوريشن المشتركة (المؤسس يضيف — الكل يختار منها) =====
const DECO_TYPES = { "image/gif": "gif", "image/png": "png", "image/apng": "png", "image/jpeg": "jpg", "image/webp": "webp", "image/avif": "avif" };
async function decoLoad(env) {
  await ensureSchema(env);
  const row = await env.DB.prepare("SELECT v FROM kv WHERE k = 'decorations'").first();
  return parse(row && row.v, []);
}
const decoSave = (env, list) => env.DB.prepare("INSERT OR REPLACE INTO kv (k, v) VALUES ('decorations', ?)").bind(JSON.stringify(list)).run();
async function decoList(env) {
  if (!env.DB) return err("hub_not_configured", 500);
  return json({ decorations: await decoLoad(env) });
}
async function decoAdd(env, request, url) {
  if (!env.MEDIA) return err("media_not_configured", 501);
  if (!env.DB) return err("hub_not_configured", 500);
  await ensureSchema(env);
  const body = { uid: url.searchParams.get("uid") || "", token: url.searchParams.get("token") || "" };
  if (!(await isFounder(env, body))) return err("founder_only", 403);
  const type = (request.headers.get("content-type") || "").split(";")[0].trim().toLowerCase();
  const ext = DECO_TYPES[type];
  if (!ext) return err("bad_type");
  const len = Number(request.headers.get("content-length") || 0);
  if (!len) return err("length_required", 411);
  if (len > 20 * 1024 * 1024) return err("too_big", 413);
  const name = String(url.searchParams.get("name") || "").replace(/[^\w\u0600-\u06FF .()-]/g, "").trim().slice(0, 60) || "decoration";
  const list = await decoLoad(env);
  if (list.length >= 300) return err("too_many", 400);
  const dup = list.find((d) => d.name === name && url.searchParams.get("unique") === "1");
  if (dup) return json({ ok: true, decoration: dup, decorations: list, existed: true });
  const key = "deco/" + hex(crypto.getRandomValues(new Uint8Array(8))) + "." + ext;
  await env.MEDIA.put(key, request.body, { httpMetadata: { contentType: type, cacheControl: "public, max-age=31536000, immutable" } });
  const d = { id: key.slice(5), name, url: "https://hub.harmoni.click/media/" + key, at: now() };
  list.push(d);
  await decoSave(env, list);
  return json({ ok: true, decoration: d, decorations: list });
}
async function decoDelete(env, request) {
  const body = (await request.json().catch(() => null)) || {};
  if (!env.DB) return err("hub_not_configured", 500);
  if (!(await isFounder(env, body))) return err("founder_only", 403);
  const list = await decoLoad(env);
  const d = list.find((x) => x.id === body.id);
  if (!d) return err("not_found", 404);
  try { if (env.MEDIA) await env.MEDIA.delete("deco/" + d.id); } catch (e) {}
  const left = list.filter((x) => x.id !== body.id);
  await decoSave(env, left);
  return json({ ok: true, decorations: left });
}

async function mediaGet(env, key) {
  if (!env.MEDIA) return err("media_not_configured", 501);
  const obj = await env.MEDIA.get(key);
  if (!obj) return err("not_found", 404);
  return new Response(obj.body, { headers: {
    "content-type": (obj.httpMetadata && obj.httpMetadata.contentType) || "application/octet-stream",
    "cache-control": "public, max-age=31536000, immutable", "access-control-allow-origin": "*",
  } });
}

// صفحة الدعوة: hub.harmoni.click/r/123456 — تشتغل دايماً (حتى لو جهاز صاحب البرنامج طافي)
function invitePage(code) {
  const html = `<!doctype html><html lang="ar" dir="rtl"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Harmoni — دعوة غرفة</title><style>
body{margin:0;min-height:100vh;display:grid;place-items:center;background:#0f1d1c;color:#eef;font:16px system-ui,-apple-system,"Segoe UI",Tahoma,sans-serif}
.c{max-width:420px;margin:16px;padding:28px;border-radius:20px;background:#173a37;text-align:center;box-shadow:0 20px 50px rgba(0,0,0,.4)}
h1{font-size:22px;margin:0 0 6px}.code{font-size:40px;letter-spacing:6px;font-weight:800;margin:14px 0;direction:ltr}
p{opacity:.85;line-height:1.7;margin:8px 0}a.b{display:inline-block;margin-top:14px;padding:12px 22px;border-radius:999px;background:#3ddc97;color:#06231a;font-weight:700;text-decoration:none}
button{margin-top:10px;background:none;border:1px solid #6aa;color:#cfe;border-radius:999px;padding:8px 16px;cursor:pointer}
</style></head><body><div class="c"><h1>🎧 تعال اسمع معي في Harmoni</h1>
<div class="code">${code}</div>
<p>عندك هارموني؟ افتحه ← «غرفة» ← اكتب الكود فوق.</p>
<button onclick="navigator.clipboard&&navigator.clipboard.writeText('${code}');this.textContent='تم النسخ ✓'">انسخ الكود</button>
<p>ما عندك البرنامج؟</p><a class="b" href="https://music.harmoni.click/?room=${code}">افتح في المتصفح</a>
</div></body></html>`;
  return new Response(html, { headers: { "content-type": "text/html; charset=utf-8", "cache-control": "no-store" } });
}

// ===================== غرف الاستماع المشتركة =====================
// الغرفة نفسها (القائمة، الأغنية الحالية، وين وصلت، مين موجود) على كلاودفلير — كذا الغرف تشتغل حتى لو
// جهاز صاحب البرنامج طافي. الصوت كل واحد يجيبه من هارموني اللي عنده (ما يمر من هنا أبداً).
const TRACK_KEYS = ["title", "artist", "duration", "thumbnail", "web_url", "source", "added_by", "cuts", "kind", "album", "explicit"];
const MEMBER_KEYS = ["uid", "name", "username", "avatar", "banner", "bio", "colors", "likes", "following"];
const tnow = () => Date.now() / 1000;
const rid16 = () => hex(crypto.getRandomValues(new Uint8Array(8)));

function cleanTrack(t, keepId) {
  if (!t || typeof t !== "object") return null;
  const out = {};
  for (const k of TRACK_KEYS) {
    if (!(k in t) || t[k] == null) continue;
    const v = t[k];
    if (k === "cuts") { if (Array.isArray(v)) out.cuts = v.slice(0, 20).filter((c) => Array.isArray(c) && c.length === 2).map((c) => [+c[0] || 0, +c[1] || 0]); continue; }
    if (k === "duration") { out.duration = +v || 0; continue; }
    if (k === "explicit") { out.explicit = !!v; continue; }
    out[k] = String(v).slice(0, k === "thumbnail" ? 2000 : 500);
  }
  if (!out.web_url && !out.title) return null;
  out.id = keepId && typeof t.id === "string" && /^[A-Za-z0-9_-]{4,64}$/.test(t.id) ? t.id : rid16();
  return out;
}
function cleanMember(m) {
  const out = {};
  for (const k of MEMBER_KEYS) {
    let v = (m || {})[k];
    if (k === "likes" || k === "following") v = Array.isArray(v) ? v.slice(0, 500) : [];
    else if (k === "colors") v = v && typeof v === "object" && !Array.isArray(v) ? v : {};
    else { v = String(v == null ? "" : v); v = (k === "avatar" || k === "banner") ? (v.length > 300000 ? "" : v) : v.slice(0, 300); }
    out[k] = v;
  }
  if (JSON.stringify(out.likes).length > 150000) out.likes = [];
  out.name = out.name.trim() || "مستمع";
  return out;
}

// منطق الغرفة بدون أي شي خاص بكلاودفلير (نفس الكود يشتغل في dev-server.mjs للاختبارات)
// io = { rid, env, load(), save(s), drop(), later(ms), list(prefix), put(k,v), del(k), conns() → [{id, att, send(text), close()}], setAtt(conn, att), stale(conn) }
export class RoomCore {
  constructor(io) { this.io = io; this.s = null; }

  async state() {
    if (this.s === null) this.s = (await this.io.load()) || false;
    return this.s;
  }
  joined() { return this.io.conns().filter((c) => c.att && c.att.joined); }
  // بيانات الأعضاء (الاسم والصورة…) بالتخزين — مو بالـ attachment لأن كلاودفلير يسمح له بـ 2KB بس
  async loadMembers() {
    if (this.mem) return;
    this.mem = new Map();
    const all = await this.io.list("m:");
    for (const [k, v] of all) this.mem.set(k.slice(2), v);
  }
  member(c) { return (this.mem && this.mem.get(c.id)) || { name: "مستمع", uid: "" }; }
  async setMember(c, m) { this.mem.set(c.id, m); await this.io.put("m:" + c.id, m); }
  async forget(c) { if (this.mem && this.mem.delete(c.id)) await this.io.del("m:" + c.id); }
  async save() { if (this.s) await this.io.save(this.s); }

  view(conn) {
    const s = this.s;
    const members = this.joined().map((c) => Object.assign({ id: c.id }, this.member(c)));
    return {
      rid: s.rid, name: s.name, queue: s.queue, current: s.current, playing: s.playing, position: s.position,
      updated_at: s.updated_at, shuffle: s.shuffle, members, owner_sid: s.owner_conn, max_members: s.max_members,
      has_password: !!s.password, can_add: s.can_add, personal: false, server_time: tnow(), cloud: true,
    };
  }
  // رسالة WebSocket بكلاودفلير حدها 1MB: لو الصور كبيرة نخففها (الغلاف ← الصورة ← الإعجابات)
  fit(state) {
    let text = JSON.stringify(state);
    for (const k of ["banner", "likes", "avatar", "bio"]) {
      if (text.length < 900000) break;
      state.members = state.members.map((m) => Object.assign({}, m, { [k]: k === "likes" ? [] : "" }));
      text = JSON.stringify(state);
    }
    return state;
  }
  broadcast(extra) {
    for (const c of this.joined()) {
      try { c.send(JSON.stringify({ t: "state", you: c.id, state: this.fit(this.view(c)) })); } catch (e) {}
      if (extra) try { c.send(JSON.stringify(extra)); } catch (e) {}
    }
  }
  async index() {
    const s = this.s, env = this.io.env;
    if (!env || !env.DB) return;
    try {
      await ensureSchema(env);
      const n = s ? this.joined().length : 0;
      if (!s || !n) { await env.DB.prepare("DELETE FROM rooms WHERE id = ?").bind(this.io.rid).run(); return; }
      const owner = this.joined().find((c) => c.id === s.owner_conn);
      await env.DB.prepare("INSERT OR REPLACE INTO rooms (id, name, owner, members, max_members, has_password, can_add, updated) VALUES (?, ?, ?, ?, ?, ?, ?, ?)")
        .bind(s.rid, s.name, owner ? this.member(owner).name : s.owner_name || "", n, s.max_members, s.password ? 1 : 0, s.can_add ? 1 : 0, now()).run();
    } catch (e) {}
  }
  pruneStale() {
    for (const c of this.io.conns()) if (this.io.stale(c)) { try { c.close(); } catch (e) {} }
  }

  async open(conn) { this.io.setAtt(conn, { joined: false, at: tnow() }); }

  async expire() {
    const s = await this.state();
    await this.loadMembers();
    if (!s || this.joined().length) return;
    if (tnow() - (s.empty_since || 0) < (this.io.grace || 110)) { await this.io.later(120000); return; }
    this.s = false;
    this.mem = new Map();
    await this.io.drop();
    await this.index();
  }

  async message(conn, text) {
    let m;
    try { m = JSON.parse(text); } catch (e) { return; }
    if (!m || typeof m !== "object") return;
    const s = await this.state();
    await this.loadMembers();
    if (m.t === "hello") return this.hello(conn, m, s);
    if (!s || !conn.att || !conn.att.joined) return;
    const owner = conn.id === s.owner_conn;
    const canEdit = owner || s.can_add;
    const send = (o) => { try { conn.send(JSON.stringify(o)); } catch (e) {} };
    let changed = true, indexIt = false, extra = null;
    switch (m.t) {
      case "add": {
        if (!canEdit) { send({ t: "add_error", error: "فقط مالك الغرفة يمكنه الإضافة" }); return; }
        const tr = cleanTrack(m.track);
        if (!tr) { send({ t: "add_error", error: "bad_track" }); return; }
        tr.added_by = tr.added_by || this.member(conn).name;
        if (m.play_now) { this.pushHistory(s.current); this.setCurrent(s, tr); }
        else if (!s.current) this.setCurrent(s, tr);
        else { if (s.queue.length >= 500) { send({ t: "add_error", error: "القائمة ممتلية" }); return; } s.queue.push(tr); }
        send({ t: "add_ok" });
        break;
      }
      case "remove":
        if (!canEdit) { send({ t: "add_error", error: "فقط مالك الغرفة يحذف الأغاني" }); return; }
        if (s.current && s.current.id === m.id) this.next(s);
        else s.queue = s.queue.filter((t) => t.id !== m.id);
        break;
      case "skip":
        if (m.id && (!s.current || s.current.id !== m.id)) return;
        this.next(s);
        break;
      case "ended": {
        const cur = s.current;
        if (!cur || cur.id !== m.id) return;
        const pos = s.position + (s.playing ? tnow() - s.updated_at : 0);
        if ((cur.duration || 0) > 30 && pos < cur.duration - 8) return;
        this.next(s);
        break;
      }
      case "failed": {
        // عضو ما قدر يشغّل الأغنية: لو نص الموجودين أو أكثر فشلوا ننتقل للي بعدها للكل
        const cur = s.current;
        if (!cur || cur.id !== m.id) return;
        s.fails = s.fails && s.fails.id === cur.id ? s.fails : { id: cur.id, who: [] };
        if (!s.fails.who.includes(conn.id)) s.fails.who.push(conn.id);
        if (s.fails.who.length < Math.ceil(this.joined().length / 2)) { await this.save(); return; }
        extra = { t: "track_failed", title: cur.title || "" };
        this.next(s);
        break;
      }
      case "prev": {
        if (!s.history.length) return;
        const p = s.history.pop();
        if (s.current) s.queue.unshift(s.current);
        this.setCurrent(s, p);
        break;
      }
      case "play_pause":
        if (!s.current) return;
        if (s.playing) { s.position += tnow() - s.updated_at; s.playing = false; } else s.playing = true;
        s.updated_at = tnow();
        break;
      case "seek":
        if (!s.current) return;
        s.position = Math.max(0, +m.pos || 0);
        s.updated_at = tnow();
        break;
      case "shuffle":
        s.shuffle = !!m.on;
        break;
      case "play_now": {
        const i = s.queue.findIndex((t) => t.id === m.id);
        if (i < 0) return;
        const [tr] = s.queue.splice(i, 1);
        this.pushHistory(s.current);
        this.setCurrent(s, tr);
        break;
      }
      case "settings":
        if (!owner) { send({ t: "add_error", error: "فقط مالك الغرفة يغيّر الإعدادات" }); return; }
        this.applySettings(s, m.data || {});
        indexIt = true;
        break;
      case "kick": {
        if (!owner || m.id === conn.id) return;
        const target = this.joined().find((c) => c.id === m.id);
        if (!target) return;
        const tm = this.member(target);
        s.banned = (s.banned || []).concat([target.id], tm.uid ? [tm.uid] : []).slice(-200);
        try { target.send(JSON.stringify({ t: "kicked" })); } catch (e) {}
        this.io.setAtt(target, Object.assign({}, target.att, { joined: false }));
        await this.forget(target);
        try { target.close(); } catch (e) {}
        indexIt = true;
        break;
      }
      case "leave":
        await this.close(conn);
        try { conn.close(); } catch (e) {}
        return;
      default:
        changed = false;
    }
    if (!changed) return;
    await this.save();
    this.broadcast(extra);
    if (indexIt) await this.index();
    this.pruneStale();
  }

  pushHistory(t) {
    if (!t) return;
    this.s.history.push(t);
    if (this.s.history.length > 50) this.s.history.shift();
  }
  setCurrent(s, t) {
    s.current = t; s.position = 0; s.updated_at = tnow(); s.playing = !!t;
  }
  next(s) {
    this.pushHistory(s.current);
    if (s.queue.length) {
      const i = s.shuffle ? Math.floor(Math.random() * s.queue.length) : 0;
      this.setCurrent(s, s.queue.splice(i, 1)[0]);
    } else {
      s.current = null; s.playing = false; s.position = 0; s.updated_at = tnow();
    }
  }
  applySettings(s, d) {
    if ("name" in d) s.name = String(d.name || "").trim().slice(0, 40) || s.name;
    if ("password" in d) s.password = String(d.password || "").trim().slice(0, 32);
    if ("can_add" in d) s.can_add = !!d.can_add;
    if ("max_members" in d) { const n = parseInt(d.max_members, 10) || 0; s.max_members = n <= 0 ? 0 : Math.max(2, Math.min(n, 100)); }
  }

  async hello(conn, m, s) {
    const send = (o) => { try { conn.send(JSON.stringify(o)); } catch (e) {} };
    if (conn.att && conn.att.joined) return;
    const member = cleanMember(m.member);
    if (member.uid && !(await authed(this.io.env, member.uid, m.token).catch(() => false))) member.uid = "";
    if (!s) {
      if (!m.create) { send({ t: "join_error", reason: "not_found" }); try { conn.close(); } catch (e) {} return; }
      const c = m.create || {};
      s = this.s = {
        rid: this.io.rid, name: String(c.name || "").trim().slice(0, 40) || "غرفة " + this.io.rid, queue: [], current: null,
        playing: false, position: 0, updated_at: tnow(), shuffle: false, history: [], owner_conn: conn.id,
        owner_uid: member.uid, owner_name: member.name, max_members: 0, password: "", can_add: c.can_add !== false,
        banned: [], created: tnow(),
      };
      this.applySettings(s, { max_members: c.max_members, password: c.password });
      const carry = c.carry || {};
      const cur = cleanTrack(carry.current, true);
      if (cur) {
        s.current = cur;
        s.position = Math.max(0, +carry.position || 0);
        s.playing = carry.playing !== false;
        s.updated_at = tnow();
      }
      if (Array.isArray(carry.queue)) s.queue = carry.queue.slice(0, 500).map((t) => cleanTrack(t, true)).filter(Boolean);
      s.shuffle = !!carry.shuffle;
    } else {
      const isOwner = member.uid && member.uid === s.owner_uid;
      if (!isOwner) {
        if ((member.uid && (s.banned || []).includes(member.uid))) return this.refuse(conn, "kicked");
        if (s.password) {
          const pw = String(m.password || "");
          if (!pw) return this.refuse(conn, "need_password");
          if (!same(pw, s.password)) return this.refuse(conn, "wrong_password");
        }
        if (s.max_members && this.joined().length >= s.max_members) return this.refuse(conn, "full");
      }
      if (isOwner || !this.joined().some((c) => c.id === s.owner_conn)) {
        s.owner_conn = conn.id;
        if (isOwner || !s.owner_uid) { s.owner_uid = member.uid; s.owner_name = member.name; }
      }
    }
    const client = String(m.client || "").slice(0, 40);
    if (client) {
      // نفس الجهاز رجع يتصل (النت انقطع بصمت): نشيل الاتصال القديم عشان ما يطلع مرتين
      for (const c of this.joined()) {
        if (c.id !== conn.id && c.att.client === client) {
          if (s.owner_conn === c.id) s.owner_conn = conn.id;
          this.io.setAtt(c, Object.assign({}, c.att, { joined: false }));
          await this.forget(c);
          try { c.close(); } catch (e) {}
        }
      }
    }
    this.io.setAtt(conn, { joined: true, at: (conn.att && conn.att.at) || tnow(), client });
    await this.setMember(conn, member);
    delete s.empty_since;
    await this.save();
    this.broadcast();
    await this.index();
    this.pruneStale();
  }
  refuse(conn, reason) {
    try { conn.send(JSON.stringify({ t: "join_error", reason })); } catch (e) {}
    try { conn.close(); } catch (e) {}
  }

  async close(conn) {
    const s = await this.state();
    await this.loadMembers();
    const was = conn.att && conn.att.joined;
    this.io.setAtt(conn, Object.assign({}, conn.att || {}, { joined: false }));
    await this.forget(conn);
    if (!s) return;
    const left = this.joined().filter((c) => c.id !== conn.id);
    if (!left.length) {
      // آخر واحد طلع: نخلي الغرفة دقيقتين (لو النت انقطع عنه ثواني يرجع لنفس الغرفة) وبعدين تنحذف
      s.empty_since = tnow();
      await this.save();
      await this.index();
      await this.io.later(120000);
      return;
    }
    if (!was) return;
    if (s.owner_conn === conn.id) {
      // المالك طلع: الملكية تنتقل لأقدم عضو (ولو رجع المالك بنفس الحساب ترجع له)
      const nxt = left.slice().sort((a, b) => (a.att.at || 0) - (b.att.at || 0))[0];
      s.owner_conn = nxt.id;
    }
    await this.save();
    this.broadcast();
    await this.index();
  }
}

// الغلاف الخاص بكلاودفلير (Durable Object + WebSocket Hibernation: ما ندفع وقت والغرفة ساكتة)
export class RoomDO {
  constructor(ctx, env) {
    this.ctx = ctx;
    this.env = env;
    this.rid = "";
    try { ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong")); } catch (e) {}
    const conn = (ws) => ({
      id: (ws.deserializeAttachment() || {}).id,
      att: ws.deserializeAttachment() || {},
      ws,
      send: (t) => ws.send(t),
      close: () => ws.close(1000, "bye"),
    });
    this.conn = conn;
    const self = this;
    this.core = new RoomCore({
      get rid() { return self.rid; },
      env,
      load: () => ctx.storage.get("s"),
      save: (s) => ctx.storage.put("s", s),
      drop: () => ctx.storage.deleteAll(),
      list: async (prefix) => [...(await ctx.storage.list({ prefix })).entries()],
      put: (k, v) => ctx.storage.put(k, v),
      del: (k) => ctx.storage.delete(k),
      later: (ms) => ctx.storage.setAlarm(Date.now() + ms),
      conns: () => ctx.getWebSockets().map(conn),
      setAtt: (c, att) => { const a = Object.assign({}, att, { id: c.id }); c.ws.serializeAttachment(a); c.att = a; },
      stale: (c) => {
        const ts = ctx.getWebSocketAutoResponseTimestamp(c.ws);
        const last = Math.max(ts ? ts.getTime() / 1000 : 0, (c.att && c.att.at) || 0);
        return tnow() - last > 150;
      },
    });
  }
  async rememberRid(fromUrl) {
    if (!this.rid) {
      this.rid = fromUrl || (await this.ctx.storage.get("rid")) || "";
      if (fromUrl) await this.ctx.storage.put("rid", fromUrl);
    }
  }
  async fetch(request) {
    const m = /\/room\/(\d{6})\/ws/.exec(new URL(request.url).pathname);
    await this.rememberRid(m ? m[1] : "");
    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);
    this.ctx.acceptWebSocket(server);
    server.serializeAttachment({ id: rid16(), joined: false, at: tnow() });
    return new Response(null, { status: 101, webSocket: client });
  }
  async webSocketMessage(ws, msg) {
    await this.rememberRid("");
    await this.core.message(this.conn(ws), typeof msg === "string" ? msg : new TextDecoder().decode(msg));
  }
  async webSocketClose(ws, code) {
    await this.rememberRid("");
    await this.core.close(this.conn(ws));
    try { ws.close(code === 1005 ? 1000 : code, "bye"); } catch (e) {}
  }
  async alarm() {
    await this.rememberRid("");
    await this.core.expire();
  }
  async webSocketError(ws) {
    await this.rememberRid("");
    await this.core.close(this.conn(ws));
  }
}
