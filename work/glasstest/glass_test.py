"""تجربة الشفافية الحقيقية (Blur خلف النافذة) — تفتح 3 نوافذ لمدة 40 ثانية وتسكر لحالها."""
import ctypes, threading, time, sys
from ctypes import wintypes
import webview

PAGE = """<html><body style="margin:0;background:transparent;font:22px 'Segoe UI';color:#fff">
<div style="margin:24px;padding:22px;border-radius:18px;background:rgba(18,22,28,.35);border:1px solid rgba(255,255,255,.15)">
<b>%s</b><br>Text must be 100%% solid — النص لازم يكون واضح بالكامل<br>
<span style="background:#1db954;color:#000;padding:4px 10px;border-radius:8px">Button</span></div></body></html>"""

user32 = ctypes.windll.user32
dwm = ctypes.windll.dwmapi


class ACCENT(ctypes.Structure):
    _fields_ = [("AccentState", ctypes.c_int), ("AccentFlags", ctypes.c_int), ("GradientColor", ctypes.c_uint), ("AnimationId", ctypes.c_int)]


class WCAD(ctypes.Structure):
    _fields_ = [("Attribute", ctypes.c_int), ("Data", ctypes.POINTER(ACCENT)), ("SizeOfData", ctypes.c_size_t)]


class MARGINS(ctypes.Structure):
    _fields_ = [("l", ctypes.c_int), ("r", ctypes.c_int), ("t", ctypes.c_int), ("b", ctypes.c_int)]


def hwnd_of(title):
    for _ in range(100):
        h = user32.FindWindowW(None, title)
        if h:
            return h
        time.sleep(0.1)
    return 0


def acrylic(h):
    a = ACCENT(4, 2, 0x30140F0C, 0)   # ACCENT_ENABLE_ACRYLICBLURBEHIND, tint AABBGGRR
    d = WCAD(19, ctypes.pointer(a), ctypes.sizeof(a))
    return user32.SetWindowCompositionAttribute(h, ctypes.byref(d))


def mica(h, kind=3):
    m = MARGINS(-1, -1, -1, -1)
    dwm.DwmExtendFrameIntoClientArea(h, ctypes.byref(m))
    dark = ctypes.c_int(1)
    dwm.DwmSetWindowAttribute(h, 20, ctypes.byref(dark), 4)
    v = ctypes.c_int(kind)
    return dwm.DwmSetWindowAttribute(h, 38, ctypes.byref(v), 4)


def black_back(win):
    try:
        import clr  # noqa
        from System import Func, Type
        from System.Drawing import Color
        f = win.native

        def setb():
            f.BackColor = Color.Black
            f.Invalidate()
        f.Invoke(Func[Type](setb))
        return "ok"
    except Exception as e:
        return repr(e)


def work(wins):
    time.sleep(3)
    log = []
    h1 = hwnd_of("TEST 1 acrylic"); log.append("1 acrylic %s" % acrylic(h1))
    h2 = hwnd_of("TEST 2 acrylic+black"); log.append("2 black %s" % black_back(wins[1])); log.append("2 acrylic %s" % acrylic(h2)); log.append("2 extend %s" % mica(h2, 0))
    h3 = hwnd_of("TEST 3 mica-acrylic+black"); log.append("3 black %s" % black_back(wins[2])); log.append("3 backdrop %s" % mica(h3, 3))
    open("glass_test.log", "w").write("\n".join(log))
    time.sleep(40)
    for w in wins:
        try:
            w.destroy()
        except Exception:
            pass


wins = [
    webview.create_window("TEST 1 acrylic", html=PAGE % "1: acrylic", width=520, height=230, x=60, y=80, transparent=True, frameless=True, on_top=True),
    webview.create_window("TEST 2 acrylic+black", html=PAGE % "2: acrylic + black", width=520, height=230, x=60, y=340, transparent=True, frameless=True, on_top=True),
    webview.create_window("TEST 3 mica-acrylic+black", html=PAGE % "3: Win11 backdrop", width=520, height=230, x=60, y=600, transparent=True, frameless=True, on_top=True),
]
webview.start(work, wins)
