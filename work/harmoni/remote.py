"""Harmoni Connect: تتحكم بهارموني اللي على الكمبيوتر من جوالك (وأنت تلعب) عن طريق QR.
- الكمبيوتر (صفحة البرنامج على 127.0.0.1) يرسل حالته كل ثانية لـ /api/remote/host ويستلم الأوامر.
- الجوال يفتح /remote?k=<مفتاح> ويرسل أوامر لـ /api/remote/cmd ويقرأ الحالة من /api/remote/state.
- المفتاح سري ويتغير من الإعدادات («إلغاء ربط كل الأجوال»).
"""
import hmac
import json
import os
import threading
import time

from flask import Blueprint, jsonify, request, send_from_directory

bp = Blueprint("harmoni_remote", __name__)

ACTIONS = {"play_pause", "skip", "prev", "seek", "volume", "mute", "loop", "shuffle", "like", "play", "play_track"}
_state = {"base": "", "static": "", "key": None, "host": None, "host_ts": 0.0, "lists": None, "lists_ts": 0.0,
          "seen": 0.0, "is_local": lambda: False, "info": lambda: {}}
_cmds = []
_lock = threading.Lock()


def init(base_dir, static_dir, is_local_owner, info):
    _state.update({"base": base_dir, "static": static_dir, "key": None, "is_local": is_local_owner, "info": info})


def _key_path():
    return os.path.join(_state["base"], "remote_key.json")


def key(regenerate=False):
    with _lock:
        if _state["key"] and not regenerate:
            return _state["key"]
        k = ""
        if not regenerate:
            try:
                with open(_key_path(), encoding="utf-8-sig") as f:
                    k = str(json.load(f).get("key") or "")
            except Exception:
                k = ""
        if len(k) < 32:
            k = os.urandom(16).hex()
            try:
                with open(_key_path(), "w", encoding="utf-8") as f:
                    json.dump({"key": k}, f)
            except Exception:
                pass
        _state["key"] = k
        return k


def _ok(k):
    return bool(k) and hmac.compare_digest(str(k), key())


def phone_active():
    """فيه جوال فاتح صفحة التحكم بآخر 20 ثانية؟ (عشان الكمبيوتر يرسل حالته أسرع)"""
    return time.time() - _state["seen"] < 20


# ===== صفحة الجوال =====
@bp.route("/remote")
def remote_page():
    resp = send_from_directory(_state["static"], "remote.html")
    resp.headers["Cache-Control"] = "no-store"
    return resp


@bp.route("/api/remote/state")
def remote_state():
    if not _ok(request.args.get("k")):
        return jsonify({"error": "bad_key"}), 403
    _state["seen"] = time.time()
    host = _state["host"]
    online = bool(host) and time.time() - _state["host_ts"] < 6
    out = {"online": online, "state": host if online else None, "ts": time.time()}
    if request.args.get("lists") == "1":
        out["lists"] = _state["lists"]
    return jsonify(out)


@bp.route("/api/remote/cmd", methods=["POST"])
def remote_cmd():
    d = request.get_json(silent=True) or {}
    if not _ok(d.get("k")):
        return jsonify({"error": "bad_key"}), 403
    action = str(d.get("action") or "")
    if action not in ACTIONS:
        return jsonify({"error": "bad_action"}), 400
    _state["seen"] = time.time()
    cmd = {"id": "%x" % int(time.time() * 1e6), "action": action, "value": d.get("value"), "kind": d.get("kind")}
    if action == "play_track" and not isinstance(d.get("value"), dict):
        return jsonify({"error": "bad_value"}), 400
    with _lock:
        _cmds.append(cmd)
        del _cmds[:-30]
    return jsonify({"ok": True})


# ===== جهة الكمبيوتر =====
@bp.route("/api/remote/host", methods=["POST"])
def remote_host():
    if not _state["is_local"]():
        return jsonify({"error": "forbidden"}), 403
    d = request.get_json(silent=True) or {}
    if isinstance(d.get("state"), dict):
        _state["host"] = d["state"]
        _state["host_ts"] = time.time()
    if isinstance(d.get("lists"), dict):
        _state["lists"] = d["lists"]
        _state["lists_ts"] = time.time()
    with _lock:
        out = list(_cmds)
        _cmds.clear()
    return jsonify({"cmds": out, "phone": phone_active(), "need_lists": time.time() - _state["lists_ts"] > 30})


@bp.route("/api/remote/info", methods=["GET", "POST"])
def remote_info():
    """للإعدادات/نافذة QR بالكمبيوتر بس: الروابط اللي يفتحها الجوال."""
    if not _state["is_local"]():
        return jsonify({"error": "forbidden"}), 403
    k = key(regenerate=request.method == "POST" and (request.get_json(silent=True) or {}).get("reset") is True)
    info = _state["info"]() or {}
    links = []
    if info.get("tunnel"):
        links.append({"kind": "internet", "url": info["tunnel"].rstrip("/") + "/remote?k=" + k})
    if info.get("ip") and info.get("port"):
        links.append({"kind": "wifi", "url": "http://%s:%s/remote?k=%s" % (info["ip"], info["port"], k)})
    return jsonify({"links": links, "phone": phone_active()})
