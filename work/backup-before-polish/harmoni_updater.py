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
            if name.endswith(".exe"):
                url = a.get("browser_download_url") or ""
                break
        data = {"repo": repo, "version": str(j.get("tag_name") or "").lstrip("vV"), "url": url,
                "notes": str(j.get("body") or "")[:2000], "size": 0}
    _CACHE.update(at=now, data=data)
    return data


def check(force=False):
    info = local_info()
    out = {"current": info["version"], "repo": info["repo"], "available": False, "latest": "",
           "notes": "", "can_install": False, "frozen": bool(getattr(sys, "frozen", False))}
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
    return out


def _allowed_url(url, repo):
    return url.startswith("https://github.com/%s/releases/download/" % repo) and url.lower().endswith(".exe")


def _download_and_run(url):
    path = os.path.join(tempfile.gettempdir(), "Harmoni-Update.exe")
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
        subprocess.Popen([path, "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/relaunch=1"],
                         creationflags=flags, close_fds=True)
        time.sleep(1.5)
        if _CFG["exit"]:
            _CFG["exit"]()
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
    out["can_install"] = bool(_CFG["is_local"]() and out["frozen"] and out["available"])
    return jsonify(out)


@bp.route("/api/update/install", methods=["POST"])
def api_install():
    if not _CFG["is_local"]():
        return jsonify({"error": "local only"}), 403
    if not getattr(sys, "frozen", False):
        return jsonify({"error": "not an installed build"}), 400
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
    threading.Thread(target=_download_and_run, args=(url,), daemon=True).start()
    return jsonify({"ok": True})


@bp.route("/api/update/status")
def api_status():
    return jsonify(dict(_STATE))
