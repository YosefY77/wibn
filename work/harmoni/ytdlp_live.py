"""تحديث yt-dlp تلقائياً داخل البرنامج المثبّت (بدون إعادة بناء ولا ملفات bat).

يوتيوب يغيّر موقعه كل فترة و yt-dlp ينزّل إصلاح خلال يوم أو يومين. هنا البرنامج:
1. يشيك على PyPI (المصدر الرسمي) كل 12 ساعة — وفوراً لو فشل التشغيل أكثر من مرة ورا بعض.
2. ينزّل ملف الـ wheel الرسمي ويتأكد من بصمته (sha256) اللي ينشرها PyPI.
3. يفكه في HarmoniData/pylib/<الحزمة>-<النسخة>/ ويجربه: لو اشتغل يصير هو المستخدم (حتى بدون إعادة تشغيل)،
   ولو خرب نرجع للنسخة اللي داخل البرنامج ونعلّم النسخة الجديدة إنها خربانة.
"""
import hashlib
import importlib
import importlib.abc
import importlib.machinery
import json
import os
import shutil
import sys
import tempfile
import threading
import time
import zipfile

PKGS = {"yt_dlp": "yt-dlp", "yt_dlp_ejs": "yt-dlp-ejs"}
CHECK_EVERY = 12 * 3600
_state = {"base": "", "roots": {}, "finder": None, "checked": 0.0, "busy": False, "last": None,
          "bundled": None, "on_swap": None}
_lock = threading.Lock()


def _dir():
    return os.path.join(_state["base"], "pylib")


def _cfg_path():
    return os.path.join(_dir(), "active.json")


def _cfg():
    try:
        with open(_cfg_path(), encoding="utf-8") as f:
            c = json.load(f)
        return c if isinstance(c, dict) else {}
    except Exception:
        return {}


def _save_cfg(c):
    os.makedirs(_dir(), exist_ok=True)
    tmp = _cfg_path() + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(c, f)
    os.replace(tmp, _cfg_path())


class _Finder(importlib.abc.MetaPathFinder):
    """يخلي «import yt_dlp» يقرأ من النسخة المحدثة بدل اللي داخل البرنامج."""

    def find_spec(self, name, path=None, target=None):
        top = name.partition(".")[0]
        root = _state["roots"].get(top)
        if not root:
            return None
        return importlib.machinery.PathFinder.find_spec(name, path if "." in name else [root])


def _vkey(v):
    out = []
    for p in str(v or "").replace("-", ".").split("."):
        out.append(int(p) if p.isdigit() else 0)
    return out


def _purge():
    for m in [m for m in sys.modules if m.partition(".")[0] in PKGS]:
        sys.modules.pop(m, None)


def _use(roots):
    _state["roots"] = dict(roots)
    if _state["finder"] is None:
        _state["finder"] = _Finder()
        sys.meta_path.insert(0, _state["finder"])
    _purge()
    importlib.invalidate_caches()


def init(base_dir):
    """قبل «import yt_dlp»: لو فيه نسخة محدثة سليمة نستخدمها."""
    _state["base"] = base_dir
    if os.path.basename(sys.argv[0] or "").startswith("test_") or os.environ.get("HARMONI_YTDLP_LIVE") == "0":
        return
    c = _cfg()
    roots = {}
    for mod, pkg in PKGS.items():
        ent = c.get(pkg) or {}
        d = os.path.join(_dir(), "%s-%s" % (pkg, ent.get("version")))
        if ent.get("version") and os.path.isfile(os.path.join(d, mod, "__init__.py")):
            roots[mod] = d
    if "yt_dlp" not in roots:
        return
    _use(roots)
    try:
        import yt_dlp  # noqa: F401
        yt_dlp.YoutubeDL
    except Exception:
        # النسخة المحدثة ما تشتغل: نرجع للي داخل البرنامج
        _state["roots"] = {}
        _purge()
        bad = c.get("bad") or []
        bad.append((c.get("yt-dlp") or {}).get("version"))
        c["bad"] = bad[-10:]
        c.pop("yt-dlp", None)
        c.pop("yt-dlp-ejs", None)
        try:
            _save_cfg(c)
        except Exception:
            pass
    _cleanup()


def _cleanup():
    """نمسح النسخ القديمة (نخلي المستخدمة بس)."""
    try:
        c = _cfg()
        keep = {"%s-%s" % (pkg, (c.get(pkg) or {}).get("version")) for pkg in PKGS.values()}
        for name in os.listdir(_dir()):
            p = os.path.join(_dir(), name)
            if os.path.isdir(p) and name not in keep:
                shutil.rmtree(p, ignore_errors=True)
    except Exception:
        pass


def current_version():
    try:
        import yt_dlp.version
        return yt_dlp.version.__version__
    except Exception:
        return ""


def status():
    c = _cfg()
    return {"version": current_version(), "live": bool(_state["roots"]), "checked": _state["checked"],
            "busy": _state["busy"], "last": _state["last"], "updated": (c.get("yt-dlp") or {}).get("at")}


def _pypi(pkg, http_get):
    j = http_get("https://pypi.org/pypi/%s/json" % pkg)
    ver = str(j["info"]["version"])
    for f in j.get("urls") or []:
        if f.get("packagetype") == "bdist_wheel" and str(f.get("filename", "")).endswith("-py3-none-any.whl"):
            return ver, f["url"], f["digests"]["sha256"]
    raise RuntimeError("no wheel for " + pkg)


def _install(pkg, ver, url, sha, http_download):
    mod = pkg.replace("-", "_")
    dest = os.path.join(_dir(), "%s-%s" % (pkg, ver))
    if os.path.isfile(os.path.join(dest, mod, "__init__.py")):
        return dest
    os.makedirs(_dir(), exist_ok=True)
    fd, tmp = tempfile.mkstemp(suffix=".whl", dir=_dir())
    os.close(fd)
    try:
        http_download(url, tmp)
        h = hashlib.sha256()
        with open(tmp, "rb") as f:
            for chunk in iter(lambda: f.read(1 << 20), b""):
                h.update(chunk)
        if h.hexdigest() != sha:
            raise RuntimeError("sha256 mismatch for " + pkg)
        staging = dest + ".part"
        shutil.rmtree(staging, ignore_errors=True)
        with zipfile.ZipFile(tmp) as z:
            for n in z.namelist():
                if n.startswith("/") or ".." in n.split("/"):
                    raise RuntimeError("bad path in wheel")
            z.extractall(staging)
        if not os.path.isfile(os.path.join(staging, mod, "__init__.py")):
            raise RuntimeError("wheel has no " + mod)
        shutil.rmtree(dest, ignore_errors=True)
        os.replace(staging, dest)
        return dest
    finally:
        try:
            os.remove(tmp)
        except OSError:
            pass


def check(http_get, http_download, test=None, force=False):
    """يشيك على تحديث وينزله ويجربه. يرجع النسخة الجديدة لو تحدّث، وإلا None."""
    if not _state["base"]:
        return None
    with _lock:
        if _state["busy"] or (not force and time.time() - _state["checked"] < CHECK_EVERY):
            return None
        _state["busy"] = True
        _state["checked"] = time.time()
    try:
        c = _cfg()
        ver, url, sha = _pypi("yt-dlp", http_get)
        if _vkey(ver) <= _vkey(current_version()) or ver in (c.get("bad") or []):
            _state["last"] = "up_to_date"
            return None
        roots = {"yt_dlp": _install("yt-dlp", ver, url, sha, http_download)}
        ejs = None
        try:
            ejs = _pypi("yt-dlp-ejs", http_get)
            roots["yt_dlp_ejs"] = _install("yt-dlp-ejs", ejs[0], ejs[1], ejs[2], http_download)
        except Exception:
            ejs = None
        old_roots = dict(_state["roots"])
        _use(roots)
        ok, broken = False, False
        try:
            mod = importlib.import_module("yt_dlp")
            mod.YoutubeDL
        except Exception:
            broken = True
        if not broken:
            try:
                ok = bool(test(mod)) if test else True
            except Exception:
                ok = False
        if not ok:
            _use(old_roots)
            importlib.import_module("yt_dlp")
            if broken:   # النسخة نفسها ما تشتغل → ما نجربها مرة ثانية
                c["bad"] = (c.get("bad") or [])[-9:] + [ver]
                _save_cfg(c)
            _state["last"] = ("bad_" if broken else "test_failed_") + ver
            return None
        c["yt-dlp"] = {"version": ver, "at": time.time()}
        if ejs:
            c["yt-dlp-ejs"] = {"version": ejs[0], "at": time.time()}
        _save_cfg(c)
        _state["last"] = "updated_" + ver
        cb = _state.get("on_swap")
        if cb:
            try:
                cb(sys.modules["yt_dlp"])
            except Exception:
                pass
        return ver
    except Exception as e:
        _state["last"] = "error: %s" % (str(e)[:120],)
        return None
    finally:
        _state["busy"] = False
