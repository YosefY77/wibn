import atexit
import base64
import concurrent.futures
import hashlib
import hmac
import itertools
import math
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

os.environ.setdefault("HARMONI_T0", str(time.time()))   # لقياس سرعة فتح البرنامج (mini_window.log)
_INSTANCE_MUTEX = [None]


def _single_instance():
    """ويندوز: نسخة وحدة بس من هارموني. لو أحد ضغط الأيقونة كذا مرة والبرنامج لسا يفتح (أول تشغيل بطيء)،
    النسخ الزايدة تطلّع نافذة الموجودة وتقفل بدل ما يطلع له كذا هارموني بشريط المهام."""
    if __name__ != "__main__" or os.name != "nt" or os.environ.get("MUSIC_HEADLESS") == "1" \
            or os.environ.get("HARMONI_ALLOW_MULTI") == "1":
        return
    import ctypes
    import urllib.request as _ur
    k = ctypes.windll.kernel32
    k.CreateMutexW.restype = ctypes.c_void_p
    k.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
    h = k.CreateMutexW(None, True, "Local\\HarmoniSingleInstance")
    _INSTANCE_MUTEX[0] = h
    if not h or k.GetLastError() != 183:   # 183 = فيه نسخة ثانية شغالة
        return
    port = int(os.environ.get("MUSIC_PORT", "5000"))
    for _ in range(60):
        try:
            req = _ur.Request("http://127.0.0.1:%d/api/focus_main" % port, data=b"{}", method="POST",
                              headers={"Content-Type": "application/json"})
            with _ur.urlopen(req, timeout=2) as r:
                if json.loads(r.read().decode("utf-8") or "{}").get("app") == "harmoni":
                    os._exit(0)
        except Exception:
            pass
        if k.WaitForSingleObject(h, 500) in (0, 0x80):   # النسخة القديمة قفلت (مثلاً بعد تحديث) → نكمل نحن
            return
    os._exit(0)


_single_instance()
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
def _default_data_root():
    """ويندوز: %LOCALAPPDATA%\\HarmoniData — ماك: ~/Library/Application Support/Harmoni"""
    if os.environ.get("HARMONI_DATA_DIR"):
        return os.environ["HARMONI_DATA_DIR"]
    if sys.platform == "darwin":
        return os.path.join(os.path.expanduser("~"), "Library", "Application Support", "Harmoni")
    return os.path.join(os.environ.get("LOCALAPPDATA") or os.path.expanduser("~"), "HarmoniData")


import ytdlp_live
# لو البرنامج نزّل نسخة أحدث من yt-dlp بنفسه (يوتيوب غيّر شي) نستخدمها بدل اللي داخل البرنامج
ytdlp_live.init(_default_data_root())


class _LazyYtDlp:
    """yt-dlp ثقيل (ثانية أو أكثر بالبرنامج المثبت) — نحمّله بالخلفية بدل ما يأخر فتح النافذة.
    أول ما أي كود يحتاجه (yt_dlp.YoutubeDL…) ينتظر لين يخلص التحميل."""

    def __init__(self):
        self.__dict__["_m"] = None
        self.__dict__["_lock"] = threading.Lock()

    def _load(self):
        m = self.__dict__["_m"]
        if m is None:
            with self.__dict__["_lock"]:
                m = self.__dict__["_m"]
                if m is None:
                    import yt_dlp as m
                    self.__dict__["_m"] = m
                    _patch_region()
        return m

    def __getattr__(self, name):
        return getattr(self._load(), name)


yt_dlp = _LazyYtDlp()

_DATA_FILES = ("profiles.json", "profiles.json.bak", ".harmoni_secret", "search_cache.json", "stream_cache.json",
               "artist_cache.json", "spotify_links.json", "spotify_config.json", "discord_config.json")


def _data_dir(app_dir):
    """بيانات المستخدم في مجلد ثابت (%LOCALAPPDATA%\\HarmoniData) ما يتأثر بالتحديثات أو إعادة التثبيت.
    أول مرة: ننقل البيانات الموجودة جنب البرنامج."""
    root = _default_data_root()
    try:
        os.makedirs(root, exist_ok=True)
    except Exception:
        return app_dir
    try:
        for name in _DATA_FILES:
            src, dst = os.path.join(app_dir, name), os.path.join(root, name)
            if os.path.isfile(src) and not os.path.exists(dst):
                shutil.copy2(src, dst)
        src_audio = os.path.join(app_dir, "audio_cache")
        if os.path.isdir(src_audio):
            dst_audio = os.path.join(root, "audio_cache")
            os.makedirs(dst_audio, exist_ok=True)
            for name in os.listdir(src_audio):
                d = os.path.join(dst_audio, name)
                if not os.path.exists(d):
                    try:
                        shutil.move(os.path.join(src_audio, name), d)
                    except Exception:
                        pass
    except Exception:
        pass
    return root


if getattr(sys, "frozen", False):
    APP_DIR = os.path.dirname(sys.executable)
    BASE_DIR = _data_dir(APP_DIR)
    _BUNDLE_DIR = sys._MEIPASS
else:
    BASE_DIR = os.environ.get("HARMONI_DATA_DIR") or os.path.dirname(os.path.abspath(__file__))
    APP_DIR = os.path.dirname(os.path.abspath(__file__))
    _BUNDLE_DIR = APP_DIR
STATIC_DIR = os.path.join(_BUNDLE_DIR, "static")

# مسار مطلق: على الماك البرنامج يفتح من Finder والمجلد الحالي «/» — المسار النسبي كان يخلي ملفات الواجهة ما تنلقى
app = Flask(__name__, static_folder=STATIC_DIR, static_url_path="/static")
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


# ===== محرك JavaScript ليوتيوب (yt-dlp يحتاجه عشان روابط الصوت تشتغل) =====
# الماك: البرنامج اللي يفتح من Finder ما يشوف Homebrew (PATH قصير) — ولو ما فيه deno/node ننزّل deno مرة وحدة
_DENO_URL = "https://github.com/denoland/deno/releases/latest/download/deno-%s-apple-darwin.zip"
_JSRT = {"state": "", "error": ""}


def _local_deno():
    return os.path.join(BASE_DIR, "bin", "deno.exe" if os.name == "nt" else "deno")


def _setup_js_runtimes():
    if sys.platform == "darwin":
        extra = ["/opt/homebrew/bin", "/usr/local/bin", os.path.expanduser("~/.deno/bin")]
        cur = os.environ.get("PATH", "").split(os.pathsep)
        os.environ["PATH"] = os.pathsep.join(cur + [d for d in extra if d not in cur and os.path.isdir(d)])
    rt = {}
    if os.path.isfile(_local_deno()):
        rt["deno"] = {"path": _local_deno()}
    elif shutil.which("deno"):
        rt["deno"] = {}
    if shutil.which("node"):
        rt["node"] = {}
    YD_BASE["js_runtimes"] = rt or {"deno": {}, "node": {}}
    _JSRT["state"] = ",".join(rt) or "none"
    return rt


def _download_deno():
    """ماك بدون deno/node: ننزّل deno الرسمي (من GitHub) لمجلد البيانات."""
    import platform
    import zipfile
    arch = "aarch64" if platform.machine() == "arm64" else "x86_64"
    dest = _local_deno()
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    tmp = dest + ".zip.part"
    _JSRT["state"] = "downloading"
    try:
        with _requests.get(_DENO_URL % arch, stream=True, timeout=(10, 120)) as r:
            r.raise_for_status()
            with open(tmp, "wb") as fh:
                for chunk in r.iter_content(262144):
                    fh.write(chunk)
        with zipfile.ZipFile(tmp) as z:
            with z.open("deno") as src, open(dest + ".part", "wb") as out:
                shutil.copyfileobj(src, out)
        os.chmod(dest + ".part", 0o755)
        os.replace(dest + ".part", dest)
        _setup_js_runtimes()
        _perf("jsrt", 0, "deno downloaded")
    except Exception as e:
        _JSRT.update(state="failed", error=repr(e)[:200])
        _perf("jsrt", 0, "deno download failed: " + repr(e)[:150])
    finally:
        try:
            os.remove(tmp)
        except OSError:
            pass


_setup_js_runtimes()

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


# على الرازبيري (أو أي سيرفر) النفق يشتغل كخدمة منفصلة — نعرف الرابط العام من المتغير
TUNNEL_URL = (os.environ.get("HARMONI_PUBLIC_URL") or "").strip().rstrip("/")
TUNNEL_PROC = None


def tunnel_disabled():
    """لو الموقع صار على جهاز ثاني (رازبيري) نوقف نفق الكمبيوتر عشان ما ينقسم الزوار بين جهازين."""
    if os.environ.get("HARMONI_NO_TUNNEL") == "1":
        return True
    try:
        return os.path.exists(os.path.join(BASE_DIR, "tunnel_disabled"))
    except Exception:
        return False


def _cloudflared_exe():
    """cloudflared: المثبّت على الجهاز، أو النسخة اللي نزّلها البرنامج بنفسه في مجلد البيانات."""
    exe = shutil.which("cloudflared")
    if exe:
        return exe
    for c in (r"C:\Program Files (x86)\cloudflared\cloudflared.exe", r"C:\Program Files\cloudflared\cloudflared.exe",
              os.path.join(BASE_DIR, "bin", "cloudflared.exe" if os.name == "nt" else "cloudflared")):
        if os.path.isfile(c):
            return c
    return None


def start_tunnel():
    """تشغيل نفق cloudflared — ثابت إذا فيه إعداد محفوظ، وإلا نفق سريع مؤقت."""
    global TUNNEL_URL, TUNNEL_PROC
    if TUNNEL_URL or TUNNEL_PROC:
        return
    if tunnel_disabled():
        print("  النفق موقوف على هذا الجهاز (الموقع شغال من جهاز ثاني).")
        return
    cf_exe = _cloudflared_exe()
    cfg = os.path.join(os.path.expanduser("~"), ".cloudflared", "config.yml")
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
            [cf_exe, "tunnel", "--url", "http://localhost:%d" % PORT, "--no-autoupdate"],
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
    url_re = re.compile(r"https://[a-z0-9]+(?:-[a-z0-9]+)+\.trycloudflare\.com")   # مو api.trycloudflare.com (يطلع في رسائل الأخطاء)

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
    # m4a (AAC) أول: يشتغل على كل الأجهزة — الآيفون ما يشغّل webm/opus زين (يتأخر أو ما يشتغل أبداً)
    opts["format"] = "bestaudio[ext=m4a]/bestaudio[acodec^=mp4a]/bestaudio/best"
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
    # m4a/AAC أول (يشتغل بالآيفون وكل المتصفحات)، وبعدها الأعلى جودة
    def _rank(f):
        aac = f.get("ext") == "m4a" or str(f.get("acodec") or "").startswith("mp4a")
        return (1 if aac else 0, f.get("abr") or f.get("tbr") or 0)
    audio.sort(key=_rank, reverse=True)
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
    rest = (source or "").partition("@")[0]
    base, _, mode = rest.partition(":")
    return base, mode


def _source_region(source):
    return (source or "").partition("@")[2]


# ===================== منطقة البحث =====================
# يوتيوب يرتب النتائج حسب المنطقة (gl). yt-dlp ما يسمح بتغييرها، فنضيفها لسياق الطلب.
# اللغة (hl) نخليها إنجليزي عشان قراءة عدد المشاهدات والتواريخ تبقى صحيحة.
_orig_extract_context = None


def _harmoni_extract_context(self, ytcfg=None, default_client="web"):
    ctx = _orig_extract_context(self, ytcfg, default_client)
    try:
        gl = (self._downloader.params or {}).get("harmoni_gl") if self._downloader else None
        if gl and isinstance(ctx, dict):
            ctx.setdefault("client", {})["gl"] = gl
    except Exception:
        pass
    return ctx


def _patch_region():
    """نضيف المنطقة لسياق يوتيوب (وننعاد بعد ما yt-dlp يتحدث تلقائياً)."""
    global _orig_extract_context
    try:
        from yt_dlp.extractor.youtube._base import YoutubeBaseInfoExtractor as _YTBase
        if _YTBase._extract_context is not _harmoni_extract_context:
            _orig_extract_context = _YTBase._extract_context
            _YTBase._extract_context = _harmoni_extract_context
    except Exception as _e:   # نسخة yt-dlp مختلفة: البحث يشتغل عادي بدون منطقة
        print("region patch unavailable:", _e)


REGIONS = {
    "SA": "السعودية", "AE": "الإمارات", "KW": "الكويت", "QA": "قطر", "BH": "البحرين", "OM": "عُمان",
    "YE": "اليمن", "IQ": "العراق", "JO": "الأردن", "SY": "سوريا", "LB": "لبنان", "PS": "فلسطين",
    "EG": "مصر", "SD": "السودان", "LY": "ليبيا", "TN": "تونس", "DZ": "الجزائر", "MA": "المغرب",
    "TR": "تركيا", "US": "أمريكا", "GB": "بريطانيا", "FR": "فرنسا", "DE": "ألمانيا", "IN": "الهند",
    "PK": "باكستان", "KR": "كوريا", "JP": "اليابان", "BR": "البرازيل",
}
_REGION_STATE = {"detected": None}


def _region_file():
    return os.path.join(BASE_DIR, "region.json")


def _detect_region():
    if _REGION_STATE["detected"] is not None:
        return _REGION_STATE["detected"]
    code = ""
    try:
        import ctypes
        buf = ctypes.create_unicode_buffer(16)
        if ctypes.windll.kernel32.GetUserDefaultGeoName(buf, 16):
            code = buf.value.strip().upper()
    except Exception:
        pass
    if not re.match(r"^[A-Z]{2}$", code or ""):
        try:
            import locale
            loc = (locale.getlocale()[0] or "")
            m = re.search(r"[_-]([A-Za-z]{2})$", loc)
            code = m.group(1).upper() if m else ""
        except Exception:
            code = ""
    _REGION_STATE["detected"] = code if re.match(r"^[A-Z]{2}$", code or "") else ""
    return _REGION_STATE["detected"]


def search_region():
    """المنطقة تلقائية دائماً من إعدادات موقع ويندوز (بدون خيار يدوي)."""
    env = os.environ.get("HARMONI_REGION")
    if env is not None:
        return "" if env.lower() in ("", "none", "off") else env.upper()[:2]
    return _detect_region()


def _drop_old_region_file():
    """نسخ قديمة كانت تحفظ منطقة يدوية — نحذفها عشان يرجع البحث تلقائي."""
    try:
        if os.path.exists(_region_file()):
            os.remove(_region_file())
    except Exception:
        pass


_drop_old_region_file()


@app.route("/api/region", methods=["GET"])
def api_region():
    return jsonify({"setting": "auto", "region": search_region(), "detected": _detect_region(),
                    "names": REGIONS})


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


def _sc_ts(value):
    try:
        import calendar
        m = re.match(r"^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})", str(value or ""))
        return calendar.timegm(tuple(int(x) for x in m.groups())) if m else None
    except Exception:
        return None


def _sc_track_search(query, limit, attempt=0):
    """بحث أغاني ساوند كلاود بطلب API واحد (بدل scsearch اللي كان ياخذ 20+ ثانية)."""
    opts = dict(YD_BASE)
    if attempt:
        opts["cachedir"] = False
    want = max(1, min(int(limit or 36), 200))
    out, offset = [], 0
    with yt_dlp.YoutubeDL(opts) as ydl:
        ie = ydl.get_info_extractor("SoundcloudSearch")
        ie.initialize()
        while len(out) < want and offset < 400:
            n = min(200, want - len(out))
            data = ie._call_api(
                ie._API_V2_BASE + "search/tracks", query, "Searching tracks",
                query={"q": query, "limit": n, "offset": offset, "linked_partitioning": 1},
                headers=ie._HEADERS) or {}
            items = [t for t in (data.get("collection") or []) if t]
            for t in items:
                row = _sc_track_row(t)
                if not row:
                    continue
                if t.get("playback_count") is not None:
                    row["views"] = int(t.get("playback_count") or 0)
                ts = _sc_ts(t.get("release_date") or t.get("display_date") or t.get("created_at"))
                if ts:
                    row["ts"] = ts
                out.append(row)
            if len(items) < n or not data.get("next_href"):
                break
            offset += len(items)
    return out[:want]


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
        except Exception as e:
            if cursor['rows']:
                # الصفحة الجاية فشلت (مثلاً 403) — نرجّع اللي عندنا بدل ما يطلع خطأ للمستخدم
                cursor['done'] = True
                _perf("search-error", 0, "continuation " + repr(e)[:120])
                return cursor['rows'][:limit]
            with _SEARCH_CURSOR_LOCK:
                if _SEARCH_CURSORS.get(key) is cursor:
                    del _SEARCH_CURSORS[key]
            raise


def _transient_search_error(e):
    msg = str(e)
    return any(x in msg for x in ("403", "Forbidden", "429", "Too Many", "500", "502", "503", "504",
                                  "timed out", "Timeout", "Connection", "Remote end closed", "IncompleteRead"))


def _do_search(query, source, limit=SEARCH_LIMIT):
    """البحث مع إعادة محاولة: يوتيوب/ساوند كلاود أحياناً يرفضون طلب (HTTP 403) بشكل مؤقت."""
    last = None
    for attempt in range(3):
        try:
            return _do_search_once(query, source, limit, attempt)
        except Exception as e:
            last = e
            if not _transient_search_error(e):
                raise
            _perf("search-error", 0, "try %d %s | %s" % (attempt + 1, source, repr(e)[:120]))
            time.sleep(0.4 + attempt * 0.6)
    raise last


def _do_search_once(query, source, limit=SEARCH_LIMIT, attempt=0):
    base, mode = _split_source(source)
    if base == "soundcloud" and mode == "playlists":
        results = _sc_playlist_search(query, limit)
    elif base == "soundcloud" and attempt < 2:
        results = _sc_track_search(query, limit, attempt)
    else:
        opts = dict(YD_BASE)
        opts["noplaylist"] = True
        if attempt:
            # محاولة نظيفة: بدون كاش yt-dlp (مفتاح ساوند كلاود القديم ممكن يكون هو سبب الـ 403)
            opts["cachedir"] = False
        if base == "youtube":
            opts["extract_flat"] = True
            if _source_region(source) and attempt < 2:
                opts["harmoni_gl"] = _source_region(source)
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


def _clean_cuts(cuts, duration=0):
    """[[من, إلى], ...] بالثواني — مرتبة ومدموجة، بحد أقصى 20 جزء."""
    out = []
    if not isinstance(cuts, list):
        return out
    try:
        dur = float(duration or 0)
    except Exception:
        dur = 0
    for c in cuts[:40]:
        try:
            a, b = float(c[0]), float(c[1])
        except Exception:
            continue
        if not (a == a and b == b) or a < 0 or b - a < 0.5:   # NaN أو جزء قصير جداً
            continue
        if dur:
            a, b = min(a, dur), min(b, dur)
            if b - a < 0.5:
                continue
        out.append([round(a, 2), round(b, 2)])
    out.sort()
    merged = []
    for a, b in out:
        if merged and a <= merged[-1][1]:
            merged[-1][1] = max(merged[-1][1], b)
        else:
            merged.append([a, b])
    return merged[:20]


_CUT_TICKER = {"on": False}


def _apply_track_cuts(track):
    cuts = _clean_cuts(track.get("cuts"), track.get("duration"))
    if cuts:
        track["cuts"] = cuts
        if not _CUT_TICKER["on"]:
            _CUT_TICKER["on"] = True
            threading.Thread(target=_cut_ticker, daemon=True).start()
    else:
        track.pop("cuts", None)


def _cut_ticker():
    while True:
        time.sleep(0.35)
        try:
            with ROOMS_LOCK:
                rooms = list(ROOMS.values())
            for r in rooms:
                r._apply_cuts()
        except Exception:
            pass


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
        self.banned = set()

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
                "personal": self.hidden,
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
            if self.hidden:
                return "not_found"
            if (uid and uid in self.banned) or sid in self.banned:
                return "kicked"
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
            if sid != self.owner_sid or target_sid == sid or target_sid not in self.members:
                return False
            m = self.members.pop(target_sid, None) or {}
            self.banned.add(target_sid)
            if m.get("uid"):
                self.banned.add(m["uid"])
        self.broadcast()
        return True

    def update_settings(self, sid, data):
        """المالك يغيّر إعدادات الغرفة وهي شغالة (الاسم، كلمة السر، مين يضيف، العدد)."""
        data = data or {}
        with self.lock:
            if sid != self.owner_sid:
                return False
            if "name" in data:
                self.name = str(data.get("name") or "").strip()[:40] or self.name
            if "password" in data:
                self.password = str(data.get("password") or "").strip()[:32]
            if "can_add" in data:
                self.can_add = bool(data.get("can_add"))
            if "max_members" in data:
                try:
                    m = int(data.get("max_members") or 0)
                except Exception:
                    m = 0
                self.max_members = 0 if m <= 0 else max(2, min(m, 100))
        self.broadcast()
        return True

    def take_over(self, track=None, src=None):
        """الغرفة الجديدة تكمل نفس الأغنية (والقائمة) اللي كان المستخدم يسمعها."""
        with self.lock:
            if track and (track.get("web_url") or track.get("title")):
                cur = {k: track.get(k) for k in ("title", "artist", "duration", "thumbnail", "web_url", "source", "added_by")}
                cur["id"] = uuid.uuid4().hex
                self.current = cur
                try:
                    self.position = max(0.0, float(track.get("position") or 0))
                except Exception:
                    self.position = 0.0
                self.playing = bool(track.get("playing", True))
                self.updated_at = time.time()
                self.gen += 1
            elif src is not None:
                with src.lock:
                    self.current = dict(src.current) if src.current else None
                    self.queue = [dict(t) for t in src.queue]
                    self.shuffle = src.shuffle
                    self.playing = src.playing
                    self.position = src.position
                    if src.playing:
                        self.position += max(0.0, time.time() - src.updated_at)
                self.updated_at = time.time()
                self.gen += 1
            cur = self.current
        if cur:
            self._ensure_resolving(cur)

    def play_track(self, track):
        """«استمع» داخل غرفة مشتركة: الأغنية تشتغل للكل فوراً (اللي قبلها تروح للسابق)."""
        track = dict(track or {})
        track["id"] = uuid.uuid4().hex
        track.pop("stream_url", None)
        _apply_track_cuts(track)
        if not track.get("web_url") and track.get("title"):
            self._find_web_url(track)
        with self.lock:
            self._push_history(self.current)
            self.current = track
            self.position = 0.0
            self.updated_at = time.time()
            self.playing = True
            self.gen += 1
        self.broadcast()
        self._ensure_resolving(track)
        return {"ok": True}

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

    def _resolve_url(self, track, refresh=False):
        """نفس طريقة التشغيل الفردي: ملف محلي محفوظ ← كاش الروابط ← يوتيوب/ساوند كلاود."""
        web = track.get("web_url") or ""
        if web.startswith("local:"):
            return ("/api/local_audio/" + web[6:], "local") if _local_track_path(web) else (None, "")
        if web:
            key = _audio_key(web)
            ent = _audio_entry(key)
            if ent and ent.get("ready") and os.path.exists(ent.get("path") or ""):
                return "/api/audio_file?k=" + key, "local"
            try:
                url = cached_stream(web, track.get("source") or "youtube", track.get("title") or "",
                                    track.get("artist") or "", refresh)
            except Exception:
                url = None
            if url:
                return url, "soundcloud" if "sndcdn" in url else "youtube"
            if web in _AGE_URLS or (track.get("source") or "youtube") == "youtube":
                # مقيدة بالعمر/ما تشتغل: النسخة الرسمية من يوتيوب ميوزك بدل أي نتيجة عشوائية
                try:
                    alt_url, _alt = _alternate_stream(web, track.get("title") or "", track.get("artist") or "", track.get("duration"))
                except Exception:
                    alt_url = None
                if alt_url:
                    return alt_url, "soundcloud" if "sndcdn" in alt_url else "youtube"
        url = self._sc_stream(track)
        return (url, "soundcloud") if url else (None, "")

    def _bg_resolve(self, track, refresh=False):
        url, src = None, ""
        try:
            url, src = self._resolve_url(track, refresh)
            if not url and not refresh:
                url, src = self._resolve_url(track, True)
        except Exception as e:
            _perf("room-resolve", 0, "error " + repr(e)[:120])
        with self.lock:
            track.pop("_resolving", None)
            if url:
                track["stream_url"] = url
                track["stream_src"] = src
                track["stream_ts"] = time.time()
                track.pop("stream_failed", None)
            elif not track.get("stream_url"):
                track["stream_failed"] = True
            is_cur = self.current is track
            if url and is_cur and self.playing and self.position == 0 and time.time() - self.updated_at < 90:
                # الكل يبدأ الأغنية من أولها لحظة ما يجهز الصوت (مو من منتصفها)
                self.updated_at = time.time()
            failed_now = is_cur and not url and not track.get("stream_url")
            tid = track.get("id")
        self.broadcast()
        if failed_now:
            _perf("room-resolve", 0, "failed " + str(track.get("title") or ""))
            socketio.emit("track_failed", {"title": track.get("title") or ""}, room=self.rid)
            self.ended(tid, force=True)

    def _ensure_resolving(self, track, refresh=False):
        if not track or not (track.get("web_url") or track.get("title")):
            return
        if track.get("_resolving"):
            return
        fresh = track.get("stream_url") and time.time() - float(track.get("stream_ts") or 0) < 25 * 60
        if fresh and not refresh:
            return
        track["_resolving"] = 1
        threading.Thread(target=self._bg_resolve, args=(track, refresh), daemon=True).start()

    def _prepare_next(self):
        """نجهّز الأغنية الجاية مقدماً عشان تبدأ فوراً."""
        with self.lock:
            nxt = self.queue[0] if self.queue and not self.shuffle else None
        if nxt:
            self._ensure_resolving(nxt)

    def stream_failed(self, tid):
        """عضو ما قدر يشغّل الرابط: نجيب رابط جديد (مرتين بالكثير) بدل ما نتخطى الأغنية على الكل."""
        with self.lock:
            cur = self.current
            if not cur or cur.get("id") != tid or cur.get("_resolving"):
                return False
            n = int(cur.get("_refreshes") or 0)
            if n >= 2 or time.time() - float(cur.get("_refresh_at") or 0) < 8:
                return False
            cur["_refreshes"] = n + 1
            cur["_refresh_at"] = time.time()
        self._ensure_resolving(cur, refresh=True)
        return True

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
        track.pop("stream_url", None)   # الرابط يجيبه السيرفر بنفسه، ما نثق برابط من العميل
        _apply_track_cuts(track)
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
            is_cur = self.current is track
            is_next = bool(self.queue) and self.queue[0] is track
        self.broadcast()
        if is_cur or is_next:
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

    def _apply_cuts(self):
        """الأجزاء المقصوصة من الأغنية: لما الغرفة توصلها نقفز بعدها للكل (أو للأغنية اللي بعدها لو كانت النهاية)."""
        changed = False
        with self.lock:
            cur = self.current
            if not cur or not self.playing or not cur.get("cuts"):
                return
            pos = self.position + (time.time() - self.updated_at)
            try:
                dur = float(cur.get("duration") or 0)
            except Exception:
                dur = 0
            for a, b in cur["cuts"]:
                if a <= pos < b - 0.3:
                    if dur and b >= dur - 1:
                        self._next()
                    else:
                        self.position = float(b)
                        self.updated_at = time.time()
                    changed = True
                    break
        if changed:
            self.broadcast()

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
        self._prepare_next()

    def skip(self, tid=None):
        with self.lock:
            # ضغطتين تخطي بنفس اللحظة (منك أو من صاحبك) ما تحذف أغنيتين
            if tid and (self.current is None or self.current.get("id") != tid):
                return
            self._next()
        self.broadcast()

    def ended(self, tid, force=False):
        with self.lock:
            cur = self.current
            if cur is None or cur.get("id") != tid:
                return
            if not force:
                try:
                    dur = float(cur.get("duration") or 0)
                except Exception:
                    dur = 0
                pos = self.position + ((time.time() - self.updated_at) if self.playing else 0)
                if dur > 30 and pos < dur - 8:
                    # الصوت انقطع عند أحد الأعضاء قبل نهاية الأغنية — ما نحذفها على الكل
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
            self._push_history(self.current)
            self.current = found
            self.position = 0.0
            self.updated_at = time.time()
            self.playing = True
            self.gen += 1
        self._ensure_resolving(found)
        self._prepare_next()
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
_RESOLVER = Room("resolver", hidden=True)   # لروابط الصوت في الغرف المشتركة (ما ينضاف لـ ROOMS)


def _cloud_prepare(track):
    track = {k: v for k, v in (track or {}).items() if not str(k).startswith("stream") and not str(k).startswith("_")}
    _apply_track_cuts(track)
    if not track.get("web_url") and track.get("title"):
        _RESOLVER._find_web_url(track)
    return track


def _cloud_ws_base():
    u = hub.url()
    return ("wss://" + u[8:]) if u.startswith("https://") else ("ws://" + u[7:]) if u.startswith("http://") else u

PROFILES = {}
PROFILES_LOCK = threading.RLock()
_PROFILES_FILE = os.path.join(BASE_DIR, "profiles.json")
import badges
badges.init(BASE_DIR)
import hub
hub.init(BASE_DIR)
import cloud_rooms
cloud_rooms.init(emit=lambda ev, data, sid: socketio.emit(ev, data, to=sid),
                 resolve=lambda tr, refresh: _RESOLVER._resolve_url(tr, refresh),
                 prepare=_cloud_prepare, ws_base=_cloud_ws_base, perf=lambda *a: _perf(*a))
import ducking
ducking.init(BASE_DIR)


@app.route("/manifest.webmanifest")
def web_manifest():
    """«إضافة للشاشة الرئيسية» في الجوال: يفتح هارموني مثل التطبيق (بدون شريط المتصفح)."""
    data = {
        "name": "Harmoni", "short_name": "Harmoni", "start_url": "/", "scope": "/", "display": "standalone",
        "orientation": "portrait", "background_color": "#113835", "theme_color": "#113835", "lang": "ar", "dir": "rtl",
        "icons": [{"src": "/static/app-icon-192.png", "sizes": "192x192", "type": "image/png", "purpose": "any maskable"},
                  {"src": "/static/app-icon-512.png", "sizes": "512x512", "type": "image/png", "purpose": "any maskable"}],
    }
    return Response(json.dumps(data, ensure_ascii=False), mimetype="application/manifest+json")


@app.route("/api/ducking", methods=["GET", "POST"])
def api_ducking():
    """تخفيض صوت الموسيقى تلقائياً أثناء الكلام (ديسكورد/الألعاب) — إعدادات جهاز المستخدم."""
    if request.method == "POST":
        if not _is_local_owner():
            return jsonify({"error": "forbidden"}), 403
        ducking.set_config(request.get_json(silent=True) or {})
    return jsonify(ducking.status())


_PROFILES_SEEN = {"mtime": 0.0}   # آخر نسخة من الملف قريناها/كتبناها
_PROFILES_DELETED = set()          # حسابات حذفناها نحن (عشان الدمج ما يرجّعها)


def _profiles_mtime():
    try:
        return os.path.getmtime(_PROFILES_FILE)
    except OSError:
        return 0.0


def _read_profiles_file(path):
    with open(path, encoding="utf-8-sig") as f:
        data = json.load(f)
    return data if isinstance(data, dict) else None


def _load_profiles():
    global PROFILES
    PROFILES = {}
    for path in (_PROFILES_FILE, _PROFILES_FILE + ".bak"):
        try:
            data = _read_profiles_file(path)
            if data is not None:
                PROFILES = data
                if path != _PROFILES_FILE:
                    _perf("restore", 0, "profiles.json was damaged - restored from profiles.json.bak")
                break
        except FileNotFoundError:
            continue
        except Exception:
            continue
    _PROFILES_SEEN["mtime"] = _profiles_mtime()
    _apply_profiles_restore()
    try:
        badges.seed(PROFILES, norm_username)
    except Exception as e:
        _perf("badges", 0, "seed failed " + repr(e)[:100])


def _apply_profiles_restore():
    """profiles.restore.json (لو موجود): نرجّع منه الحسابات الناقصة ونكمّل «أتابعهم» — مرة وحدة وبعدها ينحذف."""
    path = os.path.join(BASE_DIR, "profiles.restore.json")
    if not os.path.isfile(path):
        return
    try:
        rest = _read_profiles_file(path) or {}
    except Exception:
        return
    added = 0
    with PROFILES_LOCK:
        taken = {norm_username(p.get("username")) for p in PROFILES.values() if p.get("username")}
        for uid, pr in rest.items():
            if not isinstance(pr, dict) or uid in PROFILES:
                continue
            un = norm_username(pr.get("username"))
            if un and un in taken:
                continue
            PROFILES[uid] = pr
            taken.add(un)
            added += 1
        for uid, pr in rest.items():
            cur = PROFILES.get(uid)
            if isinstance(pr, dict) and cur is not None and cur is not pr:
                fl = list(cur.get("following") or [])
                for f in pr.get("following") or []:
                    if f not in fl:
                        fl.append(f)
                cur["following"] = [f for f in fl if f in PROFILES and f != uid]
    _save_profiles()
    try:
        os.replace(path, path + ".done")
    except OSError:
        pass
    _perf("restore", 0, "profiles.restore.json applied: +%d accounts" % added)


def _merge_disk_profiles():
    """لو نسخة ثانية من البرنامج (أو أداة) كتبت الملف بعدنا: ناخذ منها الحسابات اللي مو عندنا بدل ما نمسحها."""
    mt = _profiles_mtime()
    if not mt or mt == _PROFILES_SEEN["mtime"]:
        return
    try:
        disk = _read_profiles_file(_PROFILES_FILE) or {}
    except Exception:
        return
    for uid, pr in disk.items():
        if uid not in PROFILES and uid not in _PROFILES_DELETED and isinstance(pr, dict):
            PROFILES[uid] = pr


def _replace_retry(src, dst, tries=8):
    """os.replace مع إعادة محاولة: في ويندوز الأنتي فايروس أحياناً يمسك الملف لحظة بعد كتابته."""
    for i in range(tries):
        try:
            os.replace(src, dst)
            return True
        except PermissionError:
            if i == tries - 1:
                raise
            time.sleep(0.05 * (i + 1))
    return False


def _save_profiles():
    # نكتب البيانات كاملة لملف مؤقت، وبعدها نستبدل النسخة الاحتياطية ثم الملف الأساسي.
    # كذا لو خرب واحد منهم يبقى الثاني سليم وفيه آخر بيانات.
    with PROFILES_LOCK:
        _merge_disk_profiles()
        payload = json.dumps(PROFILES, ensure_ascii=False)
        folder = os.path.dirname(_PROFILES_FILE)
        for target in (_PROFILES_FILE + ".bak", _PROFILES_FILE):
            fd, tmp = tempfile.mkstemp(dir=folder, suffix=".tmp")
            try:
                with os.fdopen(fd, "w", encoding="utf-8") as f:
                    f.write(payload)
                    f.flush()
                    os.fsync(f.fileno())
                try:
                    _replace_retry(tmp, target)
                except Exception:
                    if target == _PROFILES_FILE:
                        raise
            finally:
                if os.path.exists(tmp):
                    try:
                        os.remove(tmp)
                    except OSError:
                        pass
        _PROFILES_SEEN["mtime"] = _profiles_mtime()


_SECRET_FILE = os.path.join(BASE_DIR, ".harmoni_secret")


def _server_secret():
    try:
        with open(_SECRET_FILE, encoding="utf-8") as f:
            sec = f.read().strip()
        if len(sec) >= 32:
            return sec
    except Exception:
        pass
    sec = os.urandom(32).hex()
    try:
        with open(_SECRET_FILE, "w", encoding="utf-8") as f:
            f.write(sec)
    except Exception:
        pass
    return sec


_SECRET = [None]


def auth_token(uid):
    if _SECRET[0] is None:
        _SECRET[0] = _server_secret()
    return hmac.new(_SECRET[0].encode(), ("uid:" + (uid or "")).encode(), hashlib.sha256).hexdigest()[:40]


def _is_local_owner():
    """طلب من نفس الجهاز مباشرة (مو عبر النفق/الإنترنت)."""
    try:
        if request.headers.get("Cf-Connecting-Ip") or request.headers.get("X-Forwarded-For") or request.headers.get("Cf-Ray"):
            return False
        return (request.remote_addr or "") in ("127.0.0.1", "::1")
    except RuntimeError:
        return False


def uid_authorized(uid, token):
    """الحسابات المسجلة بكلمة سر لازم معها توكن صحيح. الحسابات المجهولة القديمة مسموحة."""
    if not uid:
        return False
    with PROFILES_LOCK:
        pr = PROFILES.get(uid)
    if not pr or not (pr.get("auth") or pr.get("hub")):
        return True
    if token and hmac.compare_digest(str(token), auth_token(uid)):
        return True
    return _is_local_owner()


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
        "stickers": pr.get("stickers") or [],
        "decoration": pr.get("decoration") or "",
        "badges": _badges_for(pr.get("uid") or ""),
    }


_DECO_URL = re.compile(r"^(https://hub\.harmoni\.click/media/deco/[0-9a-f]{16}\.(gif|png|jpg|webp|avif)|/static/decorations/[A-Za-z0-9._-]{1,80})$")
_STICKER_URL = re.compile(r"^https://hub\.harmoni\.click/media/[A-Za-z0-9_-]{4,40}/sticker[0-2]?-[0-9a-f]{16}$")
_STICKER_SHAPES = ("none", "square", "circle", "star", "triangle")
_STICKER_MAX_COUNT = 50


def _sticker_pos(st):
    if st.get("u") == "px":   # بكسل من أقرب حافة (ما يتغير مكانه ولا حجمه لو صغّرت النافذة)
        return {"u": "px", "a": "r" if st.get("a") == "r" else "l",
                "x": round(min(5000.0, max(-200.0, float(st.get("x", 100)))), 1),
                "y": round(min(5000.0, max(-200.0, float(st.get("y", 100)))), 1),
                "w": round(min(3000.0, max(8.0, float(st.get("w", 120)))), 1),
                "rot": round(float(st.get("rot", 0)) % 360, 1)}
    return {"x": round(min(100.0, max(0.0, float(st.get("x", 50)))), 2),
            "y": round(min(100.0, max(0.0, float(st.get("y", 50)))), 2),
            "w": round(min(100.0, max(4.0, float(st.get("w", 25)))), 2),
            "rot": round(float(st.get("rot", 0)) % 360, 1)}


def clean_stickers(v):
    """ملصقات البروفايل: روابط من السيرفر المشترك بس (المساحة 1GB لكل مستخدم هي الحد).
    كل ملصق يا بصفحة البروفايل (page) يا بالبطاقة (card) — منفصلين."""
    out = []
    for st in (v if isinstance(v, list) else [])[:200]:
        if len(out) >= _STICKER_MAX_COUNT:
            break
        if not isinstance(st, dict) or not _STICKER_URL.match(str(st.get("url") or "")):
            continue
        try:
            item = {"url": st["url"], "where": "card" if st.get("where") == "card" else "page",
                    "shape": st.get("shape") if st.get("shape") in _STICKER_SHAPES else "none"}
            item.update(_sticker_pos(st))
            out.append(item)
        except (TypeError, ValueError):
            continue
    return out


# ===== الشارات (badges.py) =====
@app.route("/api/badges")
def api_badges():
    hb = _hub_badges()
    if hb:
        return jsonify(hb)
    with PROFILES_LOCK:
        assign = badges.all_badges(PROFILES)
    return jsonify({"defs": badges.defs(), "order": badges.order(), "assign": assign})


def _badge_admin(uid, token):
    """إدارة الشارات: المؤسس بس، من جهازه (مو عبر النفق)، وبتوكن حسابه."""
    if not _is_local_owner() or not uid or not token:
        return False
    try:
        if not hmac.compare_digest(str(token), auth_token(uid)):
            return False
    except Exception:
        return False
    return badges.is_admin(uid)


@app.route("/api/badges/admin", methods=["GET", "POST"])
def api_badges_admin():
    data = (request.get_json(silent=True) or {}) if request.method == "POST" else request.args
    uid, token = str(data.get("uid") or ""), str(data.get("token") or "")
    if not _badge_admin(uid, token):
        if request.method == "GET":
            return jsonify({"admin": False})
        return jsonify({"error": "forbidden"}), 403
    if request.method == "GET":
        return jsonify({"admin": True})
    if data.get("action") in ("reset_requests", "reset_password"):
        return _admin_reset(uid, data)
    fwd = _hub_badge_admin(uid, data)
    if fwd is not None:
        return fwd
    action = data.get("action")
    target = str(data.get("target") or "")
    badge = str(data.get("badge") or "")
    try:
        if action in ("grant", "revoke"):
            with PROFILES_LOCK:
                if target not in PROFILES:
                    return jsonify({"error": "no_user"}), 404
            if action == "grant":
                badges.grant(target, badge)
            else:
                badges.revoke(target, badge, uid)
        elif action == "create":
            badge = badges.create(data.get("emoji"), data.get("ar"), data.get("en"))
        elif action == "delete":
            badges.delete(badge)
        else:
            return jsonify({"error": "bad_action"}), 400
    except ValueError as e:
        return jsonify({"error": str(e)}), 400
    with PROFILES_LOCK:
        assign = badges.all_badges(PROFILES)
    return jsonify({"ok": True, "badge": badge, "defs": badges.defs(), "order": badges.order(), "assign": assign})


# ===== «نسيت كلمة السر»: الطلب يوصل للمؤسس، وهو يحط كلمة سر جديدة (من جهازه وبحسابه بس) =====
_RESET_FILE = "reset_requests.json"


def _local_resets():
    try:
        with open(os.path.join(BASE_DIR, _RESET_FILE), encoding="utf-8-sig") as f:
            v = json.load(f)
        return v if isinstance(v, list) else []
    except Exception:
        return []


def _save_local_resets(lst):
    try:
        with open(os.path.join(BASE_DIR, _RESET_FILE), "w", encoding="utf-8") as f:
            json.dump(lst[-50:], f, ensure_ascii=False)
    except Exception:
        pass


@app.route("/api/forgot_password", methods=["POST"])
def api_forgot_password():
    un = norm_username((request.get_json(silent=True) or {}).get("username"))
    if not un:
        return jsonify({"error": "اكتب اليوزر حقك"}), 400
    r = hub.call("POST", "/reset_request", {"username": un})
    if r is None or r[0] != 200:   # السيرفر ما رد (أو قديم): نحفظ الطلب عندنا
        with PROFILES_LOCK:
            pr = next((p for p in PROFILES.values() if isinstance(p, dict) and norm_username(p.get("username")) == un), None)
        if pr:
            lst = _local_resets()
            if not any(x.get("uid") == pr.get("uid") for x in lst):
                lst.append({"uid": pr.get("uid"), "username": un, "at": int(time.time())})
                _save_local_resets(lst)
    return jsonify({"ok": True})


def _admin_reset(admin_uid, data):
    action = data.get("action")
    tok = hub.token(admin_uid)
    if action == "reset_requests":
        reqs, seen = [], set()
        if tok:
            r = hub.call("POST", "/admin/reset_requests", {"uid": admin_uid, "token": tok})
            if r is not None and r[0] == 200:
                reqs = [x for x in (r[1].get("requests") or []) if isinstance(x, dict)]
        seen = {x.get("uid") for x in reqs}
        reqs += [x for x in _local_resets() if x.get("uid") not in seen]
        return jsonify({"requests": reqs})
    target = str(data.get("target") or "")
    pw = str(data.get("password") or "")
    if len(pw) < 4:
        return jsonify({"error": "short_password"}), 400
    with PROFILES_LOCK:
        pr = PROFILES.get(target)
        if pr is None and data.get("username"):
            un = norm_username(data.get("username"))
            pr = next((p for p in PROFILES.values() if isinstance(p, dict) and norm_username(p.get("username")) == un), None)
            target = (pr or {}).get("uid") or target
    if not target:
        return jsonify({"error": "no_user"}), 404
    salt = os.urandom(16).hex()
    h = _hash_password(pw, salt)
    on_hub = bool(pr and (pr.get("hub") or hub.token(target)))
    if on_hub or (pr is None and tok):
        if not tok:
            return jsonify({"error": "forbidden"}), 403
        r = hub.call("POST", "/admin/reset_password", {"uid": admin_uid, "token": tok, "target": target,
                                                       "salt": salt, "hash": h, "iter": 60000})
        if r is None:
            return jsonify({"error": "hub_down"}), 503
        if r[0] != 200:
            return jsonify({"error": r[1].get("error") or "failed"}), (r[0] if 400 <= r[0] < 500 else 400)
    elif pr is None:
        return jsonify({"error": "no_user"}), 404
    with PROFILES_LOCK:
        cur = PROFILES.get(target)
        if cur is not None and (cur.get("auth") or not cur.get("hub")):
            cur["auth"] = {"salt": salt, "hash": h}
    _save_profiles()
    _save_local_resets([x for x in _local_resets() if x.get("uid") != target])
    return jsonify({"ok": True})


# ===== ذوق المستخدم (استبيان أول مرة) =====
TASTE_GENRES = {
    "khaleeji": "اغاني خليجية", "arabic_pop": "اغاني عربية جديدة", "tarab": "طرب اصيل", "shilat": "شيلات",
    "rap_ar": "راب عربي", "pop": "pop hits", "hiphop": "hip hop hits", "rnb": "r&b songs", "rock": "rock songs",
    "electronic": "edm electronic music", "indie": "indie songs", "lofi": "lofi chill", "classical": "classical music",
    "jazz": "jazz music", "kpop": "kpop hits", "latin": "latin hits", "metal": "metal songs", "afro": "afrobeats",
    "ost": "movie soundtrack music", "nasheed": "اناشيد بدون موسيقى",
}
TASTE_MOODS = {
    "workout": "workout music", "focus": "focus study music", "party": "party songs", "drive": "driving songs",
    "chill": "relaxing chill music", "sad": "sad songs",
}


def _clean_taste(t):
    if not isinstance(t, dict):
        return None
    def keys(v, allowed):
        return [x for x in (v if isinstance(v, list) else []) if isinstance(x, str) and x in allowed][:20]
    artists = [str(a).strip()[:60] for a in (t.get("artists") if isinstance(t.get("artists"), list) else []) if str(a).strip()][:20]
    langs = keys(t.get("langs"), {"ar", "en"})
    out = {"genres": keys(t.get("genres"), TASTE_GENRES), "moods": keys(t.get("moods"), TASTE_MOODS),
           "artists": artists, "langs": langs}
    try:
        out["at"] = int(t.get("at") or 0)
    except Exception:
        out["at"] = 0
    return out if (out["genres"] or out["moods"] or out["artists"]) else None


def userdata_payload(pr):
    return {
        "likes": pr.get("likes") or [],
        "following": list(pr.get("following") or []),
        "playlists": pr.get("playlists") or [],
        "history": pr.get("history") or [],
        "stats": pr.get("stats") or {},
        "taste": pr.get("taste") or {},
    }


@app.route("/api/userdata", methods=["GET", "POST"])
def api_userdata():
    token = request.headers.get("X-Harmoni-Token") or ""
    if request.method == "GET":
        uid = request.args.get("uid") or ""
        if not uid_authorized(uid, token or request.args.get("token")):
            return jsonify({"error": "login_required"}), 403
        _hub_pull_userdata(uid)
        with PROFILES_LOCK:
            pr = PROFILES.get(uid)
            data = userdata_payload(pr) if pr else {"likes": [], "following": [], "playlists": [], "history": []}
        if pr and pr.get("auth") and not token:
            data["token"] = auth_token(uid)   # جهاز المالك: نعطيه التوكن تلقائياً
        return jsonify(data)
    data = request.get_json(force=True, silent=True) or {}
    uid = data.get("uid") or ""
    if not uid:
        return jsonify({"error": "no uid"}), 400
    if not uid_authorized(uid, token or data.get("token")):
        return jsonify({"error": "login_required"}), 403
    with PROFILES_LOCK:
        pr = PROFILES.get(uid)
        if not pr:
            pr = PROFILES[uid] = {"uid": uid}
        for k in ("likes", "following", "playlists", "history"):
            v = data.get(k)
            if isinstance(v, list):
                if k == "history" and not v and pr.get("history") and not data.get("history_clear"):
                    continue   # جهاز/نافذة ثانية سجلها فاضي ما تمسح سجل المستخدم (إلا لو ضغط مسح السجل)
                pr[k] = v[:2000]
        st = data.get("stats")
        if isinstance(st, dict) and len(json.dumps(st)) < 600000:
            pr["stats"] = st
        taste = _clean_taste(data.get("taste"))
        if taste:
            pr["taste"] = taste
        if data.get("history_clear"):
            pr["hub_history_clear"] = 1
        PROFILES[uid] = pr
        _hub_dirty(pr)
    _save_profiles()
    _hub_wake()
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
    salt = os.urandom(16).hex()
    pw_hash = _hash_password(pw, salt)
    r = hub.call("POST", "/register", {"username": un, "name": raw_un, "salt": salt, "hash": pw_hash, "iter": 60000})
    if r is None and hub.enabled():
        # بدون السيرفر المشترك ما نقدر نتأكد إن اليوزر مو ماخذه أحد بجهاز ثاني — فما نسوي حساب محلي
        return jsonify({"error": "ما قدرنا نوصل لسيرفر هارموني عشان نتأكد إن اليوزر فاضي، جرب بعد شوي"}), 503
    if r is not None:
        st, j = r
        if st == 200 and j.get("uid"):
            pr = _hub_adopt(j, {"salt": salt, "hash": pw_hash})
            return jsonify({"uid": pr["uid"], "token": auth_token(pr["uid"]), "profile": public_profile(pr), "data": userdata_payload(pr)})
        if st in (400, 409):
            return jsonify({"error": _hub_err(j.get("error"))}), 400
    with PROFILES_LOCK:
        uid = "u" + uuid.uuid4().hex[:15]
        pr = {
            "uid": uid,
            "name": raw_un,
            "username": un,
            "auth": {"salt": salt, "hash": pw_hash},
        }
        PROFILES[uid] = pr
        _save_profiles()
    _hub_wake()   # السيرفر المشترك ما رد: الحساب محلي الحين ويترفع أول ما يرجع
    return jsonify({"uid": uid, "token": auth_token(uid), "profile": public_profile(pr), "data": userdata_payload(pr)})


@app.route("/api/login", methods=["POST"])
def api_login():
    data = request.get_json(force=True, silent=True) or {}
    un = norm_username(data.get("username"))
    pw = str(data.get("password") or "")
    with PROFILES_LOCK:
        moved = None
        if not any(norm_username(p.get("username")) == un for p in PROFILES.values() if isinstance(p, dict)):
            moved = next((p.get("username") for p in PROFILES.values()
                          if isinstance(p, dict) and p.get("renamed_from") == un), None)
    if moved:
        return jsonify({"error": "يوزرك تغيّر لأن أحد ثاني كان ماخذه — صار @%s (نفس كلمة السر)" % moved,
                        "renamed_to": moved}), 401
    via_hub = _hub_login(un, pw)
    if via_hub is not None:
        return via_hub
    with PROFILES_LOCK:
        pr = None
        for k, p in PROFILES.items():
            if norm_username(p.get("username")) == un:
                pr = p
                break
    if not pr:
        return jsonify({"error": "يوزر أو كلمة سر خطأ"}), 401
    if pr.get("hub") and not pr.get("auth"):
        # حساب من جهاز ثاني (من السيرفر المشترك) والسيرفر ما يرد الحين
        return jsonify({"error": "ما قدرنا نوصل لسيرفر هارموني، جرب بعد شوي"}), 503
    if not pr.get("auth"):
        # حساب قديم بدون كلمة سر: أول من يدخل يوزر مع كلمة سر (4+ أحرف) يستلمها
        if len(pw) < 4:
            return jsonify({"error": "هذا الحساب قديم، اختر كلمة سر (4 أحرف على الأقل) لتستلمه"}), 401
        salt = os.urandom(16).hex()
        pr["auth"] = {"salt": salt, "hash": _hash_password(pw, salt)}
        with PROFILES_LOCK:
            PROFILES[pr.get("uid")] = pr
            _save_profiles()
        return jsonify({"uid": pr.get("uid"), "token": auth_token(pr.get("uid")), "profile": public_profile(pr), "data": userdata_payload(pr)})
    try:
        ok = hmac.compare_digest(
            _hash_password(pw, pr["auth"]["salt"]), pr["auth"]["hash"])
    except Exception:
        ok = False
    if not ok:
        return jsonify({"error": "يوزر أو كلمة سر خطأ"}), 401
    _hub_wake()
    return jsonify({"uid": pr.get("uid"), "token": auth_token(pr.get("uid")), "profile": public_profile(pr), "data": userdata_payload(pr)})


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
    with PROFILES_LOCK:
        _pr = PROFILES.get(uid)
    if _pr and (_pr.get("auth") or _pr.get("hub")):
        tok = str(data.get("token") or "")
        if not ((tok and hmac.compare_digest(tok, auth_token(uid))) or _is_local_owner()):
            return   # ما نسمح لأحد يعدّل بروفايل مسجّل بدون توكن
    name = data.get("name") or ""
    avatar = data.get("avatar") or ""
    following = data.get("following") or []
    with PROFILES_LOCK:
        pr = PROFILES.get(uid) or {"uid": uid}
        before = _hub_public(pr)
        if name:
            pr["name"] = name
        if avatar:
            pr["avatar"] = avatar
        pr["following"] = list(following)
        for k in ("bio", "banner", "colors", "likes"):
            v = data.get(k)
            if v:
                pr[k] = v
        if "stickers" in data:
            new_st = clean_stickers(data.get("stickers"))
            # ما نمسح ملصقات موجودة بقائمة فاضية إلا لو المستخدم شالها كلها بنفسه (stickers_clear)
            if new_st or not pr.get("stickers") or data.get("stickers_clear"):
                pr["stickers"] = new_st
        if "decoration" in data:
            d = str(data.get("decoration") or "")
            pr["decoration"] = d if _DECO_URL.match(d) else ""
        un = (data.get("username") or "").strip().lstrip("@")
        if un and norm_username(pr.get("username")) != norm_username(un):
            if not username_taken(un, uid):
                pr["username"] = un
        empty = (not pr.get("name") and not pr.get("avatar") and not pr.get("bio")
                 and not pr.get("banner") and not pr.get("likes") and not pr.get("following"))
        if empty and not pr.get("username") and not pr.get("auth"):
            PROFILES.pop(uid, None)
            _PROFILES_DELETED.add(uid)
        else:
            PROFILES[uid] = pr
            if _hub_public(pr) != before:
                _hub_dirty(pr)
    _save_profiles()
    _hub_wake()


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
                "stickers": pr.get("stickers") or [],
                "decoration": pr.get("decoration") or "",
                "badges": _badges_for(uid),
            })
        return out


# ===== Harmoni Hub: الحسابات مشتركة بين كل الأجهزة (سيرفر سحابي — hub.py) =====
# كل جهاز يحتفظ بنسخة محلية (عشان البرنامج يشتغل لو السيرفر طاح)، ويرفع تعديلاته ويسحب حسابات الباقين.
_HUB_PUBLIC = ("name", "avatar", "banner", "bio", "colors", "likes", "following", "stickers", "decoration")
_HUB_PRIVATE = ("playlists", "history", "stats", "taste")
_HUB = {"pulled": 0.0, "ver": "", "badges": None, "badges_at": 0.0, "wake": threading.Event(), "started": False, "seq": 0}
_HUB_BADGES_TTL = 600


def _hub_err(code):
    return {
        "taken": "هذا اليوزر محجوز، اختر غيره",
        "short_username": "اليوزر لازم يكون 3 أحرف على الأقل",
        "bad_username": "اليوزر فيه رموز غير مسموحة",
    }.get(code, "صار خطأ بسيرفر هارموني، جرب مرة ثانية")


def _hub_public(pr):
    out = {k: pr.get(k) for k in _HUB_PUBLIC if pr.get(k) is not None}
    out["username"] = pr.get("username") or ""
    return out


def _hub_private(pr):
    return {k: pr.get(k) for k in _HUB_PRIVATE if pr.get(k) is not None}


def _hub_dirty(pr):
    """تعديل محلي على حساب مربوط بالسيرفر → يترفع بالدورة الجاية."""
    if pr and pr.get("uid") and hub.token(pr.get("uid")):
        _HUB["seq"] += 1
        pr["hub_dirty"] = _HUB["seq"]


def _hub_wake():
    if hub.enabled():
        _HUB["wake"].set()


def _item_key(x):
    if isinstance(x, dict):
        return str(x.get("id") or x.get("url") or x.get("name") or json.dumps(x, sort_keys=True, ensure_ascii=False))
    return str(x)


def _merge_into(dst, src):
    """حساب قديم محلي بنفس اليوزر → نضم إعجاباته وقوائمه ومتابعاته للحساب المشترك بدل ما تضيع."""
    for k in ("likes", "playlists", "following"):
        cur = list(dst.get(k) or [])
        have = {_item_key(x) for x in cur}
        for x in src.get(k) or []:
            if _item_key(x) not in have:
                cur.append(x)
                have.add(_item_key(x))
        if cur:
            dst[k] = cur
    for k in ("avatar", "banner", "bio", "taste", "stats", "history"):
        if not dst.get(k) and src.get(k):
            dst[k] = src[k]


def _hub_adopt(j, auth=None):
    """حساب رجع من السيرفر (تسجيل/دخول) → نحفظه عندنا ونربطه بتوكن السيرفر."""
    uid = j.get("uid")
    prof = j.get("profile") or {}
    data = j.get("data") or {}
    hub.set_token(uid, j.get("token"))
    with PROFILES_LOCK:
        pr = PROFILES.get(uid) or {"uid": uid}
        pr["uid"] = uid
        if not pr.get("hub_dirty"):
            for k in _HUB_PUBLIC:
                if k in prof:
                    pr[k] = prof[k]
            for k in _HUB_PRIVATE:
                if data.get(k):
                    pr[k] = data[k]
            if prof.get("username"):
                pr["username"] = prof["username"]
        if auth:
            pr["auth"] = auth
        for k in ("hub", "hub_conflict"):
            pr.pop(k, None)
        un = norm_username(pr.get("username"))
        for ouid, op in list(PROFILES.items()):
            if ouid != uid and un and isinstance(op, dict) and norm_username(op.get("username")) == un:
                _merge_into(pr, op)
                PROFILES.pop(ouid, None)
                _PROFILES_DELETED.add(ouid)
                _hub_dirty(pr)
        PROFILES[uid] = pr
    _save_profiles()
    _hub_wake()
    return pr


def _hub_login(un, pw):
    """الدخول عن طريق السيرفر المشترك. يرجع None لو لازم نكمل محلياً (السيرفر طايح أو الحساب مو مرفوع للحين)."""
    r = hub.call("GET", "/salt", params={"username": un})
    if r is None or r[0] != 200 or not r[1].get("salt"):
        return None
    salt, it = r[1]["salt"], int(r[1].get("iter") or 60000)
    try:
        h = hashlib.pbkdf2_hmac("sha256", pw.encode("utf-8"), bytes.fromhex(salt), it).hex()
    except Exception:
        return None
    r = hub.call("POST", "/login", {"username": un, "hash": h})
    if r is None:
        return None
    st, j = r
    if st == 401:
        with PROFILES_LOCK:
            local = next((p for p in PROFILES.values() if isinstance(p, dict) and p.get("auth") and not p.get("hub")
                          and norm_username(p.get("username")) == un and not hub.token(p.get("uid"))), None)
        if local:
            return None   # حساب محلي قديم بنفس اليوزر (ما انرفع لأن اليوزر محجوز بالسيرفر) → نجرب محلياً
        return jsonify({"error": "يوزر أو كلمة سر خطأ"}), 401
    if st != 200 or not j.get("uid"):
        return None
    pr = _hub_adopt(j, {"salt": salt, "hash": h} if it == 60000 else None)
    uid = pr["uid"]
    return jsonify({"uid": uid, "token": auth_token(uid), "profile": public_profile(pr), "data": userdata_payload(pr)})


def _hub_badges():
    b = _HUB["badges"]
    if b and time.time() - _HUB["badges_at"] < _HUB_BADGES_TTL:
        return b
    return None


def _badges_for(uid):
    b = _hub_badges()
    if b is not None:
        return list((b.get("assign") or {}).get(uid) or [])
    return badges.badges_of(uid, PROFILES)


def _hub_set_badges(b):
    if isinstance(b, dict) and isinstance(b.get("assign"), dict):
        _HUB["badges"] = {"defs": b.get("defs") or {}, "order": b.get("order") or [], "assign": b["assign"]}
        _HUB["badges_at"] = time.time()


def _hub_badge_admin(uid, data):
    """إدارة الشارات: لو حساب المؤسس مربوط بالسيرفر → التعديل يصير هناك (ويظهر لكل الأجهزة)."""
    tok = hub.token(uid)
    if not tok:
        return None
    body = {k: data.get(k) for k in ("action", "target", "badge", "emoji", "ar", "en")}
    body.update({"uid": uid, "token": tok})
    r = hub.call("POST", "/badges/admin", body)
    if r is None:
        return None
    st, j = r
    if st == 200:
        _hub_set_badges(j)
        return jsonify(j)
    return jsonify({"error": j.get("error") or "failed"}), (st if 400 <= st < 500 else 400)


def _hub_pull_userdata(uid):
    tok = hub.token(uid)
    if not tok:
        return
    with PROFILES_LOCK:
        pr = PROFILES.get(uid)
        if pr is None or pr.get("hub_dirty"):
            return
    r = hub.call("GET", "/userdata", params={"uid": uid, "token": tok}, timeout=4)
    if r is None:
        return
    st, j = r
    if st == 403:
        hub.forget_token(uid)
        return
    if st != 200:
        return
    changed = False
    with PROFILES_LOCK:
        pr = PROFILES.get(uid)
        if pr is None or pr.get("hub_dirty"):
            return
        for k in ("likes", "following") + _HUB_PRIVATE:
            v = j.get(k)
            if v is not None and v != pr.get(k) and (v or k not in ("history",)):
                pr[k] = v
                changed = True
    if changed:
        _save_profiles()


def _hub_migrate():
    """حسابات موجودة بهالجهاز بس → نرفعها للسيرفر (نفس الـ uid ونفس كلمة السر)."""
    with PROFILES_LOCK:
        todo = [dict(p) for p in PROFILES.values() if isinstance(p, dict) and p.get("uid") and p.get("username")
                and (p.get("auth") or {}).get("hash") and not p.get("hub") and not hub.token(p.get("uid"))
                and time.time() - float(p.get("hub_conflict") or 0) > 86400]
    for p in todo:
        uid = p["uid"]
        r = hub.call("POST", "/migrate", {
            "uid": uid, "username": norm_username(p.get("username")),
            "salt": p["auth"].get("salt"), "hash": p["auth"].get("hash"), "iter": 60000,
            "profile": {k: v for k, v in _hub_public(p).items() if k != "username"}, "data": _hub_private(p)})
        if r is None:
            return
        st, j = r
        new_un = _hub_free_username(norm_username(p.get("username")), uid) \
            if st == 409 and j.get("error") == "taken" else None
        with PROFILES_LOCK:
            cur = PROFILES.get(uid)
            if cur is None:
                continue
            if st == 200 and j.get("token"):
                hub.set_token(uid, j["token"])
                cur.pop("hub_conflict", None)
            elif st == 409 and j.get("error") == "taken":
                if new_un:
                    # اليوزر ماخذه أحد ثاني بالسيرفر المشترك: نعطي هالحساب اسم جديد (ما نسمح بيوزرين نفس الشي)
                    cur.setdefault("renamed_from", norm_username(p.get("username")))
                    cur["username"] = new_un
                    cur["hub_conflict"] = time.time() - 86400 - 1   # يترفع بالدورة الجاية مباشرة
                else:
                    cur["hub_conflict"] = time.time()
            elif st in (400, 409):
                cur["hub_conflict"] = time.time()
        _save_profiles()


def _hub_free_username(base, uid):
    """أول يوزر فاضي بالسيرفر المشترك وبالجهاز: base2، base3، …"""
    base = re.sub(r"\d+$", "", base or "")[:24] or "user"
    with PROFILES_LOCK:
        local = {norm_username(p.get("username")) for k, p in PROFILES.items() if k != uid and isinstance(p, dict)}
    for n in range(2, 40):
        cand = norm_username(base + str(n))
        if cand in local:
            continue
        r = hub.call("GET", "/check_username", params={"username": cand, "uid": uid})
        if r is None:
            return None
        if r[0] == 200 and r[1].get("available"):
            return cand
    return None


def _hub_push_dirty():
    with PROFILES_LOCK:
        todo = [(p["uid"], p.get("hub_dirty"), dict(p)) for p in PROFILES.values()
                if isinstance(p, dict) and p.get("uid") and p.get("hub_dirty")]
    for uid, seq, p in todo:
        tok = hub.token(uid)
        if not tok:
            continue
        pub = _hub_public(p)
        body = {"uid": uid, "token": tok, "username": pub.pop("username", ""), "profile": pub, "data": _hub_private(p)}
        if p.get("hub_history_clear"):
            body["history_clear"] = True
        r = hub.call("POST", "/update", body)
        if r is None:
            return
        st, j = r
        with PROFILES_LOCK:
            cur = PROFILES.get(uid)
            if cur is None:
                continue
            if st == 200 or st in (400, 404):
                if cur.get("hub_dirty") == seq:
                    cur.pop("hub_dirty", None)
                    cur.pop("hub_history_clear", None)
            if st == 403:
                cur.pop("hub_dirty", None)
        if st == 403:
            hub.forget_token(uid)   # توكن قديم (السيرفر تغير سره) → نعيد الربط بالدورة الجاية
        _save_profiles()


def _hub_pull_profiles():
    r = hub.call("GET", "/profiles", params={"v": _HUB["ver"]}, timeout=8)
    if r is None or r[0] != 200:
        return
    j = r[1]
    _hub_set_badges(j.get("badges"))
    _HUB["pulled"] = time.time()
    if j.get("same"):
        return
    changed = False
    with PROFILES_LOCK:
        for p in j.get("profiles") or []:
            uid = p.get("uid") if isinstance(p, dict) else None
            if not uid:
                continue
            cur = PROFILES.get(uid)
            if cur is not None and cur.get("hub_dirty"):
                continue
            if cur is None:
                cur = {"uid": uid, "hub": 1}
            new = dict(cur)
            for k in _HUB_PUBLIC:
                if k in p:
                    new[k] = p[k]
            if p.get("username"):
                new["username"] = p["username"]
            if new != cur or uid not in PROFILES:
                PROFILES[uid] = new
                changed = True
    _HUB["ver"] = j.get("ver") or ""
    if changed:
        _save_profiles()


def _hub_import_badges():
    """مرة وحدة: الشارات اللي سواها المؤسس على جهازه قبل السيرفر → نرفعها."""
    if hub.meta("badges_imported"):
        return
    exp = badges.export()
    founder = next((u for u, l in exp["assign"].items() if "founder" in (l or []) and hub.token(u)), None)
    if not founder:
        return
    r = hub.call("POST", "/badges/admin", {"uid": founder, "token": hub.token(founder), "action": "import",
                                           "defs": exp["defs"], "assign": exp["assign"]})
    if r is not None and r[0] == 200:
        _hub_set_badges(r[1])
        hub.set_meta("badges_imported", True)


def _hub_sync_once():
    if not hub.available():
        return
    _hub_migrate()
    _hub_push_dirty()
    _hub_pull_profiles()
    _hub_import_badges()


def _hub_loop():
    while True:
        try:
            _hub_sync_once()
        except Exception as e:
            _perf("hub", 0, "sync failed " + repr(e)[:160])
        _HUB["wake"].wait(45)
        _HUB["wake"].clear()
        time.sleep(1)   # نجمع التعديلات اللي جت ورا بعض بطلب واحد


# ===================== تحديث yt-dlp تلقائياً =====================
_YTDLP_TEST_URL = "https://www.youtube.com/watch?v=YQHsXMglC9A"
_YTDLP_FORCED = [0.0]


def _ytdlp_http_get(url):
    r = _requests.get(url, timeout=20, headers={"User-Agent": "Harmoni"})
    r.raise_for_status()
    return r.json()


def _ytdlp_http_download(url, path):
    with _requests.get(url, timeout=60, stream=True, headers={"User-Agent": "Harmoni"}) as r:
        r.raise_for_status()
        with open(path, "wb") as f:
            for chunk in r.iter_content(1 << 16):
                f.write(chunk)


def _ytdlp_live_test(mod):
    """النسخة الجديدة لازم تقدر تجيب رابط صوت حقيقي من يوتيوب قبل ما نعتمدها."""
    opts = dict(YD_BASE)
    opts.update({"format": "bestaudio/best", "skip_download": True})
    with mod.YoutubeDL(opts) as ydl:
        info = ydl.extract_info(_YTDLP_TEST_URL, download=False)
    return bool(info and (info.get("url") or info.get("requested_formats") or info.get("formats")))


def _ytdlp_swapped(mod):
    global yt_dlp
    yt_dlp = mod
    _patch_region()
    _FAIL_STREAK.clear()
    _perf("ytdlp", 0, "auto-updated to " + ytdlp_live.current_version())


def ytdlp_check_soon(force=False):
    if force:
        if time.time() - _YTDLP_FORCED[0] < 1800:
            return
        _YTDLP_FORCED[0] = time.time()
    threading.Thread(target=ytdlp_live.check, args=(_ytdlp_http_get, _ytdlp_http_download, _ytdlp_live_test, force),
                     daemon=True, name="harmoni-ytdlp").start()


def _ytdlp_loop():
    time.sleep(90)
    while True:
        try:
            ytdlp_live.check(_ytdlp_http_get, _ytdlp_http_download, _ytdlp_live_test)
        except Exception:
            pass
        time.sleep(3600)


def start_ytdlp_updates():
    if os.path.basename(sys.argv[0] or "").startswith("test_"):
        return
    ytdlp_live._state["on_swap"] = _ytdlp_swapped
    threading.Thread(target=_ytdlp_loop, daemon=True, name="harmoni-ytdlp-loop").start()


# ===================== صور البروفايل الكبيرة (GIF لين 20MB) =====================
_MEDIA_MAX = 20 * 1024 * 1024
_STICKER_MAX = 95 * 1024 * 1024
_MEDIA_KINDS = ("avatar", "banner", "sticker", "sticker0", "sticker1", "sticker2")


@app.route("/api/media_upload", methods=["POST"])
def api_media_upload():
    """يرفع صورة/غلاف البروفايل للسيرفر المشترك (Cloudflare R2) ويرجع رابطها — عشان الـ GIF يتحرك وما يثقل البروفايل."""
    uid = request.args.get("uid", "")
    kind = request.args.get("kind", "")
    if kind not in _MEDIA_KINDS or not uid_authorized(uid, request.headers.get("X-Harmoni-Token", "")):
        return jsonify({"error": "forbidden"}), 403
    ctype = (request.content_type or "").split(";")[0].strip().lower()
    if ctype not in ("image/gif", "image/png", "image/jpeg", "image/webp"):
        return jsonify({"error": "bad_type"}), 400
    limit = _STICKER_MAX if kind.startswith("sticker") else _MEDIA_MAX
    if (request.content_length or 0) > limit:
        return jsonify({"error": "too_big"}), 413
    data = request.get_data(cache=False)
    if not data or len(data) > limit:
        return jsonify({"error": "too_big" if data else "empty"}), 413 if data else 400
    tok = hub.token(uid)
    if not hub.enabled() or not tok:
        return jsonify({"error": "no_hub"}), 503
    try:
        r = _requests.post(hub.url() + "/media", params={"uid": uid, "token": tok, "kind": kind}, data=data,
                           headers={"Content-Type": ctype, "User-Agent": "Harmoni"}, timeout=180)
        j = r.json()
    except Exception as e:
        _perf("media", 0, "upload failed " + repr(e)[:120])
        return jsonify({"error": "hub_unreachable"}), 503
    if r.status_code != 200 or not j.get("url"):
        return jsonify({"error": j.get("error") or "failed", "used": j.get("used"), "limit": j.get("limit")}), \
            r.status_code if r.status_code >= 400 else 502
    return jsonify({"ok": True, "url": j["url"], "key": j.get("key"), "used": j.get("used"), "limit": j.get("limit")})


def _media_hub(method, path, uid, body=None):
    tok = hub.token(uid)
    if not hub.enabled() or not tok:
        return jsonify({"error": "no_hub"}), 503
    try:
        if method == "GET":
            r = _requests.get(hub.url() + path, params={"uid": uid, "token": tok}, timeout=20, headers={"User-Agent": "Harmoni"})
        else:
            r = _requests.post(hub.url() + path, json=dict(body or {}, uid=uid, token=tok), timeout=30, headers={"User-Agent": "Harmoni"})
        return jsonify(r.json()), r.status_code
    except Exception:
        return jsonify({"error": "hub_unreachable"}), 503


# ===================== الديكوريشن (حول الصورة مثل ديسكورد) =====================
_DECO_DIR = os.path.join(STATIC_DIR, "decorations")
_DECO_EXT = {".gif": "image/gif", ".png": "image/png", ".apng": "image/apng", ".jpg": "image/jpeg", ".jpeg": "image/jpeg",
             ".webp": "image/webp", ".avif": "image/avif"}
_DECO_CACHE = {"at": 0.0, "list": None}


def _bundled_decorations():
    try:
        names = sorted(n for n in os.listdir(_DECO_DIR) if os.path.splitext(n)[1].lower() in _DECO_EXT)
    except OSError:
        return []
    return [{"id": "b:" + n, "name": os.path.splitext(n)[0], "file": n, "url": "/static/decorations/" + n, "bundled": True}
            for n in names if re.match(r"^[A-Za-z0-9._-]{1,80}$", n)]


def _hub_decorations(force=False):
    if force or _DECO_CACHE["list"] is None or time.time() - _DECO_CACHE["at"] > 60:
        r = hub.call("GET", "/decorations") if hub.available() else None
        if r is not None and r[0] == 200 and isinstance(r[1].get("decorations"), list):
            _DECO_CACHE.update(at=time.time(), list=r[1]["decorations"])
        elif _DECO_CACHE["list"] is None:
            return []
    return list(_DECO_CACHE["list"] or [])


@app.route("/api/decorations")
def api_decorations():
    """المكتبة: اللي على السيرفر المشترك (الكل يشوفها) + اللي داخل البرنامج وما انرفعت بعد."""
    online = _hub_decorations(request.args.get("force") == "1")
    names = {d.get("name") for d in online}
    local = [d for d in _bundled_decorations() if d["name"] not in names]
    return jsonify({"decorations": online + local})


@app.route("/api/decorations/add", methods=["POST"])
def api_decorations_add():
    """المؤسس يضيف ديكوريشن للمكتبة المشتركة (أي صيغة صورة)."""
    uid = request.args.get("uid", "")
    if not uid_authorized(uid, request.headers.get("X-Harmoni-Token", "")):
        return jsonify({"error": "forbidden"}), 403
    tok = hub.token(uid)
    if not hub.enabled() or not tok:
        return jsonify({"error": "no_hub"}), 503
    data = request.get_data(cache=False)
    if not data or len(data) > 20 * 1024 * 1024:
        return jsonify({"error": "too_big" if data else "empty"}), 413 if data else 400
    try:
        r = _requests.post(hub.url() + "/decorations/add", params={"uid": uid, "token": tok, "name": request.args.get("name", "")},
                           data=data, headers={"Content-Type": request.content_type or "", "User-Agent": "Harmoni"}, timeout=120)
        j = r.json()
    except Exception:
        return jsonify({"error": "hub_unreachable"}), 503
    if r.status_code == 200:
        _DECO_CACHE.update(at=time.time(), list=j.get("decorations") or [])
    return jsonify(j), r.status_code


@app.route("/api/decorations/delete", methods=["POST"])
def api_decorations_delete():
    d = request.get_json(silent=True) or {}
    uid = str(d.get("uid") or "")
    if not uid_authorized(uid, request.headers.get("X-Harmoni-Token", "")):
        return jsonify({"error": "forbidden"}), 403
    r = hub.call("POST", "/decorations/delete", {"uid": uid, "token": hub.token(uid) or "", "id": d.get("id")})
    if r is None:
        return jsonify({"error": "hub_unreachable"}), 503
    if r[0] == 200:
        _DECO_CACHE.update(at=time.time(), list=r[1].get("decorations") or [])
    return jsonify(r[1]), r[0]


def _sync_bundled_decorations():
    """جهاز المؤسس: الديكوريشن اللي داخل البرنامج تنرفع للمكتبة المشتركة (عشان تطلع عند الكل حتى بنسخ أقدم)."""
    time.sleep(20)
    bundled = _bundled_decorations()
    if not bundled or not hub.available():
        return
    names = {d.get("name") for d in _hub_decorations(True)}
    todo = [d for d in bundled if d["name"] not in names]
    if not todo:
        return
    with PROFILES_LOCK:
        uids = [u for u, p in PROFILES.items() if isinstance(p, dict) and hub.token(u)]
    for uid in uids:
        for d in todo:
            try:
                with open(os.path.join(_DECO_DIR, d["file"]), "rb") as f:
                    data = f.read()
                r = _requests.post(hub.url() + "/decorations/add",
                                   params={"uid": uid, "token": hub.token(uid), "name": d["name"], "unique": "1"}, data=data,
                                   headers={"Content-Type": _DECO_EXT[os.path.splitext(d["file"])[1].lower()], "User-Agent": "Harmoni"},
                                   timeout=120)
                if r.status_code == 403:
                    break           # مو المؤسس
                if r.status_code == 200:
                    _DECO_CACHE.update(at=time.time(), list=r.json().get("decorations") or [])
            except Exception:
                break
        else:
            return


@app.route("/api/media_usage")
def api_media_usage():
    """كم مستخدم من مساحة الصور (1GB لكل مستخدم)."""
    uid = request.args.get("uid", "")
    if not uid_authorized(uid, request.headers.get("X-Harmoni-Token", "")):
        return jsonify({"error": "forbidden"}), 403
    return _media_hub("GET", "/media/usage", uid)


@app.route("/api/media_delete", methods=["POST"])
def api_media_delete():
    """يحذف ملصقات ما عاد المستخدم يستخدمها (ترجع مساحتها)."""
    d = request.get_json(silent=True) or {}
    uid = str(d.get("uid") or "")
    if not uid_authorized(uid, request.headers.get("X-Harmoni-Token", "") or str(d.get("token") or "")):
        return jsonify({"error": "forbidden"}), 403
    keys = [k for k in (d.get("keys") or []) if isinstance(k, str) and k.startswith(uid + "/")][:200]
    return _media_hub("POST", "/media/delete", uid, {"keys": keys})


@app.route("/api/ytdlp_status")
def api_ytdlp_status():
    return jsonify(ytdlp_live.status())


def start_hub_sync():
    if _HUB["started"] or not hub.enabled():
        return
    _HUB["started"] = True
    threading.Thread(target=_hub_loop, daemon=True, name="harmoni-hub").start()


def current_room(sid):
    link = cloud_rooms.get(sid)
    if link is not None and link.joined:
        return link
    with ROOMS_LOCK:
        rid = SID_ROOM.get(sid)
        return ROOMS.get(rid)


def _leave_current(sid):
    cloud_rooms.drop(sid)
    _leave_local(sid)


def _leave_local(sid, in_request=True):
    with ROOMS_LOCK:
        rid = SID_ROOM.pop(sid, None)
        room = ROOMS.get(rid) if rid else None
    if not rid or room is None:
        return
    try:
        if in_request:
            try:
                leave_room(rid, sid=sid)
            except TypeError:
                leave_room(rid)
        else:
            socketio.server.leave_room(sid, rid, namespace="/")
    except Exception:
        pass
    with room.lock:
        room.members.pop(sid, None)
        if room.owner_sid == sid:
            # المالك طلع: الملكية تنتقل لأقدم عضو عشان أحد يقدر يدير الغرفة
            nxt = next(iter(room.members.values()), None)
            room.owner_sid = nxt["id"] if nxt else None
            if nxt:
                room.owner_uid = nxt.get("uid") or ""
                room.owner_name = nxt.get("name") or room.owner_name
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


@app.route("/bubble")
def bubble_page():
    return send_from_directory(STATIC_DIR, "bubble.html")


@app.route("/panel")
def panel_page():
    return send_from_directory(STATIC_DIR, "panel.html")


# ===================== القائمة الجانبية (Alt+P) =====================
def _panel_config_file():
    return os.path.join(BASE_DIR, "panel_config.json")


def load_panel_config():
    try:
        with open(_panel_config_file(), encoding="utf-8") as f:
            v = json.load(f) or {}
    except Exception:
        v = {}
    return {"enabled": bool(v.get("enabled")), "side": "left" if v.get("side") == "left" else "right"}


def _apply_panel_config(cfg):
    try:
        import side_panel
        side_panel.set_config(cfg)
    except Exception:
        pass


@app.route("/api/panel_config", methods=["GET", "POST"])
def api_panel_config():
    if not _is_local_owner():
        abort(403)
    cfg = load_panel_config()
    if request.method == "POST":
        d = request.get_json(silent=True) or {}
        if "enabled" in d:
            cfg["enabled"] = bool(d.get("enabled"))
        if d.get("side") in ("left", "right"):
            cfg["side"] = d["side"]
        try:
            with open(_panel_config_file(), "w", encoding="utf-8") as f:
                json.dump(cfg, f)
        except Exception:
            pass
        _apply_panel_config(cfg)
    return jsonify(dict(cfg, hotkey="Alt+P"))


@app.route("/api/window_glass", methods=["POST"])
def api_window_glass():
    """الثيم الزجاجي: النافذة الرئيسية والفقاعة والقائمة الجانبية تصير شفافة شوي."""
    if not _is_local_owner():
        abort(403)
    data = request.get_json(silent=True) or {}
    on = bool(data.get("on"))
    color = str(data.get("color") or "#8ab4ff")
    if not re.match(r"^#[0-9a-fA-F]{6}$", color):
        color = "#8ab4ff"
    try:
        alpha = max(0.0, min(1.0, float(data.get("alpha", 0.7))))
        blur = max(0.0, min(100.0, float(data.get("blur", 75))))
    except (TypeError, ValueError):
        alpha, blur = 0.7, 75.0
    native = False
    blur_ok = True
    try:
        import mini_window
        native = bool(mini_window.set_glass(on, color, alpha, blur))
        blur_ok = bool(mini_window.GLASS.get("blur_ok", True))
    except Exception:
        native = False
    # native=True: النافذة نفسها شفافة واللي وراها يبين → الصفحة تخلي خلفيتها شفافة (بس طبقة اللون)
    return jsonify({"ok": True, "on": on, "native": bool(on and native), "blur_ok": blur_ok})


@app.route("/api/open_transparency_settings", methods=["POST"])
def api_open_transparency_settings():
    """يفتح إعدادات ويندوز ← الألوان (فيها «تأثيرات الشفافية») — الضبابية تحتاجها."""
    if not _is_local_owner():
        abort(403)
    try:
        if os.name != "nt":
            return jsonify({"ok": False})
        os.startfile("ms-settings:colors")
        return jsonify({"ok": True})
    except Exception:
        return jsonify({"ok": False})


@app.route("/api/panel_open", methods=["POST"])
def api_panel_open():
    if not _is_local_owner():
        abort(403)
    try:
        import side_panel
        if not side_panel.CONFIG["enabled"]:
            return jsonify({"ok": False, "error": "disabled"})
        threading.Thread(target=side_panel.show, args=("من الإعدادات",), daemon=True).start()
        return jsonify({"ok": True})
    except Exception as e:
        return jsonify({"ok": False, "error": str(e)})


@app.route("/api/bubble_state", methods=["POST"])
def api_bubble_state():
    d = request.get_json(silent=True) or {}
    try:
        from mini_window import push_state

        return jsonify({"cmds": push_state(d.get("state") or None)})
    except Exception:
        return jsonify({"cmds": []})


def _room_of_uid(uid):
    if not uid:
        return None
    link = cloud_rooms.by_uid(uid)
    if link is not None:
        return link
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
    tok = hub.token(owner)
    if tok:
        threading.Thread(target=hub.call, args=("POST", "/remove_follower", {"owner": owner, "token": tok, "target": target}), daemon=True).start()
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


@app.route("/api/diag")
def api_diag():
    """تشخيص (افتحه بالمتصفح على نفس الجهاز): وين البيانات، yt-dlp، محرك JS، وتجربة جلب أغنية فعلياً."""
    if not _is_local_owner():
        abort(403)
    out = []
    add = out.append
    add("platform: %s %s frozen=%s" % (sys.platform, __import__("platform").machine(), bool(getattr(sys, "frozen", False))))
    add("data dir: %s (exists=%s)" % (BASE_DIR, os.path.isdir(BASE_DIR)))
    add("static: %s (exists=%s)" % (STATIC_DIR, os.path.isdir(STATIC_DIR)))
    add("PATH: " + os.environ.get("PATH", ""))
    add("js runtimes: %s  state=%s %s" % (YD_BASE.get("js_runtimes"), _JSRT.get("state"), _JSRT.get("error") or ""))
    add("deno=%s node=%s local_deno=%s" % (shutil.which("deno"), shutil.which("node"), os.path.isfile(_local_deno())))
    try:
        add("yt-dlp: %s live=%s" % (ytdlp_live.current_version(), ytdlp_live.status().get("live")))
    except Exception as e:
        add("yt-dlp: error " + repr(e))
    msgs = []

    class _L:
        def debug(self, m):
            if str(m).startswith("[debug]"):
                return
            msgs.append(str(m))
        info = debug

        def warning(self, m):
            msgs.append("WARNING: " + str(m))

        def error(self, m):
            msgs.append("ERROR: " + str(m))
    test = request.args.get("v") or "jNQXAC9IVRw"
    url = ""
    try:
        opts = dict(YD_BASE)
        opts.update({"quiet": False, "no_warnings": False, "logger": _L(),
                     "format": "bestaudio[ext=m4a]/bestaudio[acodec^=mp4a]/bestaudio/best"})
        t0 = time.time()
        with yt_dlp.YoutubeDL(opts) as ydl:
            info = ydl.extract_info("https://www.youtube.com/watch?v=" + test, download=False)
        url = info.get("url") or ""
        add("extract: OK %.1fs format=%s ext=%s acodec=%s" % (time.time() - t0, info.get("format_id"), info.get("ext"), info.get("acodec")))
    except Exception as e:
        add("extract: FAILED " + repr(e)[:400])
    for m in msgs[-15:]:
        add("  yt-dlp> " + m[:300])
    if url:
        try:
            r = _proxy_session().get(url, headers={"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)",
                                                   "Referer": "https://www.youtube.com/", "Range": "bytes=0-65535"},
                                     timeout=(6, 15))
            add("audio fetch: HTTP %s type=%s bytes=%d" % (r.status_code, r.headers.get("Content-Type"), len(r.content)))
        except Exception as e:
            add("audio fetch: FAILED " + repr(e)[:300])
    try:
        import mini_window as _mw
        with open(_mw.LOG_PATH, encoding="utf-8") as f:
            add("--- mini_window.log (last 20) ---")
            out.extend(f.read().splitlines()[-20:])
    except Exception as e:
        add("window log: " + repr(e)[:120])
    try:
        with open(_PERF_FILE, encoding="utf-8") as f:
            tail = f.read().splitlines()[-25:]
        add("--- harmoni-perf.log (last 25) ---")
        out.extend(tail)
    except Exception as e:
        add("perf log: " + repr(e)[:120])
    _perf("diag", 0, " | ".join(out[:8]))
    return Response("\n".join(out), mimetype="text/plain; charset=utf-8")


@app.route("/api/client_error", methods=["POST"])
def api_client_error():
    """أخطاء JavaScript من الواجهة (static/errbox.js) — تنكتب في harmoni-perf.log للتشخيص."""
    if not _is_local_owner():
        abort(403)
    j = request.get_json(silent=True) or {}
    try:
        _perf("js_error", 0, (str(j.get("msg") or "")[:400] + " | " + str(j.get("ua") or "")[:160]).replace("\n", " "))
    except Exception:
        pass
    return jsonify({"ok": True})


@app.route("/api/focus_main", methods=["POST"])
def api_focus_main():
    """تشغيل ثاني للبرنامج وهو شغال أصلاً: نرجّع النافذة الموجودة بدل ما نفتح نسخة ثانية."""
    if not _is_local_owner():
        abort(403)
    try:
        from mini_window import focus_main
        return jsonify({"ok": bool(focus_main()), "app": "harmoni"})
    except Exception:
        return jsonify({"ok": False, "app": "harmoni"})


def _already_running(port):
    """لو هارموني شغال على نفس المنفذ نطلب منه يطلع نافذته ونرجع True (ونقفل هالنسخة)."""
    try:
        req = urllib.request.Request("http://127.0.0.1:%d/api/focus_main" % port, data=b"{}", method="POST",
                                     headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(req, timeout=3) as r:
            return json.loads(r.read().decode("utf-8") or "{}").get("app") == "harmoni"
    except Exception:
        return False


@app.route("/api/info")
def api_info():
    return jsonify({
        "ip": get_lan_ip(),
        "port": PORT,
        "hostname": socket.gethostname(),
        "tunnel": TUNNEL_URL,
    })


# ===== رابط عام للغرفة: أي أحد حمّل البرنامج يقدر يستضيف غرفة وأصحابه يدخلون من أي مكان =====
# نستخدم نفق Cloudflare السريع المجاني (trycloudflare) — ما يحتاج حساب ولا إعداد. لو cloudflared مو موجود ننزّله مرة وحدة.
_ROOM_TUNNEL = {"state": "idle", "error": "", "progress": 0}
_ROOM_TUNNEL_LOCK = threading.Lock()
_CF_DOWNLOAD = {
    "nt": "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe",
    "posix": "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-amd64",
    "darwin": "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-darwin-%s.tgz",
}


def _download_cloudflared():
    dest_dir = os.path.join(BASE_DIR, "bin")
    os.makedirs(dest_dir, exist_ok=True)
    dest = os.path.join(dest_dir, "cloudflared.exe" if os.name == "nt" else "cloudflared")
    tmp = dest + ".part"
    if sys.platform == "darwin":
        import platform
        url = _CF_DOWNLOAD["darwin"] % ("arm64" if platform.machine() == "arm64" else "amd64")
    else:
        url = _CF_DOWNLOAD["nt" if os.name == "nt" else "posix"]
    with _requests.get(url, stream=True, timeout=(10, 60)) as r:
        r.raise_for_status()
        total = int(r.headers.get("Content-Length") or 0)
        got = 0
        with open(tmp, "wb") as fh:
            for chunk in r.iter_content(262144):
                fh.write(chunk)
                got += len(chunk)
                if total:
                    _ROOM_TUNNEL["progress"] = int(got * 100 / total)
    if url.endswith(".tgz"):   # ماك: الملف مضغوط
        import tarfile
        with tarfile.open(tmp) as tf:
            member = next((m for m in tf.getmembers() if os.path.basename(m.name) == "cloudflared" and m.isfile()), None)
            if member is None:
                raise RuntimeError("cloudflared not in archive")
            with tf.extractfile(member) as src, open(tmp + ".bin", "wb") as out:
                shutil.copyfileobj(src, out)
        os.remove(tmp)
        tmp = tmp + ".bin"
    if os.path.getsize(tmp) < 5_000_000:
        os.remove(tmp)
        raise RuntimeError("download too small")
    os.replace(tmp, dest)
    if os.name != "nt":
        os.chmod(dest, 0o755)
    return dest


def _room_tunnel_job():
    try:
        if not _cloudflared_exe():
            _ROOM_TUNNEL.update(state="downloading", progress=0)
            _download_cloudflared()
        _ROOM_TUNNEL.update(state="starting", progress=100)
        start_tunnel()
        for _ in range(90):
            if TUNNEL_URL:
                _ROOM_TUNNEL.update(state="ready", error="")
                return
            if TUNNEL_PROC is not None and TUNNEL_PROC.poll() is not None:
                break
            time.sleep(0.5)
        _ROOM_TUNNEL.update(state="error", error="tunnel_failed")
    except Exception as e:
        _perf("room-tunnel", 0, "failed " + repr(e)[:150])
        _ROOM_TUNNEL.update(state="error", error="download_failed" if _ROOM_TUNNEL.get("state") == "downloading" else "tunnel_failed")


@app.route("/api/room_tunnel", methods=["GET", "POST"])
def api_room_tunnel():
    """صاحب الجهاز يطلب رابط عام لغرفته (عشان الدعوة تفتح من أي مكان، مو بس من نفس الشبكة)."""
    if not _is_local_owner():
        return jsonify({"error": "local_only"}), 403
    global TUNNEL_PROC
    if request.method == "POST" and not TUNNEL_URL:
        with _ROOM_TUNNEL_LOCK:
            if TUNNEL_PROC is not None and TUNNEL_PROC.poll() is not None:
                TUNNEL_PROC = None   # نفق قديم مات — نشغّل واحد جديد
            if _ROOM_TUNNEL["state"] not in ("downloading", "starting"):
                _ROOM_TUNNEL.update(state="starting", error="", progress=0)
                threading.Thread(target=_room_tunnel_job, daemon=True).start()
    st = dict(_ROOM_TUNNEL)
    if TUNNEL_URL:
        st.update(state="ready", error="")
    st["url"] = TUNNEL_URL
    return jsonify(st)


_ROOM_LINK_RE = re.compile(r"^https://[A-Za-z0-9.-]+(?::\d+)?/\?(?:[^#\s]*&)?room=\d{6}(?:[&#][^\s]*)?$")


@app.route("/api/open_room", methods=["POST"])
def api_open_room():
    """يفتح غرفة صاحبك (على جهازه) في نافذة هارموني منفصلة."""
    if not _is_local_owner():
        return jsonify({"error": "local_only"}), 403
    url = str((request.get_json(silent=True) or {}).get("url") or "").strip()
    if len(url) > 500 or not _ROOM_LINK_RE.match(url):
        return jsonify({"error": "bad_link"}), 400
    try:
        from mini_window import open_room_window
        if open_room_window(url):
            return jsonify({"ok": True, "window": True})
    except Exception as e:
        _perf("open-room", 0, repr(e)[:120])
    try:
        import webbrowser
        webbrowser.open(url)
        return jsonify({"ok": True, "window": False})
    except Exception:
        return jsonify({"error": "open_failed"}), 500


# ===== الأغاني المقيدة بالعمر: بدون أي حساب — نشغّل النسخة الصوتية الرسمية (يوتيوب ميوزك) بدالها =====
_AGE_RE = re.compile(r"confirm your age|age[- ]?restrict|inappropriate for some users|age[- ]?gate|age verification", re.I)
_AGE_URLS = set()   # روابط عرفنا إنها مقيدة بالعمر (تنحفظ بالجهاز وما تنعرض بالبحث أبداً)
_AGE_FILE = os.path.join(BASE_DIR, "age_blocked.json")
try:
    with open(_AGE_FILE, encoding="utf-8-sig") as _f:
        _AGE_URLS.update(u for u in (json.load(_f) or []) if isinstance(u, str))
except Exception:
    pass


def _mark_age(web_url):
    if not web_url or web_url in _AGE_URLS:
        return
    _AGE_URLS.add(web_url)
    try:
        tmp = _AGE_FILE + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(sorted(_AGE_URLS)[-5000:], f)
        os.replace(tmp, _AGE_FILE)
    except Exception:
        pass
    _perf("age", 0, "age-restricted → hidden: " + web_url)


_AGE_FILTER_PATHS = ("/api/search", "/api/suggest", "/api/artist_songs", "/api/spotify_public/sync",
                     "/api/spotify_import", "/api/related")


@app.after_request
def _hide_age_restricted(resp):
    """الأغاني المقيدة بالعمر ما تنعرض أبداً: نشيلها من نتائج البحث والاقتراحات وصفحات الفنانين."""
    try:
        if (_AGE_URLS and request.path.startswith(_AGE_FILTER_PATHS) and resp.mimetype == "application/json"
                and not resp.direct_passthrough):
            data = resp.get_json(silent=True)
            if isinstance(data, dict):
                changed = False
                for k in ("results", "tracks", "songs", "likes"):
                    v = data.get(k)
                    if isinstance(v, list):
                        kept = [t for t in v if not (isinstance(t, dict) and t.get("web_url") in _AGE_URLS)]
                        if len(kept) != len(v):
                            data[k] = kept
                            changed = True
                if changed:
                    resp.set_data(json.dumps(data, ensure_ascii=False))
    except Exception:
        pass
    return resp


def _is_age_error(err):
    return bool(_AGE_RE.search(str(err or "")))


def _cleanup_old_youtube_link():
    """ميزة ربط يوتيوب انشالت: نحذف أي بيانات ربط قديمة من الجهاز."""
    for name in ("youtube_link.bin", "youtube_link.json", "youtube_cookies.txt", "youtube_link.bin.tmp", "youtube_link.json.tmp"):
        try:
            os.remove(os.path.join(BASE_DIR, name))
        except OSError:
            pass
    try:
        for name in os.listdir(BASE_DIR):
            if name.startswith("yt-login-"):
                shutil.rmtree(os.path.join(BASE_DIR, name), ignore_errors=True)
    except OSError:
        pass


_cleanup_old_youtube_link()


def resolve_stream_url(web_url, source="youtube", title="", artist=""):
    if web_url in _AGE_URLS:
        return None   # نعرف إنها مقيدة: ما نضيع وقت على يوتيوب
    track = {"web_url": web_url, "source": source, "title": title, "artist": artist}
    stream_url = None
    age = False
    try:
        stream_url, _ = pick_stream_url(_extract_base(web_url, fast=True))
    except Exception as e:
        stream_url = None
        age = _is_age_error(e)
    if not stream_url:
        try:
            # Live/manifest-only media still uses the full compatible extractor.
            stream_url, _ = pick_stream_url(_extract_base(web_url))
        except Exception as e:
            age = age or _is_age_error(e)
    if not stream_url and age and web_url:
        # مقيدة بالعمر: نحفظها (ما تنعرض بعدين) والنسخة الرسمية من يوتيوب ميوزك تتولاها لو موجودة
        _mark_age(web_url)
        return None
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


_PROXY_HOSTS = ("googlevideo.com", "youtube.com", "ytimg.com", "sndcdn.com", "soundcloud.com",
                "soundcloud.cloud", "scdn.co", "spotifycdn.com", "dzcdn.net")


def _proxy_allowed(url):
    """ممر الصوت يقبل مواقع الصوت المعروفة بس (ما يصير وسيط لأي موقع)."""
    try:
        host = (urllib.parse.urlparse(url).hostname or "").lower()
    except Exception:
        return False
    ok = any(host == h or host.endswith("." + h) for h in _PROXY_HOSTS)
    if not ok:
        _perf("blocked", 0, host)
    return ok


_PROXY_SESSION = [None]
_PROXY_SESSION_LOCK = threading.Lock()


def _proxy_session():
    with _PROXY_SESSION_LOCK:
        if _PROXY_SESSION[0] is None:
            sess = _requests.Session()
            if sys.platform == "darwin":
                # الماك: requests يسأل إعدادات البروكسي حق النظام مع كل طلب (ويسوي بحث DNS) — كل جزء من الأغنية
                # كان يتأخر. نستخدم بس بروكسي المتغيرات لو موجود.
                sess.trust_env = False
                env_px = urllib.request.getproxies_environment()
                if env_px:
                    sess.proxies.update({k: v for k, v in env_px.items() if k in ("http", "https")})
            adapter = _IPv4Adapter(pool_connections=4, pool_maxsize=16)
            sess.mount("https://", adapter)
            sess.mount("http://", adapter)
            _PROXY_SESSION[0] = sess
        return _PROXY_SESSION[0]


# أول جزء من كل أغنية (≈ 384KB) نحفظه بالذاكرة أول ما نجهز رابطها:
# كذا أول طلب من الجوال يرد فوراً من جهازك بدل ما ننتظر يوتيوب (يوفر تقريباً نص ثانية لكل أغنية)
_HEAD_CACHE = {}
_HEAD_LOCK = threading.Lock()
_HEAD_BYTES = 384 * 1024
_HEAD_EXEC = concurrent.futures.ThreadPoolExecutor(max_workers=4)


def _head_get(url):
    with _HEAD_LOCK:
        return _HEAD_CACHE.get(url)


def _warm_head(url):
    if not url or not url.startswith("http") or "googlevideo" not in url or _head_get(url):
        return
    try:
        r = _proxy_session().get(url, headers={"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)",
                                               "Referer": "https://www.youtube.com/",
                                               "Range": "bytes=0-%d" % (_HEAD_BYTES - 1)}, timeout=(6, 15))
        m = re.match(r"bytes 0-(\d+)/(\d+)", r.headers.get("Content-Range", ""))
        if r.status_code != 206 or not m:
            return
        data = r.content
        with _HEAD_LOCK:
            _HEAD_CACHE[url] = (data, int(m.group(2)), r.headers.get("Content-Type") or "audio/mp4", time.time())
            if len(_HEAD_CACHE) > 48:
                for k, _ in sorted(_HEAD_CACHE.items(), key=lambda kv: kv[1][3])[:12]:
                    _HEAD_CACHE.pop(k, None)
    except Exception:
        pass


def warm_head_soon(url):
    try:
        if url and "googlevideo" in url and not _head_get(url):
            _HEAD_EXEC.submit(_warm_head, url)
    except Exception:
        pass


def _head_response(url, rng):
    """طلب يبدأ من أول الملف وعندنا أوله بالذاكرة → نرد على طول (206)."""
    ent = _head_get(url)
    m = re.match(r"^bytes=0-(\d*)$", (rng or "").strip())
    if not ent or not m:
        return None
    data, total, ctype, _ = ent
    end = len(data) - 1
    if m.group(1):
        end = min(end, int(m.group(1)))
    body = data[:end + 1]
    resp = Response(body, status=206, mimetype=ctype)
    resp.headers["Content-Range"] = "bytes 0-%d/%d" % (end, total)
    resp.headers["Content-Length"] = str(len(body))
    resp.headers["Accept-Ranges"] = "bytes"
    return resp


@app.route("/api/audio_proxy")
def api_audio_proxy():
    url = request.args.get("url", "")
    if not url.startswith("http") or not _proxy_allowed(url):
        abort(400)
    fwd = {"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)", "Referer": "https://www.youtube.com/"}
    rng = request.headers.get("Range")
    fast = _head_response(url, rng)
    if fast is not None:
        return fast
    if rng:
        fwd["Range"] = rng
    # جلسة مشتركة (اتصالات مفتوحة يعاد استخدامها): التقديم ما يحتاج اتصال TLS جديد كل مرة = أسرع بكثير
    session = _proxy_session()
    try:
        up = session.get(url, headers=fwd, stream=True, timeout=(8, 25))
    except Exception:
        try:
            up = session.get(url, headers=fwd, stream=True, timeout=(8, 25))   # محاولة ثانية (اتصال قديم انقطع)
        except Exception:
            return jsonify({"error": "upstream_failed"}), 502

    def chunks():
        try:
            yield from up.iter_content(chunk_size=65536)
        except Exception:
            return
        finally:
            up.close()
    resp = Response(stream_with_context(chunks()),
                    status=up.status_code)
    resp.call_on_close(up.close)
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
_STREAM_CACHE_FILE = os.path.join(BASE_DIR, "stream_cache.json")
_PERF_FILE = os.path.join(BASE_DIR, "harmoni-perf.log")
_PERF_LOCK = threading.Lock()


def _perf(kind, secs, info=""):
    """سجل سرعة: كم أخذ البحث/تجهيز الأغنية (harmoni-perf.log)."""
    try:
        with _PERF_LOCK:
            if os.path.exists(_PERF_FILE) and os.path.getsize(_PERF_FILE) > 1_000_000:
                os.replace(_PERF_FILE, _PERF_FILE + ".old")
            with open(_PERF_FILE, "a", encoding="utf-8") as f:
                f.write("%s %-8s %6.2fs %s\n" % (time.strftime("%Y-%m-%d %H:%M:%S"), kind, secs, str(info)[:160]))
    except Exception:
        pass


def _stream_ttl(url):
    """صلاحية رابط يوتيوب من باراميتر expire (مع هامش أمان)."""
    try:
        m = re.search(r"[?&/]expire[=/](\d{9,11})", url or "")
        if m:
            left = int(m.group(1)) - time.time() - 15 * 60
            return max(0, min(left, 5 * 3600))
    except Exception:
        pass
    return _STREAM_TTL


def _load_stream_cache():
    try:
        with open(_STREAM_CACHE_FILE, encoding="utf-8") as f:
            data = json.load(f) or {}
        now = time.time()
        with _STREAM_CACHE_LOCK:
            for k, v in data.items():
                url, ts, exp = v[0], float(v[1]), float(v[2])
                if exp > now:
                    _STREAM_CACHE[k] = (url, ts, exp)
    except Exception:
        pass


_STREAM_SAVE_TIMER = [None]


def _save_stream_cache_soon():
    def save():
        try:
            with _STREAM_CACHE_LOCK:
                data = {k: list(v) for k, v in _STREAM_CACHE.items() if len(v) == 3}
            tmp = _STREAM_CACHE_FILE + ".tmp"
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump(data, f)
            os.replace(tmp, _STREAM_CACHE_FILE)
        except Exception:
            pass
    t = _STREAM_SAVE_TIMER[0]
    if t:
        t.cancel()
    t = threading.Timer(3.0, save)
    t.daemon = True
    _STREAM_SAVE_TIMER[0] = t
    t.start()

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
        # المقاطع المقيدة بالعمر ما ننزلها للكاش (عشان ما نستهلك الحساب المربوط) — تشتغل بث مباشر بس
        if web_url in _AGE_URLS:
            raise RuntimeError("age-restricted: no cache download")
        with yt_dlp.YoutubeDL(ydl_opts) as ydl:
            info = ydl.extract_info(web_url, download=True)
            final = ydl.prepare_filename(info)
        if not os.path.isfile(final):
            raise RuntimeError("download produced no file")
        with _AUDIO_DL_LOCK:
            _AUDIO_FILES[key] = {"path": final, "ready": True, "ts": time.time()}
        _prune_audio_cache()
    except Exception as e:
        if _is_age_error(e):
            _mark_age(web_url)    # كان يعيد المحاولة كل مرة على الفاضي
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


_AUDIO_CACHE_MAX = 700 * 1024 * 1024   # الحد الافتراضي للأغاني المحفوظة (يتغير من الإعدادات)
_CACHE_CFG_FILE = os.path.join(BASE_DIR, "cache_config.json")
_CACHE_LIMITS = (0, 300, 700, 2000)    # ميقا — 0 = بدون حفظ
_MIN_FREE_DISK = 2 * 1024 ** 3         # لو القرص باقي فيه أقل من 2GB ما نحفظ أغاني


def _cache_limit_mb():
    try:
        with open(_CACHE_CFG_FILE, encoding="utf-8-sig") as f:
            v = int(json.load(f).get("limit_mb"))
        return v if v in _CACHE_LIMITS else 700
    except Exception:
        return 700


def _cache_max_bytes():
    return _cache_limit_mb() * 1024 * 1024


def _disk_free():
    try:
        return shutil.disk_usage(_AUDIO_CACHE_DIR).free
    except Exception:
        return 1 << 62


def _cache_usage():
    total = n = 0
    try:
        for name in os.listdir(_AUDIO_CACHE_DIR):
            if os.path.splitext(name)[1].lower() in _AUDIO_EXTS:
                total += os.path.getsize(os.path.join(_AUDIO_CACHE_DIR, name))
                n += 1
    except Exception:
        pass
    return total, n


@app.route("/api/audio_cache", methods=["GET", "POST"])
def api_audio_cache():
    """الأغاني المحفوظة بالجهاز (للتشغيل الفوري): الحجم، الحد، ومسحها."""
    if request.method == "POST":
        if not _is_local_owner():
            return jsonify({"error": "forbidden"}), 403
        d = request.get_json(silent=True) or {}
        if "limit_mb" in d:
            try:
                v = int(d.get("limit_mb"))
            except Exception:
                v = 700
            if v not in _CACHE_LIMITS:
                v = 700
            try:
                with open(_CACHE_CFG_FILE, "w", encoding="utf-8") as f:
                    json.dump({"limit_mb": v}, f)
            except Exception:
                pass
            threading.Thread(target=_prune_audio_cache, daemon=True).start()
        if d.get("clear"):
            try:
                for name in os.listdir(_AUDIO_CACHE_DIR):
                    if os.path.splitext(name)[1].lower() in _AUDIO_EXTS:
                        try:
                            os.remove(os.path.join(_AUDIO_CACHE_DIR, name))
                        except OSError:
                            pass
                with _AUDIO_DL_LOCK:
                    for k in [k for k, v in _AUDIO_FILES.items() if v.get("ready")]:
                        _AUDIO_FILES.pop(k, None)
            except Exception:
                pass
    total, n = _cache_usage()
    return jsonify({"limit_mb": _cache_limit_mb(), "used_mb": round(total / 1048576, 1), "count": n,
                    "free_gb": round(_disk_free() / 1024 ** 3, 1), "options": list(_CACHE_LIMITS),
                    "paused": _disk_free() < _MIN_FREE_DISK})
_AUDIO_EXTS = (".m4a", ".webm", ".mp3", ".opus", ".ogg")


def _scan_audio_cache():
    """بعد إعادة التشغيل: نرجّع فهرس الأغاني المحفوظة على الجهاز."""
    try:
        for name in os.listdir(_AUDIO_CACHE_DIR):
            base, ext = os.path.splitext(name)
            if ext.lower() in _AUDIO_EXTS and len(base) == 40:
                path = os.path.join(_AUDIO_CACHE_DIR, name)
                with _AUDIO_DL_LOCK:
                    _AUDIO_FILES.setdefault(base, {"path": path, "ready": True, "ts": os.path.getmtime(path)})
    except Exception:
        pass


def _prune_audio_cache():
    try:
        files = []
        for name in os.listdir(_AUDIO_CACHE_DIR):
            path = os.path.join(_AUDIO_CACHE_DIR, name)
            if os.path.splitext(name)[1].lower() in _AUDIO_EXTS:
                files.append((os.path.getatime(path), os.path.getsize(path), path, os.path.splitext(name)[0]))
        total = sum(f[1] for f in files)
        cap = _cache_max_bytes()
        for atime, size, path, key in sorted(files):
            if total <= cap:
                break
            try:
                os.remove(path)
                total -= size
                with _AUDIO_DL_LOCK:
                    _AUDIO_FILES.pop(key, None)
            except OSError:
                pass
    except Exception:
        pass


_AUDIO_QUEUE = []
_AUDIO_QUEUE_LOCK = threading.Lock()
_AUDIO_WORKER = [False]


def _audio_worker():
    while True:
        with _AUDIO_QUEUE_LOCK:
            if not _AUDIO_QUEUE:
                _AUDIO_WORKER[0] = False
                return
            web_url = _AUDIO_QUEUE.pop(0)
            if _cache_limit_mb() == 0 or _disk_free() < _MIN_FREE_DISK:
                # الحفظ مطفي أو القرص قرب يمتلي: ما نحمّل شي (الأغاني تشتغل بث عادي)
                _AUDIO_QUEUE.clear()
                _AUDIO_WORKER[0] = False
                return
        key = _audio_key(web_url)
        with _AUDIO_DL_LOCK:
            if key in _AUDIO_FILES:
                continue
        _AUDIO_DOWNLOAD_SLOTS.acquire()
        with _AUDIO_DL_LOCK:
            _AUDIO_FILES[key] = {"path": os.path.join(_AUDIO_CACHE_DIR, key + ".m4a"), "ready": False, "ts": time.time()}
        t0 = time.time()
        _download_audio(key, web_url)   # يحرر الـ slot بنفسه
        _perf("download", time.time() - t0, web_url)


@app.route("/api/cache_audio", methods=["POST"])
def api_cache_audio():
    """يحفظ صوت المفضلة وآخر الأغاني على الجهاز بالخلفية (تشغيل فوري بعدين)."""
    data = request.get_json(silent=True) or {}
    added = 0
    with _AUDIO_QUEUE_LOCK:
        for trk in (data.get("tracks") or [])[:60]:
            url = (trk or {}).get("web_url") or ""
            if not re.match(r"^https?://(www\.|m\.|music\.)?(youtube\.com|youtu\.be|soundcloud\.com)/", url):
                continue
            if url in _AGE_URLS:
                continue
            key = _audio_key(url)
            with _AUDIO_DL_LOCK:
                have = key in _AUDIO_FILES
            if not have and url not in _AUDIO_QUEUE:
                _AUDIO_QUEUE.append(url)
                added += 1
        start = added and not _AUDIO_WORKER[0]
        if start:
            _AUDIO_WORKER[0] = True
    if start:
        threading.Thread(target=_audio_worker, daemon=True).start()
    return jsonify({"queued": added})


_PREFETCH_EXEC = concurrent.futures.ThreadPoolExecutor(max_workers=6)
_PREFETCH_SEEN = {}


@app.route("/api/prefetch", methods=["POST"])
def api_prefetch():
    """يجهّز روابط تشغيل أول النتائج بالخلفية عشان الضغطة تشتغل فوراً."""
    data = request.get_json(silent=True) or {}
    return jsonify({"queued": _prefetch_tracks(data.get("tracks") or [])})


_PREFETCH_ON_SEARCH = not os.path.basename(sys.argv[0] or "").startswith("test_")


def _search_prefetch(results):
    """أول نتائج البحث تتجهز على طول (قبل حتى ما توصل للجوال)."""
    if _PREFETCH_ON_SEARCH:
        try:
            _prefetch_tracks([r for r in (results or [])[:6] if isinstance(r, dict) and r.get("kind") != "playlist"])
        except Exception:
            pass


def _prefetch_tracks(tracks):
    n = 0
    now = time.time()
    for trk in (tracks or [])[:10]:
        if not isinstance(trk, dict):
            continue
        url = (trk or {}).get("web_url") or ""
        if not url.startswith("http") or "spotify.com" in url or url in _AGE_URLS:
            continue
        if now - _PREFETCH_SEEN.get(url, 0) < 600:
            continue
        with _AUDIO_DL_LOCK:
            ent = _AUDIO_FILES.get(_audio_key(url))
        if ent and ent.get("ready"):
            continue
        _PREFETCH_SEEN[url] = now
        n += 1

        def job(u=url, t=trk):
            t0 = time.time()
            try:
                cached_stream(u, t.get("source") or "youtube", t.get("title") or "", t.get("artist") or "")
                _perf("prefetch", time.time() - t0, t.get("title") or u)
            except Exception:
                pass
        _PREFETCH_EXEC.submit(job)
    if len(_PREFETCH_SEEN) > 2000:
        _PREFETCH_SEEN.clear()
    return n


# المغني الحقيقي للأغنية (للإحصائيات) — منقول لملف مستقل: harmoni_artists.py
import harmoni_artists
harmoni_artists.init(BASE_DIR)
app.register_blueprint(harmoni_artists.bp)
resolve_real_artist = harmoni_artists.resolve_real_artist

# التحديث التلقائي من GitHub Releases — harmoni_updater.py
import harmoni_updater
harmoni_updater.init(_BUNDLE_DIR, lambda: _is_local_owner(), lambda msg: _perf("update", 0, msg), lambda: os._exit(0))
app.register_blueprint(harmoni_updater.bp)

# Harmoni Connect: التحكم من الجوال عن طريق QR — remote.py
import remote as harmoni_remote
harmoni_remote.init(BASE_DIR, STATIC_DIR, lambda: _is_local_owner(),
                    lambda: {"ip": get_lan_ip(), "port": PORT, "tunnel": TUNNEL_URL})
app.register_blueprint(harmoni_remote.bp)


@app.route("/api/bubble_config", methods=["GET", "POST"])
def api_bubble_config():
    """إعدادات الفقاعة (مكانها فوق: يسار/وسط/يمين، تثبيتها، الظهور التلقائي)."""
    try:
        import mini_window
    except Exception:
        return jsonify({"error": "unavailable"}), 503
    if request.method == "POST":
        if not _is_local_owner():
            return jsonify({"error": "forbidden"}), 403
        return jsonify(mini_window.set_bubble_config(request.get_json(silent=True) or {}))
    return jsonify(mini_window.bubble_config())


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


def _is_webm_stream(url):
    u = urllib.parse.unquote(str(url or "")).lower()
    return "googlevideo" in u and "mime=audio/webm" in u


def cached_stream(web_url, source="youtube", title="", artist="", refresh=False):
    with _STREAM_CACHE_LOCK:
        hit = _STREAM_CACHE.get(web_url)
        if hit and _is_webm_stream(hit[0]):
            hit = None   # روابط قديمة (webm) من قبل التعديل — نجيب m4a بدالها
        if hit and not refresh:
            exp = hit[2] if len(hit) > 2 else hit[1] + _STREAM_TTL
            if time.time() < exp:
                warm_head_soon(hit[0])
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
            warm_head_soon(stream)
            now = time.time()
            with _STREAM_CACHE_LOCK:
                _STREAM_CACHE[web_url] = (stream, now, now + _stream_ttl(stream))
                if len(_STREAM_CACHE) > 600:
                    for key, _ in sorted(_STREAM_CACHE.items(), key=lambda item: item[1][1])[:200]:
                        _STREAM_CACHE.pop(key, None)
            _save_stream_cache_soon()
        pending.set_result(stream)
        return stream
    except Exception as error:
        pending.set_exception(error)
        raise
    finally:
        with _STREAM_CACHE_LOCK:
            _STREAM_INFLIGHT.pop(web_url, None)


_FAIL_STREAK = []


# ===== الأغاني المحمّلة (تشتغل بدون نت) =====
# غير «الكاش» اللي يتمسح لحاله: هذي يختارها المستخدم (أغنية / بلاي لست / المفضلة) وتبقى لين يحذفها.
# downloads/<key>.m4a + <key>.jpg (الغلاف) + downloads.json (القائمة)
_DL_DIR = os.path.join(BASE_DIR, "downloads")
_DL_INDEX = os.path.join(_DL_DIR, "downloads.json")
_DL_LOCK = threading.Lock()
_DL = {"items": {}, "queue": [], "cur": None, "pct": 0.0, "thread": None, "loaded": False, "error": ""}
_DL_MIN_FREE = 1024 ** 3   # نوقف التحميل لو القرص باقي فيه أقل من 1GB


def _dl_tkey(title, artist):
    return (re.sub(r"\s+", " ", str(artist or "")).strip() + "|" + re.sub(r"\s+", " ", str(title or "")).strip()).lower()


def _dl_load():
    if _DL["loaded"]:
        return
    _DL["loaded"] = True
    try:
        with open(_DL_INDEX, encoding="utf-8") as f:
            items = json.load(f).get("items") or {}
        for k, v in items.items():
            if isinstance(v, dict) and v.get("file") and os.path.isfile(os.path.join(_DL_DIR, v["file"])):
                _DL["items"][k] = v
    except Exception:
        pass


def _dl_save():
    try:
        os.makedirs(_DL_DIR, exist_ok=True)
        tmp = _DL_INDEX + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump({"items": _DL["items"]}, f, ensure_ascii=False)
        os.replace(tmp, _DL_INDEX)
    except Exception:
        pass


def _dl_find(web_url="", title="", artist=""):
    """أغنية محمّلة؟ (بالرابط، أو بالاسم+الفنان لو ما فيه رابط)"""
    with _DL_LOCK:
        _dl_load()
        if web_url:
            it = _DL["items"].get(_audio_key(web_url))
            if it:
                return it
        tk = _dl_tkey(title, artist) if title else ""
        if tk:
            for it in _DL["items"].values():
                if it.get("tkey") == tk:
                    return it
    return None


def _dl_public(it):
    return {"key": it.get("key"), "web_url": it.get("web_url"), "orig_url": it.get("orig_url") or it.get("web_url"),
            "title": it.get("title"), "artist": it.get("artist"), "duration": it.get("duration"),
            "thumbnail": it.get("thumbnail"), "size": it.get("size", 0), "ts": it.get("ts", 0),
            "thumb_local": bool(it.get("thumb_file"))}


def _dl_worker():
    while True:
        with _DL_LOCK:
            if not _DL["queue"]:
                _DL["cur"] = None
                _DL["thread"] = None
                return
            job = _DL["queue"].pop(0)
            _DL["cur"] = job
            _DL["pct"] = 0.0
        try:
            _dl_one(job)
            _DL["error"] = ""
        except Exception as e:
            _DL["error"] = "%s: %s" % (job.get("title") or "", str(e)[:160])
            _perf("download", 0, "failed " + str(job.get("title") or "") + " " + repr(e)[:120])


def _dl_one(job):
    if _disk_free() < _DL_MIN_FREE:
        raise RuntimeError("disk almost full")
    os.makedirs(_DL_DIR, exist_ok=True)
    web_url = job.get("web_url") or ""
    if not web_url:
        found = search((str(job.get("artist") or "") + " " + str(job.get("title") or "")).strip(), "youtube", 1)
        web_url = (found[0].get("web_url") if found else "") or ""
        if not web_url:
            raise RuntimeError("song not found")
    if web_url in _AGE_URLS:
        raise RuntimeError("age-restricted")
    key = _audio_key(web_url)
    if _dl_find(web_url):
        return
    t0 = time.time()

    def hook(d):
        try:
            if d.get("status") == "downloading":
                tot = d.get("total_bytes") or d.get("total_bytes_estimate") or 0
                if tot:
                    _DL["pct"] = round(min(99.0, d.get("downloaded_bytes", 0) * 100.0 / tot), 1)
        except Exception:
            pass
    opts = {**YD_BASE,
            "format": "bestaudio[ext=m4a]/bestaudio[acodec^=mp4a]/bestaudio/best",
            "outtmpl": os.path.join(_DL_DIR, key + ".%(ext)s"),
            "quiet": True, "no_warnings": True, "noplaylist": True, "socket_timeout": 30,
            "skip_download": False, "progress_hooks": [hook], "retries": 3}
    # الأغنية محفوظة أصلاً بالكاش؟ ننسخها بدل ما ننزلها مرة ثانية
    ent = _audio_entry(key)
    fname = ""
    if ent and ent.get("ready") and os.path.isfile(ent.get("path") or ""):
        fname = key + os.path.splitext(ent["path"])[1]
        shutil.copy2(ent["path"], os.path.join(_DL_DIR, fname))
    else:
        try:
            with yt_dlp.YoutubeDL(opts) as ydl:
                info = ydl.extract_info(web_url, download=True)
                path = ydl.prepare_filename(info)
        except Exception as e:
            if _is_age_error(e):
                _mark_age(web_url)
            raise
        if not os.path.isfile(path):
            raise RuntimeError("download produced no file")
        fname = os.path.basename(path)
    thumb = str(job.get("thumbnail") or "")
    thumb_file = ""
    if thumb.startswith("http"):
        try:
            r = _requests.get(thumb, timeout=10, headers={"User-Agent": "Mozilla/5.0"})
            if r.ok and len(r.content) < 3 * 1024 * 1024 and (r.headers.get("Content-Type") or "").startswith("image/"):
                thumb_file = key + ".jpg"
                with open(os.path.join(_DL_DIR, thumb_file), "wb") as f:
                    f.write(r.content)
        except Exception:
            pass
    it = {"key": key, "web_url": web_url, "orig_url": job.get("web_url") or "", "title": job.get("title") or "",
          "artist": job.get("artist") or "", "duration": job.get("duration") or 0, "thumbnail": thumb,
          "source": job.get("source") or "youtube", "file": fname, "thumb_file": thumb_file,
          "tkey": _dl_tkey(job.get("title"), job.get("artist")),
          "size": os.path.getsize(os.path.join(_DL_DIR, fname)), "ts": time.time()}
    with _DL_LOCK:
        _DL["items"][key] = it
        if job.get("web_url") and _audio_key(job["web_url"]) != key:
            _DL["items"][key]["orig_url"] = job["web_url"]
        _dl_save()
    _perf("download", time.time() - t0, "ok " + str(job.get("title") or web_url))


def _dl_status():
    with _DL_LOCK:
        _dl_load()
        items = sorted((_dl_public(v) for v in _DL["items"].values()), key=lambda x: -(x.get("ts") or 0))
        cur = _DL["cur"]
        return {"items": items, "count": len(items),
                "total_mb": round(sum(x.get("size") or 0 for x in items) / 1048576, 1),
                "queue": [{"title": j.get("title"), "artist": j.get("artist"), "web_url": j.get("web_url")} for j in _DL["queue"]],
                "current": ({"title": cur.get("title"), "artist": cur.get("artist"), "web_url": cur.get("web_url"),
                             "pct": _DL["pct"]} if cur else None),
                "error": _DL["error"], "free_gb": round(_disk_free() / 1024 ** 3, 1)}


@app.route("/api/downloads")
def api_downloads():
    return jsonify(_dl_status())


@app.route("/api/downloads/add", methods=["POST"])
def api_downloads_add():
    if not _is_local_owner():
        return jsonify({"error": "forbidden"}), 403
    d = request.get_json(silent=True) or {}
    tracks = d.get("tracks") if isinstance(d.get("tracks"), list) else []
    added = have = skipped = 0
    with _DL_LOCK:
        _dl_load()
        queued = {(j.get("web_url") or _dl_tkey(j.get("title"), j.get("artist"))) for j in _DL["queue"]}
        if _DL["cur"]:
            queued.add(_DL["cur"].get("web_url") or _dl_tkey(_DL["cur"].get("title"), _DL["cur"].get("artist")))
    for t in tracks[:2000]:
        if not isinstance(t, dict) or not t.get("title"):
            skipped += 1
            continue
        url = str(t.get("web_url") or "")
        if url.startswith("local:") or (url and not url.startswith("http")):
            skipped += 1   # أغانيك المرفوعة أصلاً على جهازك
            continue
        if _dl_find(url, t.get("title"), t.get("artist")):
            have += 1
            continue
        ident = url or _dl_tkey(t.get("title"), t.get("artist"))
        if ident in queued:
            have += 1
            continue
        queued.add(ident)
        job = {"web_url": url, "title": str(t.get("title"))[:300], "artist": str(t.get("artist") or "")[:300],
               "duration": t.get("duration") or 0, "thumbnail": str(t.get("thumbnail") or "")[:1000]
               if not str(t.get("thumbnail") or "").startswith("data:") else "",
               "source": str(t.get("source") or "youtube")[:20]}
        with _DL_LOCK:
            _DL["queue"].append(job)
        added += 1
    with _DL_LOCK:
        if _DL["queue"] and _DL["thread"] is None:
            _DL["thread"] = threading.Thread(target=_dl_worker, daemon=True, name="harmoni-downloads")
            _DL["thread"].start()
    return jsonify(dict(_dl_status(), added=added, already=have, skipped=skipped))


@app.route("/api/downloads/remove", methods=["POST"])
def api_downloads_remove():
    if not _is_local_owner():
        return jsonify({"error": "forbidden"}), 403
    d = request.get_json(silent=True) or {}
    urls = [str(u) for u in (d.get("web_urls") or []) if u][:5000]
    keys = set(str(k) for k in (d.get("keys") or []) if k)
    with _DL_LOCK:
        _dl_load()
        if d.get("all"):
            keys |= set(_DL["items"].keys())
            _DL["queue"] = []
        for u in urls:
            keys.add(_audio_key(u))
            for k, it in _DL["items"].items():
                if it.get("orig_url") == u:
                    keys.add(k)
        _DL["queue"] = [j for j in _DL["queue"] if (j.get("web_url") or "") not in urls]
        for k in keys:
            it = _DL["items"].pop(k, None)
            if not it:
                continue
            for fn in (it.get("file"), it.get("thumb_file")):
                if fn:
                    try:
                        os.remove(os.path.join(_DL_DIR, fn))
                    except OSError:
                        pass
        _dl_save()
    return jsonify(_dl_status())


@app.route("/api/downloads/cancel", methods=["POST"])
def api_downloads_cancel():
    if not _is_local_owner():
        return jsonify({"error": "forbidden"}), 403
    with _DL_LOCK:
        _DL["queue"] = []
    return jsonify(_dl_status())


def _dl_by_key(k):
    if not re.match(r"^[0-9a-f]{40}$", k or ""):
        return None
    with _DL_LOCK:
        _dl_load()
        return _DL["items"].get(k)


@app.route("/api/downloads/file")
def api_downloads_file():
    it = _dl_by_key(request.args.get("k", ""))
    if not it:
        abort(404)
    p = os.path.join(_DL_DIR, it["file"])
    if not os.path.isfile(p):
        abort(404)
    resp = send_file(p, conditional=True)
    resp.headers["Accept-Ranges"] = "bytes"
    return resp


@app.route("/api/downloads/thumb")
def api_downloads_thumb():
    it = _dl_by_key(request.args.get("k", ""))
    if not it or not it.get("thumb_file"):
        abort(404)
    p = os.path.join(_DL_DIR, it["thumb_file"])
    if not os.path.isfile(p):
        abort(404)
    return send_file(p, mimetype="image/jpeg", conditional=True)


@app.route("/api/resolve_stream", methods=["POST"])
def api_resolve_stream():
    data = request.get_json(silent=True) or {}
    web_url = data.get("web_url") or ""
    # محمّلة؟ تشتغل من الجهاز على طول (حتى بدون نت)
    _dl = _dl_find(web_url, data.get("title"), data.get("artist")) if not str(web_url).startswith("local:") else None
    if _dl:
        return jsonify({"stream_url": "/api/downloads/file?k=" + _dl["key"], "local": True, "offline": True})
    if not web_url and data.get("title"):
        matches = search((str(data.get("artist") or "") + " " + str(data["title"])).strip(), "youtube", 1)
        if matches:
            web_url = matches[0].get("web_url") or ""
    if not web_url:
        return jsonify({"error": "no_web_url"}), 400
    if web_url.startswith("local:"):
        path = _local_track_path(web_url)
        if not path:
            return jsonify({"error": "local_missing"}), 404
        return jsonify({"stream_url": "/api/local_audio/" + web_url[6:], "local": True})
    now = time.time()
    key = _audio_key(web_url)
    ent = _audio_entry(key)
    if ent and ent.get("ready") and os.path.exists(ent["path"]):
        _perf("stream", time.time() - now, "local-file " + str(data.get("title") or web_url))
        return jsonify({"stream_url": f"/api/audio_file?k={key}", "cached": True, "local": True})
    with _STREAM_CACHE_LOCK:
        was_cached = web_url in _STREAM_CACHE
    stream_url = cached_stream(web_url, data.get("source", "youtube"),
                               data.get("title", ""), data.get("artist", ""), bool(data.get("refresh")))
    _perf("stream", time.time() - now, ("cache " if was_cached and not data.get("refresh") else "resolve ")
          + str(data.get("title") or web_url))
    if not stream_url and (data.get("source") or "youtube") == "youtube" and data.get("title"):
        # الأغنية ما اشتغلت (مثلاً يوتيوب يطلب تأكيد العمر أو المقطع محذوف): نجرب نسخة ثانية لنفس الأغنية
        alt_url, alt_web = _alternate_stream(web_url, str(data.get("title") or ""), str(data.get("artist") or ""),
                                             data.get("duration"))
        if alt_url:
            _perf("stream", time.time() - now, "alternate " + str(data.get("title") or web_url))
            return jsonify({"stream_url": alt_url, "alternate": alt_web,
                            "age_hint": web_url in _AGE_URLS})
    if not stream_url and web_url in _AGE_URLS:
        return jsonify({"error": "age_restricted", "hint": "age"}), 502
    if not stream_url:
        _FAIL_STREAK.append(time.time())
        del _FAIL_STREAK[:-10]
        recent = [x for x in _FAIL_STREAK if time.time() - x < 600]
        hint = "ytdlp" if len(recent) >= 3 else ""
        if hint:
            _perf("ytdlp?", 0, "%d failures in 10 min — yt-dlp may need updating" % len(recent))
            ytdlp_check_soon(force=True)
        return jsonify({"error": "unavailable", "hint": hint}), 502
    _FAIL_STREAK.clear()
    start_download = False
    with _AUDIO_DL_LOCK:
        if data.get("cache_audio") and key not in _AUDIO_FILES and _AUDIO_DOWNLOAD_SLOTS.acquire(blocking=False):
            _AUDIO_FILES[key] = {"path": os.path.join(_AUDIO_CACHE_DIR, key + ".m4a"), "ready": False, "ts": time.time()}
            start_download = True
    if start_download:
        threading.Thread(target=_download_audio, args=(key, web_url), daemon=True).start()
    return jsonify({"stream_url": stream_url})


# ===== أغاني من جهاز المستخدم (ملفات صوت يرفعها بنفسه) =====
_UPLOAD_DIR = os.path.join(BASE_DIR, "uploads")
_UPLOAD_EXT = {".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".oga", ".opus", ".webm", ".weba", ".mp4", ".3gp", ".amr", ".aiff", ".aif", ".wma"}
_UPLOAD_MAX = 300 * 1024 * 1024


def _local_track_path(web_url):
    lid = str(web_url or "")[6:] if str(web_url or "").startswith("local:") else str(web_url or "")
    if not re.match(r"^[0-9a-f]{32}$", lid) or not os.path.isdir(_UPLOAD_DIR):
        return None
    for name in os.listdir(_UPLOAD_DIR):
        if name.startswith(lid + "."):
            return os.path.join(_UPLOAD_DIR, name)
    return None


@app.route("/api/upload_track", methods=["POST"])
def api_upload_track():
    """يرفع ملف صوت من جهاز المستخدم (من البرنامج نفسه بس) ويرجع رابط تشغيله."""
    if not _is_local_owner():
        return jsonify({"error": "رفع الملفات من البرنامج على جهازك بس"}), 403
    f = request.files.get("file")
    if not f or not f.filename:
        return jsonify({"error": "ما فيه ملف"}), 400
    ext = os.path.splitext(f.filename)[1].lower()
    mime = (f.mimetype or "").lower()
    if ext not in _UPLOAD_EXT and not (mime.startswith("audio/") or mime == "video/mp4" or mime == "video/webm"):
        return jsonify({"error": "هذا مو ملف صوت"}), 400
    if ext not in _UPLOAD_EXT:
        ext = {"audio/mpeg": ".mp3", "audio/mp4": ".m4a", "audio/wav": ".wav", "audio/x-wav": ".wav", "audio/ogg": ".ogg",
               "audio/flac": ".flac", "audio/webm": ".webm", "audio/aac": ".aac"}.get(mime, ".bin")
    os.makedirs(_UPLOAD_DIR, exist_ok=True)
    lid = uuid.uuid4().hex
    path = os.path.join(_UPLOAD_DIR, lid + ext)
    size = 0
    try:
        with open(path + ".part", "wb") as out:
            while True:
                chunk = f.stream.read(1024 * 1024)
                if not chunk:
                    break
                size += len(chunk)
                if size > _UPLOAD_MAX:
                    raise ValueError("too big")
                out.write(chunk)
        os.replace(path + ".part", path)
    except ValueError:
        try:
            os.remove(path + ".part")
        except OSError:
            pass
        return jsonify({"error": "الملف كبير مرة (الحد 300 ميقا)"}), 413
    except Exception:
        try:
            os.remove(path + ".part")
        except OSError:
            pass
        return jsonify({"error": "ما قدرنا نحفظ الملف"}), 500
    _perf("upload", 0, "%s %d bytes" % (ext, size))
    return jsonify({"id": lid, "web_url": "local:" + lid, "stream_url": "/api/local_audio/" + lid, "size": size})


@app.route("/api/local_audio/<lid>")
def api_local_audio(lid):
    path = _local_track_path("local:" + str(lid))
    if not path:
        abort(404)
    ext = os.path.splitext(path)[1].lower()
    mime = {".mp3": "audio/mpeg", ".m4a": "audio/mp4", ".mp4": "audio/mp4", ".aac": "audio/aac", ".wav": "audio/wav",
            ".flac": "audio/flac", ".ogg": "audio/ogg", ".oga": "audio/ogg", ".opus": "audio/ogg", ".webm": "audio/webm",
            ".weba": "audio/webm", ".3gp": "audio/3gpp", ".amr": "audio/amr", ".aiff": "audio/aiff", ".aif": "audio/aiff",
            ".wma": "audio/x-ms-wma"}.get(ext, "application/octet-stream")
    resp = send_file(path, mimetype=mime, conditional=True)
    resp.headers["Accept-Ranges"] = "bytes"
    resp.headers["Cache-Control"] = "private, max-age=86400"
    return resp


# ===== صور الأغاني المعدّلة لديسكورد (ديسكورد يحتاج رابط https عام) =====
_COVER_DIR = os.path.join(BASE_DIR, "covers")


def _discord_cover(data_url):
    if not TUNNEL_URL:
        return ""
    m = re.match(r"^data:image/(png|jpe?g|webp|gif);base64,([A-Za-z0-9+/=]+)$", data_url or "")
    if not m or len(m.group(2)) > 3_000_000:
        return ""
    raw = base64.b64decode(m.group(2))
    ext = {"jpeg": "jpg", "jpg": "jpg"}.get(m.group(1), m.group(1))
    name = hashlib.sha1(raw).hexdigest()[:24] + "." + ext
    path = os.path.join(_COVER_DIR, name)
    if not os.path.exists(path):
        os.makedirs(_COVER_DIR, exist_ok=True)
        with open(path, "wb") as f:
            f.write(raw)
    return TUNNEL_URL.rstrip("/") + "/api/cover/" + name


@app.route("/api/cover/<name>")
def api_cover(name):
    if not re.match(r"^[0-9a-f]{24}\.(png|jpg|webp|gif)$", name or ""):
        abort(404)
    path = os.path.join(_COVER_DIR, name)
    if not os.path.exists(path):
        abort(404)
    resp = send_file(path, conditional=True)
    resp.headers["Cache-Control"] = "public, max-age=604800"
    return resp


discord_presence.cover_resolver = _discord_cover


_ALT_CACHE = {}   # web_url الأصلي -> (time, رابط النسخة البديلة)


def _ytmusic_songs(query, limit=8):
    """بحث «أغاني» يوتيوب ميوزك: النسخ الصوتية الرسمية (قنوات «الفنان - Topic») — نادراً عليها قيد عمر."""
    opts = dict(YD_BASE)
    opts["extract_flat"] = True
    opts["playlistend"] = limit
    url = "https://music.youtube.com/search?q=" + urllib.parse.quote(query) + "#songs"
    with yt_dlp.YoutubeDL(opts) as ydl:
        info = ydl.extract_info(url, download=False, process=False) or {}
    rows = []
    for e in list(info.get("entries") or [])[:limit]:
        vid = (e or {}).get("id") or ""
        if not re.match(r"^[A-Za-z0-9_-]{11}$", vid):
            continue
        rows.append({"title": e.get("title") or "", "web_url": "https://www.youtube.com/watch?v=" + vid,
                     "duration": int(e.get("duration") or 0),
                     "artist": e.get("channel") or e.get("uploader") or ", ".join(e.get("artists") or []) or "",
                     "music": True})
    return rows


def _alt_matches(row, words, song_words, artist_f, dur):
    other = set(w for w in _core_title(row.get("title")).split() if len(w) > 1)
    if not other:
        return False
    d = int(row.get("duration") or 0)
    if row.get("music"):
        # عنوان يوتيوب ميوزك = اسم الأغنية بس (بدون الفنان) — نقارنه باسم الأغنية، والمدة لازم تكون قريبة جداً
        base = song_words or words
        if not base or len(base & other) / float(len(base)) < 0.6 or len(other - words) > max(1, len(other) // 2):
            return False
        ra = _fold(re.sub(r"\s*-\s*topic\s*$", "", row.get("artist") or "", flags=re.I))
        if artist_f and ra and not (set(ra.split()) & set(artist_f.split())):
            return False
        return not (dur and d and abs(d - dur) > max(12, dur * 0.08))
    if len(words & other) / float(len(words)) < 0.6:
        return False
    return not (dur and d and abs(d - dur) > max(20, dur * 0.12))


def _alternate_stream(web_url, title, artist="", duration=None):
    """نسخة بديلة لنفس الأغنية لما الأصلية ما تشتغل (قيد عمر/محذوفة) — بدون أي حساب:
    1) النسخة الصوتية الرسمية من يوتيوب ميوزك  2) رفعات ثانية (Audio/Lyrics)  3) ساوند كلاود."""
    hit = _ALT_CACHE.get(web_url)
    if hit and time.time() - hit[0] < 6 * 3600:
        url = cached_stream(hit[1], "youtube", title, artist)
        if url:
            return url, hit[1]
    core = _core_title(title)
    words = set(w for w in core.split() if len(w) > 1)
    if not words:
        return None, None
    try:
        dur = int(float(duration or 0))
    except Exception:
        dur = 0
    artist_f = _fold(artist or "")
    if not artist_f and " - " in str(title or ""):
        artist_f = _fold(str(title).split(" - ", 1)[0])
    song_words = set(w for w in words if w not in set(artist_f.split()))
    q = (re.sub(r"\([^)]*\)|\[[^\]]*\]", " ", title) + " " + (artist if artist and _fold(artist) not in _fold(title) else "")).strip()
    tried, seen = 0, {web_url}

    def attempt(rows):
        nonlocal tried
        for r in rows:
            alt = r.get("web_url") or ""
            if not alt or alt in seen or r.get("kind") == "playlist":
                continue
            seen.add(alt)
            if not _alt_matches(r, words, song_words, artist_f, dur):
                continue
            tried += 1
            url = cached_stream(alt, "youtube", title, artist)
            if url:
                _ALT_CACHE[web_url] = (time.time(), alt)
                return url, alt
            if tried >= 5:
                break
        return None

    for source in ("music", "youtube", "audio"):
        if tried >= 5:
            break
        try:
            if source == "music":
                rows = _ytmusic_songs(q)
            elif source == "youtube":
                rows = search(q, "youtube", 8) or []
            else:
                rows = search(q + " audio", "youtube", 6) or []
        except Exception:
            rows = []
        got = attempt(rows)
        if got:
            return got
    # آخر حل: ساوند كلاود (بس لو العنوان مطابق)
    try:
        sc = Room._sc_stream(None, {"title": title, "artist": artist})
        if sc:
            return sc, None
    except Exception:
        pass
    return None, None


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
    seen = {r["id"] for r in rooms}
    rooms += [r for r in _cloud_room_list() if r.get("id") not in seen]
    rooms.sort(key=lambda r: (str(r.get("name") or "").lower(), r["id"]))
    return jsonify({"rooms": rooms})


_CLOUD_LIST = {"at": 0.0, "rooms": []}


def _cloud_room_list():
    """الغرف المفتوحة على السيرفر المشترك (من كل الأجهزة)."""
    if not hub.available():
        return []
    if time.time() - _CLOUD_LIST["at"] > 4:
        r = hub.call("GET", "/rooms", timeout=4)
        _CLOUD_LIST["at"] = time.time()
        if r is not None and r[0] == 200 and isinstance(r[1].get("rooms"), list):
            _CLOUD_LIST["rooms"] = [x for x in r[1]["rooms"] if isinstance(x, dict) and x.get("id")]
    return list(_CLOUD_LIST["rooms"])


@app.route("/api/profiles")
def api_profiles():
    if time.time() - _HUB["pulled"] > 20:
        _hub_wake()
    return jsonify({"profiles": all_profiles()})


@app.route("/api/check_username")
def api_check_username():
    un = norm_username(request.args.get("username") or "")
    uid = request.args.get("uid") or ""
    if not un:
        return jsonify({"available": False})
    if username_taken(un, uid):
        return jsonify({"available": False})
    r = hub.call("GET", "/check_username", params={"username": un, "uid": uid}, timeout=3)
    if r is not None and r[0] == 200 and r[1].get("available") is False:
        return jsonify({"available": False})
    return jsonify({"available": True})


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
    try:
        with urllib.request.urlopen(req, timeout=15) as r:
            j = json.loads(r.read().decode())
    except Exception as e:
        _perf("spotify-error", 0, "token " + repr(e)[:120])
        time.sleep(0.6)
        with urllib.request.urlopen(req, timeout=15) as r:
            j = json.loads(r.read().decode())
    _SPOTIFY_TOKEN = j["access_token"]
    _SPOTIFY_TOKEN_EXP = now + int(j.get("expires_in", 3600))
    return _SPOTIFY_TOKEN


def _spotify_get(url):
    """طلب لسبوتيفاي مع علاج الرفض المؤقت (403/401/429/5xx):
    توكن جديد ← إعادة محاولة ← بدون market. يسجّل رسالة سبوتيفاي نفسها في harmoni-perf.log."""
    global _SPOTIFY_TOKEN, _SPOTIFY_TOKEN_EXP
    attempts = [url, url, re.sub(r"&market=[A-Z]{2}", "", url)]
    last = None
    for i, u in enumerate(attempts):
        tok = _spotify_token()
        if not tok:
            return None
        req = urllib.request.Request(u, headers={"Authorization": "Bearer " + tok})
        try:
            with urllib.request.urlopen(req, timeout=15) as r:
                return json.loads(r.read().decode())
        except urllib.error.HTTPError as e:
            last = e
            try:
                body = e.read().decode("utf-8", "replace")[:200]
            except Exception:
                body = ""
            _perf("spotify-error", 0, "try %d HTTP %s %s" % (i + 1, e.code, body))
            if e.code == 403 and "premium" in body.lower():
                raise RuntimeError(_sp_access_message(403, body))
            if e.code in (401, 403):
                _SPOTIFY_TOKEN, _SPOTIFY_TOKEN_EXP = None, 0      # توكن جديد بالمحاولة الجاية
            elif e.code == 429:
                try:
                    time.sleep(min(3.0, float(e.headers.get("Retry-After") or 1)))
                except Exception:
                    time.sleep(1)
            elif e.code < 500:
                raise
            time.sleep(0.3 + i * 0.4)
        except (urllib.error.URLError, TimeoutError, OSError) as e:
            last = e
            _perf("spotify-error", 0, "try %d %s" % (i + 1, repr(e)[:120]))
            time.sleep(0.5)
    raise last


_SP_PAGE = 10   # من فبراير 2026: سبوتيفاي يسمح بـ 10 نتائج كحد أقصى لكل طلب بحث


def _spotify_search_pages(query, typ, limit):
    """يجيب النتائج على صفحات (10 لكل طلب) بالتوازي."""
    limit = max(1, min(int(limit or 10), 50))
    base = ("https://api.spotify.com/v1/search?q=" + urllib.parse.quote(query) + "&type=" + typ
            + (("&market=" + search_region()) if search_region() else ""))
    offsets = list(range(0, limit, _SP_PAGE))

    def page(off):
        return _spotify_get(base + "&limit=%d&offset=%d" % (min(_SP_PAGE, limit - off), off)) or {}
    if len(offsets) == 1:
        pages = [page(0)]
    else:
        with concurrent.futures.ThreadPoolExecutor(max_workers=len(offsets)) as ex:
            futs = [ex.submit(page, o) for o in offsets]
            pages = []
            for i, f in enumerate(futs):
                try:
                    pages.append(f.result())
                except Exception:
                    if i == 0:
                        raise          # الصفحة الأولى لازم تنجح؛ الباقي نتجاوزه لو فشل
                    pages.append({})
    key = typ + "s"
    out = []
    for pg in pages:
        out.extend([x for x in ((pg.get(key) or {}).get("items") or []) if x])
    return out[:limit]


def spotify_search(query, limit=8):
    tok = _spotify_token()
    if not tok:
        return None
    tracks = _spotify_search_pages(query, "track", limit)
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
    out = []
    for p in _spotify_search_pages(query, "playlist", limit):
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


def _best_match(t, rows):
    """أقرب نسخة يوتيوب لأغنية سبوتيفاي: المدة + الاسم + نتجنب لايف/ريمكس/كفر لو مو مطلوبة."""
    want = _music_text(t.get("title"))
    artist = _music_text((t.get("artist") or "").split(",")[0])
    dur = int(t.get("duration") or 0)
    bad = re.compile(r"\b(live|remix|cover|karaoke|instrumental|sped up|slowed|nightcore|8d|reaction|لايف|ريمكس|كاريوكي)\b")

    def score(i_row):
        i, row = i_row
        title = _music_text(row.get("title"))
        ch = _music_text(row.get("artist"))
        s = -i * 0.5                       # ترتيب يوتيوب له وزن خفيف
        if want and want in title:
            s += 4
        if artist and (artist in title or artist in ch):
            s += 3
        if "topic" in ch.split() or "vevo" in ch:
            s += 2
        for m in bad.findall(title):
            if m not in want:
                s -= 5
        d = int(row.get("duration") or 0)
        if dur and d:
            diff = abs(d - dur)
            s += 4 if diff <= 3 else 2 if diff <= 10 else -2 if diff > 40 else 0
        return s
    return max(enumerate(rows), key=score)[1]


def _resolve_to_youtube(t):
    q = ((t.get("artist") or "") + " " + (t.get("title") or "")).strip()
    if not q:
        return None
    try:
        res = search(q, "youtube", 6)
    except Exception:
        return None
    if not res:
        return None
    r = dict(_best_match(t, res))
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
    if re.search(r"[\u0600-\u06FF]", query or ""):
        return query + " اغنية"   # كلمة "music" كانت تجيب نتائج أجنبية للبحث العربي
    return query + " music"


_ARABIC_RE = re.compile(r"[\u0600-\u06FF\u0750-\u077F]")


_AR_FOLD = str.maketrans({"أ": "ا", "إ": "ا", "آ": "ا", "ٱ": "ا", "ة": "ه", "ى": "ي", "ؤ": "و", "ئ": "ي"})
_VARIANT_RE = re.compile(r"\b(cover|remix|live|sped up|speed up|slowed|reverb|nightcore|8d|karaoke|instrumental|"
                         r"ريمكس|ريمكسات|لايف|حفله|حفل|بطيء|مسرعه|مسرع|عزف|كاريوكي|كوفر|موسيقى فقط|بدون موسيقى)\b")
_LONG_RE = re.compile(r"\b(full album|album|mix|playlist|mashup|nonstop|hour|hours|كامل|كامله|البوم|ميكس|ساعه|ساعات|جلسه|جلسات|حفله|حفل)\b")
_NOISE_RE = re.compile(r"\b(official|video|music video|audio|lyrics|lyric|hd|hq|4k|visualizer|clip|"
                       r"كلمات|حصريا|حصري|فيديو كليب|كليب|الفيديو|الرسميه|رسمي|بالكلمات)\b")
_QUERY_FILLER = {"اغنيه", "اغاني", "song", "songs", "music", "official", "audio", "video", "lyrics", "كلمات", "the"}


def _fold(value):
    """تطبيع للمقارنة: أحرف صغيرة، توحيد الألف/الياء/التاء المربوطة، بدون تشكيل ورموز."""
    text = _music_text(value).translate(_AR_FOLD)
    return re.sub(r"\s+", " ", text).strip()


def _core_title(title):
    """العنوان بدون (Official Video) و[Lyrics] وكلمات الزينة — عشان نكشف النسخ المكررة."""
    t = re.sub(r"\([^)]*\)|\[[^\]]*\]|【[^】]*】", " ", str(title or ""))
    t = _NOISE_RE.sub(" ", _fold(t))
    return re.sub(r"\s+", " ", t).strip()


def _dedupe_results(rows):
    """نفس الأغنية مرفوعة أكثر من مرة: نخلي أفضل نسخة (الأولى بعد الترتيب) بس."""
    out, seen = [], []
    for row in rows:
        if row.get("kind") == "playlist":
            out.append(row)
            continue
        core = _core_title(row.get("title"))
        dur = int(row.get("duration") or 0)
        dup = len(core) >= 4 and any(c == core and (not dur or not d or abs(dur - d) <= 8) for c, d in seen)
        if not dup:
            seen.append((core, dur))
            out.append(row)
    return out


def rank_music_results(rows, query):
    """ترتيب الأغاني محلياً (بدون طلبات): تطابق كلمات البحث، قناة الفنان، النسخة الأصلية، المدة والمشاهدات."""
    arabic_query = bool(_ARABIC_RE.search(str(query or "")))
    wanted = _music_text(query)
    folded_q = _fold(query)
    aliases = {wanted, folded_q} - {""}
    if wanted in {"كلاش", "klash"}:
        aliases.update({"كلاش", "klash"})
    tokens = [t for t in folded_q.split() if t not in _QUERY_FILLER and len(t) > 1] or folded_q.split()
    wants_variant = bool(_VARIANT_RE.search(folded_q))
    wants_long = bool(_LONG_RE.search(folded_q))
    music = re.compile(r"\b(music|song|songs|audio|lyrics|lyric|rap|remix|soundtrack|ost|اغنيه|اغاني|موسيقي|راب|شيله|شيلات|كليب)\b")
    unrelated = re.compile(r"\b(gameplay|gaming|clash of clans|clash royale|tutorial|walkthrough|review|reaction|interview|podcast|vlog|كلاش اوف كلانس|كلاش رويال|العاب|تحدي|شرح|تحديث|مقابله|بودكاست)\b")
    order = {id(r): i for i, r in enumerate(rows)}

    def score(row):
        raw_title = str(row.get("title") or "")
        title = _fold(raw_title)
        artist = _fold(row.get("artist"))
        text = title + " " + artist
        musical = bool(music.search(text))
        channel = "topic" in artist.split() or "vevo" in artist or "تسجيلات" in artist or "records" in artist
        points = (6 if musical else 0) + (5 if channel else 0)
        if tokens:
            # كم كلمة من البحث موجودة بالعنوان أو اسم القناة
            hit = sum(1 for t in tokens if t in text)
            points += 10.0 * hit / len(tokens) + (3 if hit == len(tokens) else 0)
            if any(t in artist for t in tokens):
                points += 3
        if any(artist == term or artist.startswith(term + " ") for term in aliases):
            points += 4
        if any(term in title for term in aliases):
            points += 3
        if "official" in title or "الرسميه" in title or "رسمي" in title:
            points += 2
        dur = row.get("duration") or 0
        if 60 <= dur <= 720:
            points += 1
        elif dur and dur < 45:
            points -= 6                       # شورتس ومقاطع قصيرة
        elif dur > 900 and not wants_long:
            points -= 5                       # ساعات/ميكسات طويلة لما ما طلبتها
        if not wants_variant and _VARIANT_RE.search(title):
            points -= 4                       # كفر/ريمكس/بطيء… لما تبي الأصلية
        if unrelated.search(text) and not musical and not channel:
            points -= 25                      # ألعاب/شروحات: تنزل تحت حتى لو فيها نفس الكلمة
        elif NON_MUSIC_RE.search(raw_title) and not musical and not channel:
            points -= 6
        if arabic_query:
            # بحث بالعربي: نقدّم الأغاني العربية على الأجنبية
            points += 5 if _ARABIC_RE.search(raw_title + str(row.get("artist") or "")) else -4
        views = row.get("views") or 0
        if views:
            points += min(3.0, math.log10(views + 1) / 2.5)
        points -= order.get(id(row), 0) * 0.04   # ترتيب الموقع نفسه عامل خفيف
        return -points
    return _dedupe_results(sorted(rows, key=score))


def sort_results(rows, sort):
    """ترتيب محلي سريع للنتائج حسب الفلتر (بدون طلبات شبكة)."""
    if sort == "popular":
        return sorted(rows, key=lambda r: (-(r.get("pop") or 0), -(r.get("views") or 0)))
    if sort == "oldest":
        return sorted(rows, key=lambda r: (r.get("ts") is None, r.get("ts") or 0))
    return rows


class _NoticeRows(list):
    notice = ""


def _spotify_search_work(query, limit, sort):
    n = max(6, min(limit or 36, 30))
    sp = spotify_playlist_search(query, n) if sort == "playlists" else spotify_search(query, n)
    if sp is None:
        raise RuntimeError("بيانات سبوتيفاي غير مضبوطة. أضف SPOTIFY_CLIENT_ID و SPOTIFY_CLIENT_SECRET في spotify_config.json")
    if sort == "playlists":
        return sp
    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as ex:
        return [x for x in ex.map(_resolve_to_youtube, sp) if x]


def _search_work(query, source, limit=None, sort=""):
    if is_url(query):
        if is_spotify(query):
            return resolve_spotify(query)
        info = _extract(query)
        entries = info.get("entries") if info and info.get("entries") else ([info] if info else [])
        return [normalize_entry(e) for e in entries if normalize_entry(e)]
    if source == "spotify":
        try:
            return _spotify_search_work(query, limit, sort)
        except Exception as e:
            if "غير مضبوطة" in str(e):
                raise
            # سبوتيفاي رفض/ما رد — نكمل البحث من يوتيوب بدل ما يطلع خطأ
            _perf("spotify-error", 0, "fallback to youtube: " + repr(e)[:120])
            rows = _NoticeRows(_search_work(query, "youtube", limit, sort))
            rows.notice = "سبوتيفاي ما رد حالياً — هذي نتائج من يوتيوب"
            return rows
    region = search_region() if source == "youtube" else ""
    suffix = ("@" + region) if region else ""
    if sort in SEARCH_SORTS:
        if source == "soundcloud" and sort != "playlists":
            key_source = "soundcloud:sorted"   # نفس طلب البحث العادي، الترتيب محلي
        else:
            key_source = source + ":" + sort + suffix
        q = music_search_query(query) if (source == "youtube" and sort != "playlists") else query
        return search(q, key_source, limit or SEARCH_LIMIT)
    return search(music_search_query(query) if source == "youtube" else query, source + suffix, limit or SEARCH_LIMIT)


@app.route("/api/search", methods=["POST"])
def api_search():
    _t0 = time.time()
    try:
        return _api_search_inner()
    finally:
        try:
            d = request.get_json(force=True, silent=True) or {}
            _perf("search", time.time() - _t0, "%s | %s %s" % (d.get("query"), d.get("source") or "youtube", d.get("sort") or ""))
        except Exception:
            pass


def _api_search_inner():
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
        notice = getattr(results, "notice", "")
        if paged:
            page = results[offset:offset + lim]
            if sort:
                page = sort_results(page, sort)
            elif not is_url(query):
                page = rank_music_results(page, query)
            _search_prefetch(page)
            return jsonify({"results": page, "notice": notice,
                            "has_more": offset + lim < min(len(results), 980)})
        if sort:
            results = sort_results(results, sort)
        elif not is_url(query):
            results = rank_music_results(results, query)
        _search_prefetch(results)
        return jsonify({"results": results, "notice": notice})
    except RuntimeError as e:
        return jsonify({"results": [], "error": _friendly_search_error(e)})
    except Exception as e:
        return jsonify({"results": [], "error": _friendly_search_error(e)})


def _friendly_search_error(e):
    msg = str(e)
    _perf("search-error", 0, "shown " + msg[:160])
    if "403" in msg or "Forbidden" in msg or "429" in msg or "Too Many" in msg:
        return "الموقع رفض البحث مؤقتاً — جرّب مرة ثانية بعد ثواني"
    if "timed out" in msg or "Timeout" in msg or "Connection" in msg:
        return "مشكلة في الاتصال بالإنترنت — تأكد من النت وجرّب مرة ثانية"
    if re.search(r"[\u0600-\u06FF]", msg):
        return msg   # رسائلنا العربية (مثل إعدادات سبوتيفاي) تطلع زي ما هي
    return "صار خطأ في البحث — جرّب مرة ثانية"


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


def _sp_item_track(it):
    """عنصر بلاي لست: الشكل الجديد (item) أو القديم (track)."""
    it = it or {}
    return it.get("item") or it.get("track")


def _sp_playlist_items(tok, pid, limit=50):
    """/playlists/{id}/items (الجديد من 2026) وإذا ما اشتغل نجرب /tracks القديم."""
    try:
        yield from _sp_items(tok, "/playlists/%s/items" % pid, limit)
    except SpotifyAccessError:
        raise
    except Exception:
        yield from _sp_items(tok, "/playlists/%s/tracks" % pid, limit)


def _sp_track_shape(t):
    if not t or (t.get("type") and t.get("type") != "track"):
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


# ===== متابعة بلاي لستات سبوتيفاي العامة (بدون تسجيل دخول) =====
_SP_PUB_CACHE = {}          # sid -> (time, (name, img, tracks))
_SP_PUB_TTL = 240


def _sp_public_playlist(sid):
    """أغاني بلاي لست عامة: API (لو سمح) وإلا صفحة العرض العامة. مع كاش 4 دقايق."""
    hit = _SP_PUB_CACHE.get(sid)
    if hit and time.time() - hit[0] < _SP_PUB_TTL:
        return hit[1]
    name, img, tracks = "", "", []
    tok = None
    try:
        tok = _spotify_token()
    except Exception:
        tok = None
    if tok:
        try:
            meta = _sp_api_get(tok, "/playlists/" + sid)
            name = meta.get("name") or ""
            imgs = meta.get("images") or []
            img = imgs[0].get("url", "") if imgs else ""
            for it in itertools.islice(_sp_playlist_items(tok, sid, 50), 1000):
                shape = _sp_track_shape(_sp_item_track(it))
                if shape and shape["title"]:
                    tracks.append(shape)
        except Exception:
            tracks = []
    if not tracks:
        ename, eimg, tracks = _sp_embed_import("playlist", sid)
        name, img = name or ename, img or eimg
    for t in tracks:
        if not t.get("thumbnail") and img:
            t["thumbnail"] = img
    out = (name, img, tracks)
    _SP_PUB_CACHE[sid] = (time.time(), out)
    return out


def _safe_pub(sid):
    try:
        return _sp_public_playlist(sid)
    except Exception:
        return None


_SP_USER_RE = re.compile(r"(?:open\.spotify\.com/(?:intl-[a-z-]+/)?user/|spotify:user:)([^/?#\s:]+)", re.I)


def _sp_parse_user(text):
    """يقبل رابط البروفايل أو اليوزر نفسه."""
    text = (text or "").strip()
    m = _SP_USER_RE.search(text)
    uid = urllib.parse.unquote(m.group(1)) if m else text.lstrip("@")
    uid = uid.strip()
    return uid if re.match(r"^[A-Za-z0-9._-]{1,64}$", uid) else ""


def _sp_read_profile(uid):
    """بلاي لستات البروفايل العامة عن طريق نافذة متصفح مخفية (sp_profile_reader)."""
    import sp_profile_reader
    return sp_profile_reader.read_profile(uid)


@app.route("/api/spotify_public/user", methods=["POST"])
def api_spotify_public_user():
    if not _is_local_owner():
        return jsonify({"error": "local only"}), 403
    data = request.get_json(force=True, silent=True) or {}
    uid = _sp_parse_user(data.get("user"))
    if not uid:
        return jsonify({"error": "الصق رابط حسابك في سبوتيفاي (open.spotify.com/user/...)"}), 400
    try:
        prof = _sp_read_profile(uid)
    except LookupError:
        return jsonify({"error": "ما لقينا هذا الحساب في سبوتيفاي"}), 404
    except ImportError:
        return jsonify({"error": "هذي الخاصية تشتغل في برنامج Harmoni المثبت بس"}), 400
    except Exception as e:
        _perf("spotify-error", 0, "profile reader " + repr(e)[:100])
        return jsonify({"error": "ما لقينا بلاي لستات عامة في هذا الحساب. تأكد إنها Public وظاهرة في البروفايل، أو الصق روابطها تحت"}), 404
    lists = [dict(p) for p in prof.get("playlists") or []]
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as ex:
        res = list(ex.map(lambda p: _safe_pub(p["id"]), lists))
    out = []
    for p, r in zip(lists, res):
        if r and r[2]:
            out.append({"id": p["id"], "name": r[0] or p.get("name") or "Spotify", "count": len(r[2])})
    if not out:
        return jsonify({"error": "البلاي لستات اللي في البروفايل فاضية أو مو عامة"}), 404
    return jsonify({"user": uid, "name": prof.get("name") or uid, "playlists": out})


@app.route("/api/spotify_public/resolve", methods=["POST"])
def api_spotify_public_resolve():
    """رابط بلاي لست عامة -> اسمها وعدد أغانيها (للإضافة لقائمة المتابعة)."""
    data = request.get_json(force=True, silent=True) or {}
    typ, sid = _spotify_resolve_id(data.get("url") or "")
    if typ != "playlist":
        return jsonify({"error": "الصق رابط بلاي لست من سبوتيفاي (مو أغنية أو ألبوم)"}), 400
    res = _safe_pub(sid)
    if not res or not res[2]:
        return jsonify({"error": "تعذر قراءة البلاي لست — تأكد إنها عامة (Public) وفيها أغاني"}), 404
    return jsonify({"id": sid, "name": res[0] or "Spotify", "img": res[1], "count": len(res[2])})


@app.route("/api/spotify_public/sync", methods=["POST"])
def api_spotify_public_sync():
    """البلاي لستات العامة اللي يتابعها المستخدم: وحدة منها (fav) تروح للمفضلة والباقي بلاي لستات."""
    data = request.get_json(force=True, silent=True) or {}
    fav = str(data.get("fav") or "")
    ids = []
    discovered = []
    uid = _sp_parse_user(data.get("user")) if data.get("user") else ""
    if uid and _is_local_owner():
        # البلاي لستات الجديدة في البروفايل (نقراه كل 25 دقيقة بالكثير)
        try:
            hidden = set(str(x) for x in (data.get("hidden") or []))
            for p in _sp_read_profile(uid).get("playlists") or []:
                if p["id"] not in hidden:
                    discovered.append(p["id"])
        except Exception as e:
            _perf("spotify-error", 0, "profile reader sync " + repr(e)[:100])
    for x in list((data.get("playlists") or [])[:60]) + discovered:
        x = str(x or "")
        if re.match(r"^[A-Za-z0-9]{22}$", x) and x not in ids:
            ids.append(x)
    if re.match(r"^[A-Za-z0-9]{22}$", fav) and fav not in ids:
        ids.append(fav)
    if not ids:
        return jsonify({"error": "ما فيه بلاي لستات للمتابعة"}), 400
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as ex:
        results = list(ex.map(_safe_pub, ids))
    likes, playlists, failed, info = [], [], [], []
    for sid, res in zip(ids, results):
        if not res:
            failed.append(sid)
            continue
        pname, pimg, tracks = res
        info.append({"id": sid, "name": pname or "Spotify", "count": len(tracks)})
        if sid == fav:
            likes = [dict(t) for t in tracks]
        else:
            playlists.append({"spotify_id": sid, "name": pname or "Spotify", "img": pimg, "tracks": [dict(t) for t in tracks]})
    return jsonify({"likes": likes, "playlists": playlists, "failed": failed, "info": info, "discovered": discovered})


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
            src = _sp_playlist_items(tok, sid, 50) if typ == "playlist" else _sp_items(tok, path + "/tracks", 50)
            for it in itertools.islice(src, 500):
                shape = _sp_track_shape(_sp_item_track(it) if typ == "playlist" else it)
                if not shape or not shape["title"]:
                    continue
                if not shape["thumbnail"] and img:
                    shape["thumbnail"] = img
                tracks.append(shape)
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
    if err == "access_denied":
        # المستخدم ضغط «إلغاء» في صفحة سبوتيفاي → نرجعه للبرنامج على طول
        return redirect(((st or {}).get("origin") or request.host_url.rstrip("/")) + "/?sp_cancel=1")
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
                if not pl or (pl.get("owner", {}).get("id") != me.get("id") and not pl.get("collaborative")):
                    continue
                name = pl.get("name") or ""
                pimgs = (pl.get("images") or [])
                pimg = pimgs[0].get("url", "") if pimgs else ""
                trs, seen = [], set()
                ok_pl = True
                try:
                    for it in _sp_playlist_items(tok, pl.get("id"), 50):
                        shape = _sp_track_shape(_sp_item_track(it))
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
    taste = _clean_taste(data.get("taste")) or {}
    if taste:
        # ذوق المستخدم: فنانينه المفضلين + تصنيفين من تصنيفاته (أكثر لو ما عنده سجل)
        fresh = not recent
        for name in taste.get("artists", [])[: (3 if fresh else 1)]:
            queries.append(name + " music")
        import random as _r
        g = list(taste.get("genres", []))
        _r.shuffle(g)
        for key in g[: (3 if fresh else 1)]:
            queries.append(TASTE_GENRES[key])
        m = list(taste.get("moods", []))
        if m and fresh:
            queries.append(TASTE_MOODS[_r.choice(m)])
    queries = list(dict.fromkeys(queries))[: (8 if taste else 6)]
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


def _cloud_member(data):
    keys = ("uid", "name", "username", "avatar", "banner", "bio", "colors", "likes", "following")
    return {k: data.get(k) for k in keys if data.get(k) is not None}


def _cloud_carry(data, prev):
    """الغرفة المشتركة تكمل نفس الأغنية (والقائمة) اللي كان المستخدم يسمعها."""
    tr = data.get("carry_track") if isinstance(data.get("carry_track"), dict) else None
    if tr and (tr.get("web_url") or tr.get("title")):
        return {"current": tr, "playing": bool(tr.get("playing", True)), "position": tr.get("position") or 0}
    if prev is None:
        return {}
    if isinstance(prev, cloud_rooms.CloudLink):
        st = prev.state()
        pos = float(st.get("position") or 0) + (time.time() - float(st.get("updated_at") or time.time()) if st.get("playing") else 0)
        return {"current": st.get("current"), "queue": st.get("queue") or [], "shuffle": st.get("shuffle"),
                "playing": st.get("playing"), "position": max(0.0, pos)}
    with prev.lock:
        pos = prev.position + (max(0.0, time.time() - prev.updated_at) if prev.playing else 0)
        return {"current": dict(prev.current) if prev.current else None, "queue": [dict(t) for t in prev.queue],
                "shuffle": prev.shuffle, "playing": prev.playing, "position": pos}


def _cloud_on_joined(sid):
    # دخلنا الغرفة المشتركة: نطلع من الغرفة المحلية (الشخصية) اللي كان فيها
    _leave_local(sid, in_request=False)


def _cloud_connect(sid, code, data, create=None, on_failed=None):
    uid = data.get("uid") or ""
    register_profile(data)
    link = cloud_rooms.connect(sid, code, _cloud_member(data), hub.token(uid) if uid else "",
                               password=str(data.get("password") or ""), create=create, on_joined=_cloud_on_joined,
                               on_failed=on_failed)
    return link


def _cloud_rooms_on():
    return hub.available() and _CLOUD_ROOMS_ENABLED


_CLOUD_ROOMS_ENABLED = not os.path.basename(sys.argv[0] or "").startswith("test_")


@socketio.on("create_room")
def on_create_room(data):
    data = data or {}
    sid = request.sid
    prev = current_room(sid) if data.get("carry") else None
    if not data.get("auto") and _cloud_rooms_on():
        r = hub.call("POST", "/rooms/new", {})
        if r is not None and r[0] == 200 and r[1].get("id"):
            create = {"name": data.get("room_name") or data.get("name") or "", "max_members": data.get("max_members") or 0,
                      "password": str(data.get("password") or ""), "can_add": bool(data.get("can_add", True)),
                      "carry": _cloud_carry(data, prev) if data.get("carry") else {}}
            def fallback(_sid, data=data, prev=prev):
                # السيرفر المشترك ما رد: نسوي الغرفة على هالجهاز بدل ما الزر ما يسوي شي
                _perf("cloud-room", 0, "create failed → local room")
                _create_local_room(_sid, data, prev, in_request=False)
            _cloud_connect(sid, r[1]["id"], data, create=create, on_failed=fallback)
            return
    _create_local_room(sid, data, prev)


def _create_local_room(sid, data, prev, in_request=True):
    rid = _gen_room_code()
    room = Room(rid, data.get("room_name") or data.get("name") or "")
    carry_track = data.get("carry_track") if isinstance(data.get("carry_track"), dict) else None
    if data.get("carry"):
        if isinstance(prev, cloud_rooms.CloudLink):
            prev = None
        room.take_over(carry_track, prev)
    if in_request:
        _leave_current(sid)
    else:
        _leave_local(sid, in_request=False)
    with ROOMS_LOCK:
        ROOMS[rid] = room
        SID_ROOM[sid] = rid
    if in_request:
        join_room(rid)
    else:
        try:
            socketio.server.enter_room(sid, rid, namespace="/")
        except Exception:
            pass
    room.create_room(sid, data)
    room.add_member(sid, data)
    socketio.emit("state", room.state(), to=sid)


@socketio.on("room_settings")
def on_room_settings(data):
    room = current_room(request.sid)
    if room is None or not room.update_settings(request.sid, data):
        emit("add_error", {"error": "فقط مالك الغرفة يغيّر الإعدادات"})


@socketio.on("join")
def on_join(data):
    data = data or {}
    sid = request.sid
    register_profile(data)
    rid = str(data.get("room") or "").strip()
    room = ROOMS.get(rid)
    if room is None:
        link = cloud_rooms.get(sid)
        if link is not None and link.rid == rid and link.joined:
            emit("state", link.state(), to=sid)
            return
        if re.match(r"^\d{6}$", rid) and _cloud_rooms_on():
            _cloud_connect(sid, rid, data)
            return
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
        with ROOMS_LOCK:
            if SID_ROOM.get(target) == room.rid:
                SID_ROOM.pop(target, None)
        try:
            leave_room(room.rid, sid=target)
        except Exception:
            pass
        socketio.emit("kicked", {}, to=target)


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
        res = room.play_track(track) if data.get("play_now") else room.add_track(track)
        if res and res.get("error"):
            socketio.emit("add_error", {"error": res["error"]}, to=sid)
        elif not (res or {}).get("pending"):   # الغرف المشتركة: التأكيد يجي من السيرفر نفسه
            socketio.emit("add_ok", {}, to=sid)

    threading.Thread(target=work, daemon=True).start()


@socketio.on("remove")
def on_remove(data):
    sid = request.sid
    room = current_room(sid)
    if not room:
        return
    if not room.can_add and sid != room.owner_sid:
        emit("add_error", {"error": "فقط مالك الغرفة يحذف الأغاني"})
        return
    tid = (data or {}).get("id")
    with room.lock:
        is_current = room.current is not None and room.current.get("id") == tid
    if is_current:
        room.skip(tid)          # حذف الأغنية الشغالة = ننتقل للي بعدها
    else:
        room.remove_track(tid)


@socketio.on("skip")
def on_skip(data=None):
    room = current_room(request.sid)
    if room:
        room.skip((data or {}).get("id") if isinstance(data, dict) else None)


@socketio.on("stream_failed")
def on_stream_failed(data):
    room = current_room(request.sid)
    if room:
        room.stream_failed((data or {}).get("id"))


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


# ماك: المنفذ 5000 ماخذه AirPlay — ونحتاج منفذ ثابت عشان بيانات الصفحة (تسجيل الدخول) ما تضيع كل مرة
PORT = int(os.environ.get("MUSIC_PORT", "5170" if sys.platform == "darwin" else "5000"))


def _warm_search():
    try:
        search("music", "youtube", 1)
    except Exception:
        pass
    try:
        # يجهّز مفتاح ساوند كلاود مقدماً عشان أول بحث يكون سريع
        with yt_dlp.YoutubeDL(dict(YD_BASE)) as ydl:
            ydl.get_info_extractor("SoundcloudSearch").initialize()
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


class _QuietAccessLog:
    """سجل الطلبات: نخلي الأخطاء بس (كان يكتب ~100 ألف سطر باليوم من طلبات الفقاعة والغرف وكبر الملف لـ 9 ميقا)."""
    _OK = re.compile(r'" (1\d\d|2\d\d|3\d\d) ')

    def filter(self, record):
        try:
            return not self._OK.search(record.getMessage())
        except Exception:
            return True


def _quiet_logs():
    import logging
    lg = logging.getLogger("werkzeug")
    if not any(isinstance(f, _QuietAccessLog) for f in lg.filters):
        lg.addFilter(_QuietAccessLog())


def _rotate_runtime_log(path, limit=5 * 1024 * 1024):
    """لو السجل كبر نحفظ نسخة وحدة قديمة (.old) ونبدأ جديد."""
    try:
        if os.path.getsize(path) > limit:
            old = path + ".old"
            if os.path.exists(old):
                os.remove(old)
            os.replace(path, old)
    except OSError:
        pass


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
    _quiet_logs()
    if sys.stdout is None or sys.stderr is None:
        _rotate_runtime_log(os.path.join(BASE_DIR, "harmoni-runtime.log"))
        log = open(os.path.join(BASE_DIR, "harmoni-runtime.log"), "a", encoding="utf-8", buffering=1)
        if sys.stdout is None:
            sys.stdout = log
        if sys.stderr is None:
            sys.stderr = log
    for output in (sys.stdout, sys.stderr):
        if hasattr(output, "reconfigure"):
            output.reconfigure(encoding="utf-8", errors="backslashreplace")
    if not headless and os.environ.get("HARMONI_ALLOW_MULTI") != "1" and _already_running(PORT):
        # نسختين مع بعض كانت تسبب مشاكل (وحدة تمسح حسابات الثانية، وتعارض الزجاج) — نكتفي بالموجودة
        raise SystemExit(0)
    http_server = create_http_server(PORT)
    PORT = http_server.server_port
    threading.Thread(target=http_server.serve_forever, daemon=True).start()
    threading.Thread(target=lambda: yt_dlp._load() if isinstance(yt_dlp, _LazyYtDlp) else None,
                     daemon=True, name="harmoni-ytdlp-import").start()
    _load_profiles()
    start_hub_sync()
    start_ytdlp_updates()
    if sys.platform == "darwin" and _JSRT.get("state") == "none":
        threading.Thread(target=_download_deno, daemon=True, name="harmoni-deno").start()
    if not headless:
        threading.Thread(target=_sync_bundled_decorations, daemon=True, name="harmoni-deco-sync").start()
    if not headless and ducking.config()["enabled"]:
        ducking.start()
    _load_search_cache()
    _apply_panel_config(load_panel_config())
    _load_stream_cache()
    threading.Thread(target=_scan_audio_cache, daemon=True).start()
    threading.Thread(target=_warm_search, daemon=True).start()
    try:
        from mini_window import start_background

        if not headless:
            start_background(PORT)
    except Exception as e:
        print("mini background error:", e)
    try:
        # النفق الدائم (لو صاحب الجهاز مجهّزه) يشتغل مع البرنامج. النفق السريع للغرف يشتغل بس لما تسوي غرفة وتدعو أحد.
        if PORT == 5000 and not headless and os.path.exists(os.path.join(os.path.expanduser("~"), ".cloudflared", "config.yml")):
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
