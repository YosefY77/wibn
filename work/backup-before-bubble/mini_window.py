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
    main_win = webview.create_window(
        "Harmoni",
        f"http://127.0.0.1:{_PORT[0]}/",
        width=1280,
        height=850,
        min_size=(940, 600),
    )
    threading.Thread(target=_set_form_icon, args=(main_win, "main"), daemon=True).start()
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
