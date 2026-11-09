import atexit
import base64
import concurrent.futures
import hashlib
import hmac
import itertools
import html
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import threading
import tempfile
import time
import requests as _requests
import urllib.parse
import urllib.request
import uuid

from flask import Flask, jsonify, redirect, request, send_from_directory, send_file, Response, stream_with_context, abort
from flask_socketio import SocketIO, emit, join_room, leave_room
from werkzeug.serving import ThreadedWSGIServer

_VENDOR_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "vendor")
if os.path.isdir(_VENDOR_DIR):
    sys.path.insert(0, _VENDOR_DIR)
import yt_dlp

if getattr(sys, "frozen", False):
    BASE_DIR = os.path.dirname(sys.executable)
    _BUNDLE_DIR = sys._MEIPASS
else:
    BASE_DIR = os.path.dirname(os.path.abspath(__file__))
    _BUNDLE_DIR = BASE_DIR
STATIC_DIR = os.path.join(_BUNDLE_DIR, "static")

app = Flask(__name__, static_folder="static", static_url_path="/static")
app.config["SEND_FILE_MAX_AGE_DEFAULT"] = 0
socketio = SocketIO(app, async_mode="threading", cors_allowed_origins="*")

from discord_presence import install as install_discord
discord_presence = install_discord(app, BASE_DIR)
atexit.register(discord_presence.close)


@app.after_request
def _no_cache(resp):
    resp.headers["Cache-Control"] = "no-cache, no-store, must-revalidate"
    resp.headers["Pragma"] = "no-cache"
    resp.headers["Expires"] = "0"
    return resp

YD_BASE = {
    "quiet": True,
    "no_warnings": True,
    "skip_download": True,
    "noplaylist": False,
    "socket_timeout": 12,
    "retries": 1,
    "js_runtimes": {"deno": {}, "node": {}},
    "source_address": "0.0.0.0",
}

SEARCH_LIMIT = 36


def get_lan_ip():
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            s.connect(("8.8.8.8", 80))
            ip = s.getsockname()[0]
        finally:
            s.close()
        return ip
    except Exception:
        return "127.0.0.1"


TUNNEL_URL = ""
TUNNEL_PROC = None


def start_tunnel():
    """تشغيل نفق cloudflared — ثابت إذا فيه إعداد محفوظ، وإلا نفق سريع مؤقت."""
    global TUNNEL_URL, TUNNEL_PROC
    if TUNNEL_URL or TUNNEL_PROC:
        return
    cf_exe = shutil.which("cloudflared")
    cfg = os.path.join(os.path.expanduser("~"), ".cloudflared", "config.yml")
    if not cf_exe and os.path.exists(r"C:\Program Files (x86)\cloudflared\cloudflared.exe"):
        cf_exe = r"C:\Program Files (x86)\cloudflared\cloudflared.exe"
    if cf_exe and os.path.exists(cfg):
        # نفق دائم باسم harmoni — الرابط الثابت مأخوذ من ملف الإعدادات
        try:
            import re as _re
            hosts = _re.findall(r"hostname:\s*(\S+)", open(cfg, encoding="utf-8").read())
            proc = subprocess.Popen(
                [cf_exe, "tunnel", "--config", cfg, "run", "harmoni"],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
            TUNNEL_PROC = proc
            atexit.register(lambda: proc.kill() if proc.poll() is None else None)
            if hosts:
                TUNNEL_URL = "https://" + hosts[0]
            print(f"  الرابط الدائم:  {TUNNEL_URL}")
            return
        except Exception as ex:
            print("  تعذّر تشغيل النفق الدائم، سيتم استخدام النفق السريع:", ex)
    if not cf_exe:
        print("  (اختياري) لم يُعثر على cloudflared — الرابط يعمل داخل الشبكة فقط.")
        print("  لتثبيته من متجر ويندوز:  winget install cloudflared")
        return
    try:
        proc = subprocess.Popen(
            ["cloudflared", "tunnel", "--url", "http://localhost:%d" % PORT, "--no-autoupdate"],
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except Exception as ex:
        print("  تعذّر تشغيل النفق العام:", ex)
        return
    TUNNEL_PROC = proc
    atexit.register(lambda: proc.kill() if proc.poll() is None else None)
    url_re = re.compile(r"https://[a-z0-9-]+\.trycloudflare\.com")

    def read():
        global TUNNEL_URL
        try:
            for line in proc.stdout:
                m = url_re.search(line)
                if m and not TUNNEL_URL:
                    TUNNEL_URL = m.group(0)
                    print(f"  الرابط العام (من أي شبكة):  {TUNNEL_URL}")
        except Exception:
            pass

    threading.Thread(target=read, daemon=True).start()


def is_url(text):
    return re.match(r"^https?://", text.strip(), re.IGNORECASE) is not None


NON_MUSIC_RE = re.compile(
    r"\b(interview|documentary|trailer|movie|episode|الحلقة|مسلسل|فيلم|تحليل|"
    r"شرح|review|reaction|gameplay|podcast|vlog|tutorial|news|debate|تغطية|أخبار|"
    r"be like|compilation|on shuffle|discog|albums that|influence|inspired|history|"
    r"ranking|trivia|story|nutshell|bands that sound like|make a song like|"
    r"favorite band|says about you|only better)\b",
    re.IGNORECASE,
)


def is_spotify(url):
    host = (urllib.parse.urlparse(url).hostname or "").lower()
    return host == "spotify.com" or host.endswith(".spotify.com") or host == "spotify.link"


def _spotify_meta(url):
    req = urllib.request.Request(url, headers={
        "User-Agent": "Mozilla/5.0",
        "Accept-Language": "en",
    })
    page = urllib.request.urlopen(req, timeout=25).read().decode("utf-8", "replace")

    def meta(prop):
        for m in re.findall(r"<meta\b[^>]*>", page):
            if ('property="' + prop + '"' in m.lower()
                    or "property='" + prop + "'" in m.lower()
                    or 'name="' + prop + '"' in m.lower()):
                mm = re.search(r'content="([^"]*)"', m)
                if mm:
                    return html.unescape(mm.group(1)).strip()
        return None

    og_title = meta("og:title") or ""
    og_desc = meta("og:description") or ""
    og_img = meta("og:image") or ""

    artist = og_desc.split("\u00b7")[0].strip() if og_desc else ""
    title = re.sub(r"\s*\|\s*Spotify\s*$", "", og_title, flags=re.IGNORECASE).strip()
    title = re.sub(r"\s*-\s*(?:Album|Single|EP|Playlist|song and lyrics|Compilation).*$",
                   "", title, flags=re.IGNORECASE).strip()
    return title, artist, og_img


def resolve_spotify(url):
    title, artist, thumb = _spotify_meta(url)
    query = f"{artist} {title}".strip() or title
    if not query:
        return []
    results = search(query, "youtube", 6)
    if thumb:
        for r in results:
            r["thumbnail"] = thumb
    return results


def _extract_base(url, clients=None, fast=False):
    opts = dict(YD_BASE)
    opts["format"] = "bestaudio/best"
    opts["noplaylist"] = True
    if clients:
        opts["extractor_args"] = {"youtube": {"player_client": clients}}
    if fast:
        opts.setdefault("extractor_args", {}).setdefault("youtube", {}).update({
            "skip": ["hls", "dash"], "player_skip": ["initial_data"]})
    with yt_dlp.YoutubeDL(opts) as ydl:
        return ydl.extract_info(url, download=False)


def _extract(url):
    return _extract_base(url)


def pick_stream_url(info):
    if not info:
        return None, None
    if info.get("requested_formats"):
        for f in info["requested_formats"]:
            if f.get("url"):
                return f["url"], f
    fmts = info.get("formats") or []
    audio = [f for f in fmts
             if f.get("url") and f.get("acodec") not in (None, "none")]
    # Prefer progressive audio-only streams: no video bytes or HLS startup.
    direct = [f for f in audio if f.get("vcodec") == "none"
              and f.get("protocol") in ("http", "https")]
    if direct:
        audio = direct
    if not audio:
        audio = [f for f in fmts if f.get("url")]
    if not audio:
        if info.get("url"):
            return info["url"], info
        return None, None
    audio.sort(key=lambda f: (f.get("abr") or f.get("tbr") or 0), reverse=True)
    return audio[0]["url"], audio[0]


def normalize_entry(e):
    if not e:
        return None
    title = e.get("title") or e.get("fulltitle") or "بدون عنوان"
    artist = (e.get("artist")
              or e.get("uploader")
              or e.get("channel")
              or e.get("creator")
              or e.get("extractor_key")
              or "")
    if isinstance(artist, list):
        artist = ", ".join(str(a) for a in artist)
    duration = 0
    try:
        duration = int(float(e.get("duration") or 0))
    except Exception:
        duration = 0
    thumb = e.get("thumbnail") or ""
    if not thumb and isinstance(e.get("thumbnails"), list) and e["thumbnails"]:
        last = e["thumbnails"][-1] or {}
        thumb = last.get("url") or ""
    web_url = e.get("webpage_url") or e.get("original_url") or e.get("url") or ""
    if not web_url and e.get("id"):
        web_url = "https://www.youtube.com/watch?v=%s" % e.get("id")
    source = "soundcloud" if (e.get("extractor_key") or "").lower() == "soundcloud" else "youtube"
    is_playlist = ("youtube.com/playlist" in web_url.lower()
                   or (e.get("ie_key") == "YoutubeTab" and "list=" in web_url))
    if not thumb and e.get("id") and "youtube" in web_url.lower() and not is_playlist:
        thumb = "https://i.ytimg.com/vi/%s/mqdefault.jpg" % e.get("id")
    row = {
        "title": str(title),
        "artist": str(artist),
        "duration": duration,
        "thumbnail": thumb,
        "web_url": web_url,
        "source": source,
    }
    try:
        if e.get("view_count") is not None:
            row["views"] = int(e.get("view_count"))
    except Exception:
        pass
    try:
        ts = e.get("timestamp") or e.get("release_timestamp")
        if ts:
            row["ts"] = int(ts)
    except Exception:
        pass
    if is_playlist:
        row["kind"] = "playlist"
        row["duration"] = 0
    return row


_SEARCH_CACHE = {}
_SEARCH_CACHE_TTL = 21600      # 6h
_SEARCH_STALE = 900            # refresh in background if older than 15min
_SEARCH_CACHE_FILE = os.path.join(
    BASE_DIR, "search_cache.json")
_SEARCH_LOCK = threading.Lock()
_REFRESHING = set()
_SEARCH_INFLIGHT = {}
_SEARCH_SAVE_LOCK = threading.Lock()


def _load_search_cache():
    try:
        with open(_SEARCH_CACHE_FILE, encoding="utf-8") as f:
            data = json.load(f)
        for k, v in (data or {}).items():
            parts = k.split("|")
            if len(parts) == 3:
                _SEARCH_CACHE[(parts[0], parts[1], int(parts[2]))] = tuple(v)
    except Exception:
        pass


def _save_search_cache():
    try:
        with _SEARCH_SAVE_LOCK:
            with _SEARCH_LOCK:
                data = {"%s|%s|%d" % k: list(v) for k, v in _SEARCH_CACHE.items()}
            tmp = _SEARCH_CACHE_FILE + ".tmp"
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump(data, f, ensure_ascii=False)
            os.replace(tmp, _SEARCH_CACHE_FILE)
    except Exception:
        pass


def _bg_refresh(key):
    query, source, limit = key
    try:
        _do_search(query, source, limit)
    except Exception:
        pass


# فلاتر البحث: كل فلتر = طلب شبكة واحد فقط (نفس تكلفة البحث العادي)
SEARCH_SORTS = ("popular", "oldest", "playlists")
_YT_SORT_PARAMS = {
    "popular": "CAMSAhAB",        # ترتيب حسب عدد المشاهدات + فيديوهات فقط
    "playlists": "EgIQAw%3D%3D",  # قوائم تشغيل فقط
}


def _split_source(source):
    base, _, mode = (source or "").partition(":")
    return base, mode


def _sc_playlist_search(query, limit):
    """بحث بلاي لستات ساوند كلاود بطلب API واحد."""
    with yt_dlp.YoutubeDL(dict(YD_BASE)) as ydl:
        ie = ydl.get_info_extractor("SoundcloudSearch")
        ie.initialize()
        data = ie._call_api(
            ie._API_V2_BASE + "search/playlists", query, "Searching playlists",
            query={"q": query, "limit": max(1, min(int(limit), 50)), "offset": 0, "linked_partitioning": 1},
            headers=ie._HEADERS) or {}
    out = []
    for p in data.get("collection") or []:
        if not p or not p.get("permalink_url"):
            continue
        thumb = p.get("artwork_url") or ""
        if not thumb:
            for t in p.get("tracks") or []:
                if t and t.get("artwork_url"):
                    thumb = t["artwork_url"]
                    break
        if not thumb:
            thumb = ((p.get("user") or {}).get("avatar_url")) or ""
        out.append({
            "title": p.get("title") or "Playlist",
            "artist": (p.get("user") or {}).get("username") or "",
            "duration": 0,
            "thumbnail": thumb.replace("-large.", "-t300x300."),
            "web_url": p["permalink_url"],
            "source": "soundcloud",
            "kind": "playlist",
            "count": int(p.get("track_count") or 0),
            "views": int(p.get("likes_count") or 0),
        })
    return out


_SEARCH_CURSORS = {}
_SEARCH_CURSOR_LOCK = threading.Lock()


def _youtube_search_rows(query, source, opts, target, limit):
    # Retain YouTube's lazy continuation iterator instead of fetching page one
    # again for every larger requested prefix. Each query/filter has its own lock.
    key = (query.strip().lower(), source)
    with _SEARCH_CURSOR_LOCK:
        now = time.monotonic()
        cursor = _SEARCH_CURSORS.get(key)
        if cursor is None or now - cursor['created'] > 900:
            cursor = dict(created=now, lock=threading.Lock(), rows=[], iterator=None, done=False)
            _SEARCH_CURSORS[key] = cursor
        while len(_SEARCH_CURSORS) > 16:
            del _SEARCH_CURSORS[next(iter(_SEARCH_CURSORS))]
    with cursor['lock']:
        try:
            if cursor['iterator'] is None and not cursor['done']:
                def entries():
                    with yt_dlp.YoutubeDL(opts) as ydl:
                        info = ydl.extract_info(target, download=False, process=False)
                        yield from (info.get('entries') or []) if info else []
                cursor['iterator'] = entries()
            mode = _split_source(source)[1]
            while len(cursor['rows']) < limit and not cursor['done']:
                entry = next(cursor['iterator'], None)
                if entry is None:
                    cursor['done'] = True
                    break
                row = normalize_entry(entry)
                if row and ((row.get('kind') == 'playlist') == (mode == 'playlists')):
                    cursor['rows'].append(row)
            return cursor['rows'][:limit]
        except Exception:
            with _SEARCH_CURSOR_LOCK:
                if _SEARCH_CURSORS.get(key) is cursor:
                    del _SEARCH_CURSORS[key]
            raise


def _do_search(query, source, limit=SEARCH_LIMIT):
    base, mode = _split_source(source)
    if base == "soundcloud" and mode == "playlists":
        results = _sc_playlist_search(query, limit)
    else:
        opts = dict(YD_BASE)
        opts["noplaylist"] = True
        if base == "youtube":
            opts["extract_flat"] = True
            if mode == "oldest":
                # تاريخ تقريبي من نفس صفحة النتائج — بدون أي طلبات إضافية
                opts["extractor_args"] = {"youtubetab": {"approximate_date": [""]}}
        if base == "youtube" and mode in _YT_SORT_PARAMS:
            target = ("https://www.youtube.com/results?search_query=%s&sp=%s"
                      % (urllib.parse.quote_plus(query), _YT_SORT_PARAMS[mode]))
        else:
            target = "%s%d:%s" % ("ytsearch" if base == "youtube" else "scsearch", 980 if base == "youtube" else limit, query)
        if base == "youtube":
            results = _youtube_search_rows(query, source, opts, target, limit)
        else:
            with yt_dlp.YoutubeDL(opts) as ydl:
                info = ydl.extract_info(target, download=False, process=False)
                entries = list(itertools.islice(info.get("entries") or [], limit)) if info else []
            results = [r for r in (normalize_entry(e) for e in entries) if r]
        if mode == "playlists":
            results = [r for r in results if r.get("kind") == "playlist"]
        elif base == "youtube":
            results = [r for r in results if r.get("kind") != "playlist"]
    with _SEARCH_LOCK:
        _SEARCH_CACHE[(query.strip().lower(), source, limit)] = (time.time(), results)
    _save_search_cache()
    return results


def search(query, source, limit=SEARCH_LIMIT):
    key = (query.strip().lower(), source, limit)
    now = time.time()
    with _SEARCH_LOCK:
        c = _SEARCH_CACHE.get(key)
        if not c:
            candidates = [(k, v) for k, v in _SEARCH_CACHE.items()
                          if k[:2] == key[:2] and k[2] >= limit]
            if candidates:
                _, entry = min(candidates, key=lambda item: item[0][2])
                c = (entry[0], entry[1][:limit])
    if c:
        age = now - c[0]
        if age < _SEARCH_STALE:
            return c[1]
        # stale-while-revalidate: serve cached instantly, refresh in background
        with _SEARCH_LOCK:
            fresh = key not in _REFRESHING
            if fresh:
                _REFRESHING.add(key)
        if fresh:
            def work():
                try:
                    _bg_refresh(key)
                finally:
                    with _SEARCH_LOCK:
                        _REFRESHING.discard(key)
            threading.Thread(target=work, daemon=True).start()
        return c[1]
    with _SEARCH_LOCK:
        pending = _SEARCH_INFLIGHT.get(key)
        leader = pending is None
        if leader:
            pending = concurrent.futures.Future()
            _SEARCH_INFLIGHT[key] = pending
    if not leader:
        return pending.result(timeout=45)
    try:
        result = _do_search(query, source, limit)
        pending.set_result(result)
        return result
    except Exception as error:
        pending.set_exception(error)
        raise
    finally:
        with _SEARCH_LOCK:
            _SEARCH_INFLIGHT.pop(key, None)


class Room:
    def __init__(self, rid, name="", hidden=False):
        self.rid = rid
        self.name = (name or "").strip() or ("غرفة " + rid)
        self.hidden = bool(hidden)
        self.lock = threading.RLock()
        self.queue = []
        self.current = None
        self.playing = False
        self.position = 0.0
        self.updated_at = time.time()
        self.shuffle = False
        self.members = {}
        self.gen = 0
        self.history = []
        self.owner_sid = None
        self.owner_uid = ""
        self.owner_name = ""
        self.max_members = 0
        self.password = ""
        self.can_add = True

    def state(self):
        with self.lock:
            return {
                "rid": self.rid,
                "name": self.name,
                "queue": self.queue,
                "current": self.current,
                "playing": self.playing,
                "position": self.position,
                "updated_at": self.updated_at,
                "shuffle": self.shuffle,
                "members": list(self.members.values()),
                "owner_sid": self.owner_sid,
                "max_members": self.max_members,
                "has_password": bool(self.password),
                "can_add": self.can_add,
                "server_time": time.time(),
            }

    def broadcast(self):
        socketio.emit("state", self.state(), room=self.rid)

    def create_room(self, sid, data):
        data = data or {}
        with self.lock:
            self.owner_sid = sid
            self.owner_uid = data.get("uid") or ""
            self.owner_name = (data.get("name") or "").strip() or "مستمع"
            try:
                m = int(data.get("max_members") or 0)
            except Exception:
                m = 0
            if m and m < 2:
                m = 2
            self.max_members = m
            self.hidden = bool(data.get("auto"))
            self.password = str(data.get("password") or "").strip()
            self.can_add = bool(data.get("can_add", True))
        self.broadcast()

    def can_join(self, sid, data):
        data = data or {}
        password = str(data.get("password") or "")
        uid = data.get("uid") or ""
        with self.lock:
            if sid in self.members:
                return None
            if uid and uid == self.owner_uid:
                return None
            if self.password:
                if not password:
                    return "need_password"
                if password != self.password:
                    return "wrong_password"
            if self.max_members and len(self.members) >= self.max_members:
                return "full"
            return None

    def kick(self, sid, target_sid):
        with self.lock:
            if sid != self.owner_sid:
                return False
            self.members.pop(target_sid, None)
        self.broadcast()
        return True

    def add_member(self, sid, data):
        data = data or {}
        with self.lock:
            self.members[sid] = {
                "id": sid,
                "uid": data.get("uid") or "",
                "name": data.get("name") or "مستمع",
                "username": data.get("username") or "",
                "avatar": data.get("avatar") or "",
                "banner": data.get("banner") or "",
                "bio": data.get("bio") or "",
                "colors": data.get("colors") or {},
                "likes": data.get("likes") or [],
                "following": data.get("following") or [],
            }
            if data.get("uid") and data.get("uid") == self.owner_uid:
                self.owner_sid = sid
                self.owner_name = (data.get("name") or "").strip() or self.owner_name
        register_profile(data)
        self.broadcast()

    def remove_member(self, sid):
        with self.lock:
            self.members.pop(sid, None)
        self.broadcast()

    def leave_member(self, sid):
        with self.lock:
            if self.owner_sid == sid:
                self.owner_sid = None
            self.members.pop(sid, None)
        self.broadcast()

    def _sc_stream(self, track):
        q = ((track.get("artist") or "") + " " + (track.get("title") or "")).strip()
        if not q:
            return None
        try:
            opts = dict(YD_BASE)
            opts["noplaylist"] = True
            with yt_dlp.YoutubeDL(opts) as ydl:
                info = ydl.extract_info("scsearch1:%s" % q, download=False, process=False)
            entries = list(info.get("entries") or []) if info else []
            if not entries:
                return None
            url = entries[0].get("url") or entries[0].get("webpage_url")
            if not url:
                return None
            stream_url, _ = pick_stream_url(_extract_base(url))
            return stream_url
        except Exception:
            return None

    def _bg_resolve(self, track):
        stream_url = None
        src = ""
        try:
            stream_url, _ = pick_stream_url(_extract_base(track["web_url"]))
            if stream_url:
                src = "youtube"
        except Exception:
            stream_url = None
        if not stream_url:
            # youtube struggling -> soundcloud immediately
            stream_url = self._sc_stream(track)
            if stream_url:
                src = "soundcloud"
        if not stream_url and track.get("web_url"):
            try:
                stream_url, _ = pick_stream_url(_extract_base(track["web_url"], ["android"]))
                if stream_url:
                    src = "youtube"
            except Exception:
                pass
        with self.lock:
            track["stream_url"] = stream_url
            if stream_url:
                track["stream_src"] = src
            track.pop("_resolving", None)
        self.broadcast()

    def _ensure_resolving(self, track):
        if not track or not track.get("web_url"):
            return
        if track.get("stream_url") or track.get("_resolving"):
            return
        track["_resolving"] = 1
        threading.Thread(target=self._bg_resolve, args=(track,), daemon=True).start()

    def _find_web_url(self, track):
        q = ((track.get("artist") or "") + " " + (track.get("title") or "")).strip()
        if not q:
            return
        try:
            res = search(q, "youtube", 1)
            if res:
                track["web_url"] = res[0]["web_url"]
                if not track.get("thumbnail"):
                    track["thumbnail"] = res[0].get("thumbnail") or ""
        except Exception:
            pass

    def add_track(self, track):
        track = dict(track)
        track.setdefault("id", uuid.uuid4().hex)
        if not track.get("web_url") and track.get("title"):
            self._find_web_url(track)
        with self.lock:
            if self.current is None:
                self.current = track
                self.position = 0.0
                self.updated_at = time.time()
                self.playing = True
                self.gen += 1
            else:
                self.queue.append(track)
        self.broadcast()
        self._ensure_resolving(track)
        return {"ok": True}

    def remove_track(self, tid):
        with self.lock:
            self.queue = [t for t in self.queue if t.get("id") != tid]
        self.broadcast()

    def _push_history(self, old_current):
        if old_current:
            self.history.append(old_current)
            if len(self.history) > 50:
                self.history.pop(0)

    def _next(self):
        with self.lock:
            self.gen += 1
            self._push_history(self.current)
            if self.shuffle and self.queue:
                import random
                idx = random.randrange(len(self.queue))
                self.current = self.queue.pop(idx)
            elif self.queue:
                self.current = self.queue.pop(0)
            else:
                self.current = None
                self.playing = False
                self.position = 0.0
                self.updated_at = time.time()
                return
            self.position = 0.0
            self.updated_at = time.time()
            self.playing = True
        self._ensure_resolving(self.current)

    def skip(self):
        self._next()
        self.broadcast()

    def ended(self, tid):
        with self.lock:
            if self.current is None or self.current.get("id") != tid:
                return
            self._next()
        self.broadcast()

    def play_now(self, tid):
        with self.lock:
            found = None
            for i, t in enumerate(self.queue):
                if t.get("id") == tid:
                    found = self.queue.pop(i)
                    break
            if found is None:
                return
            if self.current is not None:
                self._push_history(self.current)
                self.queue.insert(0, self.current)
            self.current = found
            self.position = 0.0
            self.updated_at = time.time()
            self.playing = True
            self.gen += 1
        self._ensure_resolving(found)
        self.broadcast()

    def previous(self):
        with self.lock:
            if not self.history:
                return
            prev_track = self.history.pop()
            if self.current is not None:
                self.queue.insert(0, self.current)
            self.current = prev_track
            self.position = 0.0
            self.updated_at = time.time()
            self.playing = True
            self.gen += 1
        self._ensure_resolving(self.current)
        self.broadcast()

    def play_pause(self):
        with self.lock:
            if self.current is None:
                return
            if self.playing:
                self.position = self.position + (time.time() - self.updated_at)
                self.playing = False
            else:
                self.playing = True
            self.updated_at = time.time()
        self.broadcast()

    def seek(self, pos):
        with self.lock:
            if self.current is None:
                return
            self.position = max(0.0, float(pos))
            self.updated_at = time.time()
        self.broadcast()

    def set_shuffle(self, on):
        with self.lock:
            self.shuffle = bool(on)
        self.broadcast()

    def reset(self):
        with self.lock:
            self.queue = []
            self.current = None
            self.playing = False
            self.position = 0.0
            self.updated_at = time.time()
            self.shuffle = False
            self.gen += 1
        self.broadcast()


ROOMS = {}
SID_ROOM = {}
ROOMS_LOCK = threading.RLock()

PROFILES = {}
PROFILES_LOCK = threading.RLock()
_PROFILES_FILE = os.path.join(BASE_DIR, "profiles.json")


def _load_profiles():
    global PROFILES
    try:
        with open(_PROFILES_FILE, encoding="utf-8") as f:
            PROFILES = json.load(f)
    except Exception:
        PROFILES = {}


def _save_profiles():
    # Serialize writers and replace only after the complete file reaches disk.
    with PROFILES_LOCK:
        fd, tmp = tempfile.mkstemp(dir=os.path.dirname(_PROFILES_FILE), suffix=".tmp")
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as f:
                json.dump(PROFILES, f, ensure_ascii=False)
                f.flush()
                os.fsync(f.fileno())
            os.replace(tmp, _PROFILES_FILE)
        finally:
            if os.path.exists(tmp):
                os.remove(tmp)


def norm_username(s):
    return (s or "").strip().lstrip("@").lower()


def _hash_password(pw, salt_hex):
    return hashlib.pbkdf2_hmac(
        "sha256", (pw or "").encode("utf-8"), bytes.fromhex(salt_hex), 60000).hex()


def public_profile(pr):
    return {
        "uid": pr.get("uid") or "",
        "name": pr.get("name") or "مستمع",
        "avatar": pr.get("avatar") or "",
        "banner": pr.get("banner") or "",
        "bio": pr.get("bio") or "",
        "colors": pr.get("colors") or {},
        "likes": pr.get("likes") or [],
        "following": list(pr.get("following") or []),
        "username": pr.get("username") or "",
    }


def userdata_payload(pr):
    return {
        "likes": pr.get("likes") or [],
        "following": list(pr.get("following") or []),
        "playlists": pr.get("playlists") or [],
        "history": pr.get("history") or [],
    }


@app.route("/api/userdata", methods=["GET", "POST"])
def api_userdata():
    if request.method == "GET":
        uid = request.args.get("uid") or ""
        with PROFILES_LOCK:
            pr = PROFILES.get(uid)
            data = userdata_payload(pr) if pr else {"likes": [], "following": [], "playlists": [], "history": []}
        return jsonify(data)
    data = request.get_json(force=True, silent=True) or {}
    uid = data.get("uid") or ""
    if not uid:
        return jsonify({"error": "no uid"}), 400
    with PROFILES_LOCK:
        pr = PROFILES.get(uid)
        if not pr:
            pr = PROFILES[uid] = {"uid": uid}
        for k in ("likes", "following", "playlists", "history"):
            v = data.get(k)
            if isinstance(v, list):
                pr[k] = v[:2000]
        PROFILES[uid] = pr
    _save_profiles()
    return jsonify({"ok": True})


@app.route("/api/register", methods=["POST"])
def api_register():
    data = request.get_json(force=True, silent=True) or {}
    raw_un = (data.get("username") or "").strip().lstrip("@")
    un = norm_username(raw_un)
    pw = str(data.get("password") or "")
    if len(un) < 3:
        return jsonify({"error": "اليوزر لازم يكون 3 أحرف على الأقل"}), 400
    if not re.match(r"^[a-zA-Z0-9_.\u0600-\u06FF-]+$", un):
        return jsonify({"error": "اليوزر فيه رموز غير مسموحة"}), 400
    if len(pw) < 4:
        return jsonify({"error": "كلمة السر لازم تكون 4 أحرف على الأقل"}), 400
    with PROFILES_LOCK:
        for k, p in PROFILES.items():
            if norm_username(p.get("username")) == un:
                return jsonify({"error": "هذا اليوزر محجوز، اختر غيره"}), 400
        uid = "u" + uuid.uuid4().hex[:15]
        salt = os.urandom(16).hex()
        pr = {
            "uid": uid,
            "name": raw_un,
            "username": un,
            "auth": {"salt": salt, "hash": _hash_password(pw, salt)},
        }
        PROFILES[uid] = pr
        _save_profiles()
    return jsonify({"uid": uid, "profile": public_profile(pr), "data": userdata_payload(pr)})


@app.route("/api/login", methods=["POST"])
def api_login():
    data = request.get_json(force=True, silent=True) or {}
    un = norm_username(data.get("username"))
    pw = str(data.get("password") or "")
    with PROFILES_LOCK:
        pr = None
        for k, p in PROFILES.items():
            if norm_username(p.get("username")) == un:
                pr = p
                break
    if not pr:
        return jsonify({"error": "يوزر أو كلمة سر خطأ"}), 401
    if not pr.get("auth"):
        # حساب قديم بدون كلمة سر: أول من يدخل يوزر مع كلمة سر (4+ أحرف) يستلمها
        if len(pw) < 4:
            return jsonify({"error": "هذا الحساب قديم، اختر كلمة سر (4 أحرف على الأقل) لتستلمه"}), 401
        salt = os.urandom(16).hex()
        pr["auth"] = {"salt": salt, "hash": _hash_password(pw, salt)}
        with PROFILES_LOCK:
            PROFILES[pr.get("uid")] = pr
            _save_profiles()
        return jsonify({"uid": pr.get("uid"), "profile": public_profile(pr), "data": userdata_payload(pr)})
    try:
        ok = hmac.compare_digest(
            _hash_password(pw, pr["auth"]["salt"]), pr["auth"]["hash"])
    except Exception:
        ok = False
    if not ok:
        return jsonify({"error": "يوزر أو كلمة سر خطأ"}), 401
    return jsonify({"uid": pr.get("uid"), "profile": public_profile(pr), "data": userdata_payload(pr)})


def username_taken(un, exclude_uid=""):
    un = norm_username(un)
    if not un:
        return False
    with PROFILES_LOCK:
        for k, p in PROFILES.items():
            if k != exclude_uid and norm_username(p.get("username")) == un:
                return True
    return False


def register_profile(data):
    data = data or {}
    uid = data.get("uid") or ""
    if not uid:
        return
    name = data.get("name") or ""
    avatar = data.get("avatar") or ""
    following = data.get("following") or []
    with PROFILES_LOCK:
        pr = PROFILES.get(uid) or {"uid": uid}
        if name:
            pr["name"] = name
        if avatar:
            pr["avatar"] = avatar
        pr["following"] = list(following)
        for k in ("bio", "banner", "colors", "likes"):
            v = data.get(k)
            if v:
                pr[k] = v
        un = (data.get("username") or "").strip().lstrip("@")
        if un and norm_username(pr.get("username")) != norm_username(un):
            if not username_taken(un, uid):
                pr["username"] = un
        empty = (not pr.get("name") and not pr.get("avatar") and not pr.get("bio")
                 and not pr.get("banner") and not pr.get("likes") and not pr.get("following"))
        if empty and not pr.get("username") and not pr.get("auth"):
            PROFILES.pop(uid, None)
        else:
            PROFILES[uid] = pr
    _save_profiles()


def all_profiles():
    with PROFILES_LOCK:
        out = []
        for uid, pr in PROFILES.items():
            out.append({
                "uid": uid,
                "name": pr.get("name") or "مستمع",
                "avatar": pr.get("avatar") or "",
                "banner": pr.get("banner") or "",
                "bio": pr.get("bio") or "",
                "colors": pr.get("colors") or {},
                "likes": pr.get("likes") or [],
                "following": list(pr.get("following") or []),
                "username": pr.get("username") or "",
            })
        return out


def current_room(sid):
    with ROOMS_LOCK:
        rid = SID_ROOM.get(sid)
        return ROOMS.get(rid)


def _leave_current(sid):
    with ROOMS_LOCK:
        rid = SID_ROOM.pop(sid, None)
        room = ROOMS.get(rid) if rid else None
    if not rid or room is None:
        return
    leave_room(rid)
    with room.lock:
        if room.owner_sid == sid:
            room.owner_sid = None
        room.members.pop(sid, None)
    room.broadcast()
    with ROOMS_LOCK:
        if not room.members:
            ROOMS.pop(rid, None)


@app.route("/")
@app.route("/spotify_done")
def index():
    return send_from_directory(STATIC_DIR, "index.html")


@app.route("/mini")
def mini_page():
    return send_from_directory(STATIC_DIR, "mini.html")


def _room_of_uid(uid):
    if not uid:
        return None
    with ROOMS_LOCK:
        for room in ROOMS.values():
            with room.lock:
                for m in room.members.values():
                    if (m or {}).get("uid") == uid:
                        return room
    return None


SOLO_STATE = {}


@app.route("/api/remove_follower", methods=["POST"])
def api_remove_follower():
    d = request.get_json(silent=True) or {}
    owner = d.get("owner") or ""
    target = d.get("target") or ""
    if not owner or not target:
        return jsonify({"ok": False})
    with PROFILES_LOCK:
        pr = PROFILES.get(target)
        if pr and isinstance(pr.get("following"), list) and owner in pr["following"]:
            pr["following"].remove(owner)
            _save_profiles()
    return jsonify({"ok": True})


@app.route("/api/client_debug", methods=["POST"])
def api_client_debug():
    try:
        d = request.get_json(silent=True) or {}
        with open(os.path.join(BASE_DIR, "debug_mecard.log"), "a", encoding="utf-8") as f:
            f.write(time.strftime("[%Y-%m-%d %H:%M:%S] ") + json.dumps(d, ensure_ascii=False) + "\n")
    except Exception:
        pass
    return jsonify({"ok": True})


@app.route("/api/solo_state", methods=["POST"])
def api_solo_state():
    d = request.get_json(silent=True) or {}
    uid = d.get("uid") or ""
    if not uid:
        return jsonify({"ok": False})
    trk = d.get("track")
    if not trk:
        SOLO_STATE.pop(uid, None)
        return jsonify({"ok": True})
    try:
        dur = float(trk.get("duration") or 0)
    except Exception:
        dur = 0
    SOLO_STATE[uid] = {
        "current": {
            "title": trk.get("title") or "",
            "artist": trk.get("artist") or "",
            "thumbnail": trk.get("thumbnail") or "",
            "duration": dur,
        },
        "playing": bool(d.get("playing")),
        "position": float(d.get("position") or 0),
        "updated_at": time.time(),
    }
    return jsonify({"ok": True})


@app.route("/api/state_snapshot")
def api_state_snapshot():
    uid = request.args.get("uid", "")
    room = _room_of_uid(uid)
    if not room:
        snap = SOLO_STATE.get(uid)
        if snap:
            out = {"rid": ""}
            out.update(snap)
            return jsonify(out)
        return jsonify({"rid": ""})
    snap = room.state()
    snap["queue"] = []
    snap["members"] = []
    return jsonify(snap)


SOLO_CMDS = {}


@app.route("/api/solo_cmd")
def api_solo_cmd():
    uid = request.args.get("uid", "")
    c = SOLO_CMDS.pop(uid, None)
    if not c:
        return jsonify({"cmd": None})
    return jsonify({"cmd": c[0], "pos": c[1]})


@app.route("/api/cmd", methods=["POST"])
def api_cmd():
    d = request.get_json(silent=True) or {}
    room = _room_of_uid(d.get("uid") or "")
    if not room:
        uid = d.get("uid") or ""
        if uid and SOLO_STATE.get(uid):
            act = d.get("action")
            if act == "seek":
                try:
                    SOLO_CMDS[uid] = ("seek", float(d.get("pos") or 0))
                except Exception:
                    SOLO_CMDS[uid] = ("seek", 0)
            elif act in ("play_pause", "skip", "prev"):
                SOLO_CMDS[uid] = (act, None)
            return jsonify({"ok": True, "solo": True})
        return jsonify({"ok": False, "error": "not_in_room"})
    act = d.get("action")
    if act == "play_pause":
        room.play_pause()
    elif act == "skip":
        room.skip()
    elif act == "seek":
        try:
            room.seek(float(d.get("pos") or 0))
        except Exception:
            pass
    else:
        return jsonify({"ok": False, "error": "bad_action"})
    return jsonify({"ok": True})


@app.route("/api/mini_uid", methods=["POST"])
def api_mini_uid():
    d = request.get_json(silent=True) or {}
    try:
        from mini_window import set_uid

        set_uid(d.get("uid") or "")
    except Exception:
        pass
    return jsonify({"ok": True})


@app.route("/api/mini_status")
def api_mini_status():
    try:
        from mini_window import status

        return jsonify(status())
    except Exception:
        return jsonify({"window": False, "visible": False, "hotkey": False, "uid": False})


@app.route("/api/open_mini", methods=["POST"])
def api_open_mini():
    d = request.get_json(silent=True) or {}
    uid = d.get("uid") or ""
    threading.Thread(target=_open_mini_async, args=(PORT, uid), daemon=True).start()
    return jsonify({"ok": True})


def _open_mini_async(port, uid):
    try:
        from mini_window import open_mini

        open_mini(port, uid)
    except Exception as e:
        print("mini window error:", e)


@app.route("/api/sys_volume", methods=["GET", "POST"])
def api_sys_volume():
    try:
        from mini_window import get_master_volume, set_master_volume
    except Exception:
        return jsonify({"ok": False})
    if request.method == "GET":
        v = get_master_volume()
        return jsonify({"ok": v is not None, "volume": v})
    d = request.get_json(silent=True) or {}
    ok = set_master_volume(d.get("volume") or 0)
    return jsonify({"ok": ok})


@app.route("/api/info")
def api_info():
    return jsonify({
        "ip": get_lan_ip(),
        "port": PORT,
        "hostname": socket.gethostname(),
        "tunnel": TUNNEL_URL,
    })


def resolve_stream_url(web_url, source="youtube", title="", artist=""):
    track = {"web_url": web_url, "source": source, "title": title, "artist": artist}
    stream_url = None
    try:
        stream_url, _ = pick_stream_url(_extract_base(web_url, fast=True))
    except Exception:
        stream_url = None
    if not stream_url:
        try:
            # Live/manifest-only media still uses the full compatible extractor.
            stream_url, _ = pick_stream_url(_extract_base(web_url))
        except Exception:
            pass
    if not stream_url:
        try:
            stream_url = Room._sc_stream(None, track)
        except Exception:
            stream_url = None
    if not stream_url and web_url:
        try:
            stream_url, _ = pick_stream_url(_extract_base(web_url, ["android"]))
        except Exception:
            pass
    return stream_url


@app.route("/api/audio_proxy")
def api_audio_proxy():
    url = request.args.get("url", "")
    if not url.startswith("http"):
        abort(400)
    fwd = {"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)", "Referer": "https://www.youtube.com/"}
    rng = request.headers.get("Range")
    if rng:
        fwd["Range"] = rng
    session = _requests.Session()
    adapter = _IPv4Adapter()
    session.mount("https://", adapter)
    session.mount("http://", adapter)
    try:
        up = session.get(url, headers=fwd, stream=True, timeout=(10, 30))
    except Exception:
        session.close()
        return jsonify({"error": "upstream_failed"}), 502
    def chunks():
        try:
            yield from up.iter_content(chunk_size=16384)
        finally:
            up.close()
            session.close()
    resp = Response(stream_with_context(chunks()),
                    status=up.status_code)
    resp.call_on_close(up.close)
    resp.call_on_close(session.close)
    for h in ("Content-Range", "Content-Length", "Content-Type"):
        if h in up.headers:
            resp.headers[h] = up.headers[h]
    resp.headers["Accept-Ranges"] = "bytes"
    return resp


class _IPv4Adapter(_requests.adapters.HTTPAdapter):
    def init_poolmanager(self, connections, maxsize, block=False, **kwargs):
        kwargs["source_address"] = ("0.0.0.0", 0)
        return super().init_poolmanager(connections, maxsize, block, **kwargs)


_STREAM_CACHE = {}
_STREAM_CACHE_LOCK = threading.Lock()
_STREAM_INFLIGHT = {}
_STREAM_TTL = 20 * 60

_AUDIO_CACHE_DIR = os.path.join(BASE_DIR, "audio_cache")
os.makedirs(_AUDIO_CACHE_DIR, exist_ok=True)
_AUDIO_FILES = {}
_AUDIO_DL_LOCK = threading.Lock()
_AUDIO_DOWNLOAD_SLOTS = threading.BoundedSemaphore(2)


def _audio_key(web_url):
    return hashlib.sha1((web_url or "").encode("utf-8")).hexdigest()


def _audio_entry(key):
    with _AUDIO_DL_LOCK:
        return _AUDIO_FILES.get(key)


def _download_audio(key, web_url):
    final = os.path.join(_AUDIO_CACHE_DIR, key + ".m4a")
    template = os.path.join(_AUDIO_CACHE_DIR, key + ".%(ext)s")
    ydl_opts = {**YD_BASE,
        "format": "bestaudio[ext=m4a]/bestaudio/best",
        "outtmpl": template,
        "quiet": True,
        "no_warnings": True,
        "noplaylist": True,
        "socket_timeout": 30,
        "skip_download": False,
    }
    try:
        with yt_dlp.YoutubeDL(ydl_opts) as ydl:
            info = ydl.extract_info(web_url, download=True)
            final = ydl.prepare_filename(info)
        if not os.path.isfile(final):
            raise RuntimeError("download produced no file")
        with _AUDIO_DL_LOCK:
            _AUDIO_FILES[key] = {"path": final, "ready": True, "ts": time.time()}
    except Exception:
        with _AUDIO_DL_LOCK:
            _AUDIO_FILES.pop(key, None)
        try:
            part = os.path.join(_AUDIO_CACHE_DIR, key + ".m4a.part")
            if os.path.exists(part):
                os.remove(part)
        except OSError:
            pass
    finally:
        _AUDIO_DOWNLOAD_SLOTS.release()


@app.route("/api/audio_file")
def api_audio_file():
    key = request.args.get("k", "")
    ent = _audio_entry(key)
    if not ent or not ent.get("ready"):
        abort(404)
    p = ent["path"]
    if not os.path.exists(p):
        abort(404)
    resp = send_file(p, conditional=True)
    resp.headers["Accept-Ranges"] = "bytes"
    return resp


def cached_stream(web_url, source="youtube", title="", artist="", refresh=False):
    with _STREAM_CACHE_LOCK:
        hit = _STREAM_CACHE.get(web_url)
        if hit and time.time() - hit[1] < _STREAM_TTL and not refresh:
            return hit[0]
        pending = _STREAM_INFLIGHT.get(web_url)
        leader = pending is None
        if leader:
            pending = concurrent.futures.Future()
            _STREAM_INFLIGHT[web_url] = pending
    if not leader:
        return pending.result(timeout=45)
    try:
        stream = resolve_stream_url(web_url, source, title, artist)
        if stream:
            with _STREAM_CACHE_LOCK:
                _STREAM_CACHE[web_url] = (stream, time.time())
                if len(_STREAM_CACHE) > 300:
                    for key, _ in sorted(_STREAM_CACHE.items(), key=lambda item: item[1][1])[:100]:
                        _STREAM_CACHE.pop(key, None)
        pending.set_result(stream)
        return stream
    except Exception as error:
        pending.set_exception(error)
        raise
    finally:
        with _STREAM_CACHE_LOCK:
            _STREAM_INFLIGHT.pop(web_url, None)


@app.route("/api/resolve_stream", methods=["POST"])
def api_resolve_stream():
    data = request.get_json(silent=True) or {}
    web_url = data.get("web_url") or ""
    if not web_url and data.get("title"):
        matches = search((str(data.get("artist") or "") + " " + str(data["title"])).strip(), "youtube", 1)
        if matches:
            web_url = matches[0].get("web_url") or ""
    if not web_url:
        return jsonify({"error": "no_web_url"}), 400
    now = time.time()
    key = _audio_key(web_url)
    ent = _audio_entry(key)
    if ent and ent.get("ready") and os.path.exists(ent["path"]):
        return jsonify({"stream_url": f"/api/audio_file?k={key}", "cached": True, "local": True})
    stream_url = cached_stream(web_url, data.get("source", "youtube"),
                               data.get("title", ""), data.get("artist", ""), bool(data.get("refresh")))
    if not stream_url:
        return jsonify({"error": "unavailable"}), 502
    start_download = False
    with _AUDIO_DL_LOCK:
        if data.get("cache_audio") and key not in _AUDIO_FILES and _AUDIO_DOWNLOAD_SLOTS.acquire(blocking=False):
            _AUDIO_FILES[key] = {"path": os.path.join(_AUDIO_CACHE_DIR, key + ".m4a"), "ready": False, "ts": time.time()}
            start_download = True
    if start_download:
        threading.Thread(target=_download_audio, args=(key, web_url), daemon=True).start()
    return jsonify({"stream_url": stream_url})


@app.route("/api/rooms")
def api_rooms():
    rooms = []
    with ROOMS_LOCK:
        for rid, room in ROOMS.items():
            with room.lock:
                if room.hidden:
                    continue
                rooms.append({
                    "id": rid,
                    "name": room.name,
                    "owner": room.owner_name or "",
                    "members": len(room.members),
                    "max_members": room.max_members,
                    "has_password": bool(room.password),
                    "can_add": room.can_add,
                })
    rooms.sort(key=lambda r: (r["name"].lower(), r["id"]))
    return jsonify({"rooms": rooms})


@app.route("/api/profiles")
def api_profiles():
    return jsonify({"profiles": all_profiles()})


@app.route("/api/check_username")
def api_check_username():
    un = norm_username(request.args.get("username") or "")
    uid = request.args.get("uid") or ""
    if not un:
        return jsonify({"available": False})
    return jsonify({"available": not username_taken(un, uid)})


_ARTIST_CACHE = {}


@app.route("/api/artist_songs", methods=["POST"])
def api_artist_songs():
    data = request.get_json(force=True, silent=True) or {}
    name = (data.get("artist") or "").strip()
    if not name:
        return jsonify({"tracks": [], "channel": None})
    key = name.lower()
    now = time.time()
    c = _ARTIST_CACHE.get(key)
    if c and now - c[0] < _SEARCH_CACHE_TTL:
        return jsonify(c[1])
    result = {"tracks": [], "channel": None}
    try:
        opts = dict(YD_BASE)
        opts["extract_flat"] = True
        opts["playlistend"] = 48
        opts["socket_timeout"] = 12
        with yt_dlp.YoutubeDL(opts) as ydl:
            sinfo = ydl.extract_info("ytsearch3:%s" % name, download=False, process=False)
        ch_url, ch_name = None, name
        for e in list(sinfo.get("entries") or []):
            if e.get("channel_url"):
                ch_url = e["channel_url"]
                ch_name = e.get("channel") or ch_name
                break
        if ch_url:
            result["channel"] = {"name": ch_name, "url": ch_url}
            try:
                meta_opts = dict(opts)
                meta_opts.pop("playlistend", None)
                with yt_dlp.YoutubeDL(meta_opts) as ydl:
                    minfo = ydl.extract_info(ch_url, download=False, process=False)
                if isinstance(minfo, dict):
                    av = ""
                    if isinstance(minfo.get("thumbnails"), list) and minfo["thumbnails"]:
                        av = (minfo["thumbnails"][-1] or {}).get("url") or ""
                    result["channel"]["avatar"] = av or ""
                    result["channel"]["subscribers"] = minfo.get("channel_follower_count") or 0
            except Exception:
                pass
            with yt_dlp.YoutubeDL(opts) as ydl:
                vinfo = ydl.extract_info(ch_url.rstrip("/") + "/videos", download=False)
            tracks, seen = [], set()
            for e in list(vinfo.get("entries") or []):
                t = normalize_entry(e)
                if t and t.get("web_url") and t["web_url"] not in seen:
                    seen.add(t["web_url"])
                    t["artist"] = ch_name
                    tracks.append(t)
            result["tracks"] = tracks
    except Exception as e:
        result["error"] = str(e)
    _ARTIST_CACHE[key] = (now, result)
    return jsonify(result)


def _spotify_credentials():
    cid = os.environ.get("SPOTIFY_CLIENT_ID", "").strip()
    sec = os.environ.get("SPOTIFY_CLIENT_SECRET", "").strip()
    if not cid or not sec:
        try:
            path = os.path.join(BASE_DIR, "spotify_config.json")
            if not os.path.isfile(path):
                path = os.path.join(_BUNDLE_DIR, "spotify_config.json")
            with open(path, encoding="utf-8") as config_file:
                cfg = json.load(config_file)
            cid = cid or str(cfg.get("client_id") or "").strip()
            sec = sec or str(cfg.get("client_secret") or "").strip()
        except Exception:
            pass
    return cid, sec


@app.route("/api/spotify_config", methods=["GET", "POST"])
def api_spotify_config():
    path = os.path.join(BASE_DIR, "spotify_config.json")
    if request.method == "GET":
        cid, sec = _spotify_credentials()
        src = "env" if os.environ.get("SPOTIFY_CLIENT_ID") else "file"
        return jsonify({"client_id": cid, "has_secret": bool(sec), "source": src})
    d = request.get_json(force=True, silent=True) or {}
    cid = str(d.get("client_id") or "").strip()
    sec = str(d.get("client_secret") or "").strip()
    if not cid:
        return jsonify({"error": "Client ID مطلوب"}), 400
    try:
        cfg = {}
        if os.path.exists(path):
            try:
                cfg = json.load(open(path, encoding="utf-8"))
            except Exception:
                cfg = {}
        cfg["client_id"] = cid
        if sec:
            cfg["client_secret"] = sec
        with open(path, "w", encoding="utf-8") as f:
            json.dump(cfg, f, ensure_ascii=False, indent=2)
    except Exception as ex:
        return jsonify({"error": str(ex)}), 500
    global _SPOTIFY_TOKEN, _SPOTIFY_TOKEN_EXP
    _SPOTIFY_TOKEN = None
    _SPOTIFY_TOKEN_EXP = 0
    return jsonify({"ok": True})


_SPOTIFY_TOKEN = None
_SPOTIFY_TOKEN_EXP = 0


def _spotify_token():
    global _SPOTIFY_TOKEN, _SPOTIFY_TOKEN_EXP
    cid, sec = _spotify_credentials()
    if not cid or not sec:
        return None
    now = time.time()
    if _SPOTIFY_TOKEN and now < _SPOTIFY_TOKEN_EXP - 30:
        return _SPOTIFY_TOKEN
    auth = base64.b64encode(("%s:%s" % (cid, sec)).encode()).decode()
    req = urllib.request.Request(
        "https://accounts.spotify.com/api/token",
        data=urllib.parse.urlencode({"grant_type": "client_credentials"}).encode(),
        headers={
            "Authorization": "Basic " + auth,
            "Content-Type": "application/x-www-form-urlencoded",
        },
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=15) as r:
        j = json.loads(r.read().decode())
    _SPOTIFY_TOKEN = j["access_token"]
    _SPOTIFY_TOKEN_EXP = now + int(j.get("expires_in", 3600))
    return _SPOTIFY_TOKEN


def spotify_search(query, limit=8):
    tok = _spotify_token()
    if not tok:
        return None
    url = "https://api.spotify.com/v1/search?q=" + urllib.parse.quote(query) + "&type=track&limit=%d" % limit
    req = urllib.request.Request(url, headers={"Authorization": "Bearer " + tok})
    with urllib.request.urlopen(req, timeout=15) as r:
        j = json.loads(r.read().decode())
    tracks = (j.get("tracks") or {}).get("items") or []
    out = []
    for t in tracks:
        name = t.get("name") or ""
        artists = ", ".join(a.get("name", "") for a in (t.get("artists") or []))
        img = ""
        images = (t.get("album") or {}).get("images") or []
        if images:
            img = images[0].get("url", "")
        row = {
            "title": name,
            "artist": artists,
            "duration": int((t.get("duration_ms") or 0) / 1000),
            "thumbnail": img,
            "web_url": (t.get("external_urls") or {}).get("spotify", ""),
            "source": "spotify",
        }
        if t.get("popularity") is not None:
            row["pop"] = t.get("popularity")
        ts = _release_ts((t.get("album") or {}).get("release_date"))
        if ts is not None:
            row["ts"] = ts
        out.append(row)
    return out


def _release_ts(value):
    m = re.match(r"^(\d{4})(?:-(\d{2}))?(?:-(\d{2}))?", str(value or ""))
    if not m:
        return None
    try:
        import calendar
        return calendar.timegm((int(m.group(1)), int(m.group(2) or 1), int(m.group(3) or 1), 0, 0, 0))
    except Exception:
        return None


def spotify_playlist_search(query, limit=8):
    tok = _spotify_token()
    if not tok:
        return None
    url = ("https://api.spotify.com/v1/search?q=" + urllib.parse.quote(query)
           + "&type=playlist&limit=%d" % limit)
    req = urllib.request.Request(url, headers={"Authorization": "Bearer " + tok})
    with urllib.request.urlopen(req, timeout=15) as r:
        j = json.loads(r.read().decode())
    out = []
    for p in (j.get("playlists") or {}).get("items") or []:
        if not p:
            continue
        link = (p.get("external_urls") or {}).get("spotify", "")
        if not link:
            continue
        images = p.get("images") or []
        out.append({
            "title": p.get("name") or "Playlist",
            "artist": (p.get("owner") or {}).get("display_name") or "",
            "duration": 0,
            "thumbnail": images[0].get("url", "") if images else "",
            "web_url": link,
            "source": "spotify",
            "kind": "playlist",
            "count": int(((p.get("tracks") or p.get("items") or {}) or {}).get("total") or 0),
        })
    return out


def _resolve_to_youtube(t):
    q = ((t.get("artist") or "") + " " + (t.get("title") or "")).strip()
    if not q:
        return None
    try:
        res = search(q, "youtube", 3)
    except Exception:
        return None
    if not res:
        return None
    r = dict(res[0])
    r.pop("views", None)
    r.pop("ts", None)
    for k in ("pop", "ts"):
        if t.get(k) is not None:
            r[k] = t[k]
    r["title"] = t.get("title") or r.get("title")
    r["artist"] = t.get("artist") or r.get("artist")
    r["source"] = "spotify"
    return r


_SEARCH_EXEC = concurrent.futures.ThreadPoolExecutor(max_workers=4)


def _music_text(value):
    text = str(value or "").lower()
    text = re.sub(r"[\u064b-\u065f\u0670\u0640]", "", text)
    return re.sub(r"[^\w\s]", " ", text).strip()


def music_search_query(query):
    """Disambiguate within the existing search, without a second network call."""
    normalized = _music_text(query)
    if normalized in {"كلاش", "klash"}:
        return query + " راب"
    if re.search(r"\b(music|song|songs|audio|lyrics|rap|راب|اغنية|أغنية|اغاني|أغاني|موسيقى|شيلة|شيلات)\b", normalized):
        return query
    return query + " music"


def rank_music_results(rows, query):
    """Stable, local music-first ranking. No metadata extraction or API requests."""
    wanted = _music_text(query)
    aliases = {wanted} if wanted else set()
    if wanted in {"كلاش", "klash"}:
        aliases.update({"كلاش", "klash"})
    music = re.compile(r"\b(music|song|songs|audio|lyrics|lyric|rap|remix|soundtrack|ost|اغنية|أغنية|اغاني|أغاني|موسيقى|راب|شيلة|شيلات|كليب)\b")
    unrelated = re.compile(r"\b(gameplay|gaming|clash of clans|clash royale|tutorial|walkthrough|review|reaction|interview|podcast|vlog|كلاش اوف كلانس|كلاش أوف كلانس|كلاش رويال|العاب|ألعاب|تحدي|شرح|تحديث|مقابلة|بودكاست)\b")
    def score(row):
        title = _music_text(row.get("title"))
        artist = _music_text(row.get("artist"))
        text = title + " " + artist
        musical = bool(music.search(text))
        channel = "topic" in artist.split() or "vevo" in artist or "تسجيلات" in artist
        points = (6 if musical else 0) + (5 if channel else 0)
        if any(artist == term or artist.startswith(term + " ") for term in aliases):
            points += 4
        if any(term in title for term in aliases):
            points += 3
        if "official" in title or "الرسمية" in title:
            points += 2
        if 60 <= (row.get("duration") or 0) <= 720:
            points += 1
        if unrelated.search(text) and not musical and not channel:
            points -= 12
        return -points
    return sorted(rows, key=score)


def sort_results(rows, sort):
    """ترتيب محلي سريع للنتائج حسب الفلتر (بدون طلبات شبكة)."""
    if sort == "popular":
        return sorted(rows, key=lambda r: (-(r.get("pop") or 0), -(r.get("views") or 0)))
    if sort == "oldest":
        return sorted(rows, key=lambda r: (r.get("ts") is None, r.get("ts") or 0))
    return rows


def _search_work(query, source, limit=None, sort=""):
    if is_url(query):
        if is_spotify(query):
            return resolve_spotify(query)
        info = _extract(query)
        entries = info.get("entries") if info and info.get("entries") else ([info] if info else [])
        return [normalize_entry(e) for e in entries if normalize_entry(e)]
    if source == "spotify" and sort == "playlists":
        sp = spotify_playlist_search(query, max(6, min(limit or 36, 30)))
        if sp is None:
            raise RuntimeError("بيانات سبوتيفاي غير مضبوطة. أضف SPOTIFY_CLIENT_ID و SPOTIFY_CLIENT_SECRET في spotify_config.json")
        return sp
    if source == "spotify":
        sp = spotify_search(query, max(6, min(limit or 36, 30)))
        if sp is None:
            raise RuntimeError("بيانات سبوتيفاي غير مضبوطة. أضف SPOTIFY_CLIENT_ID و SPOTIFY_CLIENT_SECRET في spotify_config.json")
        with concurrent.futures.ThreadPoolExecutor(max_workers=6) as ex:
            return [x for x in ex.map(_resolve_to_youtube, sp) if x]
    if sort in SEARCH_SORTS:
        if source == "soundcloud" and sort != "playlists":
            key_source = "soundcloud:sorted"   # نفس طلب البحث العادي، الترتيب محلي
        else:
            key_source = source + ":" + sort
        q = music_search_query(query) if (source == "youtube" and sort != "playlists") else query
        return search(q, key_source, limit or SEARCH_LIMIT)
    return search(music_search_query(query) if source == "youtube" else query, source, limit or SEARCH_LIMIT)


@app.route("/api/search", methods=["POST"])
def api_search():
    data = request.get_json(force=True, silent=True) or {}
    query = (data.get("query") or "").strip()
    source = data.get("source") or "youtube"
    if source not in ("youtube", "soundcloud", "spotify"):
        source = "youtube"
    sort = data.get("sort") or ""
    if sort not in SEARCH_SORTS or is_url(query):
        sort = ""
    if not query:
        return jsonify({"results": [], "error": "أدخل كلمات البحث"})
    try:
        try:
            lim = max(1, min(int(data.get("limit") or SEARCH_LIMIT), 72))
        except Exception:
            lim = SEARCH_LIMIT
        paged = "offset" in data
        offset = max(0, min(int(data.get("offset") or 0), 931)) if paged else 0
        requested = min(980, offset + lim + 1) if paged else lim
        fut = _SEARCH_EXEC.submit(_search_work, query, source, requested, sort) if sort \
            else _SEARCH_EXEC.submit(_search_work, query, source, requested)
        try:
            results = fut.result(timeout=45)
        except concurrent.futures.TimeoutError:
            fut.cancel()
            return jsonify({"results": [], "error": "يوتيوب بطيء حالياً، انتظر ثوانٍ وأعد المحاولة"})
        if paged:
            page = results[offset:offset + lim]
            if sort:
                page = sort_results(page, sort)
            elif not is_url(query):
                page = rank_music_results(page, query)
            return jsonify({"results": page,
                            "has_more": offset + lim < min(len(results), 980)})
        if sort:
            results = sort_results(results, sort)
        elif not is_url(query):
            results = rank_music_results(results, query)
        return jsonify({"results": results})
    except RuntimeError as e:
        return jsonify({"results": [], "error": str(e)})
    except Exception as e:
        return jsonify({"results": [], "error": str(e)})


@app.route("/api/resolve", methods=["POST"])
def api_resolve():
    data = request.get_json(force=True, silent=True) or {}
    track = data.get("track") or {}
    try:
        stream_url, _ = pick_stream_url(_extract(track.get("web_url")))
        return jsonify({"stream_url": stream_url})
    except Exception as e:
        return jsonify({"stream_url": None, "error": str(e)})


_SPOTIFY_OAUTH = {}

_TUNNEL_HOST = "music.harmoni.click"


def _sp_redirect_uri(host=None):
    """رابط العودة بعد تفويض سبوتيفاي — يتولد حسب طريقة دخول المستخدم نفسه."""
    if not host:
        try:
            host = request.host
        except RuntimeError:
            host = None
    hl = (host or "").split("/")[0].lower()
    if not hl or hl.startswith("127.0.0.1") or hl.startswith("localhost"):
        # صاحب الجهاز: يعود محلياً حتى بدون إنترنت
        return "http://127.0.0.1:%d/spotify/callback" % PORT
    # أي مستخدم آخر (شبكة منزلية أو نفق): يعود عبر الدومين الدائم
    return "https://%s/spotify/callback" % _TUNNEL_HOST


@app.route("/api/spotify_redirect_uri")
def api_spotify_redirect_uri():
    return jsonify({"redirect_uri": _sp_redirect_uri()})


class SpotifyAccessError(RuntimeError):
    pass


def _sp_access_message(status, body):
    if status == 403 and "premium" in body.lower():
        return "سبوتيفاي رفض الربط لأن حساب مالك تطبيق الربط في Spotify Developer Dashboard يحتاج اشتراك Premium فعّال. فعّل اشتراك المالك أو استخدم بيانات تطبيق تملكه بحساب Premium، ثم أعد المحاولة. قد يستغرق تحديث الاشتراك لدى سبوتيفاي بضع ساعات."
    if status == 403:
        return "سبوتيفاي رفض الوصول إلى المكتبة. تأكد من إضافة حسابك ضمن المستخدمين المسموح لهم في إعدادات تطبيق سبوتيفاي ومن صلاحيات الربط."
    if status == 429:
        return "سبوتيفاي أوقف الطلبات مؤقتًا بسبب حد الاستخدام. انتظر قبل إعادة المحاولة."
    if status in (400, 401):
        return "تعذر تفويض سبوتيفاي. تحقق من بيانات تطبيق الربط ورابط العودة ثم أعد ربط الحساب."
    return "تعذر الاتصال بسبوتيفاي (HTTP %s). حاول لاحقًا." % status


def _sp_api_get(tok, path, params=None):
    url = "https://api.spotify.com/v1" + path
    if params:
        url += "?" + urllib.parse.urlencode(params)
    with _requests.Session() as session:
        session.mount("https://", _IPv4Adapter())
        response = session.get(url, headers={"Authorization": "Bearer " + tok}, timeout=(5, 15))
        if not response.ok:
            raise SpotifyAccessError(_sp_access_message(response.status_code, response.text))
        return response.json()


def _sp_items(tok, path, limit=50):
    offset = 0
    while True:
        page = _sp_api_get(tok, path, {"limit": limit, "offset": offset})
        items = page.get("items") or []
        yield from items
        if not page.get("next") or not items:
            break
        offset += len(items)


def _sp_track_shape(t):
    if not t:
        return None
    artists = ", ".join(a.get("name", "") for a in (t.get("artists") or []))
    images = (t.get("album") or {}).get("images") or []
    img = images[-1].get("url", "") if images else ""
    return {
        "title": t.get("name") or "",
        "artist": artists,
        "duration": int((t.get("duration_ms") or 0) / 1000),
        "thumbnail": img,
        "source": "spotify",
    }


@app.route("/api/spotify/auth")
def api_spotify_auth():
    cid, sec = _spotify_credentials()
    if not cid or not sec:
        return jsonify({"error": "بيانات سبوتيفاي غير مضبوطة في spotify_config.json"}), 400
    uid = (request.args.get("uid") or "").strip()[:64]
    origin = (request.args.get("origin") or "").strip()
    if not re.match(r"^https?://[^/\s]+", origin):
        origin = request.host_url.rstrip("/")
    state = uuid.uuid4().hex
    verifier = base64.urlsafe_b64encode(os.urandom(48)).decode().rstrip("=")
    challenge = base64.urlsafe_b64encode(
        hashlib.sha256(verifier.encode()).digest()).decode().rstrip("=")
    _SPOTIFY_OAUTH[state] = {
        "verifier": verifier, "uid": uid, "origin": origin, "exp": time.time() + 600,
    }
    redirect_uri = _sp_redirect_uri()
    params = {
        "response_type": "code",
        "client_id": cid,
        "scope": "user-library-read playlist-read-private playlist-read-collaborative",
        "redirect_uri": redirect_uri,
        "state": state,
        "code_challenge_method": "S256",
        "code_challenge": challenge,
        "show_dialog": "true",
    }
    return redirect("https://accounts.spotify.com/authorize?" + urllib.parse.urlencode(params))


_SP_ID_RE = re.compile(r"(playlist|album|track)[/:]([A-Za-z0-9]{22})")


def _spotify_resolve_id(url):
    url = (url or "").strip()
    m = _SP_ID_RE.search(url)
    if m:
        return m.group(1), m.group(2)
    if "spotify.link" in url or "spoti.fi" in url:
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
            with urllib.request.urlopen(req, timeout=15) as r:
                final = r.geturl()
            m = _SP_ID_RE.search(final)
            if m:
                return m.group(1), m.group(2)
        except Exception:
            pass
    return None, None


def _sp_embed_find_key(obj, key, depth=0):
    if depth > 8:
        return
    if isinstance(obj, dict):
        for k, v in obj.items():
            if k == key:
                yield v
            yield from _sp_embed_find_key(v, key, depth + 1)
    elif isinstance(obj, list):
        for v in obj[:12]:
            yield from _sp_embed_find_key(v, key, depth + 1)


def _sp_embed_find_entity(obj, depth=0):
    if depth > 8:
        return None
    if isinstance(obj, dict):
        if isinstance(obj.get("name"), str) and obj.get("name") and "trackList" in obj:
            return obj
        for v in obj.values():
            r = _sp_embed_find_entity(v, depth + 1)
            if r:
                return r
    elif isinstance(obj, list):
        for v in obj[:12]:
            r = _sp_embed_find_entity(v, depth + 1)
            if r:
                return r
    return None


def _sp_embed_import(typ, sid):
    url = "https://open.spotify.com/embed/" + typ + "/" + sid
    req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
    page = urllib.request.urlopen(req, timeout=20).read().decode("utf-8", "replace")
    m = re.search(r'<script[^>]*id="__NEXT_DATA__"[^>]*>(.*?)</script>', page, re.S)
    if not m:
        raise RuntimeError("no embedded data")
    d = json.loads(html.unescape(m.group(1)))
    entity = _sp_embed_find_entity(d) or {}
    name = entity.get("name") or ""
    img = ""
    im = re.search(r'<meta property="og:image" content="([^"]*)"', page)
    if im:
        img = html.unescape(im.group(1))
    tracks = []
    for tl in _sp_embed_find_key(d, "trackList"):
        for t in tl or []:
            title = (t.get("title") or "").strip()
            if not title:
                continue
            artists = re.sub(r"\s+", " ", (t.get("subtitle") or "").replace("\u00a0", " ")).strip()
            tracks.append({
                "title": title,
                "artist": artists,
                "duration": int((t.get("duration") or 0) / 1000),
                "thumbnail": "",
                "source": "spotify",
            })
        break
    return name, img, tracks


@app.route("/api/spotify_import", methods=["POST"])
def api_spotify_import():
    data = request.get_json(force=True, silent=True) or {}
    url = (data.get("url") or "").strip()
    tok = _spotify_token()
    typ, sid = _spotify_resolve_id(url)
    if typ not in ("playlist", "album"):
        return jsonify({"error": "الرابط لازم يكون رابط بلاي لست أو ألبوم من سبوتيفاي"}), 400

    if tok:
        path = "/playlists/" + sid if typ == "playlist" else "/albums/" + sid
        try:
            meta = _sp_api_get(tok, path)
            name = meta.get("name") or "Spotify"
            imgs = meta.get("images") or []
            img = imgs[-1].get("url", "") if imgs else ""
            tracks = []
            offset = 0
            while offset < 500:
                j = _sp_api_get(tok, path + "/tracks", {"limit": 100, "offset": offset})
                items = j.get("items") or []
                for it in items:
                    shape = _sp_track_shape((it or {}).get("track"))
                    if not shape or not shape["title"]:
                        continue
                    if not shape["thumbnail"] and img:
                        shape["thumbnail"] = img
                    tracks.append(shape)
                if len(items) < 100 or not j.get("next"):
                    break
                offset += 100
            if tracks:
                return jsonify({"name": name, "img": img, "tracks": tracks})
        except Exception:
            pass

    try:
        name, img, tracks = _sp_embed_import(typ, sid)
        if tracks:
            return jsonify({"name": name or "Spotify", "img": img, "tracks": tracks,
                            "note": "تم استيراد أول %d أغنية فقط (وضع المعاينة). لاستيراد كامل اربط حسابك." % len(tracks)})
        return jsonify({"error": "تعذر الوصول لهذا البلاي لست (تأكد أنه عام Public)"}), 404
    except Exception:
        return jsonify({"error": "تعذر الوصول لهذا البلاي لست (تأكد أنه عام Public)"}), 404



_PL_CACHE = {}
_PL_CACHE_TTL = 900
_PL_MAX = 200


def _sc_track_row(t):
    if not t or not t.get("title"):
        return None
    return {
        "title": t.get("title") or "",
        "artist": (t.get("user") or {}).get("username") or "",
        "duration": int((t.get("duration") or 0) / 1000),
        "thumbnail": (t.get("artwork_url") or "").replace("-large.", "-t300x300."),
        "web_url": t.get("permalink_url") or "",
        "source": "soundcloud",
    }


def _playlist_tracks(url):
    host = (urllib.parse.urlparse(url).hostname or "").lower()
    if "soundcloud.com" in host:
        with yt_dlp.YoutubeDL(dict(YD_BASE)) as ydl:
            ie = ydl.get_info_extractor("Soundcloud")
            ie.initialize()
            data = ie._call_api(ie._API_V2_BASE + "resolve", "playlist", "Resolving playlist",
                                query={"url": url}, headers=ie._HEADERS) or {}
            raw = (data.get("tracks") or [])[:_PL_MAX]
            missing = [str(t.get("id")) for t in raw if t and not t.get("title") and t.get("id")]
            full = {}
            for i in range(0, len(missing), 50):
                chunk = ie._call_api(ie._API_V2_BASE + "tracks", "playlist", "Downloading tracks",
                                     query={"ids": ",".join(missing[i:i + 50])}, headers=ie._HEADERS) or []
                for t in chunk:
                    full[str(t.get("id"))] = t
        tracks = [r for r in (_sc_track_row(full.get(str(t.get("id")), t)) for t in raw) if r and r["web_url"]]
        img = (data.get("artwork_url") or "").replace("-large.", "-t300x300.")
        return {"name": data.get("title") or "SoundCloud", "img": img or (tracks[0]["thumbnail"] if tracks else ""), "tracks": tracks}
    opts = dict(YD_BASE)
    opts["extract_flat"] = "in_playlist"
    opts["noplaylist"] = False
    with yt_dlp.YoutubeDL(opts) as ydl:
        info = ydl.extract_info(url, download=False, process=False) or {}
        entries = list(itertools.islice(info.get("entries") or [], _PL_MAX))
    tracks = [r for r in (normalize_entry(e) for e in entries)
              if r and r.get("kind") != "playlist" and r.get("title") not in ("[Private video]", "[Deleted video]")]
    img = ""
    thumbs = info.get("thumbnails") or []
    if thumbs:
        img = (thumbs[-1] or {}).get("url") or ""
    return {"name": info.get("title") or "YouTube", "img": img or (tracks[0]["thumbnail"] if tracks else ""), "tracks": tracks}


@app.route("/api/playlist_tracks", methods=["POST"])
def api_playlist_tracks():
    data = request.get_json(force=True, silent=True) or {}
    url = (data.get("url") or "").strip()
    if not is_url(url):
        return jsonify({"error": "رابط غير صالح"}), 400
    if is_spotify(url):
        return api_spotify_import()
    c = _PL_CACHE.get(url)
    if c and time.time() - c[0] < _PL_CACHE_TTL:
        return jsonify(c[1])
    try:
        fut = _SEARCH_EXEC.submit(_playlist_tracks, url)
        payload = fut.result(timeout=45)
    except Exception:
        return jsonify({"error": "تعذر فتح البلاي لست"}), 502
    if not payload.get("tracks"):
        return jsonify({"error": "البلاي لست فاضية أو خاصة"}), 404
    _PL_CACHE[url] = (time.time(), payload)
    return jsonify(payload)


_SPOTIFY_TICKETS = {}


@app.route("/spotify/callback")
def spotify_callback():
    code = request.args.get("code") or ""
    state = request.args.get("state") or ""
    err = request.args.get("error") or ""
    st = _SPOTIFY_OAUTH.pop(state, None)
    if err:
        return _sp_result_page(ok=False, msg="رفضت سبوتيفاي الطلب: " + err)
    if not st or st["exp"] < time.time():
        return _sp_result_page(ok=False, msg="انتهت صلاحية العملية، حاول مرة أخرى")
    ticket = uuid.uuid4().hex
    _SPOTIFY_TICKETS[ticket] = {
        "uid": st.get("uid") or "",
        "status": "pending",
        "data": None,
        "error": "",
        "exp": time.time() + 600,
    }
    threading.Thread(target=_sp_import_work, args=(ticket, code, st), daemon=True).start()
    target = st.get("origin") or request.host_url.rstrip("/")
    return redirect(target + "/spotify_done?t=" + ticket)


def _sp_fail(ticket, msg):
    ent = _SPOTIFY_TICKETS.get(ticket)
    if ent is not None:
        ent["status"] = "error"
        ent["error"] = msg


_SP_LINK_LOCK = threading.RLock()
_SP_SYNC_BUSY = set()
_SP_SYNC_LAST = {}


def _sp_links():
    try:
        with open(os.path.join(BASE_DIR, "spotify_links.json"), encoding="utf-8") as f:
            return json.load(f)
    except FileNotFoundError:
        return {}


def _sp_write_links(links):
    path = os.path.join(BASE_DIR, "spotify_links.json")
    temp = path + ".tmp"
    with open(temp, "w", encoding="utf-8") as f:
        json.dump(links, f)
    os.replace(temp, path)


def _sp_link_id(key):
    return hashlib.sha256(key.encode()).hexdigest()


@app.route("/api/spotify_sync", methods=["POST"])
def api_spotify_sync():
    key = str((request.get_json(silent=True) or {}).get("sync_key") or "")
    with _SP_LINK_LOCK:
        if not key or _sp_link_id(key) not in _sp_links():
            return jsonify({"error": "أعد ربط حساب سبوتيفاي لتفعيل التحديث التلقائي"}), 401
        if key in _SP_SYNC_BUSY or time.time() - _SP_SYNC_LAST.get(key, 0) < 295:
            return jsonify({"status": "busy"}), 429
        _SP_SYNC_BUSY.add(key)
        _SP_SYNC_LAST[key] = time.time()
    ticket = uuid.uuid4().hex
    _SPOTIFY_TICKETS[ticket] = {"status": "pending", "exp": time.time() + 600}
    threading.Thread(target=_sp_import_work, args=(ticket, "", {"sync_key": key}), daemon=True).start()
    return jsonify({"ticket": ticket})


@app.route("/api/spotify_unlink", methods=["POST"])
def api_spotify_unlink():
    key = str((request.get_json(silent=True) or {}).get("sync_key") or "")
    with _SP_LINK_LOCK:
        links = _sp_links()
        links.pop(_sp_link_id(key), None)
        _sp_write_links(links)
    return jsonify({"ok": True})


def _sp_import_work(ticket, code, st):
    try:
        cid, sec = _spotify_credentials()
        if not cid or not sec:
            return _sp_fail(ticket, "بيانات سبوتيفاي غير مضبوطة")
        sync_key = st.get("sync_key")
        if sync_key:
            with _SP_LINK_LOCK:
                link = _sp_links().get(_sp_link_id(sync_key))
            if not link:
                return _sp_fail(ticket, "أعد ربط حساب سبوتيفاي")
            token_body = {"grant_type": "refresh_token", "refresh_token": link["refresh_token"], "client_id": cid, "client_secret": sec}
        else:
            token_body = {"grant_type": "authorization_code", "code": code, "redirect_uri": _sp_redirect_uri(), "client_id": cid, "client_secret": sec, "code_verifier": st["verifier"]}
        req = urllib.request.Request(
            "https://accounts.spotify.com/api/token", data=urllib.parse.urlencode(token_body).encode(),
            headers={"Content-Type": "application/x-www-form-urlencoded"}, method="POST")
        with urllib.request.urlopen(req, timeout=15) as r:
            tokens = json.loads(r.read().decode())
        tok = tokens["access_token"]
        refresh = tokens.get("refresh_token") or (link["refresh_token"] if sync_key else None)
        if refresh:
            key = sync_key or (uuid.uuid4().hex + uuid.uuid4().hex)
            with _SP_LINK_LOCK:
                links = _sp_links()
                if sync_key and _sp_link_id(key) not in links:
                    return _sp_fail(ticket, "تم إلغاء الربط")
                links[_sp_link_id(key)] = {"refresh_token": refresh}
                _sp_write_links(links)
            sync_key = key
        me = _sp_api_get(tok, "/me")
        display = me.get("display_name") or ""
        errs = []
        likes = []
        try:
            for item in _sp_items(tok, "/me/tracks"):
                shape = _sp_track_shape(item.get("track"))
                if shape:
                    likes.append(shape)
        except urllib.error.HTTPError as e:
            errs.append("المفضلة (HTTP %s)" % e.code)
        except Exception:
            errs.append("المفضلة")
        playlists = []
        try:
            for pl in _sp_items(tok, "/me/playlists"):
                if not pl or pl.get("owner", {}).get("id") != me.get("id"):
                    continue
                name = pl.get("name") or ""
                pimgs = (pl.get("images") or [])
                pimg = pimgs[0].get("url", "") if pimgs else ""
                trs, seen = [], set()
                ok_pl = True
                try:
                    for it in _sp_items(tok, "/playlists/%s/tracks" % pl.get("id"), 100):
                        shape = _sp_track_shape((it or {}).get("track"))
                        if not shape or not shape["title"]:
                            continue
                        key = (shape["title"] + "|" + shape["artist"]).lower()
                        if key not in seen:
                            seen.add(key)
                            trs.append(shape)
                except Exception:
                    ok_pl = False
                    errs.append(name)
                if ok_pl:
                    playlists.append({"spotify_id": pl.get("id"), "name": name, "img": pimg, "tracks": trs})
        except Exception as e:
            errs.append("البلاي لستات (%s)" % str(e)[:40])
        if not likes and not playlists and errs:
            return _sp_fail(
                ticket,
                "تعذر قراءة مكتبتك من سبوتيفاي. تحقق من الاتصال وصلاحيات الحساب ثم حاول مرة أخرى.")
        payload = {"user": display, "likes": likes, "playlists": playlists, "sync_key": sync_key}
        if errs:
            payload["note"] = "تعذر استيراد: " + " و ".join(errs)
        ent = _SPOTIFY_TICKETS.get(ticket)
        if ent is not None:
            ent["status"] = "done"
            ent["data"] = payload
            ent["exp"] = time.time() + 300
    except SpotifyAccessError as e:
        _sp_fail(ticket, str(e))
    except urllib.error.HTTPError as e:
        _sp_fail(ticket, _sp_access_message(e.code, e.read().decode("utf-8", "replace")))
    except Exception as e:
        _sp_fail(ticket, "تعذر تحديث سبوتيفاي؛ تحقق من الاتصال أو أعد ربط الحساب")
    finally:
        with _SP_LINK_LOCK:
            _SP_SYNC_BUSY.discard(st.get("sync_key"))


@app.route("/api/spotify_ticket")
def api_spotify_ticket():
    t = (request.args.get("t") or "").strip()
    ent = _SPOTIFY_TICKETS.get(t)
    if not ent or ent["exp"] < time.time():
        _SPOTIFY_TICKETS.pop(t, None)
        return jsonify({"error": "منتهي أو غير صالح"}), 404
    if ent.get("status") == "pending":
        return jsonify({"status": "pending"})
    _SPOTIFY_TICKETS.pop(t, None)
    if ent.get("status") == "error":
        return jsonify({"error": ent.get("error") or "فشل الاستيراد"})
    d = dict(ent.get("data") or {})
    d["status"] = "done"
    return jsonify(d)


_SP_RESULT_TMPL = """<!DOCTYPE html><html lang="ar" dir="rtl"><head><meta charset="utf-8">
<title>ربط سبوتيفاي</title><style>body{font-family:"Segoe UI",Tahoma,sans-serif;background:#121212;color:#f5f5f5;display:flex;align-items:center;justify-content:center;height:100vh;margin:0}div{max-width:420px;text-align:center;line-height:1.7}a{color:#1db954}</style></head>
<body><div id="m">جارٍ استيراد بياناتك…</div><script id="d" type="application/json">__DATA__</script>
<script>
var ok = __OK__, msg = "__MSG__";
try {
if (!ok) { throw msg; }
var D = JSON.parse(document.getElementById('d').textContent);
function key(t){ return ((t.title||'')+'|'+(t.artist||'')).toLowerCase(); }
function nid(){ return 'sp' + Math.random().toString(36).slice(2,10); }
function rd(k){ try { return JSON.parse(localStorage.getItem(k)||'[]'); } catch(e){ return []; } }
var likes = rd('mu_likes'), have = {};
likes.forEach(function(t){ have[key(t)] = 1; });
var nl = 0;
(D.likes||[]).forEach(function(t){ var k=key(t); if(!have[k]){ t.id=nid(); likes.push(t); have[k]=1; nl++; } });
localStorage.setItem('mu_likes', JSON.stringify(likes));
var pls = rd('mu_playlists'), names = {};
pls.forEach(function(p){ names[(p.name||'').toLowerCase()] = 1; });
var np = 0;
(D.playlists||[]).forEach(function(p){
  var nk=(p.name||'').toLowerCase(); if(names[nk]) return;
  var seen={}, trs=[];
  (p.tracks||[]).forEach(function(t){ var k=key(t); if(seen[k])return; seen[k]=1; t.id=nid(); trs.push(t); });
  pls.push({id:'pl'+Math.random().toString(36).slice(2,10), name:p.name, img:p.img||'', tracks:trs, created:Date.now()});
  names[nk]=1; np++;
});
localStorage.setItem('mu_playlists', JSON.stringify(pls));
localStorage.setItem('mu_sp_import', JSON.stringify({likes:nl, playlists:np}));
document.getElementById('m').innerHTML = 'تم ربط الحساب ✓<br>استوردنا ' + nl + ' أغنية مفضلة و ' + np + ' بلاي لست<br><a href="/">العودة للموقع</a>';
setTimeout(function(){ location.replace('/'); }, 1200);
} catch(e) {
document.getElementById('m').innerHTML = (typeof e === 'string' ? e : 'حدث خطأ غير متوقع') + '<br><a href="/">العودة للموقع</a>';
}
</script></body></html>"""


def _sp_result_page(ok=True, msg="", data=None):
    raw = json.dumps(data or {}, ensure_ascii=False).replace("</", "<\\/")
    page = (_SP_RESULT_TMPL
            .replace("__DATA__", raw)
            .replace("__OK__", "true" if ok else "false")
            .replace("__MSG__", html.escape(msg)))
    return page


_RAW_CACHE = {}
_RAW_CACHE_TTL = 900
SUGGEST_DISCOVERY = ("songs like {}", "songs similar to {}", "{} radio")


def _search_cached(q):
    now = time.time()
    c = _RAW_CACHE.get(q)
    if c and now - c[0] < _RAW_CACHE_TTL:
        return c[1]
    res = search(q, "youtube", 8)
    _RAW_CACHE[q] = (now, res)
    return res


def _norm(s):
    s = (s or "").lower().strip()
    s = s.lstrip("@")
    s = re.sub(r"[^\w\s\u0600-\u06ff]", "", s)
    return re.sub(r"\s+", " ", s).strip()


def _belongs_to(artist, r):
    n = _norm(artist)
    nc = re.sub(r"\s+", "", n)
    if not nc:
        return False
    for field in ("artist", "uploader", "channel"):
        v = _norm(r.get(field))
        vc = re.sub(r"\s+", "", v)
        if vc and (vc == nc or nc in vc or vc in nc):
            return True
    raw = r.get("title") or ""
    m = re.match(r"^\s*@?([^\-—:|]+?)\s*[\-—:|]+\s*", raw)
    if m:
        g = re.sub(r"\s+", "", _norm(m.group(1)))
        if g and (g == nc or g in nc or nc in g):
            return True
    name_n = _norm(raw)
    if name_n.startswith(n + " ") and len(name_n) > len(n) + 3:
        return True
    return False


@app.route("/api/suggest", methods=["POST"])
def api_suggest():
    data = request.get_json(force=True, silent=True) or {}
    artists = data.get("artists") or []
    if isinstance(artists, list):
        artists = [str(a).strip() for a in artists if str(a).strip()]
    else:
        artists = []
    title = (data.get("title") or "").strip()
    artist = (data.get("artist") or "").strip()
    current = (data.get("current") or artist or "").strip()
    played = data.get("played") or []
    if isinstance(played, list):
        played = [str(p).lower().strip() for p in played if str(p).strip()]
    else:
        played = []
    if not current and title and artists:
        current = artists[0]
    recent = data.get("recent") or []
    recent = [r for r in recent[:12] if isinstance(r, dict)] if isinstance(recent, list) else []
    weights = {}
    names = {}
    for index, track in enumerate(recent):
        name = str(track.get("artist") or "").strip()
        if name:
            key = _norm(name)
            weights[key] = weights.get(key, 0) + (12 - index)
            names[key] = name
    for index, name in enumerate(([current] if current else []) + artists):
        key = _norm(name)
        if key and key not in weights:
            weights[key] = max(1, 6 - index)
            names[key] = name
    seeds = [names[key] for key in sorted(weights, key=weights.get, reverse=True)[:3]]
    queries = []
    for seed in seeds:
        queries.extend([seed + " music", "songs similar to " + seed])
    if not queries and recent:
        queries = [str(track.get("title") or "") + " similar songs" for track in recent[:2] if track.get("title")]
    queries = list(dict.fromkeys(queries))[:6]
    raw = []
    ex = concurrent.futures.ThreadPoolExecutor(max_workers=min(len(queries), 8) or 1)
    futures = {ex.submit(_search_cached, q): q for q in queries}
    try:
        for f in concurrent.futures.as_completed(futures, timeout=15):
            try:
                raw.extend(f.result())
            except Exception:
                continue
    except concurrent.futures.TimeoutError:
        pass
    except Exception:
        pass
    ex.shutdown(wait=False, cancel_futures=True)
    collected = []
    seen = set()
    for r in raw:
        name = (r.get("title") or "").strip()
        if not name:
            continue
        key = (r.get("web_url") or name).lower()
        if key in seen:
            continue
        seen.add(key)
        dur = r.get("duration") or 0
        if not (60 <= dur <= 720):
            continue
        if NON_MUSIC_RE.search(name):
            continue
        if played and name.lower().strip() in played:
            continue
        collected.append(r)

    def _score(r):
        text = (r.get("title") or "").lower()
        affinity = max((weights[_norm(seed)] for seed in seeds if _belongs_to(seed, r)), default=0)
        quality = (3 if "official" in text else 0) + (2 if "audio" in text else 0)
        return (-affinity, -quality, text)

    collected.sort(key=_score)
    # Mix artists rather than filling the entire first page with one channel.
    selected, deferred, counts = [], [], {}
    for row in collected:
        key = _norm(row.get("artist") or "")
        if counts.get(key, 0) >= 6:
            deferred.append(row)
        else:
            selected.append(row)
            counts[key] = counts.get(key, 0) + 1
    return jsonify({"results": (selected + deferred)[:36]})



@socketio.on("connect")
def on_connect():
    pass


def _gen_room_code():
    import random

    with ROOMS_LOCK:
        existing = set(ROOMS.keys())
    for _ in range(80):
        code = str(random.randint(100000, 999999))
        if code not in existing:
            return code
    return str(random.randint(100000, 999999))


@socketio.on("create_room")
def on_create_room(data):
    data = data or {}
    sid = request.sid
    _leave_current(sid)
    rid = _gen_room_code()
    room = Room(rid, data.get("room_name") or data.get("name") or "")
    ROOMS[rid] = room
    SID_ROOM[sid] = rid
    join_room(rid)
    room.create_room(sid, data)
    room.add_member(sid, data)
    emit("state", room.state(), to=sid)


@socketio.on("join")
def on_join(data):
    data = data or {}
    sid = request.sid
    register_profile(data)
    rid = str(data.get("room") or "").strip()
    room = ROOMS.get(rid)
    if room is None:
        emit("join_error", {"reason": "not_found"})
        return
    err = room.can_join(sid, data)
    if err:
        emit("join_error", {"reason": err})
        return
    _leave_current(sid)
    SID_ROOM[sid] = rid
    join_room(rid)
    room.add_member(sid, data)
    emit("state", room.state(), to=sid)


@socketio.on("leave")
def on_leave():
    _leave_current(request.sid)
    emit("leave_ok", {})


@socketio.on("kick")
def on_kick(data):
    target = (data or {}).get("sid")
    room = current_room(request.sid)
    if room and target and room.kick(request.sid, target):
        socketio.emit("kicked", {}, to=target)
        socketio.server.disconnect(target)


@socketio.on("disconnect")
def on_disconnect():
    _leave_current(request.sid)


@socketio.on("add")
def on_add(data):
    data = data or {}
    sid = request.sid
    room = current_room(sid)
    if room is None:
        emit("add_error", {"error": "انضم إلى غرفة أولاً"})
        return
    track = data.get("track") or {}
    if not room.can_add and sid != room.owner_sid:
        emit("add_error", {"error": "فقط مالك الغرفة يمكنه الإضافة"})
        return

    def work():
        res = room.add_track(track)
        if res and res.get("error"):
            socketio.emit("add_error", {"error": res["error"]}, to=sid)
        else:
            socketio.emit("add_ok", {}, to=sid)

    threading.Thread(target=work, daemon=True).start()


@socketio.on("remove")
def on_remove(data):
    room = current_room(request.sid)
    if room:
        room.remove_track((data or {}).get("id"))


@socketio.on("skip")
def on_skip():
    room = current_room(request.sid)
    if room:
        room.skip()


@socketio.on("prev")
def on_prev():
    room = current_room(request.sid)
    if room:
        room.previous()


@socketio.on("play_pause")
def on_play_pause():
    room = current_room(request.sid)
    if room:
        room.play_pause()


@socketio.on("seek")
def on_seek(data):
    room = current_room(request.sid)
    if room:
        room.seek((data or {}).get("pos", 0))


@socketio.on("shuffle")
def on_shuffle(data):
    room = current_room(request.sid)
    if room:
        room.set_shuffle((data or {}).get("on", False))


@socketio.on("play_now")
def on_play_now(data):
    room = current_room(request.sid)
    if room:
        room.play_now((data or {}).get("id"))


@socketio.on("ended")
def on_ended(data):
    room = current_room(request.sid)
    if room:
        room.ended((data or {}).get("id"))


PORT = int(os.environ.get("MUSIC_PORT", "5000"))


def _warm_search():
    try:
        search("music", "youtube", 1)
    except Exception:
        pass


def _banner():
    ip = get_lan_ip()
    print("=" * 50)
    print("  تطبيق الموسيقى الجماعية جاهز")
    print(f"  افتح الرابط على جهازك:  http://localhost:{PORT}")
    print(f"  شارك هذا الرابط داخل شبكتك:  http://{ip}:{PORT}")
    print("  (أغلق نافذة التطبيق لإيقاف السيرفر)")
    print("=" * 50)


def _serve():
    socketio.run(app, host="0.0.0.0", port=PORT, debug=False, allow_unsafe_werkzeug=True)


class HarmoniHTTPServer(ThreadedWSGIServer):
    allow_reuse_address = False

    def server_bind(self):
        if hasattr(socket, "SO_EXCLUSIVEADDRUSE"):
            self.socket.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        super().server_bind()


def create_http_server(preferred_port):
    # Bind before creating the window; never display another running build.
    try:
        return HarmoniHTTPServer("0.0.0.0", preferred_port, app)
    except (SystemExit, OSError):
        return HarmoniHTTPServer("0.0.0.0", 0, app)


if __name__ == "__main__":
    headless = os.environ.get("MUSIC_HEADLESS") == "1"
    if not headless:
        discord_presence.start()
    if sys.stdout is None or sys.stderr is None:
        log = open(os.path.join(BASE_DIR, "harmoni-runtime.log"), "a", encoding="utf-8", buffering=1)
        if sys.stdout is None:
            sys.stdout = log
        if sys.stderr is None:
            sys.stderr = log
    for output in (sys.stdout, sys.stderr):
        if hasattr(output, "reconfigure"):
            output.reconfigure(encoding="utf-8", errors="backslashreplace")
    http_server = create_http_server(PORT)
    PORT = http_server.server_port
    threading.Thread(target=http_server.serve_forever, daemon=True).start()
    _load_profiles()
    _load_search_cache()
    threading.Thread(target=_warm_search, daemon=True).start()
    try:
        from mini_window import start_background

        if not headless:
            start_background(PORT)
    except Exception as e:
        print("mini background error:", e)
    try:
        if PORT == 5000 and not headless:
            start_tunnel()
    except Exception:
        pass

    if not headless:
        try:
            from mini_window import main_loop

            _banner()
            main_loop()
            raise SystemExit(0)
        except SystemExit:
            raise
        except ImportError:
            print("pywebview غير متوفرة — تشغيل بدون نافذة (ثبّت: pip install pywebview)")
        except Exception as e:
            print("تعذر فتح نافذة التطبيق، تشغيل بدون واجهة:", e)
    else:
        _banner()
    while True:
        time.sleep(3600)
