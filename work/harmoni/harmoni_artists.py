"""المغني الحقيقي للأغنية (مو اسم قناة يوتيوب) + صورته — للإحصائيات.
مفصول عن server.py: ما يعتمد على أي شيء فيه غير مجلد البيانات."""
import concurrent.futures
import json
import os
import re
import threading

import requests as _requests
from flask import Blueprint, jsonify, request

bp = Blueprint("harmoni_artists", __name__)
_STATE = {"base_dir": "."}


def init(base_dir):
    _STATE["base_dir"] = base_dir


def _cache_file():
    return os.path.join(_STATE["base_dir"], "artist_cache.json")


_CACHE = {}
_LOCK = threading.Lock()
_LOADED = [False]
_NOISE_RE = re.compile(
    r"\(.*?\)|\[.*?\]|【.*?】|official\s*(music\s*)?(video|audio|lyric[s]?\s*video)?|lyrics?|"
    r"music\s*video|audio|video\s*clip|clip|visuali[sz]er|hd|4k|remaster(ed)?|"
    r"فيديو\s*كليب|كليب|كلمات|حصريا|حصرياً|الرسمية|رسمي|official", re.I)
_CHANNEL_NOISE_RE = re.compile(r"\s*-\s*topic$|vevo$|\s*official$|\s*music$|\s*records?$|^تسجيلات\s*", re.I)


def _anorm(s):
    s = str(s or "").lower()
    s = re.sub(r"[ً-ٰٟـ]", "", s)
    s = s.replace("أ", "ا").replace("إ", "ا").replace("آ", "ا").replace("ة", "ه").replace("ى", "ي")
    return re.sub(r"[^\w]+", " ", s).strip()


def _clean_title(title):
    t = _NOISE_RE.sub(" ", str(title or ""))
    t = re.sub(r"[#@|│｜•·~_]+", " ", t)
    return re.sub(r"\s+", " ", t).strip(" -–—")


def _guess_from_title(title):
    """'Artist - Song' أو 'Artist ft. X | Song' → Artist"""
    t = str(title or "")
    for sep in (" - ", " – ", " — ", " | ", "｜"):
        if sep in t:
            left = t.split(sep)[0]
            left = re.split(r"\s+(ft\.?|feat\.?|x|&|و)\s+", left, flags=re.I)[0]
            left = _clean_title(left)
            if 1 < len(left) <= 40:
                return left
    return ""


def _deezer(path, params):
    try:
        r = _requests.get("https://api.deezer.com/" + path, params=params, timeout=6)
        return r.json() or {}
    except Exception:
        return {}


def _load_cache():
    if _LOADED[0]:
        return
    _LOADED[0] = True
    try:
        with open(_cache_file(), encoding="utf-8") as f:
            _CACHE.update(json.load(f) or {})
    except Exception:
        pass


def _save_cache():
    try:
        with _LOCK:
            data = dict(_CACHE)
        tmp = _cache_file() + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False)
        os.replace(tmp, _cache_file())
    except Exception:
        pass


def _artist_picture(name):
    key = "pic:" + _anorm(name)
    with _LOCK:
        if key in _CACHE:
            return _CACHE[key]
    pic = ""
    data = _deezer("search/artist", {"q": name, "limit": 5})
    for a in data.get("data") or []:
        if _anorm(a.get("name")) == _anorm(name):
            pic = a.get("picture_xl") or a.get("picture_big") or ""
            break
    with _LOCK:
        _CACHE[key] = pic
    return pic


def resolve_real_artist(title, uploader, source=""):
    """يرجع اسم المغني الحقيقي (مو اسم القناة) وصورته."""
    key = "t:" + _anorm(title) + "|" + _anorm(uploader)
    with _LOCK:
        hit = _CACHE.get(key)
    if hit:
        return hit
    name = ""
    if source == "spotify" and uploader:
        name = uploader.split(",")[0].strip()
    title_n = _anorm(title)
    up_n = _anorm(_CHANNEL_NOISE_RE.sub("", str(uploader or "")))
    if not name:
        q = _clean_title(title)
        data = _deezer("search", {"q": q, "limit": 8}) if q else {}
        for tr in data.get("data") or []:
            an = (tr.get("artist") or {}).get("name") or ""
            a_n = _anorm(an)
            if a_n and (a_n in title_n or (up_n and (a_n in up_n or up_n in a_n))):
                name = an
                pic = (tr.get("artist") or {}).get("picture_xl") or ""
                with _LOCK:
                    _CACHE["pic:" + a_n] = pic
                break
    if not name:
        name = _guess_from_title(title)
    if not name:
        name = _CHANNEL_NOISE_RE.sub("", str(uploader or "")).strip() or str(uploader or "")
    out = {"artist": name, "picture": _artist_picture(name) if name else ""}
    with _LOCK:
        _CACHE[key] = out
    return out


@bp.route("/api/artist_resolve", methods=["POST"])
def api_artist_resolve():
    data = request.get_json(silent=True) or {}
    items = (data.get("items") or [])[:40]
    _load_cache()
    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as ex:
        res = list(ex.map(lambda it: resolve_real_artist((it or {}).get("title") or "", (it or {}).get("artist") or "",
                                                          (it or {}).get("source") or ""), items))
    _save_cache()
    return jsonify({"items": res})
