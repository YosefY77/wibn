"""ربط يوتيوب الآمن — ملف cookies من حساب ثانوي، يُستخدم بس للأغاني المقيدة بالعمر.

الأمان:
  * الملف ينحفظ مشفّر بتشفير ويندوز (DPAPI): ما ينفتح إلا من حسابك على نفس الجهاز —
    لو أحد نسخ الملف لجهاز ثاني ما يقدر يستخدمه.
  * نحفظ كوكيز youtube.com بس (نشيل جوجل وأي موقع ثاني).
  * النسخة المفكوكة اللي يحتاجها yt-dlp تنكتب في ملف مؤقت وتنحذف أول ما يخلص.
  * يُستخدم بس لطلباتك أنت من جهازك (إلا لو سمحت لأصحابك في الإعدادات).
  * حد أقصى للاستخدام بالساعة عشان يوتيوب ما يشك في الحساب.
"""
import base64
import contextlib
import json
import os
import re
import sys
import tempfile
import threading
import time

AGE_RE = re.compile(r"confirm your age|age[- ]?restrict|inappropriate for some users|age[- ]?gate|age verification", re.I)
LOGIN_COOKIES = ("SID", "__Secure-1PSID", "__Secure-3PSID", "SAPISID", "__Secure-3PAPISID", "LOGIN_INFO")
HOURLY_LIMIT = 30
MAX_FILE = 2_000_000
_MAGIC = b"HMYT1\n"

_lock = threading.RLock()
_local = threading.local()
_uses = []
_paths = {"data": "", "cfg": "", "legacy": ""}


def init(base_dir):
    _paths["data"] = os.path.join(base_dir, "youtube_link.bin")
    _paths["cfg"] = os.path.join(base_dir, "youtube_link.json")
    _paths["legacy"] = os.path.join(base_dir, "youtube_cookies.txt")
    _migrate_legacy()


# ---------- التشفير (ويندوز DPAPI) ----------
def _dpapi(data, protect):
    import ctypes
    from ctypes import wintypes

    class BLOB(ctypes.Structure):
        _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_char))]

    buf = ctypes.create_string_buffer(data, len(data))
    src = BLOB(len(data), ctypes.cast(buf, ctypes.POINTER(ctypes.c_char)))
    out = BLOB()
    entropy_raw = b"harmoni-youtube-link"
    ebuf = ctypes.create_string_buffer(entropy_raw, len(entropy_raw))
    ent = BLOB(len(entropy_raw), ctypes.cast(ebuf, ctypes.POINTER(ctypes.c_char)))
    fn = ctypes.windll.crypt32.CryptProtectData if protect else ctypes.windll.crypt32.CryptUnprotectData
    # CRYPTPROTECT_UI_FORBIDDEN = 0x1
    if protect:
        ok = fn(ctypes.byref(src), "Harmoni YouTube", ctypes.byref(ent), None, None, 0x1, ctypes.byref(out))
    else:
        ok = fn(ctypes.byref(src), None, ctypes.byref(ent), None, None, 0x1, ctypes.byref(out))
    if not ok:
        raise OSError("dpapi failed")
    try:
        return ctypes.string_at(out.pbData, out.cbData)
    finally:
        ctypes.windll.kernel32.LocalFree(out.pbData)


def _seal(text):
    raw = text.encode("utf-8")
    if sys.platform == "win32":
        return _MAGIC + b"D" + _dpapi(raw, True)
    return _MAGIC + b"P" + base64.b64encode(raw)   # غير ويندوز (للتجارب): الملف بصلاحيات صاحبه بس


def _unseal(blob):
    if not blob.startswith(_MAGIC):
        raise ValueError("bad_store")
    kind, body = blob[len(_MAGIC):len(_MAGIC) + 1], blob[len(_MAGIC) + 1:]
    if kind == b"D":
        return _dpapi(body, False).decode("utf-8")
    if kind == b"P":
        return base64.b64decode(body).decode("utf-8")
    raise ValueError("bad_store")


def _write_atomic(path, data):
    tmp = path + ".tmp"
    with open(tmp, "wb") as fh:
        fh.write(data)
    try:
        os.chmod(tmp, 0o600)
    except OSError:
        pass
    os.replace(tmp, path)


# ---------- الإعدادات ----------
def config():
    try:
        with open(_paths["cfg"], encoding="utf-8") as fh:
            cfg = json.load(fh)
    except Exception:
        cfg = {}
    return {"share_friends": bool(cfg.get("share_friends")), "uses": int(cfg.get("uses") or 0),
            "last_used": float(cfg.get("last_used") or 0), "linked_at": float(cfg.get("linked_at") or 0),
            "count": int(cfg.get("count") or 0)}


def _save_config(cfg):
    _write_atomic(_paths["cfg"], json.dumps(cfg).encode("utf-8"))


def set_share(flag):
    with _lock:
        cfg = config()
        cfg["share_friends"] = bool(flag)
        _save_config(cfg)
        return cfg


# ---------- الملف ----------
def clean(text):
    """يقبل cookies.txt (صيغة Netscape) ويبقي كوكيز youtube.com بس. يرجع (النص، العدد) أو ValueError."""
    if not isinstance(text, str) or len(text) > MAX_FILE:
        raise ValueError("bad_file")
    keep, names = [], set()
    for line in text.replace("\r\n", "\n").replace("\r", "\n").split("\n"):
        raw = line.strip(" \n")
        if not raw:
            continue
        if raw.startswith("#HttpOnly_"):
            body = raw[len("#HttpOnly_"):]
        elif raw.startswith("#"):
            continue
        else:
            body = raw
        parts = body.split("\t")
        if len(parts) != 7:
            continue
        dom = parts[0].lstrip(".").lower()
        if not (dom == "youtube.com" or dom.endswith(".youtube.com")):
            continue
        keep.append(raw)
        names.add(parts[5])
    if not keep:
        raise ValueError("no_youtube")
    if not any(n in names for n in LOGIN_COOKIES):
        raise ValueError("not_signed_in")
    return "# Netscape HTTP Cookie File\n" + "\n".join(keep) + "\n", len(keep)


def linked():
    return bool(_paths["data"]) and os.path.isfile(_paths["data"])


def save(text):
    cleaned, n = clean(text)
    with _lock:
        _write_atomic(_paths["data"], _seal(cleaned))
        cfg = config()
        cfg.update({"linked_at": time.time(), "count": n, "uses": 0, "last_used": 0})
        _save_config(cfg)
    return n


def unlink():
    with _lock:
        for p in (_paths["data"], _paths["legacy"]):
            try:
                os.remove(p)
            except FileNotFoundError:
                pass
        cfg = config()
        cfg.update({"linked_at": 0, "count": 0, "uses": 0, "last_used": 0})
        _save_config(cfg)
        del _uses[:]


def _migrate_legacy():
    """النسخة الأولى كانت تحفظ الملف نص عادي — ننقله للمخزن المشفّر ونحذف القديم."""
    leg = _paths["legacy"]
    if not os.path.isfile(leg):
        return
    try:
        with open(leg, encoding="utf-8") as fh:
            text = fh.read()
        if not linked():
            save(text)
    except Exception:
        pass
    try:
        os.remove(leg)
    except OSError:
        pass


# ---------- مين يحق له يستخدم الربط ----------
def set_requester(is_owner):
    _local.owner = bool(is_owner)


def allowed():
    if not linked():
        return False
    if getattr(_local, "owner", False):
        return True
    return config()["share_friends"]


def _take_slot():
    now = time.time()
    with _lock:
        _uses[:] = [t for t in _uses if now - t < 3600]
        if len(_uses) >= HOURLY_LIMIT:
            return False
        _uses.append(now)
        cfg = config()
        cfg["uses"] = cfg["uses"] + 1
        cfg["last_used"] = now
        _save_config(cfg)
        return True


@contextlib.contextmanager
def cookie_file():
    """ملف مؤقت مفكوك ليستخدمه yt-dlp، وبعدها نرجّع الكوكيز المحدّثة للمخزن المشفّر ونحذف المؤقت."""
    if not allowed():
        raise PermissionError("not_allowed")
    if not _take_slot():
        raise PermissionError("rate_limited")
    with _lock:
        with open(_paths["data"], "rb") as fh:
            text = _unseal(fh.read())
    fd, path = tempfile.mkstemp(prefix="hm-", suffix=".txt")
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(text)
        yield path
        # يوتيوب يجدّد بعض الكوكيز — نحفظ الجديد عشان الربط يعيش أطول
        try:
            with open(path, encoding="utf-8") as fh:
                fresh = fh.read()
            if fresh and fresh != text:
                cleaned, _ = clean(fresh)
                with _lock:
                    if linked():
                        _write_atomic(_paths["data"], _seal(cleaned))
        except Exception:
            pass
    finally:
        try:
            os.remove(path)
        except OSError:
            pass


def is_age_error(err):
    return bool(AGE_RE.search(str(err or "")))


def status():
    cfg = config()
    return {"linked": linked(), "ts": cfg["linked_at"], "count": cfg["count"], "uses": cfg["uses"],
            "last_used": cfg["last_used"], "share_friends": cfg["share_friends"],
            "encrypted": sys.platform == "win32", "hourly_limit": HOURLY_LIMIT}


# =====================================================================
# تسجيل دخول مباشر (بدون إضافة): نفتح متصفح Edge/Chrome الحقيقي بملف شخصي مؤقت خاص بهارموني،
# المستخدم يسجّل دخول بنفسه في صفحة جوجل الرسمية (هارموني ما يشوف كلمة السر أبد)،
# وأول ما يكتمل الدخول نقرأ كوكيز يوتيوب من المتصفح نفسه، نسكّره، ونحذف الملف الشخصي المؤقت.
# النتيجة: جلسة مستقلة باسم «Microsoft Edge» تقدر تلغيها لحالها من صفحة أمان جوجل.
# =====================================================================
import json as _json
import secrets
import shutil
import socket
import struct
import subprocess
import urllib.request

LOGIN_URL = ("https://accounts.google.com/ServiceLogin?service=youtube&uilel=3&passive=true&continue="
             "https%3A%2F%2Fwww.youtube.com%2Fsignin%3Faction_handle_signin%3Dtrue%26app%3Ddesktop%26next%3D%252F")
LOGIN_TIMEOUT = 15 * 60
_login = {"state": "idle", "error": "", "started": 0.0, "proc": None, "profile": ""}
_login_lock = threading.Lock()


def find_browser():
    env = os.environ.get("HARMONI_LOGIN_BROWSER")
    if env and os.path.isfile(env):
        return env
    cands = []
    for base in (os.environ.get("ProgramFiles(x86)"), os.environ.get("ProgramFiles"), os.environ.get("LOCALAPPDATA")):
        if base:
            cands += [os.path.join(base, "Microsoft", "Edge", "Application", "msedge.exe"),
                      os.path.join(base, "Google", "Chrome", "Application", "chrome.exe"),
                      os.path.join(base, "BraveSoftware", "Brave-Browser", "Application", "brave.exe")]
    for c in cands:
        if os.path.isfile(c):
            return c
    import shutil as _sh
    for n in ("microsoft-edge", "google-chrome", "chromium", "chromium-browser"):
        p = _sh.which(n)
        if p:
            return p
    return None


class _WS:
    """عميل WebSocket صغير (للاتصال بالمتصفح على 127.0.0.1 فقط)."""

    def __init__(self, url, timeout=10):
        assert url.startswith("ws://127.0.0.1:") or url.startswith("ws://localhost:")
        rest = url[len("ws://"):]
        hostport, path = rest.split("/", 1)
        host, port = hostport.split(":")
        self.s = socket.create_connection((host, int(port)), timeout=timeout)
        key = base64.b64encode(secrets.token_bytes(16)).decode()
        req = ("GET /%s HTTP/1.1\r\nHost: %s\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
               "Sec-WebSocket-Key: %s\r\nSec-WebSocket-Version: 13\r\n\r\n") % (path, hostport, key)
        self.s.sendall(req.encode())
        head = b""
        while b"\r\n\r\n" not in head:
            chunk = self.s.recv(4096)
            if not chunk:
                raise OSError("ws handshake failed")
            head += chunk
        if b" 101 " not in head.split(b"\r\n", 1)[0]:
            raise OSError("ws handshake refused")
        self.buf = head.split(b"\r\n\r\n", 1)[1]
        self.n = 0

    def _recv(self, n):
        while len(self.buf) < n:
            chunk = self.s.recv(65536)
            if not chunk:
                raise OSError("ws closed")
            self.buf += chunk
        out, self.buf = self.buf[:n], self.buf[n:]
        return out

    def send(self, text):
        data = text.encode("utf-8")
        hdr = bytearray([0x81])
        n = len(data)
        if n < 126:
            hdr.append(0x80 | n)
        elif n < 65536:
            hdr.append(0x80 | 126)
            hdr += struct.pack(">H", n)
        else:
            hdr.append(0x80 | 127)
            hdr += struct.pack(">Q", n)
        mask = secrets.token_bytes(4)
        self.s.sendall(bytes(hdr) + mask + bytes(b ^ mask[i % 4] for i, b in enumerate(data)))

    def recv(self):
        msg = b""
        while True:
            b1, b2 = self._recv(2)
            op, n = b1 & 0x0F, b2 & 0x7F
            if n == 126:
                n = struct.unpack(">H", self._recv(2))[0]
            elif n == 127:
                n = struct.unpack(">Q", self._recv(8))[0]
            mask = self._recv(4) if b2 & 0x80 else None
            payload = self._recv(n)
            if mask:
                payload = bytes(b ^ mask[i % 4] for i, b in enumerate(payload))
            if op == 0x8:
                raise OSError("ws closed")
            if op in (0x9, 0xA):
                continue
            msg += payload
            if b1 & 0x80:
                return msg.decode("utf-8", "replace")

    def call(self, method, params=None):
        self.n += 1
        mid = self.n
        self.send(_json.dumps({"id": mid, "method": method, "params": params or {}}))
        while True:
            j = _json.loads(self.recv())
            if j.get("id") == mid:
                if "error" in j:
                    raise OSError(str(j["error"]))
                return j.get("result") or {}

    def close(self):
        try:
            self.s.close()
        except Exception:
            pass


def _netscape(cookies):
    lines = ["# Netscape HTTP Cookie File"]
    for c in cookies:
        dom = c.get("domain") or ""
        if not (dom.lstrip(".") == "youtube.com" or dom.endswith(".youtube.com")):
            continue
        exp = c.get("expires") or 0
        exp = int(exp) if exp and exp > 0 else 0
        line = "\t".join([dom, "TRUE" if dom.startswith(".") else "FALSE", c.get("path") or "/",
                          "TRUE" if c.get("secure") else "FALSE", str(exp), c.get("name") or "", c.get("value") or ""])
        lines.append(("#HttpOnly_" if c.get("httpOnly") else "") + line)
    return "\n".join(lines) + "\n"


def _logged_in(cookies):
    names = {c.get("name") for c in cookies if (c.get("domain") or "").lstrip(".").endswith("youtube.com")}
    return "LOGIN_INFO" in names and bool(names & {"SAPISID", "__Secure-3PAPISID", "__Secure-3PSID", "SID"})


def _rmtree_retry(path):
    for _ in range(20):
        if not os.path.exists(path):
            return
        shutil.rmtree(path, ignore_errors=True)
        time.sleep(0.5)


def _set_login(**kw):
    with _login_lock:
        _login.update(kw)


def login_status():
    with _login_lock:
        return {"state": _login["state"], "error": _login["error"]}


def start_login(base_dir, browser=None, extra_args=()):
    """يفتح المتصفح لتسجيل الدخول. يرجع (ok, error)."""
    with _login_lock:
        if _login["state"] == "waiting":
            return True, ""
    exe = browser or find_browser()
    if not exe:
        _set_login(state="error", error="no_browser")
        return False, "no_browser"
    profile = os.path.join(base_dir, "yt-login-" + secrets.token_hex(4))
    os.makedirs(profile, exist_ok=True)
    args = [exe, "--user-data-dir=" + profile, "--remote-debugging-port=0", "--remote-debugging-address=127.0.0.1",
            "--no-first-run", "--no-default-browser-check", "--disable-sync", "--disable-background-mode",
            "--disable-features=msEdgeStartupBoost,msImplicitSignin", "--new-window"] + list(extra_args) + [LOGIN_URL]
    try:
        proc = subprocess.Popen(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0) if sys.platform == "win32" else 0)
    except Exception as e:
        _rmtree_retry(profile)
        _set_login(state="error", error="launch_failed")
        return False, "launch_failed"
    _set_login(state="waiting", error="", started=time.time(), proc=proc, profile=profile)
    threading.Thread(target=_watch_login, args=(proc, profile), daemon=True).start()
    return True, ""


def cancel_login():
    with _login_lock:
        proc = _login.get("proc")
        if _login["state"] != "waiting":
            return
        _login["state"] = "cancelled"
    try:
        proc.terminate()
    except Exception:
        pass


def _devtools_ws(profile, proc, deadline):
    port_file = os.path.join(profile, "DevToolsActivePort")
    while time.time() < deadline:
        if proc.poll() is not None:
            return None
        try:
            with open(port_file, encoding="utf-8") as fh:
                lines = fh.read().split()
            if len(lines) >= 2:
                return "ws://127.0.0.1:%s%s" % (lines[0], lines[1])
        except OSError:
            pass
        time.sleep(0.4)
    return None


def _watch_login(proc, profile):
    ws = None
    result = ("closed", "")
    deadline = time.time() + LOGIN_TIMEOUT
    try:
        url = _devtools_ws(profile, proc, time.time() + 30)
        if not url:
            result = ("error", "no_devtools") if proc.poll() is None else ("closed", "")
        else:
            ws = _WS(url)
            seen_at = 0.0
            while time.time() < deadline:
                with _login_lock:
                    if _login["state"] != "waiting":
                        result = (_login["state"], "")
                        break
                if proc.poll() is not None:
                    result = ("closed", "")
                    break
                try:
                    cookies = ws.call("Storage.getCookies").get("cookies") or []
                except OSError:
                    result = ("closed", "")
                    break
                if _logged_in(cookies):
                    # ننتظر ثانيتين بعد ما يكتمل الدخول عشان كل الكوكيز تنكتب
                    if not seen_at:
                        seen_at = time.time()
                    elif time.time() - seen_at >= 2:
                        cookies = ws.call("Storage.getCookies").get("cookies") or cookies
                        save(_netscape(cookies))
                        result = ("done", "")
                        break
                time.sleep(1.0)
            else:
                result = ("error", "timeout")
    except ValueError as e:
        result = ("error", str(e))
    except Exception:
        result = ("error", "failed")
    finally:
        try:
            if ws is not None:
                try:
                    ws.call("Browser.close")
                except Exception:
                    pass
                ws.close()
        finally:
            try:
                proc.wait(timeout=10)
            except Exception:
                try:
                    proc.kill()
                except Exception:
                    pass
            # نحذف الملف الشخصي المؤقت: الجلسة تبقى في مخزن هارموني المشفّر بس
            _rmtree_retry(profile)
            _set_login(state=result[0], error=result[1], proc=None, profile="")


def cleanup_stale(base_dir):
    """لو البرنامج انقفل وقت تسجيل الدخول: نحذف أي ملف شخصي مؤقت باقي."""
    try:
        for n in os.listdir(base_dir):
            if n.startswith("yt-login-"):
                shutil.rmtree(os.path.join(base_dir, n), ignore_errors=True)
    except OSError:
        pass
