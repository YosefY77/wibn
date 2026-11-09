"""القائمة الجانبية (Side Panel): نافذة ثابتة على يمين أو يسار الشاشة.

- تنفتح وتتسكر بالاختصار Alt+P (لو مفعلة من الإعدادات)
- فيها الـ CD اللي يلف، الأغنية الحالية، التحكم الكامل، والبلاي لست الحالية أو المفضلة
- ما تتحرك بالماوس، وتختفي لو ضغطت أي مكان برّاها أو نفس الاختصار
"""
import json
import re
import threading
import time

import mini_window as mw

PANEL_TITLE = "Harmoni · Panel"
PANEL_WIDTH = 380
CONFIG = {"enabled": False, "side": "right"}
_P = {"win": None, "hwnd": 0, "visible": False, "shown_at": 0.0, "gen": 0, "prev": 0, "fs": False, "refocus": 0,
      "ready": False, "hidden_at": 0.0, "creating": False}
IDLE_FREE = 180   # القائمة مخفية 3 دقايق ⇒ نحذف نافذتها (توفير ذاكرة)، وAlt+P الجاي ينشئها من جديد


def set_config(cfg):
    cfg = cfg if isinstance(cfg, dict) else {}
    CONFIG["enabled"] = bool(cfg.get("enabled"))
    CONFIG["side"] = "left" if cfg.get("side") == "left" else "right"
    if not CONFIG["enabled"] and _P["visible"]:
        hide("disabled")
    if not CONFIG["enabled"] and _P.get("win") is not None:
        destroy()   # انطفت من الإعدادات: نحرر ذاكرتها


def _work_area_at_cursor():
    """منطقة العمل (بدون شريط المهام) للشاشة اللي فيها الماوس."""
    import ctypes
    from ctypes import wintypes

    class MONITORINFO(ctypes.Structure):
        _fields_ = [("cbSize", wintypes.DWORD), ("rcMonitor", wintypes.RECT),
                    ("rcWork", wintypes.RECT), ("dwFlags", wintypes.DWORD)]
    user32 = mw._u32()
    x, y = mw._cursor()
    try:
        user32.MonitorFromPoint.restype = wintypes.HMONITOR
        user32.MonitorFromPoint.argtypes = [wintypes.POINT, wintypes.DWORD]
        mon = user32.MonitorFromPoint(wintypes.POINT(x, y), 2)
        mi = MONITORINFO()
        mi.cbSize = ctypes.sizeof(MONITORINFO)
        if mon and user32.GetMonitorInfoW(mon, ctypes.byref(mi)):
            r = mi.rcWork
            return r.left, r.top, r.right, r.bottom
    except Exception:
        pass
    return 0, 0, user32.GetSystemMetrics(0), user32.GetSystemMetrics(1)


def _style(hwnd):
    """بدون تاسك بار، فوق كل شيء، وتدعم الشفافية (للظهور/الاختفاء الناعم)."""
    user32 = mw._u32()
    GWL_EXSTYLE = -20
    WS_EX_TOOLWINDOW, WS_EX_APPWINDOW, WS_EX_TOPMOST, WS_EX_LAYERED = 0x80, 0x40000, 0x8, 0x80000
    ex = user32.GetWindowLongW(mw._h(hwnd), GWL_EXSTYLE)
    ex = (ex | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_LAYERED) & ~WS_EX_APPWINDOW
    user32.SetWindowLongW(mw._h(hwnd), GWL_EXSTYLE, ex)
    _alpha(hwnd, 0)


def _alpha(hwnd, a):
    import ctypes
    from ctypes import wintypes
    u = mw._u32()
    u.SetLayeredWindowAttributes.argtypes = [wintypes.HWND, wintypes.DWORD, ctypes.c_ubyte, wintypes.DWORD]
    u.SetLayeredWindowAttributes(hwnd, 0, max(0, min(255, int(a))), 0x2)


def _force_foreground(hwnd):
    """ويندوز يمنع سرقة التركيز، فنربط الخيط مؤقتاً بخيط النافذة الحالية."""
    import ctypes
    user32 = mw._u32()
    kernel32 = ctypes.windll.kernel32
    try:
        fg = user32.GetForegroundWindow()
        fg_thread = user32.GetWindowThreadProcessId(fg, None) if fg else 0
        me = kernel32.GetCurrentThreadId()
        attached = bool(fg_thread and fg_thread != me and user32.AttachThreadInput(me, fg_thread, True))
        user32.SetForegroundWindow(mw._h(hwnd))
        user32.SetFocus(mw._h(hwnd))
        if attached:
            user32.AttachThreadInput(me, fg_thread, False)
    except Exception as e:
        mw._log("panel focus error: " + repr(e))


def _is_fullscreen(hwnd):
    """النافذة مغطية الشاشة كلها (لعبة أو فيديو بوضع ملء الشاشة)."""
    if not hwnd:
        return False
    import ctypes
    from ctypes import wintypes

    class MONITORINFO(ctypes.Structure):
        _fields_ = [("cbSize", wintypes.DWORD), ("rcMonitor", wintypes.RECT),
                    ("rcWork", wintypes.RECT), ("dwFlags", wintypes.DWORD)]
    try:
        user32 = mw._u32()
        if hwnd in (user32.GetDesktopWindow(), user32.GetShellWindow()):
            return False
        l, t, r, b = mw._rect(hwnd)
        user32.MonitorFromWindow.restype = wintypes.HMONITOR
        mon = user32.MonitorFromWindow(mw._h(hwnd), 2)
        mi = MONITORINFO()
        mi.cbSize = ctypes.sizeof(MONITORINFO)
        if not mon or not user32.GetMonitorInfoW(mon, ctypes.byref(mi)):
            return False
        m = mi.rcMonitor
        return l <= m.left and t <= m.top and r >= m.right and b >= m.bottom
    except Exception:
        return False


def _monitor_rect_of(hwnd):
    import ctypes
    from ctypes import wintypes

    class MONITORINFO(ctypes.Structure):
        _fields_ = [("cbSize", wintypes.DWORD), ("rcMonitor", wintypes.RECT),
                    ("rcWork", wintypes.RECT), ("dwFlags", wintypes.DWORD)]
    try:
        user32 = mw._u32()
        user32.MonitorFromWindow.restype = wintypes.HMONITOR
        mon = user32.MonitorFromWindow(mw._h(hwnd), 2)
        mi = MONITORINFO()
        mi.cbSize = ctypes.sizeof(MONITORINFO)
        if mon and user32.GetMonitorInfoW(mon, ctypes.byref(mi)):
            m = mi.rcMonitor
            return m.left, m.top, m.right, m.bottom
    except Exception:
        pass
    return None


def _notification_state():
    """3 = لعبة DirectX بملء الشاشة الحصري (ما فيه نافذة تقدر تطلع فوقها بدون ما تطلع من اللعبة)."""
    try:
        import ctypes
        st = ctypes.c_int(0)
        if ctypes.windll.shell32.SHQueryUserNotificationState(ctypes.byref(st)) == 0:
            return st.value
    except Exception:
        pass
    return 0


def _top(hwnd):
    """نرجعها فوق كل شيء (الألعاب بملء الشاشة أحياناً تحط نفسها topmost بعدنا)."""
    from ctypes import wintypes
    try:
        mw._u32().SetWindowPos(mw._h(hwnd), wintypes.HWND(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10)  # NOSIZE|NOMOVE|NOACTIVATE
    except Exception:
        pass


def _release_cursor():
    """الألعاب تحبس الماوس داخلها (ClipCursor) — نفكه عشان تقدر تستخدم القائمة."""
    try:
        mw._u32().ClipCursor(None)
    except Exception:
        pass


def _restore_prev():
    """بعد ما تتسكر القائمة نرجع اللعبة/البرنامج اللي كان قدامك."""
    prev = _P.get("prev") or 0
    _P["prev"] = 0
    if not prev:
        return
    try:
        user32 = mw._u32()
        if not user32.IsWindow(mw._h(prev)):
            return
        if user32.IsIconic(mw._h(prev)):
            user32.ShowWindow(mw._h(prev), 9)   # SW_RESTORE
        _force_foreground(prev)
    except Exception as e:
        mw._log("panel restore error: " + repr(e))


def _js(code):
    def run():
        try:
            w = _P.get("win")
            if w:
                w.evaluate_js(code)
        except Exception:
            pass
    threading.Thread(target=run, daemon=True).start()


def show(why=""):
    hwnd = _P.get("hwnd")
    if not hwnd or _P["visible"]:
        return
    from ctypes import wintypes
    user32 = mw._u32()
    l, t, r, b = _work_area_at_cursor()
    wl, wt, wr, wb = mw._rect(hwnd)
    w = max(300, wr - wl)
    x = l if CONFIG["side"] == "left" else r - w
    _P["gen"] += 1
    gen = _P["gen"]
    prev = user32.GetForegroundWindow() or 0
    _P["prev"] = prev if prev and prev != hwnd else 0
    _P["fs"] = _is_fullscreen(_P["prev"])
    qs = _notification_state()
    if _P["fs"]:
        # فوق لعبة/برنامج بملء الشاشة: نستخدم حدود الشاشة كاملة (ما فيه شريط مهام ظاهر)
        l, t, r, b = _monitor_rect_of(_P["prev"]) or (l, t, r, b)
        x = l if CONFIG["side"] == "left" else r - w
    _alpha(hwnd, 0)
    _release_cursor()
    user32.SetWindowPos(mw._h(hwnd), wintypes.HWND(-1), x, t, w, b - t, 0x40 | 0x10)  # SHOWWINDOW|NOACTIVATE
    _P["visible"] = True
    _P["shown_at"] = time.time()
    _P["refocus"] = 0
    mw.wv_memory(_P.get("win"), False)
    _force_foreground(hwnd)
    try:
        user32.BringWindowToTop(mw._h(hwnd))
    except Exception:
        pass
    _top(hwnd)
    _js("window.__panelShown && window.__panelShown(%s)" % json.dumps(CONFIG["side"]))
    mw._log("القائمة الجانبية ظهرت (%s) side=%s fullscreen=%s state=%s" % (why, CONFIG["side"], _P["fs"], qs))

    def fade():
        start = time.perf_counter()
        while _P["gen"] == gen:
            k = min(1.0, (time.perf_counter() - start) / 0.16)
            _alpha(hwnd, mw.float_alpha() * k)
            if k >= 1:
                return
            time.sleep(1 / 120)
    threading.Thread(target=fade, daemon=True).start()


def hide(why="", restore=True):
    hwnd = _P.get("hwnd")
    if not hwnd or not _P["visible"]:
        return
    from ctypes import wintypes
    user32 = mw._u32()
    _P["visible"] = False
    _P["hidden_at"] = time.time()
    _P["gen"] += 1
    gen = _P["gen"]
    threading.Timer(1.0, lambda: (not _P["visible"]) and mw.wv_memory(_P.get("win"), True)).start()
    mw._log("القائمة الجانبية اختفت (%s)" % why)

    def fade():
        start = time.perf_counter()
        while _P["gen"] == gen:
            k = min(1.0, (time.perf_counter() - start) / 0.14)
            _alpha(hwnd, mw.float_alpha() * (1 - k))
            if k >= 1:
                user32.SetWindowPos(mw._h(hwnd), wintypes.HWND(-1), -6000, -6000, 0, 0, 0x1 | 0x10 | 0x80)
                if restore:
                    _restore_prev()   # نرجّع اللعبة/البرنامج اللي كان قدامك
                else:
                    _P["prev"] = 0
                return
            time.sleep(1 / 120)
    threading.Thread(target=fade, daemon=True).start()


def toggle():
    if not CONFIG["enabled"]:
        mw._log("Alt+P: القائمة الجانبية مقفلة من الإعدادات")
        return
    if _P["visible"]:
        hide("Alt+P")
        return
    if not _P.get("ready"):
        # أول مرة (أو انحذفت لأنها ما استُخدمت فترة): ننشئها الحين وننتظرها تجهز (~ثانية)
        if not _P.get("creating"):
            _P["creating"] = True
            try:
                if _P.get("win") is None:
                    if _P.get("main") is None:
                        return
                    create(_P["main"])
            finally:
                _P["creating"] = False
        for _ in range(100):
            if _P.get("ready"):
                break
            time.sleep(0.05)
        if not _P.get("ready"):
            mw._log("Alt+P: القائمة ما جهزت")
            return
    show("Alt+P")


class PanelApi:
    def state(self):
        if not _P["visible"]:
            return {"hidden": True}
        raw, ok = mw._eval_main("window.__harmoniBubble ? window.__harmoniBubble.state() : ''", timeout=1.0)
        try:
            data = json.loads(raw) if ok and isinstance(raw, str) and raw else None
        except Exception:
            data = None
        return data or mw._PUSHED.get("state")

    def lists(self):
        raw, ok = mw._eval_main("window.__harmoniPanel ? window.__harmoniPanel.lists() : ''", timeout=1.5)
        try:
            return json.loads(raw) if ok and isinstance(raw, str) and raw else None
        except Exception:
            return None

    def playlist(self, pid):
        pid = str(pid or "")
        if not re.match(r"^[\w.-]{1,64}$", pid):
            return None
        raw, ok = mw._eval_main("window.__harmoniPanel ? window.__harmoniPanel.playlist(%s) : ''" % json.dumps(pid), timeout=1.5)
        try:
            return json.loads(raw) if ok and isinstance(raw, str) and raw else None
        except Exception:
            return None

    def play_track(self, track):
        """أغنية من البحث داخل القائمة الجانبية."""
        if not isinstance(track, dict):
            return False
        clean = {}
        for k in ("title", "artist", "thumbnail", "web_url", "source"):
            v = track.get(k)
            if isinstance(v, str):
                clean[k] = v[:500]
        try:
            clean["duration"] = int(float(track.get("duration") or 0))
        except Exception:
            clean["duration"] = 0
        if not clean.get("title"):
            return False
        mw._eval_main("window.__harmoniPanel && window.__harmoniPanel.playTrack(%s)" % json.dumps(clean))
        return True

    def play(self, kind, index):
        kind = str(kind or "")
        if kind != "likes" and not re.match(r"^pl:[\w.-]{1,64}$", kind):
            return False
        try:
            index = int(index)
        except Exception:
            return False
        mw._eval_main("window.__harmoniPanel && window.__harmoniPanel.play(%s, %d)" % (json.dumps(kind), index))
        return True

    def cmd(self, action, value=None):
        if action not in ("play_pause", "skip", "prev", "mute", "seek", "volume", "loop", "shuffle", "like"):
            return False
        arg = "null" if value is None else json.dumps(float(value))
        mw._eval_main("window.__harmoniPanel && window.__harmoniPanel.cmd(%s, %s)" % (json.dumps(action), arg))
        return True

    def hide(self):
        hide("زر/Esc")
        return True


def _watch():
    """تختفي لو ضغطت برّا القائمة (النافذة الأمامية صارت غيرها)."""
    hwnd = 0
    for _ in range(400):
        hwnd = mw._find_own_window(PANEL_TITLE)
        if hwnd:
            break
        time.sleep(0.15)
    if not hwnd:
        mw._log("panel: النافذة غير موجودة")
        return
    _P["hwnd"] = hwnd
    try:
        _P["win"].events.loaded.wait(timeout=25)
    except Exception:
        pass
    user32 = mw._u32()
    from ctypes import wintypes
    user32.GetAncestor.restype = wintypes.HWND
    user32.GetAncestor.argtypes = [wintypes.HWND, wintypes.UINT]
    _style(hwnd)
    user32.SetWindowPos(mw._h(hwnd), wintypes.HWND(-1), -6000, -6000, 0, 0, 0x1 | 0x10 | 0x80)
    _P["ready"] = True
    _P["hidden_at"] = time.time()
    mw._log("القائمة الجانبية جاهزة (enabled=%s side=%s)" % (CONFIG["enabled"], CONFIG["side"]))
    away_since = 0.0
    last_top = 0.0
    was_down = False
    me = _P.get("win")
    while _P.get("win") is me and me is not None:
        time.sleep(0.05)
        if not _P["visible"]:
            away_since = 0.0
            was_down = False
            if time.time() - (_P.get("hidden_at") or time.time()) > IDLE_FREE:
                mw._log("القائمة الجانبية: ما استُخدمت %d ثانية — نحذف نافذتها لين تحتاجها" % IDLE_FREE)
                destroy()
                return
            continue
        try:
            now = time.time()
            if now - last_top > 0.25:          # نبقى فوق اللعبة حتى لو حاولت تطلع فوقنا
                _top(hwnd)
                last_top = now
            # ضغطة ماوس برّا القائمة = تختفي (أدق من مراقبة النافذة الأمامية مع الألعاب)
            down = bool((user32.GetAsyncKeyState(0x01) | user32.GetAsyncKeyState(0x02)) & 0x8000)
            if down and not was_down and now - _P["shown_at"] > 0.3:
                cx, cy = mw._cursor()
                l, t, r, b = mw._rect(hwnd)
                if not (l <= cx < r and t <= cy < b):
                    # فوق لعبة بملء الشاشة: الضغطة برّا = رجعة للعبة. غير كذا: المستخدم ضغط على شي ثاني فنخليه
                    hide("ضغطة برّا القائمة", restore=_P["fs"])
                    was_down = down
                    continue
            was_down = down
            fg = user32.GetForegroundWindow()
            root = user32.GetAncestor(fg, 2) if fg else None   # GA_ROOT
            mine = (root or fg) == hwnd
            if mine or now - _P["shown_at"] < 0.6:
                away_since = 0.0
                continue
            prev = _P.get("prev") or 0
            if fg and (root or fg) == prev:
                # اللعبة سحبت التركيز منا: نرجعه كم مرة بس، والقائمة تبقى ظاهرة فوقها
                if _P["refocus"] < 4 and now - _P["shown_at"] < 4:
                    _P["refocus"] += 1
                    _force_foreground(hwnd)
                away_since = 0.0
                continue
            away_since = away_since or now
            if now - away_since > 0.15:
                hide("تحولت لبرنامج ثاني", restore=False)
                away_since = 0.0
        except Exception as e:
            mw._log("panel watch error: " + repr(e))
            time.sleep(1)


def create(main_win):
    import webview
    opts = dict(js_api=PanelApi(), width=PANEL_WIDTH, height=700, x=-6000, y=-6000,
                resizable=False, frameless=True, on_top=True, background_color="#141a1d",
                shadow=False, easy_drag=False)
    url = "http://127.0.0.1:%d/panel" % mw._PORT[0]
    try:
        w = webview.create_window(PANEL_TITLE, url, **opts)
    except TypeError:
        for k in ("shadow", "easy_drag"):
            opts.pop(k, None)
        w = webview.create_window(PANEL_TITLE, url, **opts)
    _P["win"] = w
    _P["hwnd"] = 0
    _P["ready"] = False
    threading.Thread(target=_watch, daemon=True).start()
    mw._log("نافذة القائمة الجانبية أُنشئت")
    return w


def destroy():
    w = _P.get("win")
    _P["win"] = None
    _P["hwnd"] = 0
    _P["ready"] = False
    _P["visible"] = False
    try:
        if w:
            w.destroy()
    except Exception:
        pass
