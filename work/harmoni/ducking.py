"""تخفيض الصوت التلقائي أثناء الكلام (Audio Ducking) — ويندوز بس.
نراقب مستوى الصوت لبرامج المكالمات (ديسكورد وغيره) أو أي برنامج ثاني، ولما يطلع منها صوت (كلام)
نخفض صوت هارموني نفسه (من خلاط الصوت حق ويندوز، بدون ما نلمس مستوى الصوت اللي اختاره المستخدم)،
وأول ما يسكت الكلام يرجع الصوت بالتدريج.
"""
import json
import os
import sys
import threading
import time

VOICE_APPS = ("discord", "discordptb", "discordcanary", "teamspeak", "ts3client_win64", "ts3client_win32",
              "mumble", "skype", "teams", "ms-teams", "zoom", "whatsapp", "telegram", "slack", "guilded",
              "steamwebhelper", "vesktop", "element", "signal", "viber", "line")

DEFAULTS = {"enabled": False, "amount": 60, "source": "voice", "sensitivity": 50, "release": 1.2}

_state = {"cfg": None, "base": "", "active": False, "level": 1.0, "peak": 0.0, "who": "", "thread": None,
          "available": sys.platform == "win32", "error": ""}
_lock = threading.RLock()


def init(base_dir):
    _state["base"] = base_dir
    _state["cfg"] = None


def _cfg_path():
    return os.path.join(_state["base"], "ducking_config.json")


def config():
    with _lock:
        if _state["cfg"] is None:
            cfg = dict(DEFAULTS)
            try:
                with open(_cfg_path(), encoding="utf-8-sig") as f:
                    got = json.load(f)
                if isinstance(got, dict):
                    cfg.update({k: got[k] for k in DEFAULTS if k in got})
            except Exception:
                pass
            _state["cfg"] = clean(cfg)
        return dict(_state["cfg"])


def clean(cfg):
    out = dict(DEFAULTS)
    try:
        out["enabled"] = bool(cfg.get("enabled"))
        out["amount"] = max(10, min(95, int(cfg.get("amount", DEFAULTS["amount"]))))
        out["source"] = "any" if cfg.get("source") == "any" else "voice"
        out["sensitivity"] = max(1, min(100, int(cfg.get("sensitivity", DEFAULTS["sensitivity"]))))
        out["release"] = max(0.3, min(5.0, float(cfg.get("release", DEFAULTS["release"]))))
    except Exception:
        pass
    return out


def set_config(new):
    with _lock:
        cfg = config()
        cfg.update({k: new[k] for k in DEFAULTS if k in (new or {})})
        cfg = clean(cfg)
        _state["cfg"] = cfg
        try:
            tmp = _cfg_path() + ".tmp"
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump(cfg, f)
            os.replace(tmp, _cfg_path())
        except Exception:
            pass
    if cfg["enabled"]:
        start()
    return cfg


def status():
    cfg = config()
    return {"config": cfg, "available": _state["available"], "active": _state["active"],
            "level": round(_state["level"], 3), "who": _state["who"], "error": _state["error"]}


def threshold(sensitivity):
    """الحساسية 1..100 → أقل مستوى صوت نعتبره كلام (100 = أي همسة)."""
    return 0.002 + (1 - sensitivity / 100.0) * 0.08


def is_voice_app(name):
    n = (name or "").lower()
    if n.endswith(".exe"):
        n = n[:-4]
    return n in VOICE_APPS or n.startswith("discord")


def is_self(name, pid, own_pids):
    # صوت هارموني نفسه = البرنامج + كل العمليات اللي شغّلها (WebView2 اللي يطلع منه الصوت)
    return pid in own_pids or (name or "").lower().startswith("harmoni")


def should_duck(sources, cfg):
    """sources: [(name, pid, peak, mine)] → (هل نخفض، مين السبب)"""
    th = threshold(cfg["sensitivity"])
    best = (0.0, "")
    for name, pid, peak, mine in sources:
        if mine or not pid:   # pid 0 = أصوات ويندوز (التنبيهات)
            continue
        if cfg["source"] == "voice" and not is_voice_app(name):
            continue
        if peak > best[0]:
            best = (peak, name)
    return best[0] >= th, best[1]


def target_level(active, cfg):
    return (1.0 - cfg["amount"] / 100.0) if active else 1.0


def start():
    if not _state["available"]:
        return
    with _lock:
        t = _state["thread"]
        if t and t.is_alive():
            return
        t = threading.Thread(target=_loop, daemon=True, name="harmoni-ducking")
        _state["thread"] = t
        t.start()


def _own_pids():
    try:
        import psutil
        me = psutil.Process(os.getpid())
        return {me.pid} | {c.pid for c in me.children(recursive=True)}
    except Exception:
        return {os.getpid()}


def _loop():
    try:
        import comtypes
        comtypes.CoInitialize()
        from pycaw.pycaw import AudioUtilities, IAudioMeterInformation, ISimpleAudioVolume
    except Exception as e:
        _state["error"] = "pycaw: " + repr(e)[:120]
        _state["available"] = False
        return
    sessions, own, last_enum, last_loud = [], set(), 0.0, 0.0
    saved = {}   # pid → مستوى صوت هارموني بالخلاط قبل ما نخفضه
    level = 1.0
    while True:
        cfg = config()
        now = time.time()
        if not cfg["enabled"] and level >= 0.999:
            _state.update({"active": False, "level": 1.0, "who": ""})
            _restore(saved)
            time.sleep(1.0)
            continue
        if now - last_enum > 2.0:
            last_enum = now
            own = _own_pids()
            sessions = []
            try:
                for s in AudioUtilities.GetAllSessions():
                    try:
                        pid = s.ProcessId
                        name = s.Process.name() if s.Process else ""
                        meter = s._ctl.QueryInterface(IAudioMeterInformation)
                        vol = s._ctl.QueryInterface(ISimpleAudioVolume)
                        sessions.append((name, pid, meter, vol, is_self(name, pid, own)))
                    except Exception:
                        continue
            except Exception as e:
                _state["error"] = repr(e)[:120]
        readings = []
        for name, pid, meter, vol, mine in sessions:
            try:
                readings.append((name, pid, float(meter.GetPeakValue()), mine))
            except Exception:
                continue
        loud, who = should_duck(readings, cfg) if cfg["enabled"] else (False, "")
        if loud:
            last_loud = now
            _state["who"] = who
        active = cfg["enabled"] and (loud or now - last_loud < cfg["release"])
        goal = target_level(active, cfg)
        # نزول سريع (عشان ما يضيع أول الكلام) وطلوع هادي
        step = 0.25 if goal < level else 0.06
        level = goal if abs(goal - level) <= step else level + (step if goal > level else -step)
        _state.update({"active": active, "level": level})
        for name, pid, meter, vol, mine in sessions:
            if not mine:
                continue
            try:
                if pid not in saved:
                    saved[pid] = (vol, float(vol.GetMasterVolume()))
                base = saved[pid][1] or 1.0
                vol.SetMasterVolume(max(0.0, min(1.0, base * level)), None)
            except Exception:
                continue
        if level >= 0.999 and not active:
            _restore(saved)
        time.sleep(0.08)


def _restore(saved):
    for pid, (vol, base) in list(saved.items()):
        try:
            vol.SetMasterVolume(base, None)
        except Exception:
            pass
        saved.pop(pid, None)
