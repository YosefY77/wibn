"""نظام الشارات: شارات تظهر جنب اسم الشخص.
- مدمجة: 👑 المؤسس، 🔥 الأكثر استماعاً (تلقائية)، 🥇 أول مستخدم، ⭐ مميز، 💎 داعم
- المؤسس بس (من جهازه وبحسابه) يعطي/يشيل الشارات ويسوي شارات جديدة.
"""
import json
import os
import re
import threading
import time

BUILTIN = {
    "founder":      {"emoji": "👑", "ar": "مؤسس هارموني", "en": "Harmoni founder", "color": "#f5c542"},
    "top_listener": {"emoji": "🔥", "ar": "الأكثر استماعاً", "en": "Top listener", "color": "#ff7a3d", "auto": True},
    "first_user":   {"emoji": "🥇", "ar": "أول مستخدم", "en": "First user", "color": "#e0b04a"},
    "vip":          {"emoji": "⭐", "ar": "مميز", "en": "VIP", "color": "#ffd166"},
    "supporter":    {"emoji": "💎", "ar": "داعم", "en": "Supporter", "color": "#5cc8ff"},
}
ORDER = ["founder", "first_user", "top_listener", "vip", "supporter"]
SEED = {"founder": "hiro", "first_user": "md5n"}   # أول تشغيل: نعطيها لأصحابها باليوزر

_lock = threading.RLock()
_state = {"path": "", "data": None, "top": (0.0, None)}


def init(base_dir):
    _state["path"] = os.path.join(base_dir, "badges.json")
    _state["data"] = None


def _blank():
    return {"defs": {}, "assign": {}, "seeded": False}


def _load():
    if _state["data"] is not None:
        return _state["data"]
    data = _blank()
    try:
        with open(_state["path"], encoding="utf-8-sig") as f:
            got = json.load(f)
        if isinstance(got, dict):
            data.update({k: got.get(k, data[k]) for k in data})
    except FileNotFoundError:
        pass
    except Exception:
        pass
    _state["data"] = data
    return data


def _save():
    data = _load()
    tmp = _state["path"] + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False)
    os.replace(tmp, _state["path"])


def seed(profiles, norm_username):
    """أول مرة: المؤسس لـ @hiro وأول مستخدم لـ @md5n."""
    with _lock:
        data = _load()
        if data.get("seeded"):
            return
        by_un = {norm_username(p.get("username")): uid for uid, p in profiles.items() if p.get("username")}
        for badge, un in SEED.items():
            uid = by_un.get(norm_username(un))
            if uid:
                lst = data["assign"].setdefault(uid, [])
                if badge not in lst:
                    lst.append(badge)
        if any("founder" in (lst or []) for lst in data["assign"].values()):
            data["seeded"] = True   # لو المؤسس ما سجّل للحين نعيد المحاولة بالتشغيل الجاي
        _save()


def defs():
    with _lock:
        out = {k: dict(v) for k, v in BUILTIN.items()}
        for k, v in (_load().get("defs") or {}).items():
            if k not in out:
                out[k] = dict(v, custom=True)
        return out


def order():
    d = defs()
    return ORDER + sorted(k for k in d if k not in ORDER)


def top_listener(profiles):
    """أكثر واحد استماعاً (بالوقت، وبعدها بعدد الأغاني) — نحسبها كل دقيقة."""
    ts, uid = _state["top"]
    if time.time() - ts < 60:
        return uid
    best, best_key = None, (0, 0)
    for u, p in profiles.items():
        st = p.get("stats") if isinstance(p, dict) else None
        if not isinstance(st, dict):
            continue
        key = (float(st.get("secs") or 0), int(st.get("total") or 0))
        if key > best_key:
            best, best_key = u, key
    _state["top"] = (time.time(), best)
    return best


def badges_of(uid, profiles):
    with _lock:
        lst = [b for b in (_load()["assign"].get(uid) or []) if b in defs()]
    if uid and uid == top_listener(profiles):
        lst.append("top_listener")
    o = order()
    return sorted(set(lst), key=lambda b: o.index(b) if b in o else 99)


def all_badges(profiles):
    return {u: badges_of(u, profiles) for u in profiles if badges_of(u, profiles)}


def is_admin(uid):
    with _lock:
        return bool(uid) and "founder" in (_load()["assign"].get(uid) or [])


def grant(target, badge):
    with _lock:
        if badge not in defs() or BUILTIN.get(badge, {}).get("auto"):
            raise ValueError("bad_badge")
        lst = _load()["assign"].setdefault(target, [])
        if badge not in lst:
            lst.append(badge)
        _save()


def revoke(target, badge, admin_uid):
    with _lock:
        if badge == "founder" and target == admin_uid:
            raise ValueError("keep_founder")   # ما تشيل التاج عن نفسك (عشان ما تقفل على نفسك)
        data = _load()
        lst = data["assign"].get(target) or []
        if badge in lst:
            lst.remove(badge)
        if not lst:
            data["assign"].pop(target, None)
        _save()


_EMOJI_OK = re.compile(r"^\S{1,8}$")


def create(emoji, ar, en=""):
    emoji, ar, en = str(emoji or "").strip(), str(ar or "").strip()[:30], str(en or "").strip()[:30]
    if not emoji or not _EMOJI_OK.match(emoji) or not ar:
        raise ValueError("bad_def")
    with _lock:
        data = _load()
        key = "c" + format(int(time.time() * 1000), "x")
        data["defs"][key] = {"emoji": emoji, "ar": ar, "en": en or ar, "color": "#9aa4ff"}
        _save()
        return key


def delete(badge):
    with _lock:
        data = _load()
        if badge not in (data.get("defs") or {}):
            raise ValueError("builtin")
        data["defs"].pop(badge, None)
        for u in list(data["assign"]):
            data["assign"][u] = [b for b in data["assign"][u] if b != badge]
            if not data["assign"][u]:
                data["assign"].pop(u)
        _save()


def export():
    """نسخة من الشارات المحلية (عشان نرفعها للسيرفر المشترك مرة وحدة)."""
    with _lock:
        data = _load()
        return {"defs": dict(data.get("defs") or {}), "assign": {u: list(l) for u, l in (data.get("assign") or {}).items()}}
