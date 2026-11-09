# تجربة طرق الشفافية لنافذة pywebview على ويندوز:  python glass_lab.py A
import sys, threading, time, ctypes
from ctypes import wintypes
import webview

M = (sys.argv[1] if len(sys.argv) > 1 else "A").upper()
HTML = """<html><body style="margin:0;background:transparent;font:28px Segoe UI;color:#fff">
<div style="position:fixed;inset:0;background:rgba(0,160,255,.25)"></div>
<div style="position:relative;padding:30px">METHOD %s — لو تشوف اللي ورا النافذة = نجحت<br>
<div style="margin-top:20px;width:300px;height:120px;background:rgba(255,255,255,.08);border:1px solid rgba(255,255,255,.3);border-radius:16px"></div></div>
</body></html>""" % M

u32 = ctypes.windll.user32
dwm = ctypes.windll.dwmapi


class MARGINS(ctypes.Structure):
    _fields_ = [("l", ctypes.c_int), ("r", ctypes.c_int), ("t", ctypes.c_int), ("b", ctypes.c_int)]


class ACCENT(ctypes.Structure):
    _fields_ = [("AccentState", ctypes.c_int), ("AccentFlags", ctypes.c_int), ("GradientColor", ctypes.c_uint), ("AnimationId", ctypes.c_int)]


class WCAD(ctypes.Structure):
    _fields_ = [("Attribute", ctypes.c_int), ("Data", ctypes.POINTER(ACCENT)), ("SizeOfData", ctypes.c_size_t)]


class DWM_BLURBEHIND(ctypes.Structure):
    _fields_ = [("dwFlags", wintypes.DWORD), ("fEnable", wintypes.BOOL), ("hRgnBlur", wintypes.HRGN), ("fTransitionOnMaximized", wintypes.BOOL)]


def accent(h, state, grad=0x01000000):
    ac = ACCENT(state, 2, grad, 0)
    d = WCAD(19, ctypes.pointer(ac), ctypes.sizeof(ac))
    return u32.SetWindowCompositionAttribute(wintypes.HWND(h), ctypes.byref(d))


def extend(h, v=-1):
    m = MARGINS(v, v, v, v)
    return dwm.DwmExtendFrameIntoClientArea(wintypes.HWND(h), ctypes.byref(m))


def blurbehind(h):
    rgn = ctypes.windll.gdi32.CreateRectRgn(0, 0, -1, -1)
    bb = DWM_BLURBEHIND(0x1 | 0x2, True, rgn, False)
    return dwm.DwmEnableBlurBehindWindow(wintypes.HWND(h), ctypes.byref(bb))


def apply(win):
    time.sleep(2.5)
    form = win.native
    h = int(form.Handle.ToInt64())
    from System.Drawing import Color
    from System import Func, Type

    def ui():
        log = []
        if M in ("A", "B", "D", "F", "G"):
            form.BackColor = Color.FromArgb(255, 0, 0, 0)
        if M == "A":
            log.append(accent(h, 4)); log.append(extend(h))
        elif M == "B":
            log.append(blurbehind(h)); log.append(accent(h, 4)); log.append(extend(h))
        elif M == "C":
            form.BackColor = Color.FromArgb(255, 255, 0, 255)
            form.TransparencyKey = Color.FromArgb(255, 255, 0, 255)
        elif M == "D":
            v = ctypes.c_int(3)
            log.append(extend(h)); log.append(dwm.DwmSetWindowAttribute(wintypes.HWND(h), 38, ctypes.byref(v), 4))
        elif M == "E":
            try:
                form.BackColor = Color.Transparent
                log.append("transparent ok")
            except Exception as e:
                log.append(repr(e))
            log.append(accent(h, 4)); log.append(extend(h))
        elif M == "F":
            log.append(blurbehind(h)); log.append(extend(h))
        elif M == "G":
            log.append(blurbehind(h)); log.append(accent(h, 2, 0)); log.append(extend(h))
        form.Invalidate(True)
        print("method", M, log, flush=True)
    form.Invoke(Func[Type](ui))


w = webview.create_window("GlassLab " + M, html=HTML, width=900, height=500, transparent=True, background_color="#000000")
threading.Thread(target=apply, args=(w,), daemon=True).start()
webview.start(private_mode=True)
