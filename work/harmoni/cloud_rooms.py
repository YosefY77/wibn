"""غرف الاستماع المشتركة على السيرفر المشترك (Cloudflare Durable Objects).

الغرفة نفسها (القائمة، الأغنية الحالية، وين وصلت، مين موجود) عايشة على كلاودفلير، فأي أحد عنده هارموني يقدر
يسوي غرفة أو يدخلها حتى لو جهاز صاحب البرنامج طافي. هارموني اللي عند كل واحد هو اللي يجيب الصوت من يوتيوب.

الواجهة (app.js) ما تدري بشي: تكلم السيرفر المحلي بنفس أحداث socket.io (add, skip, seek…)، وهنا CloudLink
يحولها لرسائل WebSocket للغرفة، ويرجّع حالة الغرفة للواجهة بنفس شكل الغرف المحلية (مع رابط صوت محلي).
"""
import json
import os
import socket
import threading
import time

import harmoni_ws

LINKS = {}            # sid → CloudLink
_LOCK = threading.RLock()
_deps = {}
PING_EVERY = 25


def init(emit, resolve, prepare, ws_base, perf=None):
    """emit(event, data, sid) · resolve(track, refresh) → (url, src) · prepare(track) → track · ws_base() → wss://…"""
    _deps.update(emit=emit, resolve=resolve, prepare=prepare, ws_base=ws_base, perf=perf or (lambda *a: None))


def get(sid):
    with _LOCK:
        return LINKS.get(sid)


def drop(sid):
    with _LOCK:
        link = LINKS.pop(sid, None)
    if link:
        link.close()
    return link


def by_uid(uid):
    if not uid:
        return None
    with _LOCK:
        for link in LINKS.values():
            if link.my_uid == uid and link.joined:
                return link
    return None


def connect(sid, code, member, token="", password="", create=None, on_joined=None, on_failed=None):
    drop(sid)
    link = CloudLink(sid, code, member, token, password, create)
    link.on_joined = on_joined
    link.on_failed = on_failed
    with _LOCK:
        LINKS[sid] = link
    link.start()
    return link


def _small_member(member):
    """رسالة WebSocket حدها 1MB: الصور الكبيرة جداً ما نرسلها (تظهر من بروفايله بدالها)."""
    m = dict(member or {})
    for k in ("avatar", "banner"):
        if len(str(m.get(k) or "")) > 300000:
            m[k] = ""
    try:
        if len(json.dumps(m.get("likes") or [])) > 150000:
            m["likes"] = []
    except Exception:
        m["likes"] = []
    return m


class CloudLink:
    hidden = False

    def __init__(self, sid, code, member, token, password, create):
        self.sid = sid
        self.rid = str(code)
        self.lock = threading.RLock()
        self.member = _small_member(member)
        self.my_uid = self.member.get("uid") or ""
        self.token = token or ""
        self.password = password or ""
        self.create = create
        self.ws = None
        self.stopping = False
        self.joined = False
        self.raw = None          # آخر حالة من الغرفة (بوقت هالجهاز)
        self.you = ""
        self._res = {}           # id الأغنية → رابط الصوت المحلي
        self._resolving = set()
        self._refresh = {}
        self.name = ""
        self.can_add = True
        self.owner_sid = None
        self.current = None
        self.members = {}
        self.on_joined = None
        self.on_failed = None
        self.client = os.urandom(8).hex()

    # ===== الاتصال =====
    def start(self):
        threading.Thread(target=self._run, daemon=True, name="harmoni-cloud-room").start()

    def _emit(self, event, data):
        try:
            _deps["emit"](event, data, self.sid)
        except Exception:
            pass

    def _send(self, obj):
        ws = self.ws
        if ws is None:
            return False
        try:
            ws.send(json.dumps(obj, ensure_ascii=False))
            return True
        except Exception:
            return False

    def _hello(self):
        msg = {"t": "hello", "member": self.member, "token": self.token, "password": self.password, "client": self.client}
        if self.create is not None and not self.joined:
            msg["create"] = self.create
        self._send(msg)

    def _run(self):
        tries = 0
        while not self.stopping:
            try:
                self.ws = harmoni_ws.WebSocket(_deps["ws_base"]() + "/room/%s/ws" % self.rid, timeout=10)
                self.ws.settimeout(PING_EVERY if self.joined else 8)
                self._hello()
                _deps["perf"]("cloud-room", 0, "connected %s (%s)" % (self.rid, "create" if self.create is not None else "join"))
                if not self.joined:
                    # أول رد من الغرفة لازم يجي بسرعة — لو ما جا نعتبر الدخول فشل بدل ما الزر يعلق
                    try:
                        text = self.ws.recv()
                    except socket.timeout:
                        raise harmoni_ws.Closed("no reply from room")
                    if text != "pong":
                        self._on_message(text)
                    if self.stopping:
                        break
                    self.ws.settimeout(PING_EVERY)
                tries = 0
                last_rx = time.time()
                while not self.stopping:
                    try:
                        text = self.ws.recv()
                    except socket.timeout:
                        if time.time() - last_rx > PING_EVERY * 3:
                            raise harmoni_ws.Closed("no pong")   # الاتصال مات بصمت (نوم الجهاز/النت) → نعيد الاتصال
                        self.ws.send("ping")
                        continue
                    last_rx = time.time()
                    if text != "pong":
                        self._on_message(text)
            except Exception as e:
                if self.stopping:
                    break
                _deps["perf"]("cloud-room", 0, "link %s: %s" % (self.rid, repr(e)[:120]))
            finally:
                try:
                    if self.ws:
                        self.ws.close()
                except Exception:
                    pass
            if self.stopping:
                break
            tries += 1
            if tries > 4 or not self.joined:
                # ما قدرنا نوصل للغرفة: الواجهة ترجع لوضعها الطبيعي (أو غرفة على هالجهاز لو كنا نسوي وحدة جديدة)
                self._finish()
                if self.on_failed and not self.joined:
                    try:
                        self.on_failed(self.sid)
                    except Exception as e:
                        _deps["perf"]("cloud-room", 0, "fallback error " + repr(e)[:120])
                elif self.joined:
                    self._emit("room_lost", {})          # كان داخل الغرفة والاتصال مات
                else:
                    self._emit("join_error", {"reason": "not_found"})
                break
            time.sleep(min(10, 1.5 * tries))

    def _finish(self):
        self.stopping = True
        with _LOCK:
            if LINKS.get(self.sid) is self:
                LINKS.pop(self.sid, None)

    def close(self):
        if self.stopping:
            return
        self.stopping = True
        self._send({"t": "leave"})
        try:
            if self.ws:
                self.ws.close()
        except Exception:
            pass

    def _on_message(self, text):
        try:
            m = json.loads(text)
        except Exception:
            return
        t = m.get("t")
        if t == "state":
            first = not self.joined
            self.joined = True
            self.create = None
            if first:
                _deps["perf"]("cloud-room", 0, "joined " + self.rid)
            if first and self.on_joined:
                try:
                    self.on_joined(self.sid)
                except Exception:
                    pass
            self._on_state(m.get("state") or {}, m.get("you") or "")
        elif t == "join_error":
            _deps["perf"]("cloud-room", 0, "join_error %s %s" % (self.rid, m.get("reason")))
            self._emit("join_error", {"reason": m.get("reason") or "not_found"})
            self._finish()
        elif t == "kicked":
            self._emit("kicked", {})
            self._finish()
        elif t in ("add_error", "add_ok", "track_failed"):
            self._emit(t, {k: v for k, v in m.items() if k != "t"})

    # ===== الحالة =====
    def _on_state(self, cloud, you):
        now = time.time()
        offset = float(cloud.get("server_time") or now) - now
        st = dict(cloud)
        st["updated_at"] = float(cloud.get("updated_at") or now) - offset   # بوقت هالجهاز
        st["server_time"] = now
        st["owner_sid"] = self.sid if cloud.get("owner_sid") == you else cloud.get("owner_sid")
        st["members"] = [dict(x, id=self.sid) if x.get("id") == you else x for x in (cloud.get("members") or [])]
        with self.lock:
            self.you = you
            self.raw = st
            self.name = st.get("name") or ""
            self.can_add = bool(st.get("can_add", True))
            self.owner_sid = st["owner_sid"]
            self.members = {x.get("id"): x for x in st["members"]}
            self.current = self._with_stream(st.get("current"))
            want = [st.get("current")]
            if st.get("queue") and not st.get("shuffle"):
                want.append(st["queue"][0])
            keep = {x.get("id") for x in want if x} | {x.get("id") for x in (st.get("queue") or [])[:3]}
            for k in [k for k in self._res if k not in keep]:
                self._res.pop(k, None)
        for tr in want:
            if tr:
                self._ensure(tr)
        self.broadcast()

    def _with_stream(self, tr):
        if not tr:
            return None
        out = dict(tr)
        r = self._res.get(tr.get("id"))
        if r and r.get("stream_url"):
            out.update(r)
        elif r and r.get("failed"):
            out["stream_failed"] = True
        else:
            out["_resolving"] = 1
        return out

    def state(self):
        with self.lock:
            if not self.raw:
                return {"rid": self.rid, "name": self.name, "queue": [], "current": None, "playing": False,
                        "position": 0, "updated_at": time.time(), "shuffle": False, "members": [],
                        "owner_sid": None, "max_members": 0, "has_password": False, "can_add": True,
                        "personal": False, "server_time": time.time(), "cloud": True}
            st = dict(self.raw)
            st["current"] = self._with_stream(st.get("current"))
            st["queue"] = [self._with_stream(x) if x and x.get("id") in self._res else x for x in st.get("queue") or []]
            st["server_time"] = time.time()
            return st

    def broadcast(self):
        self._emit("state", self.state())

    # ===== الصوت (كل جهاز يجيبه بنفسه) =====
    def _ensure(self, tr, refresh=False):
        tid = tr.get("id")
        if not tid:
            return
        with self.lock:
            r = self._res.get(tid)
            fresh = r and r.get("stream_url") and time.time() - float(r.get("stream_ts") or 0) < 25 * 60
            if tid in self._resolving or (fresh and not refresh) or (r and r.get("failed") and not refresh):
                return
            self._resolving.add(tid)
        threading.Thread(target=self._resolve, args=(dict(tr), refresh), daemon=True).start()

    def _resolve(self, tr, refresh):
        tid = tr.get("id")
        url, src = None, ""
        try:
            url, src = _deps["resolve"](tr, refresh)
            if not url and not refresh:
                url, src = _deps["resolve"](tr, True)
        except Exception as e:
            _deps["perf"]("cloud-room", 0, "resolve error " + repr(e)[:120])
        with self.lock:
            self._resolving.discard(tid)
            if url:
                self._res[tid] = {"stream_url": url, "stream_src": src, "stream_ts": time.time()}
            else:
                self._res[tid] = {"failed": True}
            is_cur = bool(self.raw and (self.raw.get("current") or {}).get("id") == tid)
            if is_cur:
                self.current = self._with_stream(self.raw.get("current"))
        if is_cur:
            self.broadcast()
            if not url:
                self._emit("add_error", {"error": "ما قدرنا نشغّل «%s» عندك" % (tr.get("title") or "")})
                self._send({"t": "failed", "id": tid})

    def stream_failed(self, tid):
        with self.lock:
            cur = (self.raw or {}).get("current") or {}
            if cur.get("id") != tid or tid in self._resolving:
                return False
            n, at = self._refresh.get(tid, (0, 0))
            if time.time() - at < 8:
                return False
            if n >= 2:
                self._send({"t": "failed", "id": tid})
                return False
            self._refresh[tid] = (n + 1, time.time())
        self._ensure(cur, refresh=True)
        return True

    # ===== أوامر (نفس أسماء Room عشان أحداث socket.io تشتغل بدون تغيير) =====
    def add_track(self, track):
        tr = _deps["prepare"](dict(track or {}))
        tr.setdefault("added_by", self.member.get("name") or "")
        self._send({"t": "add", "track": tr})
        return {"pending": True}

    def play_track(self, track):
        tr = _deps["prepare"](dict(track or {}))
        tr.setdefault("added_by", self.member.get("name") or "")
        self._send({"t": "add", "track": tr, "play_now": True})
        return {"pending": True}

    def remove_track(self, tid):
        self._send({"t": "remove", "id": tid})

    def skip(self, tid=None):
        self._send({"t": "skip", "id": tid})

    def ended(self, tid, force=False):
        self._send({"t": "ended", "id": tid})

    def previous(self):
        self._send({"t": "prev"})

    def play_pause(self):
        self._send({"t": "play_pause"})

    def seek(self, pos):
        try:
            self._send({"t": "seek", "pos": max(0.0, float(pos))})
        except (TypeError, ValueError):
            pass

    def set_shuffle(self, on):
        self._send({"t": "shuffle", "on": bool(on)})

    def play_now(self, tid):
        self._send({"t": "play_now", "id": tid})

    def kick(self, sid, target):
        if sid != self.owner_sid or not target or target == sid:
            return False
        self._send({"t": "kick", "id": target})
        return True

    def update_settings(self, sid, data):
        if sid != self.owner_sid:
            return False
        d = {k: (data or {}).get(k) for k in ("name", "password", "can_add", "max_members") if k in (data or {})}
        self._send({"t": "settings", "data": d})
        return True
