"""نسخة تطبيق سطح المكتب: نافذة رئيسية للتطبيق + مشغل مصغر عائم (pywebview)."""
import os
import json
import sys
import threading
import time

IS_WIN = sys.platform == "win32"   # الفقاعة والقائمة الجانبية والاختصارات وتأثيرات النافذة: ويندوز بس (حالياً)

_MINI = {"win": None, "visible": False}
_LOCK = threading.RLock()
_HOTKEY_DONE = [False]
_LOOP_READY = [False]
LAST_UID = [""]
_PORT = [5000]

LOG_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "mini_window.log")
if sys.platform == "darwin" and getattr(sys, "frozen", False):   # داخل Harmoni.app ما نقدر نكتب — نحطه مع البيانات
    LOG_PATH = os.path.join(os.environ.get("HARMONI_DATA_DIR") or os.path.expanduser("~/Library/Application Support/Harmoni"),
                            "mini_window.log")
ICON_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "static", "harmoni.ico")


def _set_form_icon(win, label=""):
    """يثبّت الأيقونة على نافذة الويندوز عبر Win32 لضمان ظهورها بالتاسك بار."""
    if not IS_WIN:
        return
    try:
        win.events.loaded.wait(timeout=25)
    except Exception:
        pass
    try:
        import ctypes

        user32 = ctypes.windll.user32
        WM_SETICON = 0x0080
        IMAGE_ICON = 1
        LR_LOADFROMFILE = 0x10
        title = getattr(win, "title", "") or "Harmoni"
        hwnd = user32.FindWindowW(None, title)
        if not hwnd:
            _log(f"taskbar icon {label}: hwnd غير موجود للعنوان '{title}'")
            return
        big = user32.LoadImageW(None, ICON_PATH, IMAGE_ICON, 32, 32, LR_LOADFROMFILE)
        small = user32.LoadImageW(None, ICON_PATH, IMAGE_ICON, 16, 16, LR_LOADFROMFILE)
        ok_big = bool(user32.SendMessageW(hwnd, WM_SETICON, 1, big)) if big else False
        ok_small = bool(user32.SendMessageW(hwnd, WM_SETICON, 0, small)) if small else False
        if big:
            user32.DestroyIcon.restype = None
        _log(f"taskbar icon {label} applied (win32) big={ok_big} small={ok_small}")
    except Exception as e:
        _log(f"win32 icon error {label}: " + repr(e))


def _log(msg):
    try:
        with open(LOG_PATH, "a", encoding="utf-8") as f:
            f.write(time.strftime("[%Y-%m-%d %H:%M:%S] ") + str(msg) + "\n")
    except Exception:
        pass


def set_uid(uid):
    if uid:
        LAST_UID[0] = uid


def status():
    return {
        "window": _MINI["win"] is not None,
        "visible": _MINI["visible"],
        "hotkey": _HOTKEY_DONE[0],
        "uid": bool(LAST_UID[0]),
        "loop": _LOOP_READY[0],
    }


class MiniApi:
    def hide(self):
        try:
            w = _MINI.get("win")
            if w:
                w.hide()
                _MINI["visible"] = False
                _log("hide عبر الصفحة")
        except Exception as e:
            _log("hide error: " + repr(e))

    def quit_app(self):
        try:
            w = _MINI.get("win")
            _MINI["win"] = None
            _MINI["visible"] = False
            if w:
                w.destroy()
            _log("إغلاق المشغل المصغر")
        except Exception:
            pass


def _set_window_alpha(title, alpha):
    """Apply per-window opacity (0-255) via Win32 layered windows."""
    if not IS_WIN:
        return
    import ctypes
    user32 = ctypes.windll.user32
    for _ in range(100):
        hwnd = user32.FindWindowW(None, title)
        if hwnd:
            try:
                GWL_EXSTYLE = -20
                WS_EX_LAYERED = 0x80000
                LWA_ALPHA = 2
                user32.SetWindowLongW(
                    hwnd, GWL_EXSTYLE,
                    user32.GetWindowLongW(hwnd, GWL_EXSTYLE) | WS_EX_LAYERED)
                user32.SetLayeredWindowAttributes(hwnd, 0, int(alpha), LWA_ALPHA)
                _log(f"window alpha applied ({alpha}/255)")
            except Exception as e:
                _log("alpha error: " + repr(e))
            return
        time.sleep(0.15)


def _spawn_mini(port, uid):
    import webview

    api = MiniApi()
    url = f"http://127.0.0.1:{port}/mini?uid={uid}"
    try:
        sw = webview.screens[0].width
    except Exception:
        sw = 1920
    w = webview.create_window(
        "Harmoni · Mini",
        url,
        js_api=api,
        width=470,
        height=250,
        x=24,
        y=40,
        resizable=False,
        frameless=True,
        on_top=True,
        hidden=True,
    )
    _MINI["win"] = w
    threading.Thread(target=_set_form_icon, args=(w, "mini"), daemon=True).start()
    threading.Thread(target=_set_window_alpha, args=("Harmoni · Mini", 255), daemon=True).start()
    _log("نافذة المصغر أُنشئت")

    def show_later():
        for _ in range(80):
            time.sleep(0.15)
            if _MINI["win"] is not w:
                return
            try:
                if w.events.loaded.is_set():
                    break
            except Exception:
                break
        try:
            w.show()
            _MINI["visible"] = True
            _log("المصغر ظاهر")
        except Exception as e:
            _log("show error: " + repr(e))

    threading.Thread(target=show_later, daemon=True).start()


def open_mini(port, uid):
    try:
        if uid:
            LAST_UID[0] = uid
        _PORT[0] = port
        _register_hotkey()
        with _LOCK:
            if _MINI["win"] is not None:
                threading.Thread(target=_hotkey_toggle, daemon=True).start()
                return True
            _spawn_mini(port, LAST_UID[0] or uid)
        return True
    except Exception as e:
        _log("open_mini error: " + repr(e))
        return False


def _hotkey_toggle():
    try:
        w = _MINI.get("win")
        if not w:
            if LAST_UID[0]:
                _log("Alt+M: إنشاء تلقائي للمصغر")
                open_mini(_PORT[0], LAST_UID[0])
            else:
                _log("Alt+M: لا يوجد uid بعد")
            return
        if _MINI["visible"]:
            w.hide()
            _MINI["visible"] = False
            _log("Alt+M: إخفاء")
        else:
            w.show()
            try:
                w.evaluate_js("window.focus && window.focus()")
            except Exception:
                pass
            _MINI["visible"] = True
            _log("Alt+M: إظهار")
    except Exception as e:
        _log("toggle error: " + repr(e))


def _register_hotkey():
    if _HOTKEY_DONE[0]:
        return True
    if not IS_WIN:   # مكتبة keyboard على ماك تحتاج صلاحيات root
        return False
    try:
        import keyboard

        keyboard.add_hotkey("alt+m", lambda: threading.Thread(target=_hotkey_toggle, daemon=True).start())
        keyboard.add_hotkey("alt+shift+m", lambda: threading.Thread(target=MiniApi().quit_app, daemon=True).start())
        _HOTKEY_DONE[0] = True
        _log("الاختصار العام Alt+M مسجل")
        return True
    except Exception as e:
        _log("فشل تسجيل الاختصار العام: " + repr(e))
        return False


def start_background(port=5000):
    _PORT[0] = port
    if IS_WIN:
        threading.Thread(target=_register_hotkey, daemon=True).start()


_BACK_JS = r"""
(function () {
  if (document.getElementById('harmoni-back')) return;
  var home = %s;
  var b = document.createElement('button');
  b.id = 'harmoni-back';
  b.type = 'button';
  b.textContent = '\u2190 \u0631\u062c\u0648\u0639 \u0644\u0647\u0627\u0631\u0645\u0648\u0646\u064a';
  b.title = 'Back to Harmoni (Esc)';
  b.style.cssText = 'position:fixed;top:14px;left:14px;z-index:2147483647;padding:9px 16px;border-radius:999px;' +
    'border:1px solid rgba(255,255,255,.25);background:rgba(20,22,26,.92);color:#fff;font:600 14px Segoe UI,Tahoma,sans-serif;' +
    'cursor:pointer;box-shadow:0 4px 18px rgba(0,0,0,.35)';
  b.onclick = function () { location.href = home; };
  document.addEventListener('keydown', function (e) { if (e.key === 'Escape') location.href = home; });
  (document.body || document.documentElement).appendChild(b);
})();
"""


def _external_back_button(win):
    """لما النافذة تروح لصفحة برا هارموني (مثل تسجيل الدخول في سبوتيفاي) نحط زر «رجوع» عشان يقدر يكنسل."""
    try:
        cur = win.get_current_url() or ""
    except Exception:
        return
    home = "http://127.0.0.1:%d" % _PORT[0]
    if not cur or cur.startswith(home) or cur.startswith("http://localhost:%d" % _PORT[0]) or cur.startswith("about:"):
        return
    try:
        win.evaluate_js(_BACK_JS % json.dumps(home + "/?sp_cancel=1"))
    except Exception as e:
        _log("back button error: " + repr(e))


_STARTUP_LOGGED = [False]


def _log_startup_once(*a):
    if _STARTUP_LOGGED[0]:
        return
    _STARTUP_LOGGED[0] = True
    try:
        _log("startup: page loaded after %.2fs" % (time.time() - float(os.environ.get("HARMONI_T0") or time.time())))
    except Exception:
        pass


_MAC_ACTIVITY = [None]


def _mac_allow_autoplay():
    """ماك: WKWebView افتراضياً يمنع تشغيل الصوت بدون ضغطة من المستخدم — فالأغنية الجاية (لما تخلص الحالية
    أو تضغط التالي وننتظر الرابط) ما تشتغل. pywebview ينشئ الإعدادات بنفسه، فنعدّلها قبل ما ينشئ النافذة."""
    if sys.platform != "darwin":
        return
    try:
        import webview.platforms.cocoa as cocoa
        real = cocoa.WebKit
        if getattr(real, "_harmoni_patched", False):
            return

        class _Alloc:
            def init(self):
                cfg = real.WKWebViewConfiguration.alloc().init()
                try:
                    cfg.setMediaTypesRequiringUserActionForPlayback_(0)   # WKAudiovisualMediaTypeNone
                except Exception as e:
                    _log("autoplay cfg error: " + repr(e))
                try:
                    cfg.setAllowsAirPlayForMediaPlayback_(True)
                except Exception:
                    pass
                return cfg

        class _Cfg:
            @staticmethod
            def alloc():
                return _Alloc()

        class _WebKitProxy:
            _harmoni_patched = True
            WKWebViewConfiguration = _Cfg

            def __getattr__(self, name):
                return getattr(real, name)

        cocoa.WebKit = _WebKitProxy()
        _log("mac autoplay allowed")
    except Exception as e:
        _log("mac autoplay patch error: " + repr(e))


def _mac_no_app_nap():
    """ماك: «App Nap» يبطّئ البرنامج لما نافذته مو قدامك (أو مغطاة) — السيرفر اللي يجيب الأغاني ويمرّر الصوت
    يصير بطيء والأغنية تتأخر أو تعلق. نقول للماك إن البرنامج يشغّل صوت ولازم يبقى سريع."""
    if sys.platform != "darwin":
        return
    try:
        import Foundation
        opts = (getattr(Foundation, "NSActivityUserInitiatedAllowingIdleSystemSleep", 0x00FFFFFF & ~(1 << 20))
                | getattr(Foundation, "NSActivityLatencyCritical", 0xFF00000000))
        _MAC_ACTIVITY[0] = Foundation.NSProcessInfo.processInfo().beginActivityWithOptions_reason_(
            opts, "Harmoni music playback")
        _log("App Nap disabled")
    except Exception as e:
        _log("App Nap disable error: " + repr(e))


def main_loop():
    """يُستدعى من الخيط الرئيسي: ينشئ نافذة التطبيق ويشغّل حلقة العرض."""
    # نوافذ هارموني كلها (الرئيسية + الفقاعة + القائمة الجانبية) من نفس الموقع ⇒ عملية وحدة تعرضها بدل عملية لكل نافذة
    if "--process-per-site" not in os.environ.get("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", ""):
        os.environ["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] = (
            os.environ.get("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "") + " --process-per-site --disable-features=ElasticOverscroll").strip()
    import webview

    if IS_WIN:
        try:
            import ctypes
            ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID("hiro.harmoni.1")
        except Exception:
            pass
    _LOOP_READY[0] = True
    _mac_no_app_nap()
    _mac_allow_autoplay()
    _log("فتح نافذة التطبيق الرئيسية")
    main_opts = dict(width=1280, height=850, min_size=(940, 600), background_color="#0d1113")
    if IS_WIN:
        main_opts["transparent"] = True   # يسمح بالزجاج الحقيقي (Blur ويندوز 11) — الخلفية العادية تبقى معتمة
    try:
        main_win = webview.create_window("Harmoni", f"http://127.0.0.1:{_PORT[0]}/", **main_opts)
    except TypeError:
        main_opts.pop("transparent", None)
        main_win = webview.create_window("Harmoni", f"http://127.0.0.1:{_PORT[0]}/", **main_opts)
    _MAIN["win"] = main_win
    try:
        main_win.events.loaded += lambda *a: _external_back_button(main_win)
    except Exception as e:
        _log("back button hook error: " + repr(e))
    if IS_WIN:
        threading.Thread(target=_main_backcolor_when_ready, daemon=True).start()
        threading.Thread(target=_set_form_icon, args=(main_win, "main"), daemon=True).start()
        try:
            _create_bubble(main_win)
        except Exception as e:
            _log("bubble create error: " + repr(e))
    try:
        if not IS_WIN:
            raise RuntimeError("side panel: windows only")
        import side_panel
        # القائمة الجانبية (Alt+P) نافذة كاملة: ما تنشأ مع البرنامج — تنشأ أول ما تضغط Alt+P، وتنحذف لو ما استخدمتها فترة
        side_panel._P["main"] = main_win
    except Exception as e:
        _log("panel create error: " + repr(e))
    try:
        main_win.events.loaded += _log_startup_once
    except Exception:
        pass
    try:
        if IS_WIN:
            webview.start(private_mode=False, icon=ICON_PATH)
        else:
            # ماك: نفعّل «Inspect Element» بكليك يمين (للتشخيص) بدون ما تنفتح أدوات المطور لحالها
            try:
                webview.settings["OPEN_DEVTOOLS_IN_DEBUG"] = False
            except Exception:
                pass
            webview.start(private_mode=False, debug=True)
    except TypeError:
        webview.start(private_mode=False)
    _log("أُغلقت نافذة التطبيق")


def get_master_volume():
    try:
        from pycaw.pycaw import AudioUtilities, IAudioEndpointVolume
        from comtypes import CLSCTX_ALL

        devices = AudioUtilities.GetSpeakers()
        dev = getattr(devices, "_dev", devices)
        interface = dev.Activate(IAudioEndpointVolume._iid_, CLSCTX_ALL, None)
        vol = interface.QueryInterface(IAudioEndpointVolume)
        return round(vol.GetMasterVolumeLevelScalar() * 100)
    except Exception:
        return None


def set_master_volume(pct):
    try:
        from pycaw.pycaw import AudioUtilities, IAudioEndpointVolume
        from comtypes import CLSCTX_ALL

        devices = AudioUtilities.GetSpeakers()
        dev = getattr(devices, "_dev", devices)
        interface = dev.Activate(IAudioEndpointVolume._iid_, CLSCTX_ALL, None)
        vol = interface.QueryInterface(IAudioEndpointVolume)
        val = min(1.0, max(0.0, float(pct) / 100.0))
        vol.SetMasterVolumeLevelScalar(val, None)
        return True
    except Exception:
        return False



# ======================================================================
#  فقاعة التشغيل العلوية (Bubble)
#  - تظهر لما يكون التطبيق مصغّر (minimized) وترفع الماوس لأعلى الشاشة
#  - تختفي لما تبعد الماوس عنها
#  - الاختصار Alt+B يظهرها/يخفيها في أي وقت
# ======================================================================
BUBBLE_TITLE = "Harmoni · Bubble"
MAIN_TITLE = "Harmoni"
_BUBBLE = {"win": None, "main": None, "hwnd": 0, "visible": False, "pinned_until": 0.0,
           "entered": False, "min_event": False, "hotkey": "", "why": "", "user_hidden": False}

# ===== إعدادات الفقاعة (bubble_config.json بمجلد البيانات) =====
_BCFG_DEFAULT = {"auto": True, "position": "center", "pinned": False}
_BCFG = {"cfg": None}


def _bcfg_path():
    root = os.environ.get("HARMONI_DATA_DIR") or (
        os.path.expanduser("~/Library/Application Support/Harmoni") if sys.platform == "darwin" else
        os.path.join(os.environ.get("LOCALAPPDATA") or os.path.expanduser("~"), "HarmoniData"))
    return os.path.join(root, "bubble_config.json")


def _bcfg_clean(c):
    out = dict(_BCFG_DEFAULT)
    c = c if isinstance(c, dict) else {}
    out["auto"] = bool(c.get("auto", out["auto"]))
    out["pinned"] = bool(c.get("pinned", out["pinned"]))
    out["position"] = c.get("position") if c.get("position") in ("left", "center", "right") else "center"
    return out


def bubble_config():
    if _BCFG["cfg"] is None:
        try:
            with open(_bcfg_path(), encoding="utf-8-sig") as f:
                _BCFG["cfg"] = _bcfg_clean(json.load(f))
        except Exception:
            _BCFG["cfg"] = dict(_BCFG_DEFAULT)
    return dict(_BCFG["cfg"])


def set_bubble_config(new):
    old = bubble_config()
    cfg = _bcfg_clean(dict(old, **{k: v for k, v in (new or {}).items() if k in _BCFG_DEFAULT}))
    _BCFG["cfg"] = cfg
    try:
        os.makedirs(os.path.dirname(_bcfg_path()), exist_ok=True)
        tmp = _bcfg_path() + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(cfg, f)
        os.replace(tmp, _bcfg_path())
    except Exception:
        pass
    hwnd = _BUBBLE.get("hwnd")
    try:
        if hwnd and _BUBBLE["visible"] and cfg["position"] != old["position"]:
            _slide_bubble(hwnd)                # تنزلق بنعومة لمكانها الجديد
        if cfg["pinned"] and not old["pinned"]:
            _BUBBLE["user_hidden"] = False
            if _BUBBLE["visible"]:
                _BUBBLE["why"] = "pin"
    except Exception as e:
        _log("bubble config apply error: " + repr(e))
    _bubble_js("window.__bubbleConfig && window.__bubbleConfig(%s)" % json.dumps(cfg))
    return cfg


def _zone_center(ml, mr):
    pos = bubble_config()["position"]
    if pos == "left":
        return ml + _TRIGGER_HALF
    if pos == "right":
        return mr - _TRIGGER_HALF
    return (ml + mr) / 2

# إعدادات الحساسية
_EDGE_PX = 4            # كم بكسل من أعلى الشاشة يعتبر "فوق"
_TRIGGER_HALF = 230     # نصف عرض منطقة التفعيل (نص الشاشة فقط)
_DWELL = 0.15           # لازم الماوس يبقى فوق هالمدة قبل الظهور
_LEAVE_GRACE = 0.35     # مدة السماح بعد ما يطلع الماوس قبل الإخفاء
_MARGIN = 14            # هامش حول الفقاعة


def _u32():
    import ctypes
    return ctypes.windll.user32


def _own_windows(title):
    """كل النوافذ بهذا العنوان التابعة لنفس البرنامج."""
    import ctypes
    from ctypes import wintypes
    user32 = _u32()
    user32.FindWindowExW.restype = wintypes.HWND
    user32.FindWindowExW.argtypes = [wintypes.HWND, wintypes.HWND, wintypes.LPCWSTR, wintypes.LPCWSTR]
    pid = os.getpid()
    out = []
    hwnd = None
    for _ in range(64):
        hwnd = user32.FindWindowExW(None, hwnd, None, title)
        if not hwnd:
            break
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(wintypes.HWND(hwnd), ctypes.byref(owner))
        if owner.value == pid:
            out.append(hwnd)
    return out


def _find_own_window(title):
    w = _own_windows(title)
    return w[0] if w else 0


def _h(hwnd):
    from ctypes import wintypes
    return wintypes.HWND(hwnd)


def _rect(hwnd):
    import ctypes
    from ctypes import wintypes
    r = wintypes.RECT()
    _u32().GetWindowRect(_h(hwnd), ctypes.byref(r))
    return r.left, r.top, r.right, r.bottom


def _cursor():
    import ctypes
    from ctypes import wintypes
    p = wintypes.POINT()
    _u32().GetCursorPos(ctypes.byref(p))
    return p.x, p.y


def _monitor_at(x, y):
    """حدود الشاشة اللي فيها الماوس (يدعم أكثر من شاشة)."""
    import ctypes
    from ctypes import wintypes

    class MONITORINFO(ctypes.Structure):
        _fields_ = [("cbSize", wintypes.DWORD), ("rcMonitor", wintypes.RECT),
                    ("rcWork", wintypes.RECT), ("dwFlags", wintypes.DWORD)]
    user32 = _u32()
    try:
        user32.MonitorFromPoint.restype = wintypes.HMONITOR
        user32.MonitorFromPoint.argtypes = [wintypes.POINT, wintypes.DWORD]
        mon = user32.MonitorFromPoint(wintypes.POINT(x, y), 2)  # NEAREST
        mi = MONITORINFO()
        mi.cbSize = ctypes.sizeof(MONITORINFO)
        if mon and user32.GetMonitorInfoW(mon, ctypes.byref(mi)):
            r = mi.rcMonitor
            return r.left, r.top, r.right, r.bottom
    except Exception:
        pass
    return 0, 0, user32.GetSystemMetrics(0), user32.GetSystemMetrics(1)


def _main_minimized():
    try:
        user32 = _u32()
        mains = _own_windows(MAIN_TITLE)
        if mains:
            return any(user32.IsIconic(_h(h)) for h in mains)
    except Exception:
        pass
    return bool(_BUBBLE.get("min_event"))


def _style_bubble(hwnd):
    """أداة عائمة: بدون تاسك بار/Alt+Tab، ما تسرق التركيز، فوق كل شيء.
    مو layered: ويندوز 11 ما يدوّر زوايا النوافذ الـ layered (عشان كذا كانت الزوايا مربعة) —
    والظهور صار انزلاق من فوق الشاشة بدل التلاشي، فما نحتاج الشفافية."""
    user32 = _u32()
    GWL_EXSTYLE = -20
    WS_EX_TOOLWINDOW = 0x80
    WS_EX_APPWINDOW = 0x40000
    WS_EX_NOACTIVATE = 0x08000000
    WS_EX_TOPMOST = 0x8
    WS_EX_LAYERED = 0x80000
    ex = user32.GetWindowLongW(_h(hwnd), GWL_EXSTYLE)
    ex = (ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST) & ~(WS_EX_APPWINDOW | WS_EX_LAYERED)
    user32.SetWindowLongW(_h(hwnd), GWL_EXSTYLE, ex)
    # الحواف: ويندوز 11 نفسه يرسم الزوايا الدائرية (ناعمة بدون تسنين) + حد رفيع — بدل قص النافذة بشكل GDI
    # (القص القديم + لون المفتاح كانوا يطلّعون زوايا مسننة وبقايا رمادية)
    try:
        import ctypes
        dwm = ctypes.windll.dwmapi
        pref = ctypes.c_int(2)            # DWMWCP_ROUND
        r1 = dwm.DwmSetWindowAttribute(_h(hwnd), 33, ctypes.byref(pref), ctypes.sizeof(pref))
        border = ctypes.c_uint(0x003A3330)  # COLORREF (BGR) = #30333A حد خفيف
        r2 = dwm.DwmSetWindowAttribute(_h(hwnd), 34, ctypes.byref(border), ctypes.sizeof(border))
        dark = ctypes.c_int(1)
        dwm.DwmSetWindowAttribute(_h(hwnd), 20, ctypes.byref(dark), 4)
        _log("حواف الفقاعة: زوايا ويندوز (round=%s border=%s)" % (r1, r2))
    except Exception as e:
        _log("bubble corners error: " + repr(e))


def _set_alpha(hwnd, alpha):
    import ctypes
    from ctypes import wintypes
    u = _u32()
    u.SetLayeredWindowAttributes.argtypes = [wintypes.HWND, wintypes.DWORD, ctypes.c_ubyte, wintypes.DWORD]
    # LWA_ALPHA بس (للظهور/الاختفاء الناعم) — الزوايا من ويندوز نفسه، ما نحتاج لون مفتاح
    u.SetLayeredWindowAttributes(hwnd, 0, max(0, min(255, int(alpha))), 0x2)


def _round_one(hwnd, w, h, rad):
    import ctypes
    from ctypes import wintypes
    gdi = ctypes.windll.gdi32
    u = _u32()
    gdi.CreateRoundRectRgn.restype = wintypes.HRGN
    gdi.CreateRoundRectRgn.argtypes = [ctypes.c_int] * 6
    u.SetWindowRgn.restype = ctypes.c_int
    u.SetWindowRgn.argtypes = [wintypes.HWND, wintypes.HRGN, wintypes.BOOL]
    rgn = gdi.CreateRoundRectRgn(0, 0, int(w) + 1, int(h) + 1, int(rad * 2), int(rad * 2))
    if not rgn:
        return 0
    return u.SetWindowRgn(hwnd, rgn, True)   # النظام يتملك الـ region بعدها


def _child_windows(hwnd):
    import ctypes
    from ctypes import wintypes
    u = _u32()
    out = []
    proto = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

    def cb(h, _l):
        out.append(h)
        return True
    u.EnumChildWindows.argtypes = [wintypes.HWND, proto, wintypes.LPARAM]
    u.EnumChildWindows(hwnd, proto(cb), 0)
    return out


def _apply_round(hwnd, log=False):
    """حواف دائرية للنافذة وكل النوافذ الداخلية (عشان محتوى WebView2 ينقص معها)."""
    l, t, r, b = _rect(hwnd)
    w, h = r - l, b - t
    rad = 18 * h / 104.0
    res = [_round_one(hwnd, w, h, rad)]
    for ch in _child_windows(hwnd):
        cl, ct, cr, cb = _rect(ch)
        # حدود الابن بالنسبة للأب: نقص نفس الشكل بنفس المكان
        ox, oy = cl - l, ct - t
        import ctypes
        from ctypes import wintypes
        gdi = ctypes.windll.gdi32
        gdi.CreateRoundRectRgn.restype = wintypes.HRGN
        gdi.CreateRoundRectRgn.argtypes = [ctypes.c_int] * 6
        u = _u32()
        u.SetWindowRgn.argtypes = [wintypes.HWND, wintypes.HRGN, wintypes.BOOL]
        rgn = gdi.CreateRoundRectRgn(-ox, -oy, w - ox + 1, h - oy + 1, int(rad * 2), int(rad * 2))
        res.append(u.SetWindowRgn(ch, rgn, True) if rgn else 0)
    if log:
        _log("حواف الفقاعة: %dx%d نصف قطر %.0f نتيجة=%s" % (w, h, rad, res))


_ANIM = {"gen": 0}

# الثيم الزجاجي: النوافذ تصير شفافة شوي (يبين اللي وراها) بدل ما تكون معتمة تماماً
GLASS = {"on": False}
GLASS_MAIN_ALPHA = 255   # النص لازم يكون غير شفاف أبداً — الزجاج داخل التصميم نفسه
GLASS_FLOAT_ALPHA = 255


def float_alpha():
    """أقصى شفافية للفقاعة والقائمة الجانبية."""
    return GLASS_FLOAT_ALPHA if GLASS["on"] else 255


_MAIN = {"win": None}
_DARK_BACK = (13, 17, 19)


def _form_invoke(form, fn):
    from System import Func, Type
    if form.InvokeRequired:
        form.Invoke(Func[Type](fn))
    else:
        fn()


def wv_memory(win, low):
    """WebView2: نافذة مخفية/مصغرة ⇒ «ذاكرة منخفضة» (المتصفح يرمي الكاش اللي ما يحتاجه ويقلل استهلاكه)،
    ولما ترجع تظهر ⇒ عادي. ما يغير شي بالشكل ولا يوقف الصوت."""
    if not IS_WIN or win is None:
        return False
    form = getattr(win, "native", None)
    if form is None:
        return False

    def apply():
        try:
            from Microsoft.Web.WebView2.Core import CoreWebView2MemoryUsageTargetLevel as Lvl
            core = form.browser.webview.CoreWebView2
            if core is not None:
                core.MemoryUsageTargetLevel = Lvl.Low if low else Lvl.Normal
        except Exception as e:
            _log("wv memory error: " + repr(e)[:160])
    try:
        _form_invoke(form, apply)
        return True
    except Exception:
        return False


def _set_backcolor(rgb):
    w = _MAIN.get("win")
    form = getattr(w, "native", None) if w else None
    if form is None:
        return False
    from System.Drawing import Color

    def apply():
        form.BackColor = Color.FromArgb(255, rgb[0], rgb[1], rgb[2])
        form.Invalidate()
    _form_invoke(form, apply)
    return True


def _main_backcolor_when_ready():
    """النافذة صارت transparent: نعطيها لون التطبيق الداكن عشان ما يطلع لون ويندوز الرمادي."""
    for _ in range(120):
        w = _MAIN.get("win")
        if w is not None and getattr(w, "native", None) is not None:
            break
        time.sleep(0.25)
    try:
        if not GLASS["on"]:
            _set_backcolor(_DARK_BACK)
    except Exception as e:
        _log("backcolor error: " + repr(e))


def _main_hwnd():
    w = _MAIN.get("win")
    form = getattr(w, "native", None) if w else None
    if form is not None:
        try:
            return int(form.Handle.ToInt64())
        except Exception:
            pass
    hs = _own_windows(MAIN_TITLE)
    return hs[0] if hs else 0


def _hex_rgb(color):
    c = str(color or "").lstrip("#")
    if len(c) != 6:
        return (138, 180, 255)
    try:
        return tuple(int(c[i:i + 2], 16) for i in (0, 2, 4))
    except ValueError:
        return (138, 180, 255)


def _refresh_frame(h):
    """نخلي ويندوز يعيد رسم إطار النافذة بلونها الجديد — بدون هز الحجم (كان يسبب وميض)."""
    try:
        user32 = _u32()
        SWP = 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020 | 0x0200   # NOSIZE|NOMOVE|NOZORDER|NOACTIVATE|FRAMECHANGED|NOOWNERZORDER
        user32.SetWindowPos(_h(h), None, 0, 0, 0, 0, SWP)
        user32.RedrawWindow(_h(h), None, None, 0x0001 | 0x0004 | 0x0080 | 0x0100 | 0x0400)   # INVALIDATE|ERASE|FRAME|ALLCHILDREN|UPDATENOW
    except Exception as e:
        _log("glass refresh error: " + repr(e))


GLASS.update({"color": "#8ab4ff", "alpha": 0.7, "blur": 75.0, "applied": None, "moving": False, "hooked": False, "mode": "off", "blur_ok": True})


def _accent(h, state, gradient=0):
    import ctypes
    from ctypes import wintypes

    class ACCENT(ctypes.Structure):
        _fields_ = [("AccentState", ctypes.c_int), ("AccentFlags", ctypes.c_int),
                    ("GradientColor", ctypes.c_uint), ("AnimationId", ctypes.c_int)]

    class WCAD(ctypes.Structure):
        _fields_ = [("Attribute", ctypes.c_int), ("Data", ctypes.POINTER(ACCENT)), ("SizeOfData", ctypes.c_size_t)]

    ac = ACCENT(state, 2, gradient, 0)
    data = WCAD(19, ctypes.pointer(ac), ctypes.sizeof(ac))
    return _u32().SetWindowCompositionAttribute(wintypes.HWND(h), ctypes.byref(data))


def _extend(h, full):
    import ctypes
    from ctypes import wintypes

    class MARGINS(ctypes.Structure):
        _fields_ = [("l", ctypes.c_int), ("r", ctypes.c_int), ("t", ctypes.c_int), ("b", ctypes.c_int)]

    v = -1 if full else 0
    m = MARGINS(v, v, v, v)
    ctypes.windll.dwmapi.DwmExtendFrameIntoClientArea(wintypes.HWND(h), ctypes.byref(m))


def _glass_level():
    """شريط الضبابية: 0 = بدون غبش، 1 = غبش (ويندوز يرسم غبش اللي ورا النافذة)."""
    return 0 if float(GLASS["blur"]) < 8 else 1


def windows_transparency_enabled():
    """«تأثيرات الشفافية» في إعدادات ويندوز — لو مطفية ويندوز يرسم الغبش لون صلب."""
    try:
        import winreg
        k = winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")
        v, _ = winreg.QueryValueEx(k, "EnableTransparency")
        return bool(v)
    except Exception:
        return True


_KEY_RGB = (255, 0, 254)    # لون «المفتاح»: أي بكسل بهاللون في خلفية النافذة يصير شفاف تماماً


def _set_layered_key(form, on):
    from System.Drawing import Color
    if on:
        k = Color.FromArgb(255, *_KEY_RGB)
        if form.BackColor.ToArgb() != k.ToArgb():
            form.BackColor = k
        if form.TransparencyKey.ToArgb() != k.ToArgb():
            form.TransparencyKey = k
    else:
        try:
            if not form.TransparencyKey.IsEmpty:
                form.TransparencyKey = Color.Empty
        except Exception:
            pass


def _apply_glass_now(force=False):
    """يطبّق الزجاج على النافذة (من خيط الواجهة). اللون والـ grain ترسمهم الصفحة نفسها.
    طريقتين (جربناهم على جهاز فعلي):
    - بدون غبش (أو «تأثيرات الشفافية» بويندوز مطفية): خلفية النافذة لون مفتاح شفاف ← اللي ورا النافذة يبين صافي 100%
    - مع غبش: ويندوز 11 يرسم Acrylic ورا النافذة (يحتاج «تأثيرات الشفافية» مفعّلة)"""
    import ctypes
    from ctypes import wintypes
    w = _MAIN.get("win")
    form = getattr(w, "native", None) if w else None
    h = _main_hwnd()
    if not h or form is None:
        return False
    user32 = _u32()
    dwm = ctypes.windll.dwmapi
    try:
        form.browser.webview.DefaultBackgroundColor = __import__("System.Drawing", fromlist=["Color"]).Color.Transparent
    except Exception:
        pass
    dark = ctypes.c_int(1)
    dwm.DwmSetWindowAttribute(wintypes.HWND(h), 20, ctypes.byref(dark), 4)
    if not GLASS["on"]:
        _set_layered_key(form, False)
        try:
            class _BB(ctypes.Structure):
                _fields_ = [("dwFlags", wintypes.DWORD), ("fEnable", wintypes.BOOL), ("hRgnBlur", wintypes.HRGN), ("fTransitionOnMaximized", wintypes.BOOL)]
            off = _BB(0x1, False, None, False)
            dwm.DwmEnableBlurBehindWindow(wintypes.HWND(h), ctypes.byref(off))
        except Exception:
            pass
        _accent(h, 0)
        v = ctypes.c_int(1)
        dwm.DwmSetWindowAttribute(wintypes.HWND(h), 38, ctypes.byref(v), 4)
        _extend(h, False)
        GLASS["applied"] = None
        GLASS["mode"] = "off"
        return True
    blur_ok = windows_transparency_enabled()
    mode = "blur" if (_glass_level() and blur_ok) else "clear"
    key = (mode,)
    if not force and GLASS["applied"] == key:
        return True
    # الطريقة اللي نجحت على جهاز فعلي (B): نافذة عادية (مو layered — عشان الضغطات توصل للبرنامج مو للي وراه)
    # + DwmEnableBlurBehindWindow بمنطقة فاضية (يخلي ويندوز يحترم شفافية البكسلات) + accent + تمديد الإطار
    _set_layered_key(form, False)
    ex = user32.GetWindowLongW(_h(h), -20)
    if ex & 0x80000:
        user32.SetWindowLongW(_h(h), -20, ex & ~0x80000)
    _set_backcolor_now(form, (0, 0, 0))

    class DWM_BLURBEHIND(ctypes.Structure):
        _fields_ = [("dwFlags", wintypes.DWORD), ("fEnable", wintypes.BOOL), ("hRgnBlur", wintypes.HRGN), ("fTransitionOnMaximized", wintypes.BOOL)]
    if mode == "clear":
        # صافي (جرّبناه على جهاز فعلي): BlurBehind بمنطقة فاضية + accent شفاف → اللي ورا يبين بدون غبش، والضغطات توصل للبرنامج
        v = ctypes.c_int(1)
        dwm.DwmSetWindowAttribute(wintypes.HWND(h), 38, ctypes.byref(v), 4)
        rgn = ctypes.windll.gdi32.CreateRectRgn(0, 0, -1, -1)
        bb = DWM_BLURBEHIND(0x1 | 0x2, True, rgn, False)
        dwm.DwmEnableBlurBehindWindow(wintypes.HWND(h), ctypes.byref(bb))
        ctypes.windll.gdi32.DeleteObject(rgn)
        ok = bool(_accent(h, 2, 0))
    else:
        # غبش (جرّبناه على جهاز فعلي): Acrylic ويندوز 11 (backdrop 3) بدون BlurBehind ولا accent
        bb = DWM_BLURBEHIND(0x1, False, None, False)
        dwm.DwmEnableBlurBehindWindow(wintypes.HWND(h), ctypes.byref(bb))
        _accent(h, 0)
        v = ctypes.c_int(3)
        ok = dwm.DwmSetWindowAttribute(wintypes.HWND(h), 38, ctypes.byref(v), 4) == 0
        if not ok:                                   # ويندوز 10: الطريقة القديمة
            ok = bool(_accent(h, 4, 0x01000000))
    _extend(h, True)
    GLASS["applied"] = key
    GLASS["mode"] = mode
    GLASS["blur_ok"] = blur_ok
    form.Invalidate(True)
    _refresh_frame(h)
    return bool(ok)


def _hook_main_events():
    """نراقب سحب/تكبير/تصغير النافذة عشان الزجاج ما يقطّع ولا يخرب بعد الرجوع."""
    if GLASS["hooked"]:
        return
    w = _MAIN.get("win")
    form = getattr(w, "native", None) if w else None
    if form is None:
        return
    try:
        from System import EventHandler

        def begin(sender, args):
            if GLASS["on"]:
                GLASS["moving"] = True
                _safe_apply()

        def end(sender, args):
            if GLASS["moving"]:
                GLASS["moving"] = False
                _safe_apply()

        state_box = {"ws": None}

        def resized(sender, args):
            ws = str(form.WindowState)
            if ws != state_box["ws"]:
                state_box["ws"] = ws
                if GLASS["on"] and ws != "Minimized":
                    # بعد التكبير/الرجوع من التصغير ويندوز أحياناً يفقد الغبش — نعيده بعد ما يستقر الرسم
                    threading.Timer(0.15, lambda: _form_invoke(form, lambda: _safe_apply(True))).start()

        def activated(sender, args):
            if GLASS["on"]:
                _safe_apply()

        form.ResizeBegin += EventHandler(begin)
        form.ResizeEnd += EventHandler(end)
        form.Resize += EventHandler(resized)
        form.Activated += EventHandler(activated)
        GLASS["hooked"] = True
    except Exception as e:
        _log("glass hook error: " + repr(e))


def _safe_apply(force=False):
    try:
        _apply_glass_now(force)
    except Exception as e:
        _log("glass apply error: " + repr(e))


def set_glass(on, color="#8ab4ff", alpha=0.7, blur=75, mac=False):
    """الوضع الزجاجي الحقيقي: ويندوز يلوّن ويغبّش خلفية النافذة بلون المستخدم وشفافيته.
    - غبش (Acrylic) لما blur >= 15، وإلا لون شفاف بدون غبش
    - وقت سحب النافذة/تغيير حجمها نوقف الغبش مؤقتاً (عشان ما تقطّع) ونرجعه أول ما تفلت
    - بعد التكبير/التصغير نعيد تطبيقه (ويندوز أحياناً يضيعه)"""
    GLASS.update({"on": bool(on), "color": color, "alpha": float(alpha), "blur": float(blur)})
    if not IS_WIN:
        return False
    w = _MAIN.get("win")
    form = getattr(w, "native", None) if w else None
    if form is None:
        return False
    result = {"ok": False}

    def run():
        _hook_main_events()
        result["ok"] = _apply_glass_now()
        if not GLASS["on"]:
            _set_backcolor_now(form, _DARK_BACK)
    try:
        _form_invoke(form, run)
        _log("الثيم الزجاجي: %s (color=%s alpha=%.2f blur=%s)" % ("مفعّل" if GLASS["on"] else "مطفي", color, float(alpha), blur))
    except Exception as e:
        _log("glass error: " + repr(e))
        return False
    return bool(result["ok"])


def _set_backcolor_now(form, rgb):
    from System.Drawing import Color
    c = Color.FromArgb(255, rgb[0], rgb[1], rgb[2])
    if form.BackColor.ToArgb() != c.ToArgb():
        form.BackColor = c
        form.Invalidate(True)   # الأسود هو اللي ويندوز يحوله لزجاج — لازم ينرسم فعلاً


_FADE_IN = 0.18     # ثواني
_FADE_OUT = 0.2


def _place_bubble(hwnd, show, cx=None):
    """ظهور: الفقاعة تنزل من فوق الشاشة لمكانها (تبدأ سريعة وتهدّي، مع ارتداد خفيف مثل أبل).
    اختفاء: ترجع لفوق وتطلع من الشاشة. «تأثيرات الحركة» مطفية بويندوز ⇒ تطلع/تختفي على طول."""
    from ctypes import wintypes
    user32 = _u32()
    SWP_NOSIZE, SWP_NOACTIVATE, SWP_SHOWWINDOW, SWP_HIDEWINDOW = 0x1, 0x10, 0x40, 0x80
    _ANIM["gen"] += 1
    _ANIM["slide"] = _ANIM.get("slide", 0) + 1     # يوقف أي انزلاق يمين/يسار شغال
    gen = _ANIM["gen"]
    l, t, r, b = _rect(hwnd)
    H = max(1, b - t)
    anim = _animations_on()
    if show:
        nx, ny = _bubble_xy(hwnd, cx)
        ml, mt, mr, mb = _monitor_at(nx + (r - l) // 2, ny)
        y0 = mt - H - 6                                  # فوق حافة الشاشة (مخفية)
        user32.SetWindowPos(_h(hwnd), wintypes.HWND(-1), nx, ny if not anim else y0, 0, 0,
                            SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW)
        if not anim:
            return

        def down():
            dur, start = 0.3, time.perf_counter()
            c1, c3 = 1.25, 2.25                          # easeOutBack: تتجاوز مكانها شوي وترجع (ارتداد ناعم)
            while _ANIM["gen"] == gen:
                k = min(1.0, (time.perf_counter() - start) / dur)
                e = 1 + c3 * (k - 1) ** 3 + c1 * (k - 1) ** 2
                user32.SetWindowPos(_h(hwnd), wintypes.HWND(-1), nx, int(round(y0 + (ny - y0) * e)), 0, 0,
                                    SWP_NOSIZE | SWP_NOACTIVATE)
                if k >= 1.0:
                    return
                time.sleep(1 / 144)
        threading.Thread(target=down, daemon=True, name="harmoni-bubble-in").start()
        return

    def gone():
        user32.SetWindowPos(_h(hwnd), wintypes.HWND(-1), -4000, -4000, 0, 0,
                            SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW)
    if not anim:
        gone()
        return
    ml, mt, mr, mb = _monitor_at((l + r) // 2, max(t, 0) + 1)
    y1 = mt - H - 6

    def up():
        dur, start = 0.16, time.perf_counter()
        while _ANIM["gen"] == gen:
            k = min(1.0, (time.perf_counter() - start) / dur)
            e = k ** 3                                   # ease-in: تبدأ هادية وتسرع لفوق
            user32.SetWindowPos(_h(hwnd), wintypes.HWND(-1), l, int(round(t + (y1 - t) * e)), 0, 0,
                                SWP_NOSIZE | SWP_NOACTIVATE)
            if k >= 1.0:
                gone()
                return
            time.sleep(1 / 144)
    threading.Thread(target=up, daemon=True, name="harmoni-bubble-out").start()


def _animations_on():
    """إعدادات ويندوز ← إمكانية الوصول ← «تأثيرات الحركة» (لو مطفية: ننقل على طول بدون حركة)."""
    try:
        import ctypes
        v = ctypes.c_int(1)
        if ctypes.windll.user32.SystemParametersInfoW(0x1042, 0, ctypes.byref(v), 0):   # SPI_GETCLIENTAREAANIMATION
            return bool(v.value)
    except Exception:
        pass
    return True


def _slide_bubble(hwnd, dur=0.42):
    """تغيير المكان (يسار/وسط/يمين) والفقاعة ظاهرة: تنزلق للمكان الجديد بحركة ناعمة (تبدأ سريعة وتهدّي بالنهاية)."""
    from ctypes import wintypes
    user32 = _u32()
    l, t, r, b = _rect(hwnd)
    W = max(1, r - l)
    ml, mt, mr, mb = _monitor_at((l + r) // 2, (t + b) // 2)   # نفس الشاشة اللي عليها الفقاعة
    pos = bubble_config()["position"]
    tx = int(ml + 16) if pos == "left" else int(mr - W - 16) if pos == "right" else int((ml + mr) / 2 - W / 2)
    ty = mt + 8
    _ANIM["slide"] = _ANIM.get("slide", 0) + 1
    gen = _ANIM["slide"]
    # الفقاعة بتبعد عن الماوس وهي تنتقل: نخليها ظاهرة شوي بدل ما تختفي على طول
    _BUBBLE["entered"] = False
    _BUBBLE["pinned_until"] = time.time() + 3.0
    flags = 0x1 | 0x10 | 0x4   # NOSIZE | NOACTIVATE | NOZORDER
    if not _animations_on() or abs(tx - l) < 2:
        user32.SetWindowPos(_h(hwnd), None, tx, ty, 0, 0, flags)
        return

    def run():
        x0, y0 = l, t
        start = time.perf_counter()
        while _ANIM.get("slide") == gen and _BUBBLE.get("visible"):
            k = min(1.0, (time.perf_counter() - start) / dur)
            e = 1 - (1 - k) ** 4                       # ease-out (مثل حركة أبل: انطلاقة سريعة ووقفة ناعمة)
            user32.SetWindowPos(_h(hwnd), None, int(round(x0 + (tx - x0) * e)), int(round(y0 + (ty - y0) * e)), 0, 0, flags)
            if k >= 1.0:
                return
            time.sleep(1 / 144)
    threading.Thread(target=run, daemon=True, name="harmoni-bubble-slide").start()


def _bubble_xy(hwnd, cx=None):
    l, t, r, b = _rect(hwnd)
    W = max(1, r - l)
    x, y = _cursor()
    ml, mt, mr, mb = _monitor_at(x, y)
    pos = bubble_config()["position"]
    if cx is not None:
        nx = int(min(max(cx - W / 2, ml + 8), mr - W - 8))
    elif pos == "left":
        nx = int(ml + 16)
    elif pos == "right":
        nx = int(mr - W - 16)
    else:
        nx = int((ml + mr) / 2 - W / 2)
    return nx, mt + 8


def _bubble_js(code):
    def run():
        try:
            w = _BUBBLE.get("win")
            if w:
                w.evaluate_js(code)
        except Exception:
            pass
    threading.Thread(target=run, daemon=True).start()


def _bubble_show(hwnd, cx=None, why=""):
    if _BUBBLE["visible"]:
        return
    _BUBBLE["why"] = why
    _BUBBLE["visible"] = True
    _BUBBLE["entered"] = False
    wv_memory(_BUBBLE.get("win"), False)
    _place_bubble(hwnd, True, cx)
    _bubble_js("window.__bubbleShown && window.__bubbleShown()")
    _log("الفقاعة ظهرت (%s)" % why)


def _bubble_hide(hwnd, why=""):
    if not _BUBBLE["visible"]:
        return
    _BUBBLE["visible"] = False
    _BUBBLE["pinned_until"] = 0.0
    _bubble_js("window.__bubbleHiding && window.__bubbleHiding()")
    _place_bubble(hwnd, False)
    threading.Timer(1.0, lambda: (not _BUBBLE["visible"]) and wv_memory(_BUBBLE.get("win"), True)).start()
    _log("الفقاعة اختفت (%s)" % why)


# حالة يرسلها التطبيق الرئيسي للسيرفر (احتياط لو evaluate_js ما رد)، وأوامر تنتظر التنفيذ
_PUSHED = {"state": None, "ts": 0.0}
_CMDQ = []
_CMDQ_LOCK = threading.Lock()


def push_state(state):
    """يُستدعى من /api/bubble_state — يرجّع الأوامر اللي تنتظر."""
    if _PUSHED["state"] is None and state is not None:
        _log("أول حالة وصلت من التطبيق: %s" % ((state or {}).get("title") or "لا شيء"))
    errs = (state or {}).get("errors") or []
    if errs and not _PUSHED.get("errs_logged"):
        _PUSHED["errs_logged"] = True
        _log("أخطاء في صفحة التطبيق: %s" % " | ".join(str(e) for e in errs))
    _PUSHED["state"] = state
    _PUSHED["ts"] = time.time()
    with _CMDQ_LOCK:
        out = list(_CMDQ)
        _CMDQ.clear()
    return out


def _eval_main(code, timeout=0.7):
    """evaluate_js على النافذة الرئيسية مع مهلة (ما يعلّق لو النافذة مصغّرة)."""
    main = _BUBBLE.get("main")
    if not main:
        return None, False
    box = {}

    def run():
        try:
            box["v"] = main.evaluate_js(code)
        except Exception as e:
            box["e"] = e
    th = threading.Thread(target=run, daemon=True)
    th.start()
    th.join(timeout)
    if th.is_alive():
        return None, False
    if "e" in box:
        return None, False
    return box.get("v"), True


_DIAG = {"state_fail": 0}


class BubbleApi:
    def state(self):
        if not _BUBBLE["visible"]:
            return {"hidden": True}
        import json
        raw, ok = _eval_main("window.__harmoniBubble ? window.__harmoniBubble.state() : ''")
        data = None
        if ok and raw:
            try:
                data = json.loads(raw) if isinstance(raw, str) else raw
            except Exception:
                data = None
        if not data or not data.get("title"):
            pushed = _PUSHED.get("state")
            if pushed and time.time() - _PUSHED["ts"] < 8 and (pushed.get("title") or not data):
                if pushed.get("playing"):
                    pushed = dict(pushed)
                    pushed["position"] = float(pushed.get("position") or 0) + (time.time() - _PUSHED["ts"])
                data = pushed
            if _DIAG["state_fail"] < 5 and not (ok and raw):
                _DIAG["state_fail"] += 1
                _log("bubble state: evaluate_js ما رد (ok=%s) — استخدام الحالة المرسلة" % ok)
        return data

    def get_config(self):
        return bubble_config()

    def set_config(self, cfg):
        return set_bubble_config(cfg if isinstance(cfg, dict) else {})

    def cmd(self, action, value=None):
        if action not in ("play_pause", "skip", "prev", "mute", "seek", "volume"):
            return False
        import json
        cid = "%x" % int(time.time() * 1000000)
        arg = "null" if value is None else json.dumps(float(value))
        res, ok = _eval_main("window.__harmoniBubble && window.__harmoniBubble.cmd(%s, %s, %s)"
                             % (json.dumps(action), arg, json.dumps(cid)))
        if not ok:
            with _CMDQ_LOCK:
                _CMDQ.append({"id": cid, "action": action, "value": value})
            _log("bubble cmd عبر الطابور: %s" % action)
        return True


def _bubble_toggle():
    hwnd = _BUBBLE.get("hwnd")
    if not hwnd:
        _log("Alt+B: الفقاعة مو جاهزة")
        return
    if _BUBBLE["visible"]:
        _BUBBLE["user_hidden"] = True     # لو مثبتة: ما ترجع تطلع لين يتصغر البرنامج مرة ثانية
        _bubble_hide(hwnd, "Alt+B")
    else:
        _BUBBLE["user_hidden"] = False
        _bubble_show(hwnd, None, "Alt+B")
        _BUBBLE["pinned_until"] = time.time() + 4.0   # تبقى 4 ثواني حتى لو الماوس بعيد


def _hotkey_thread():
    """Alt+B عبر RegisterHotKey (أثبت من مكتبة keyboard)، ولو فشل نرجع لمكتبة keyboard."""
    import ctypes
    from ctypes import wintypes
    user32 = _u32()
    MOD_ALT, MOD_NOREPEAT, VK_B, VK_P, WM_HOTKEY = 0x1, 0x4000, 0x42, 0x50, 0x0312
    panel_ok = bool(user32.RegisterHotKey(None, 0xB0C, MOD_ALT | MOD_NOREPEAT, VK_P))
    _log("اختصار القائمة الجانبية Alt+P " + ("مسجل" if panel_ok else "فشل (برنامج ثاني ماسكه؟)"))
    if user32.RegisterHotKey(None, 0xB0B, MOD_ALT | MOD_NOREPEAT, VK_B) or panel_ok:
        _BUBBLE["hotkey"] = "RegisterHotKey"
        _log("اختصار الفقاعة Alt+B مسجل (RegisterHotKey)")
        msg = wintypes.MSG()
        while user32.GetMessageW(ctypes.byref(msg), None, 0, 0) > 0:
            if msg.message == WM_HOTKEY:
                if msg.wParam == 0xB0C:
                    try:
                        import side_panel
                        threading.Thread(target=side_panel.toggle, daemon=True).start()
                    except Exception as e:
                        _log("panel toggle error: " + repr(e))
                else:
                    threading.Thread(target=_bubble_toggle, daemon=True).start()
        return
    _log("RegisterHotKey(Alt+B) فشل — غالباً برنامج ثاني ماسك الاختصار، نجرب مكتبة keyboard")
    try:
        import keyboard
        keyboard.add_hotkey("alt+b", lambda: threading.Thread(target=_bubble_toggle, daemon=True).start())
        _BUBBLE["hotkey"] = "keyboard"
        _log("اختصار الفقاعة Alt+B مسجل (keyboard)")
    except Exception as e:
        _log("فشل تسجيل اختصار الفقاعة: " + repr(e))


def _bubble_watch():
    """مراقبة خفيفة لمكان الماوس وحالة النافذة الرئيسية (كل 50ms)."""
    hwnd = 0
    for _ in range(400):
        try:
            hwnd = _find_own_window(BUBBLE_TITLE)
        except Exception as e:
            _log("bubble find error: " + repr(e))
            return
        if hwnd:
            break
        time.sleep(0.15)
    if not hwnd:
        _log("bubble: النافذة غير موجودة")
        return
    _BUBBLE["hwnd"] = hwnd
    w = _BUBBLE.get("win")
    try:
        w.events.loaded.wait(timeout=25)
    except Exception:
        pass
    user32 = _u32()
    _style_bubble(hwnd)
    try:
        from ctypes import wintypes
        _u32().SetWindowPos(_h(hwnd), wintypes.HWND(-1), -4000, -4000, 0, 0, 0x1 | 0x10 | 0x80)
    except Exception:
        pass
    _BUBBLE["visible"] = False
    wv_memory(w, True)
    mains = _own_windows(MAIN_TITLE)
    try:
        if mains and not user32.IsIconic(_h(mains[0])):
            user32.SetForegroundWindow(_h(mains[0]))
    except Exception:
        pass
    threading.Thread(target=_hotkey_thread, daemon=True).start()
    _log("الفقاعة جاهزة: bubble=%s main=%s" % (hwnd, mains))

    edge_since = 0.0
    out_since = 0.0
    was_min = False
    last_diag = 0.0
    diag_left = 40
    while _BUBBLE.get("win") is not None:
        time.sleep(0.05)
        try:
            if not user32.IsWindow(_h(hwnd)):
                _log("bubble: النافذة انسكرت")
                return
            minimized = _main_minimized()
            x, y = _cursor()
            now = time.time()
            if minimized != was_min:
                was_min = minimized
                if minimized:
                    _BUBBLE["user_hidden"] = False
                _log("التطبيق %s" % ("تصغّر" if minimized else "رجع"))
            cfg = bubble_config()
            if not _BUBBLE["visible"]:
                if not minimized:
                    edge_since = 0.0
                    continue
                if cfg["pinned"] and not _BUBBLE["user_hidden"]:
                    _bubble_show(hwnd, None, "pin")      # مثبتة: تطلع على طول لما يتصغر البرنامج
                    continue
                if not cfg["auto"]:
                    continue
                ml, mt, mr, mb = _monitor_at(x, y)
                at_top = y <= mt + _EDGE_PX and abs(x - _zone_center(ml, mr)) <= _TRIGGER_HALF
                if diag_left > 0 and (at_top or now - last_diag > 3):
                    last_diag = now
                    diag_left -= 1
                    _log("تشخيص: mouse=(%d,%d) monitor=(%d,%d,%d,%d) top=%s" % (x, y, ml, mt, mr, mb, at_top))
                if at_top:
                    edge_since = edge_since or now
                    if now - edge_since >= _DWELL:
                        _bubble_show(hwnd, None, "الماوس فوق")
                        out_since = 0.0
                        edge_since = 0.0
                else:
                    edge_since = 0.0
                continue
            if cfg["pinned"]:
                if not minimized and _BUBBLE.get("why") != "Alt+B":
                    _bubble_hide(hwnd, "التطبيق رجع")
                continue                                   # مثبتة: ما تختفي لو بعدت الماوس
            l, t, r, b = _rect(hwnd)
            inside = (l - _MARGIN <= x <= r + _MARGIN) and (t - 40 <= y <= b + _MARGIN)
            if inside:
                _BUBBLE["entered"] = True
                out_since = 0.0
                continue
            if now < _BUBBLE["pinned_until"] and not _BUBBLE["entered"]:
                continue
            out_since = out_since or now
            if now - out_since >= _LEAVE_GRACE:
                _bubble_hide(hwnd, "الماوس بعيد")
                out_since = 0.0
        except Exception as e:
            _log("bubble watch error: " + repr(e))
            time.sleep(1)


def _create_bubble(main_win):
    import webview
    _BUBBLE["main"] = main_win
    opts = dict(
        js_api=BubbleApi(),
        width=520,   # الغلاف ياخذ الربع اليمين
        height=104,
        x=-4000,
        y=-4000,
        resizable=False,
        frameless=True,
        on_top=True,
        background_color="#15191d",
        shadow=False,   # بدون إطار/ظل ويندوز اللي كان يطلع كحواف حادة ورا الفقاعة
        focus=False,
        easy_drag=False,   # الفقاعة ثابتة: ما تتحرك لو سحبتها بالماوس بالغلط
    )
    try:
        w = webview.create_window(BUBBLE_TITLE, f"http://127.0.0.1:{_PORT[0]}/bubble", **opts)
    except TypeError:
        opts.pop("shadow", None)
        opts.pop("focus", None)
        opts.pop("easy_drag", None)
        w = webview.create_window(BUBBLE_TITLE, f"http://127.0.0.1:{_PORT[0]}/bubble", **opts)
    _BUBBLE["win"] = w

    def _on_main_closed(*_a):
        # لما تتسكر النافذة الرئيسية نسكر الفقاعة والمصغر عشان البرنامج يطلع بالكامل
        bw = _BUBBLE.get("win")
        _BUBBLE["win"] = None
        try:
            import side_panel
            side_panel.destroy()
        except Exception:
            pass
        for other in (bw, _MINI.get("win")):
            try:
                if other:
                    other.destroy()
            except Exception:
                pass
        _MINI["win"] = None

    def _on_min(*_a):
        _BUBBLE["min_event"] = True
        wv_memory(main_win, True)    # البرنامج مصغّر: ذاكرة أقل (الصوت يكمل عادي)

    def _on_restore(*_a):
        _BUBBLE["min_event"] = False
        wv_memory(main_win, False)

    for name, fn in (("closed", _on_main_closed), ("minimized", _on_min),
                     ("restored", _on_restore), ("maximized", _on_restore), ("shown", _on_restore)):
        try:
            ev = getattr(main_win.events, name)
            ev += fn
        except Exception:
            pass
    threading.Thread(target=_bubble_watch, daemon=True).start()
    _log("نافذة الفقاعة أُنشئت")


_ROOM_WINS = []


def open_room_window(url):
    """غرفة صاحبك (على جهازه) في نافذة هارموني منفصلة — نفس شكل البرنامج، بدون ما تطلع للمتصفح."""
    if not _LOOP_READY[0]:
        return False
    import webview

    for w in list(_ROOM_WINS):
        try:
            w.destroy()
        except Exception:
            pass
        _ROOM_WINS.remove(w)
    w = webview.create_window("Harmoni · غرفة صديق", url, width=1180, height=800, min_size=(900, 600),
                              background_color="#0d1113")
    _ROOM_WINS.append(w)
    threading.Thread(target=_set_form_icon, args=(w, "room"), daemon=True).start()
    _log("فتح غرفة صديق: " + url.split("?")[0])
    return True


def focus_main():
    """نسخة ثانية من البرنامج انفتحت: بدالها نرجّع النافذة الموجودة للواجهة."""
    if not IS_WIN:
        w = _MAIN.get("win")
        if not w:
            return False
        for fn in ("restore", "show"):
            try:
                getattr(w, fn)()
            except Exception:
                pass
        return True
    import ctypes
    h = _main_hwnd()
    if not h:
        return False
    user32 = _u32()
    if user32.IsIconic(_h(h)):
        user32.ShowWindow(_h(h), 9)          # SW_RESTORE
    else:
        user32.ShowWindow(_h(h), 5)          # SW_SHOW
    try:
        # ويندوز يمنع سرقة التركيز — نضغط Alt وهمي عشان يسمح
        user32.keybd_event(0x12, 0, 0, 0)
        user32.keybd_event(0x12, 0, 2, 0)
    except Exception:
        pass
    user32.SetForegroundWindow(_h(h))
    return True
