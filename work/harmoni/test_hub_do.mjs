// يجرب غلاف Durable Object (RoomDO) بمحاكاة لواجهات كلاودفلير: WebSocketPair، acceptWebSocket، الـ attachments، الـ alarm.
import assert from "node:assert/strict";
const RealResponse = globalThis.Response;
globalThis.Response = class extends RealResponse {
  constructor(body, init = {}) {
    const st = init.status === 101 ? 200 : init.status;
    super(body, Object.assign({}, init, { status: st }));
    this.webSocket = init.webSocket || null;
    this._status = init.status;
  }
};
class MockWS {
  constructor() { this.sent = []; this.att = null; this.closed = false; this.peer = null; }
  send(t) { if (this.closed) throw new Error("closed"); if (t.length > 1048576) throw new Error("message over 1MB"); this.sent.push(t); }
  close() { this.closed = true; }
  serializeAttachment(a) {
    const t = JSON.stringify(a);
    if (t.length > 2048) throw new Error("attachment over 2KB");   // نفس حد كلاودفلير
    this.att = JSON.parse(t);
  }
  deserializeAttachment() { return this.att ? JSON.parse(JSON.stringify(this.att)) : null; }
}
globalThis.WebSocketPair = function () { const a = new MockWS(), b = new MockWS(); a.peer = b; b.peer = a; return { 0: a, 1: b }; };
globalThis.WebSocketRequestResponsePair = function (q, r) { this.q = q; this.r = r; };
const { RoomDO } = await import("./hub/worker.js");

const store = new Map();
let alarmAt = null;
const sockets = [];
const ctx = {
  storage: {
    get: async (k) => (store.has(k) ? JSON.parse(store.get(k)) : undefined),
    put: async (k, v) => { store.set(k, JSON.stringify(v)); },
    deleteAll: async () => { store.clear(); },
    delete: async (k) => store.delete(k),
    list: async ({ prefix }) => new Map([...store.entries()].filter(([k]) => k.startsWith(prefix)).map(([k, v]) => [k, JSON.parse(v)])),
    setAlarm: async (t) => { alarmAt = t; },
  },
  acceptWebSocket: (ws) => sockets.push(ws),
  getWebSockets: () => sockets.filter((w) => !w.closed),
  setWebSocketAutoResponse: () => {},
  getWebSocketAutoResponseTimestamp: () => null,
};
const env = { DB: null };
let dobj = new RoomDO(ctx, env);
async function open() {
  const r = await dobj.fetch(new Request("https://hub/room/123456/ws", { headers: { Upgrade: "websocket" } }));
  assert.equal(r._status, 101);
  return sockets[sockets.length - 1];
}
const last = (ws) => JSON.parse(ws.sent[ws.sent.length - 1]);

const a = await open();
const bigAvatar = "data:image/png;base64," + "A".repeat(250000);
await dobj.webSocketMessage(a, JSON.stringify({ t: "hello", member: { name: "A", avatar: bigAvatar, likes: Array.from({ length: 300 }, (_, i) => "like" + i) }, create: { name: "Room", carry: { current: { title: "S1", web_url: "u1", duration: 100 } } } }));
let m = last(a);
assert.equal(m.t, "state");
assert.equal(m.state.rid, "123456");
assert.equal(m.state.owner_sid, m.you);
assert.equal(m.state.current.title, "S1");

// بعد «السبات» (hibernation): كائن جديد بدون ذاكرة — لازم يكمل من التخزين
dobj = new RoomDO(ctx, env);
const b = await open();
await dobj.webSocketMessage(b, JSON.stringify({ t: "hello", member: { name: "B" } }));
m = last(b);
assert.equal(m.state.members.length, 2);
assert.equal(m.state.current.title, "S1");
assert.equal(m.state.members.find((x) => x.name === "A").avatar, bigAvatar);   // الصورة محفوظة بالتخزين مو بالـ attachment
await dobj.webSocketMessage(b, JSON.stringify({ t: "add", track: { title: "S2", web_url: "u2" } }));
assert.equal(last(a).t === "state" ? last(a).state.queue.length : JSON.parse(a.sent[a.sent.length - 1]).state.queue.length, 1);

dobj = new RoomDO(ctx, env);
a.closed = true;
await dobj.webSocketClose(a, 1000);
m = last(b);
assert.equal(m.state.members.length, 1);
assert.equal(m.state.owner_sid, m.you);   // الملكية انتقلت
b.closed = true;
await dobj.webSocketClose(b, 1006);
assert.ok(alarmAt, "alarm scheduled for empty room");
assert.ok(store.has("s"));
// الـ alarm بعد دقيقتين يحذف الغرفة
const s = JSON.parse(store.get("s")); s.empty_since = Date.now() / 1000 - 999; store.set("s", JSON.stringify(s));
dobj = new RoomDO(ctx, env);
await dobj.alarm();
assert.equal(store.size, 0);

const c = await open();
await dobj.webSocketMessage(c, JSON.stringify({ t: "hello", member: { name: "C" } }));
assert.equal(last(c).t, "join_error");
// 4 أعضاء بصور كبيرة: الرسالة تنضغط تحت 1MB بدل ما تفشل
store.clear();
dobj = new RoomDO(ctx, env);
const big = [];
for (let i = 0; i < 4; i++) {
  const w = await open();
  await dobj.webSocketMessage(w, JSON.stringify({ t: "hello", member: { name: "M" + i, avatar: bigAvatar, banner: bigAvatar }, create: { name: "big" } }));
  big.push(w);
}
const bm = last(big[3]);
assert.equal(bm.t, "state");
assert.equal(bm.state.members.length, 4);
for (const w of big) { w.closed = true; await dobj.webSocketClose(w, 1000); }
console.log("RoomDO OK");
