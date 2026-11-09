"""Harmoni Hub client: يكلم السيرفر السحابي المشترك (Cloudflare Worker) عشان الحسابات تكون وحدة بكل الأجهزة.
لو السيرفر طايح أو مو متاح: البرنامج يرجع يشتغل محلياً مثل قبل، ويرفع التغييرات أول ما يرجع.
"""
import json
import os
import sys
import threading
import time

import requests

DEFAULT_URL = "https://hub.harmoni.click"
TIMEOUT = 5
COOLDOWN = 60   # بعد فشل الاتصال نوقف نحاول لمدة دقيقة

_lock = threading.RLock()
_state = {"base": "", "url": None, "down_until": 0.0, "tokens": None, "meta": None}
_session = requests.Session()
if __import__("sys").platform == "darwin":
    _session.trust_env = False   # الماك: بدون فحص بروكسي النظام مع كل طلب (بطيء)


def init(base_dir):
    _state.update({"base": base_dir, "url": None, "down_until": 0.0, "tokens": None, "meta": None})


def _path(name):
    return os.path.join(_state["base"], name)


def url():
    if _state["url"] is None:
        u = os.environ.get("HARMONI_HUB")
        if u is None and os.path.basename(sys.argv[0] or "").startswith("test_"):
            u = ""   # الاختبارات ما تكلم السيرفر الحقيقي
        if u is None:
            u = DEFAULT_URL
            try:
                with open(_path("hub_config.json"), encoding="utf-8-sig") as f:
                    cfg = json.load(f)
                if cfg.get("enabled") is False:
                    u = ""
                elif cfg.get("url"):
                    u = str(cfg["url"])
            except Exception:
                pass
        _state["url"] = (u or "").strip().rstrip("/")
    return _state["url"]


def enabled():
    return bool(url())


def available():
    return enabled() and time.time() >= _state["down_until"]


def mark_down():
    _state["down_until"] = time.time() + COOLDOWN


def call(method, path, body=None, params=None, timeout=TIMEOUT):
    """يرجع (status, json) — أو None لو السيرفر ما يرد (وقتها نشتغل محلياً)."""
    if not available():
        return None
    try:
        r = _session.request(method, url() + path, json=body, params=params, timeout=timeout,
                             headers={"User-Agent": "Harmoni"})
    except Exception:
        mark_down()
        return None
    if r.status_code >= 500 or r.status_code in (404,) and not r.headers.get("content-type", "").startswith("application/json"):
        mark_down()
        return None
    try:
        data = r.json()
    except Exception:
        mark_down()
        return None
    if not isinstance(data, dict):
        mark_down()
        return None
    _state["down_until"] = 0.0
    return r.status_code, data


# ===== توكنات السيرفر (لكل حساب على هالجهاز) =====
def _load_json(name, default):
    try:
        with open(_path(name), encoding="utf-8-sig") as f:
            v = json.load(f)
        return v if isinstance(v, type(default)) else default
    except Exception:
        return default


def _save_json(name, data):
    p = _path(name)
    tmp = p + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False)
    os.replace(tmp, p)


def _tokens():
    if _state["tokens"] is None:
        _state["tokens"] = _load_json("hub_tokens.json", {})
    return _state["tokens"]


def token(uid):
    with _lock:
        return _tokens().get(uid or "") or ""


def set_token(uid, tok):
    with _lock:
        t = _tokens()
        if uid and tok and t.get(uid) != tok:
            t[uid] = tok
            try:
                _save_json("hub_tokens.json", t)
            except Exception:
                pass


def meta(key, default=None):
    with _lock:
        if _state["meta"] is None:
            _state["meta"] = _load_json("hub_state.json", {})
        return _state["meta"].get(key, default)


def set_meta(key, value):
    with _lock:
        meta(key)
        _state["meta"][key] = value
        try:
            _save_json("hub_state.json", _state["meta"])
        except Exception:
            pass


def forget_token(uid):
    with _lock:
        t = _tokens()
        if t.pop(uid or "", None) is not None:
            try:
                _save_json("hub_tokens.json", t)
            except Exception:
                pass
