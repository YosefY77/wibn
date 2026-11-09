"""التحديث التلقائي: البرنامج يشيك على GitHub Releases ويحدّث نفسه بضغطة.

- version.json (ينحط داخل البرنامج وقت البناء): {"version": "2026.10.12.1530", "repo": "user/harmoni-app"}
- /api/update/check  : هل فيه نسخة أحدث؟ (كاش 30 دقيقة)
- /api/update/install: ينزّل المثبّت ويشغله بصمت ثم يقفل البرنامج — المثبّت يرجّع يفتحه (/relaunch=1)
- /api/update/status : تقدم التحميل
"""
import json
import os
import re
import subprocess
import sys
import tempfile
import threading
import time

import requests
from flask import Blueprint, jsonify, request

bp = Blueprint("harmoni_updater", __name__)
_CFG = {"bundle_dir": "", "is_local": lambda: False, "log": lambda *a: None, "exit": None}
_CACHE = {"at": 0.0, "data": None}
_STATE = {"phase": "idle", "done": 0, "total": 0, "error": ""}
_LOCK = threading.Lock()
CHECK_TTL = 30 * 60
IS_MAC = sys.platform == "darwin"
ASSET_EXT = ".dmg" if IS_MAC else ".exe"


def _mac_app_path():
    """ماك: مسار Harmoni.app الشغّال (لو يقدر يتحدّث بنفسه)، وإلا ''."""
    if not IS_MAC or not getattr(sys, "frozen", False):
        return ""
    app = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(sys.executable))))
    if not app.endswith(".app") or app.startswith("/Volumes/"):   # شغّال من داخل ملف الـ dmg نفسه
        return ""
    if not os.access(os.path.dirname(app), os.W_OK) or not os.access(app, os.W_OK):
        return ""
    return app


def _mac_manual():
    return IS_MAC and not _mac_app_path()


def init(bundle_dir, is_local, log=None, exit_fn=None):
    _CFG["bundle_dir"] = bundle_dir
    _CFG["is_local"] = is_local
    if log:
        _CFG["log"] = log
    _CFG["exit"] = exit_fn


def local_info():
    try:
        with open(os.path.join(_CFG["bundle_dir"], "version.json"), encoding="utf-8") as f:
            d = json.load(f) or {}
    except Exception:
        d = {}
    repo = str(d.get("repo") or "").strip()
    if not re.match(r"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$", repo):
        repo = ""
    return {"version": str(d.get("version") or "0"), "repo": repo}


def vtuple(v):
    """'v2026.10.12.1530' -> (2026, 10, 12, 1530) للمقارنة."""
    return tuple(int(x) for x in re.findall(r"\d+", str(v or ""))[:6]) or (0,)


def _latest(repo, force=False):
    now = time.time()
    if not force and _CACHE["data"] and now - _CACHE["at"] < CHECK_TTL and _CACHE["data"].get("repo") == repo:
        return _CACHE["data"]
    r = requests.get("https://api.github.com/repos/%s/releases/latest" % repo, timeout=8,
                     headers={"Accept": "application/vnd.github+json", "User-Agent": "Harmoni-Updater"})
    if r.status_code == 404:
        data = {"repo": repo, "version": "", "url": "", "notes": ""}
    else:
        r.raise_for_status()
        j = r.json() or {}
        url = ""
        for a in j.get("assets") or []:
            name = str(a.get("name") or "").lower()
            if name.endswith(ASSET_EXT):
                url = a.get("browser_download_url") or ""
                break
        data = {"repo": repo, "version": str(j.get("tag_name") or "").lstrip("vV"), "url": url,
                "notes": str(j.get("body") or "")[:2000], "size": 0}
    _CACHE.update(at=now, data=data)
    return data


def check(force=False):
    info = local_info()
    out = {"current": info["version"], "repo": info["repo"], "available": False, "latest": "",
           "notes": "", "can_install": False, "frozen": bool(getattr(sys, "frozen", False)), "manual": _mac_manual()}
    if not info["repo"]:
        out["reason"] = "no_repo"
        return out
    try:
        lat = _latest(info["repo"], force)
    except Exception as e:
        out["reason"] = "offline"
        _CFG["log"]("update check failed: " + repr(e)[:120])
        return out
    out["latest"] = lat.get("version") or ""
    out["notes"] = lat.get("notes") or ""
    out["available"] = bool(lat.get("url")) and vtuple(out["latest"]) > vtuple(info["version"])
    pend = _pending()
    if pend:
        out["failed_update"] = pend.get("target") or True
        out["releases"] = "https://github.com/%s/releases/latest" % info["repo"]
    return out


def _allowed_url(url, repo):
    return url.startswith("https://github.com/%s/releases/download/" % repo) and url.lower().endswith(ASSET_EXT)


def _pending_path():
    return os.path.join(tempfile.gettempdir(), "Harmoni-Update-pending.json")


def _install_log():
    return os.path.join(tempfile.gettempdir(), "Harmoni-Update.log")


def _pending():
    """تحديث بدأ قبل وما انطبق؟ (النسخة لسا أقدم من اللي حاولنا نثبتها)"""
    try:
        with open(_pending_path(), encoding="utf-8") as f:
            p = json.load(f) or {}
    except Exception:
        return None
    if vtuple(local_info()["version"]) >= vtuple(p.get("target")) or time.time() - float(p.get("ts") or 0) > 3 * 86400:
        try:
            os.remove(_pending_path())
        except OSError:
            pass
        return None
    return p


def log_tail(n=40):
    try:
        with open(_install_log(), encoding="utf-8", errors="replace") as f:
            return "".join(f.readlines()[-n:])
    except Exception:
        return ""


def _mac_download_and_run(url, target=""):
    """ماك: ننزّل الـ dmg، ننسخ Harmoni.app الجديد جنب القديم، وسكربت صغير يبدّلهم بعد ما نقفل ويفتح الجديد.
    (الملف اللي ينزّله البرنامج نفسه ما عليه «حجر» أبل ⇒ ما يطلع تحذير الأمان بعد التحديث)"""
    app = _mac_app_path()
    ver = re.sub(r"[^0-9.]", "", target) or "new"
    tmpdir = tempfile.mkdtemp(prefix="harmoni-upd-")
    dmg = os.path.join(tmpdir, "Harmoni-%s.dmg" % ver)
    mnt = os.path.join(tmpdir, "mnt")
    staged = os.path.join(os.path.dirname(app), ".Harmoni-update.app")
    attached = False
    try:
        with requests.get(url, stream=True, timeout=(10, 60), headers={"User-Agent": "Harmoni-Updater"}) as r:
            r.raise_for_status()
            total = int(r.headers.get("Content-Length") or 0)
            _STATE.update(phase="downloading", done=0, total=total, error="")
            with open(dmg, "wb") as f:
                for chunk in r.iter_content(256 * 1024):
                    f.write(chunk)
                    _STATE["done"] += len(chunk)
        if os.path.getsize(dmg) < 5 * 1024 * 1024:
            raise RuntimeError("downloaded file is too small")
        _STATE["phase"] = "installing"
        os.makedirs(mnt, exist_ok=True)
        subprocess.run(["/usr/bin/hdiutil", "attach", "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mnt, dmg],
                       check=True, capture_output=True, timeout=120)
        attached = True
        src = os.path.join(mnt, "Harmoni.app")
        if not os.path.isdir(src):
            raise RuntimeError("Harmoni.app not found in the dmg")
        subprocess.run(["/bin/rm", "-rf", staged], timeout=60)
        subprocess.run(["/usr/bin/ditto", src, staged], check=True, capture_output=True, timeout=300)
        subprocess.run(["/usr/bin/hdiutil", "detach", mnt, "-force"], capture_output=True, timeout=60)
        attached = False
        if not os.path.isfile(os.path.join(staged, "Contents", "MacOS", "Harmoni")):
            raise RuntimeError("copied app is incomplete")
        script = os.path.join(tmpdir, "swap.sh")
        q = lambda p: "'" + p.replace("'", "'\\''") + "'"
        with open(script, "w", encoding="utf-8") as f:
            f.write("#!/bin/bash\n"
                    "exec >> %(log)s 2>&1\n"
                    "echo \"$(date) swapping Harmoni -> %(ver)s\"\n"
                    "for i in $(seq 1 100); do kill -0 %(pid)d 2>/dev/null || break; sleep 0.3; done\n"
                    "rm -rf %(old)s\n"
                    "if mv %(app)s %(old)s && mv %(new)s %(app)s; then rm -rf %(old)s; echo ok; "
                    "else echo 'swap failed, restoring'; [ -d %(app)s ] || mv %(old)s %(app)s; fi\n"
                    "xattr -dr com.apple.quarantine %(app)s 2>/dev/null\n"
                    "open %(app)s\n"
                    "rm -rf %(tmp)s\n" % {"log": q(_install_log()), "ver": ver, "pid": os.getpid(), "app": q(app),
                                          "old": q(app + ".old"), "new": q(staged), "tmp": q(tmpdir)})
        os.chmod(script, 0o755)
        try:
            with open(_pending_path(), "w", encoding="utf-8") as f:
                json.dump({"target": target, "from": local_info()["version"], "ts": time.time()}, f)
        except Exception:
            pass
        _CFG["log"]("update: swapping app (mac)")
        subprocess.Popen(["/bin/bash", script], start_new_session=True, close_fds=True,
                         stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(1.0)
        try:
            if _CFG["exit"]:
                _CFG["exit"]()
        finally:
            os._exit(0)
    except Exception as e:
        _STATE.update(phase="error", error=str(e)[:200])
        _CFG["log"]("update failed (mac): " + repr(e)[:200])
        if attached:
            subprocess.run(["/usr/bin/hdiutil", "detach", mnt, "-force"], capture_output=True)
        subprocess.run(["/bin/rm", "-rf", staged, tmpdir], capture_output=True)


def _download_and_run(url, target=""):
    path = os.path.join(tempfile.gettempdir(), "Harmoni-Update-%s.exe" % (re.sub(r"[^0-9.]", "", target) or "new"))
    try:
        with requests.get(url, stream=True, timeout=(10, 60), headers={"User-Agent": "Harmoni-Updater"}) as r:
            r.raise_for_status()
            total = int(r.headers.get("Content-Length") or 0)
            _STATE.update(phase="downloading", done=0, total=total, error="")
            with open(path + ".part", "wb") as f:
                for chunk in r.iter_content(256 * 1024):
                    f.write(chunk)
                    _STATE["done"] += len(chunk)
        with open(path + ".part", "rb") as f:
            head = f.read(2)
        if head != b"MZ" or os.path.getsize(path + ".part") < 1024 * 1024:
            raise RuntimeError("downloaded file is not an installer")
        os.replace(path + ".part", path)
        _STATE["phase"] = "installing"
        _CFG["log"]("update: running installer")
        flags = 0x00000008 | 0x00000200  # DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP
        try:
            with open(_pending_path(), "w", encoding="utf-8") as f:
                json.dump({"target": target, "from": local_info()["version"], "ts": time.time()}, f)
        except Exception:
            pass
        # سجل المثبّت: لو التحديث ما انطبق نعرف السبب (يطلع للمستخدم ويقدر ينسخه)
        subprocess.Popen([path, "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/relaunch=1",
                          "/LOG=" + _install_log()], creationflags=flags, close_fds=True)
        time.sleep(1.5)
        try:
            if _CFG["exit"]:
                _CFG["exit"]()
        finally:
            os._exit(0)
    except Exception as e:
        _STATE.update(phase="error", error=str(e)[:200])
        _CFG["log"]("update failed: " + repr(e)[:200])
        try:
            os.remove(path + ".part")
        except OSError:
            pass


@bp.route("/api/update/check")
def api_check():
    out = check(force=request.args.get("force") == "1")
    out["can_install"] = bool(_CFG["is_local"]() and out["frozen"] and out["available"] and not out["manual"])
    return jsonify(out)


@bp.route("/api/update/install", methods=["POST"])
def api_install():
    if not _CFG["is_local"]():
        return jsonify({"error": "local only"}), 403
    if not getattr(sys, "frozen", False):
        return jsonify({"error": "not an installed build"}), 400
    if _mac_manual():
        return jsonify({"error": "manual"}), 400
    info = check()
    if not info["available"]:
        return jsonify({"error": "no update"}), 400
    url = (_CACHE["data"] or {}).get("url") or ""
    if not _allowed_url(url, info["repo"]):
        return jsonify({"error": "bad url"}), 400
    with _LOCK:
        if _STATE["phase"] in ("downloading", "installing"):
            return jsonify({"ok": True, "already": True})
        _STATE.update(phase="downloading", done=0, total=0, error="")
    target = _mac_download_and_run if IS_MAC else _download_and_run
    threading.Thread(target=target, args=(url, info.get("latest") or ""), daemon=True).start()
    return jsonify({"ok": True})


@bp.route("/api/update/log")
def api_log():
    if not _CFG["is_local"]():
        return jsonify({"error": "local only"}), 403
    return jsonify({"log": log_tail(80)})


@bp.route("/api/update/open_releases", methods=["POST"])
def api_open_releases():
    if not _CFG["is_local"]():
        return jsonify({"error": "local only"}), 403
    repo = local_info()["repo"]
    if not repo:
        return jsonify({"error": "no repo"}), 400
    import webbrowser
    url = "https://github.com/%s/releases/latest" % repo
    if IS_MAC:   # ماك: نفتح رابط الـ dmg مباشرة (ينزل بالمتصفح)
        try:
            u = (_latest(repo) or {}).get("url") or ""
            if _allowed_url(u, repo):
                url = u
        except Exception:
            pass
    webbrowser.open(url)
    return jsonify({"ok": True})


@bp.route("/api/update/status")
def api_status():
    return jsonify(dict(_STATE))
