// تشغيل الـ hub محلياً للاختبار (بدون كلاودفلير): node hub/dev-server.mjs [port] [db-file]
// يحاكي D1 بـ node:sqlite (Node 22.5+).
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { DatabaseSync } from "node:sqlite";
import crypto from "node:crypto";
import worker, { RoomCore } from "./worker.js";

const here = path.dirname(fileURLToPath(import.meta.url));
const port = Number(process.argv[2] || 8787);
const db = new DatabaseSync(process.argv[3] || ":memory:");
db.exec(fs.readFileSync(path.join(here, "schema.sql"), "utf8"));

const D1 = {
  async batch(list) { for (const st of list) await st.run(); return []; },
  prepare(sql) {
    let args = [];
    const st = {
      bind(...a) { args = a; return st; },
      async first() { return db.prepare(sql).get(...args) || null; },
      async all() { return { results: db.prepare(sql).all(...args) }; },
      async run() { const i = db.prepare(sql).run(...args); return { success: true, meta: { changes: Number(i.changes) } }; },
    };
    return st;
  },
};
// محاكاة R2 (صور البروفايل)
const media = new Map();
const MEDIA = {
  async put(key, body, opts) { const buf = Buffer.from(await new Response(body).arrayBuffer()); media.set(key, { buf, type: (opts && opts.httpMetadata && opts.httpMetadata.contentType) || "" }); },
  async get(key) { const m = media.get(key); return m ? { body: m.buf, httpMetadata: { contentType: m.type } } : null; },
  async delete(key) { media.delete(key); },
  async list({ prefix }) { return { objects: [...media.entries()].filter(([k]) => k.startsWith(prefix || "")).map(([k, m]) => ({ key: k, size: m.buf.length })), truncated: false }; },
};
const env = { DB: D1, HUB_SECRET: process.env.HUB_SECRET || "", MEDIA: process.env.HUB_NO_MEDIA ? undefined : MEDIA, MEDIA_QUOTA: process.env.HUB_MEDIA_QUOTA || "" };

// ===== محاكاة Durable Objects + WebSocket (نفس RoomCore اللي على كلاودفلير) =====
const rooms = new Map();   // rid → { core, conns: Set, store }
function roomOf(rid) {
  if (!rooms.has(rid)) {
    const r = { conns: new Set(), store: null, kv: new Map() };
    r.core = new RoomCore({
      rid, env, grace: Number(process.env.HUB_EMPTY_S || 110),
      load: async () => r.store,
      save: async (st) => { r.store = JSON.parse(JSON.stringify(st)); },
      drop: async () => { r.store = null; r.kv.clear(); },
      list: async (prefix) => [...r.kv.entries()].filter(([k]) => k.startsWith(prefix)).map(([k, v]) => [k, JSON.parse(v)]),
      put: async (k, v) => { r.kv.set(k, JSON.stringify(v)); },
      del: async (k) => { r.kv.delete(k); },
      setAtt: (c, att) => {
        const a = Object.assign({}, att, { id: c.id });
        if (JSON.stringify(a).length > 2048) throw new Error("attachment over 2KB (Cloudflare limit)");
        c.att = a;
      },
      later: async (ms) => { clearTimeout(r.timer); r.timer = setTimeout(() => r.core.expire(), Number(process.env.HUB_EMPTY_MS || ms)); },
      conns: () => [...r.conns],
      stale: (c) => Date.now() / 1000 - Math.max(c.lastPing || 0, (c.att && c.att.at) || 0) > Number(process.env.HUB_STALE || 150),
    });
    rooms.set(rid, r);
  }
  return rooms.get(rid);
}
function frame(op, payload) {
  const len = payload.length;
  const head = len < 126 ? Buffer.from([0x80 | op, len]) : len < 65536 ? Buffer.from([0x80 | op, 126, len >> 8, len & 255])
    : Buffer.concat([Buffer.from([0x80 | op, 127]), (() => { const b = Buffer.alloc(8); b.writeBigUInt64BE(BigInt(len)); return b; })()]);
  return Buffer.concat([head, payload]);
}
function upgrade(req, sock) {
  const m = /^\/room\/(\d{6})\/ws/.exec(req.url || "");
  if (!m) { sock.destroy(); return; }
  const accept = crypto.createHash("sha1").update(req.headers["sec-websocket-key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").digest("base64");
  sock.write("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
  const room = roomOf(m[1]);
  let closed = false;
  const conn = {
    id: crypto.randomBytes(8).toString("hex"), att: {}, lastPing: 0,
    send: (t) => { if (!closed) sock.write(frame(1, Buffer.from(t))); },
    close: () => { if (closed) return; try { sock.write(frame(8, Buffer.alloc(0))); } catch (e) {} finish(); },
  };
  const finish = () => {
    if (closed) return;
    closed = true;
    room.core.close(conn).finally(() => { room.conns.delete(conn); sock.end(); });
  };
  room.conns.add(conn);
  room.core.open(conn);
  let buf = Buffer.alloc(0), parts = [];
  let chain = Promise.resolve();
  sock.on("data", (d) => {
    buf = Buffer.concat([buf, d]);
    for (;;) {
      if (buf.length < 2) return;
      const fin = buf[0] & 0x80, op = buf[0] & 15, masked = buf[1] & 0x80;
      let len = buf[1] & 127, off = 2;
      if (len === 126) { if (buf.length < 4) return; len = buf.readUInt16BE(2); off = 4; }
      else if (len === 127) { if (buf.length < 10) return; len = Number(buf.readBigUInt64BE(2)); off = 10; }
      const mk = masked ? buf.subarray(off, off + 4) : null;
      if (masked) off += 4;
      if (buf.length < off + len) return;
      let p = Buffer.from(buf.subarray(off, off + len));
      buf = buf.subarray(off + len);
      if (mk) for (let i = 0; i < p.length; i++) p[i] ^= mk[i & 3];
      if (op === 8) { finish(); return; }
      if (op === 9) { sock.write(frame(10, p)); continue; }
      if (op === 10) continue;
      parts.push(p);
      if (!fin) continue;
      const text = Buffer.concat(parts).toString("utf8");
      parts = [];
      if (text === "ping") { conn.lastPing = Date.now() / 1000; conn.send("pong"); continue; }   // مثل setWebSocketAutoResponse
      chain = chain.then(() => room.core.message(conn, text)).catch((e) => console.error(e));
    }
  });
  sock.on("close", finish);
  sock.on("error", finish);
}

const server = http.createServer(async (req, res) => {
  const chunks = [];
  for await (const c of req) chunks.push(c);
  const body = Buffer.concat(chunks);
  const r = await worker.fetch(new Request("http://hub" + req.url, {
    method: req.method, headers: req.headers, body: ["GET", "HEAD"].includes(req.method) ? undefined : body,
  }), env);
  res.writeHead(r.status, Object.fromEntries(r.headers));
  res.end(Buffer.from(await r.arrayBuffer()));
});
server.on("upgrade", upgrade);
server.listen(port, "127.0.0.1", () => console.log("hub ready " + port));
