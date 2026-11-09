"""نسخة تطبيق سطح المكتب: نافذة رئيسية للتطبيق + مشغل مصغر عائم (pywebview)."""
import os
import threading
import time

_MINI = {"win": None, "visible": False}
_LOCK = threading.RLock()
_HOTKEY_DONE = [False]
_LOOP_READY = [False]
LAST_UID = [""]
_PORT = [5000]

LOG_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "mini_window.log")
ICON_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "static", "harmoni.ico")


def _set_form_icon(win, label=""):
    """يثبّت الأيقونة على نافذة الويندوز عبر Win32 لضمان ظهورها بالتاسك بار."""
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
    threading.Thread(target=_register_hotkey, daemon=True).start()


def main_loop():
    """يُستدعى من الخيط الرئيسي: ينشئ نافذة التطبيق ويشغّل حلقة العرض."""
    import webview
    import ctypes

    try:
        ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID("hiro.harmoni.1")
    except Exception:
        pass
    _LOOP_READY[0] = True
    _log("فتح نافذة التطبيق الرئيسية")
    main_opts = dict(width=1280, height=850, min_size=(940, 600),
                     transparent=True,             # يسمح بالزجاج الحقيقي (Blur ويندوز 11) — الخلفية العادية تبقى معتمة
                     background_color="#0d1113")
    try:
        main_win = webview.create_window("Harmoni", f"http://127.0.0.1:{_PORT[0]}/", **main_opts)
    except TypeError:
        main_opts.pop("transparent", None)
        main_win = webview.create_window("Harmoni", f"http://127.0.0.1:{_PORT[0]}/", **main_opts)
    _MAIN["win"] = main_win
    threading.Thread(target=_main_backcolor_when_ready, daemon=True).start()
    threading.Thread(target=_set_form_icon, args=(main_win, "main"), daemon=True).start()
    try:
        _create_bubble(main_win)
    except Exception as e:
        _log("bubble create error: " + repr(e))
    try:
        import side_panel
        side_panel.create(main_win)
    except Exception as e:
        _log("panel create error: " + repr(e))
    try:
        webview.start(private_mode=False, icon=ICON_PATH)
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
           "entered": False, "min_event": False, "hotkey": ""}

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
    """أداة عائمة: بدون تاسك بار/Alt+Tab، ما تسرق التركيز، فوق كل شيء، وتدعم الشفافية."""
    user32 = _u32()
    GWL_EXSTYLE = -20
    WS_EX_TOOLWINDOW = 0x80
    WS_EX_APPWINDOW = 0x40000
    WS_EX_NOACTIVATE = 0x08000000
    WS_EX_TOPMOST = 0x8
    WS_EX_LAYERED = 0x80000
    ex = user32.GetWindowLongW(_h(hwnd), GWL_EXSTYLE)
    ex = (ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_LAYERED) & ~WS_EX_APPWINDOW
    user32.SetWindowLongW(_h(hwnd), GWL_EXSTYLE, ex)
    _set_alpha(hwnd, 0)
    try:
        import ctypes
        dwm = ctypes.windll.dwmapi
        pref = ctypes.c_int(1)            # DWMWCP_DONOTROUND: الشكل نحدده نحن فقط
        dwm.DwmSetWindowAttribute(_h(hwnd), 33, ctypes.byref(pref), ctypes.sizeof(pref))
        none = ctypes.c_uint(0xFFFFFFFE)  # DWMWA_COLOR_NONE: بدون إطار ويندوز
        dwm.DwmSetWindowAttribute(_h(hwnd), 34, ctypes.byref(none), ctypes.sizeof(none))
    except Exception:
        pass
    _apply_round(hwnd, log=True)


def _set_alpha(hwnd, alpha):
    import ctypes
    from ctypes import wintypes
    u = _u32()
    u.SetLayeredWindowAttributes.argtypes = [wintypes.HWND, wintypes.DWORD, ctypes.c_ubyte, wintypes.DWORD]
    # LWA_ALPHA للشفافية + LWA_COLORKEY: لون الزوايا (#010203) يصير شفاف تماماً
    u.SetLayeredWindowAttributes(hwnd, 0x00030201, max(0, min(255, int(alpha))), 0x2 | 0x1)


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
    """ويندوز ما يعيد رسم لون/غبش النافذة إلا لما تنرسم من جديد — نجبره (وإذا مو مكبّرة نهزّ حجمها بكسل)."""
    import ctypes
    from ctypes import wintypes
    try:
        user32 = _u32()
        SWP = 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020   # NOSIZE|NOMOVE|NOZORDER|NOACTIVATE|FRAMECHANGED
        user32.SetWindowPos(_h(h), None, 0, 0, 0, 0, SWP)
        if not user32.IsZoomed(_h(h)) and not user32.IsIconic(_h(h)):
            r = wintypes.RECT()
            if user32.GetWindowRect(_h(h), ctypes.byref(r)):
                w, ht = r.right - r.left, r.bottom - r.top
                flags = 0x0002 | 0x0004 | 0x0010   # NOMOVE|NOZORDER|NOACTIVATE
                user32.SetWindowPos(_h(h), None, 0, 0, w + 1, ht, flags)
                user32.SetWindowPos(_h(h), None, 0, 0, w, ht, flags)
        user32.RedrawWindow(_h(h), None, None, 0x0001 | 0x0004 | 0x0080 | 0x0100 | 0x0400)
    except Exception as e:
        _log("glass refresh error: " + repr(e))


def set_glass(on, color="#8ab4ff", alpha=0.7, blur=75):
    """الوضع الزجاجي الحقيقي: لون المستخدم يتحط بخلفية النافذة نفسها (ويندوز) بالشفافية اللي يختارها.
    الطريقة اللي نجحت بالتجربة (D/F): BackColor أسود + DwmExtendFrameIntoClientArea + SetWindowCompositionAttribute
    - blur >= 15: ACCENT_ENABLE_ACRYLICBLURBEHIND (غبش)   - أقل: ACCENT_ENABLE_TRANSPARENTGRADIENT (بدون غبش)
    وما يتحول رمادي لما البرنامج مو النافذة الأمامية (عكس backdrop ويندوز 11)."""
    import ctypes
    from ctypes import wintypes
    GLASS["on"] = bool(on)
    ok = False
    try:
        user32 = _u32()
        dwm = ctypes.windll.dwmapi

        class MARGINS(ctypes.Structure):
            _fields_ = [("l", ctypes.c_int), ("r", ctypes.c_int), ("t", ctypes.c_int), ("b", ctypes.c_int)]

        class ACCENT(ctypes.Structure):
            _fields_ = [("AccentState", ctypes.c_int), ("AccentFlags", ctypes.c_int),
                        ("GradientColor", ctypes.c_uint), ("AnimationId", ctypes.c_int)]

        class WCAD(ctypes.Structure):
            _fields_ = [("Attribute", ctypes.c_int), ("Data", ctypes.POINTER(ACCENT)), ("SizeOfData", ctypes.c_size_t)]

        def accent(h, state, gradient=0):
            ac = ACCENT(state, 2, gradient, 0)
            data = WCAD(19, ctypes.pointer(ac), ctypes.sizeof(ac))
            return user32.SetWindowCompositionAttribute(wintypes.HWND(h), ctypes.byref(data))

        h = _main_hwnd()
        if not h:
            return False
        GWL_EXSTYLE, WS_EX_LAYERED = -20, 0x80000
        ex = user32.GetWindowLongW(_h(h), GWL_EXSTYLE)
        if ex & WS_EX_LAYERED:
            user32.SetLayeredWindowAttributes.argtypes = [wintypes.HWND, wintypes.DWORD, ctypes.c_ubyte, wintypes.DWORD]
            user32.SetLayeredWindowAttributes(_h(h), 0, 255, 0x2)
            user32.SetWindowLongW(_h(h), GWL_EXSTYLE, ex & ~WS_EX_LAYERED)
        dark = ctypes.c_int(1)
        dwm.DwmSetWindowAttribute(wintypes.HWND(h), 20, ctypes.byref(dark), 4)
        none = ctypes.c_int(1)
        dwm.DwmSetWindowAttribute(wintypes.HWND(h), 38, ctypes.byref(none), 4)   # نطفي backdrop ويندوز 11 (يصير رمادي)
        if GLASS["on"]:
            r, g, b = _hex_rgb(color)
            a = int(round(max(0.06, min(1.0, float(alpha))) * 255))
            gradient = (a << 24) | (b << 16) | (g << 8) | r      # AABBGGRR
            _set_backcolor((0, 0, 0))
            accent(h, 0)   # نصفّر أول عشان ويندوز يعيد رسم اللون الجديد
            m0 = MARGINS(0, 0, 0, 0)
            dwm.DwmExtendFrameIntoClientArea(wintypes.HWND(h), ctypes.byref(m0))
            ok = bool(accent(h, 4 if float(blur) >= 15 else 2, gradient))
            m = MARGINS(-1, -1, -1, -1)
            dwm.DwmExtendFrameIntoClientArea(wintypes.HWND(h), ctypes.byref(m))
            _refresh_frame(h)
        else:
            accent(h, 0)
            m = MARGINS(0, 0, 0, 0)
            dwm.DwmExtendFrameIntoClientArea(wintypes.HWND(h), ctypes.byref(m))
            _set_backcolor(_DARK_BACK)
            ok = True
        _log("الثيم الزجاجي: %s (accent=%s color=%s alpha=%.2f blur=%s)" % ("مفعّل" if GLASS["on"] else "مطفي", ok, color, float(alpha), blur))
    except Exception as e:
        _log("glass error: " + repr(e))
        ok = False
    return ok


_FADE_IN = 0.18     # ثواني
_FADE_OUT = 0.16


def _place_bubble(hwnd, show, cx=None):
    """ظهور/اختفاء بتغيير الشفافية فقط."""
    from ctypes import wintypes
    user32 = _u32()
    SWP_NOSIZE, SWP_NOACTIVATE, SWP_SHOWWINDOW, SWP_HIDEWINDOW = 0x1, 0x10, 0x40, 0x80
    _ANIM["gen"] += 1
    gen = _ANIM["gen"]
    if show:
        l, t, r, b = _rect(hwnd)
        W = max(1, r - l)
        x, y = _cursor()
        ml, mt, mr, mb = _monitor_at(x, y)
        cx = (ml + mr) / 2 if cx is None else cx
        nx = int(min(max(cx - W / 2, ml + 8), mr - W - 8))
        _set_alpha(hwnd, 0)
        try:
            _apply_round(hwnd, log=not _ANIM.get("logged"))
            _ANIM["logged"] = True
        except Exception as e:
            _log("round error: " + repr(e))
        user32.SetWindowPos(_h(hwnd), wintypes.HWND(-1), nx, mt + 8, 0, 0,
                            SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW)
    dur = _FADE_IN if show else _FADE_OUT

    def fade():
        start = time.perf_counter()
        while _ANIM["gen"] == gen:
            k = min(1.0, (time.perf_counter() - start) / dur)
            k = k * k * (3 - 2 * k)
            _set_alpha(hwnd, float_alpha() * (k if show else 1 - k))
            if k >= 1.0:
                if not show:
                    user32.SetWindowPos(_h(hwnd), wintypes.HWND(-1), -4000, -4000, 0, 0,
                                        SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW)
                return
            time.sleep(1 / 120)
    threading.Thread(target=fade, daemon=True).start()


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
    _BUBBLE["visible"] = True
    _BUBBLE["entered"] = False
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
        _bubble_hide(hwnd, "Alt+B")
    else:
        x, y = _cursor()
        ml, mt, mr, mb = _monitor_at(x, y)
        _bubble_show(hwnd, (ml + mr) / 2, "Alt+B")
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
                _log("التطبيق %s" % ("تصغّر" if minimized else "رجع"))
            if not _BUBBLE["visible"]:
                if not minimized:
                    edge_since = 0.0
                    continue
                ml, mt, mr, mb = _monitor_at(x, y)
                at_top = y <= mt + _EDGE_PX and abs(x - (ml + mr) / 2) <= _TRIGGER_HALF
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
        width=440,
        height=104,
        x=-4000,
        y=-4000,
        resizable=False,
        frameless=True,
        on_top=True,
        background_color="#010203",
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

    def _on_restore(*_a):
        _BUBBLE["min_event"] = False

    for name, fn in (("closed", _on_main_closed), ("minimized", _on_min),
                     ("restored", _on_restore), ("maximized", _on_restore), ("shown", _on_restore)):
        try:
            ev = getattr(main_win.events, name)
            ev += fn
        except Exception:
            pass
    threading.Thread(target=_bubble_watch, daemon=True).start()
    _log("نافذة الفقاعة أُنشئت")
