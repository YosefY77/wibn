"""تجربة الشفافية الحقيقية (Blur خلف النافذة) — تفتح 3 نوافذ لمدة 40 ثانية وتسكر لحالها."""
import ctypes, threading, time, sys
from ctypes import wintypes
import webview

PAGE = """<html><body style="margin:0;overflow:hidden;background:transparent;font:22px 'Segoe UI';color:#fff">
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



def grab(path, x=0, y=0, w=700, h=900):
    """لقطة شاشة للمنطقة (BMP) عشان نشوف النتيجة."""
    gdi = ctypes.windll.gdi32
    sdc = user32.GetDC(0)
    mdc = gdi.CreateCompatibleDC(sdc)
    bmp = gdi.CreateCompatibleBitmap(sdc, w, h)
    gdi.SelectObject(mdc, bmp)
    gdi.BitBlt(mdc, 0, 0, w, h, sdc, x, y, 0x00CC0020 | 0x40000000)

    class BIH(ctypes.Structure):
        _fields_ = [("biSize", ctypes.c_uint32), ("biWidth", ctypes.c_int32), ("biHeight", ctypes.c_int32), ("biPlanes", ctypes.c_uint16),
                    ("biBitCount", ctypes.c_uint16), ("biCompression", ctypes.c_uint32), ("biSizeImage", ctypes.c_uint32),
                    ("biXPelsPerMeter", ctypes.c_int32), ("biYPelsPerMeter", ctypes.c_int32), ("biClrUsed", ctypes.c_uint32), ("biClrImportant", ctypes.c_uint32)]
    bih = BIH(ctypes.sizeof(BIH), w, h, 1, 24, 0, 0, 0, 0, 0, 0)
    stride = (w * 3 + 3) & ~3
    buf = ctypes.create_string_buffer(stride * h)
    gdi.GetDIBits(mdc, bmp, 0, h, buf, ctypes.byref(bih), 0)
    import struct
    with open(path, "wb") as f:
        f.write(b"BM" + struct.pack("<IHHI", 14 + 40 + len(buf.raw), 0, 0, 54))
        f.write(bytes(bih))
        f.write(buf.raw)
    gdi.DeleteObject(bmp); gdi.DeleteDC(mdc); user32.ReleaseDC(0, sdc)


def accent(h, state, color=0x40181410):
    a = ACCENT(state, 2, color, 0)
    d = WCAD(19, ctypes.pointer(a), ctypes.sizeof(a))
    return user32.SetWindowCompositionAttribute(h, ctypes.byref(d))


def work(wins):
    time.sleep(3)
    log = ["started v3"]
    h1 = hwnd_of("TEST A"); log.append("A black %s" % black_back(wins[0])); log.append("A acrylic %s" % accent(h1, 4))
    h2 = hwnd_of("TEST B"); log.append("B black %s" % black_back(wins[1])); log.append("B blurbehind %s" % accent(h2, 3, 0))
    h3 = hwnd_of("TEST C"); log.append("C black %s" % black_back(wins[2])); log.append("C backdrop %s" % mica(h3, 3))
    time.sleep(1)
    user32.SetForegroundWindow(h3)
    open("glass_test.log", "w").write("\n".join(log))
    time.sleep(3)
    try:
        grab("glass_test3.bmp")
    except Exception as e:
        open("glass_test.log", "a").write("\ngrab " + repr(e))
    time.sleep(20)
    for w in wins:
        try:
            w.destroy()
        except Exception:
            pass


wins = [
    webview.create_window("TEST A", html=PAGE % "A: black + acrylic", width=520, height=230, x=60, y=80, transparent=True, frameless=True, on_top=True),
    webview.create_window("TEST B", html=PAGE % "B: black + blur", width=520, height=230, x=60, y=340, transparent=True, frameless=True, on_top=True),
    webview.create_window("TEST C", html=PAGE % "C: Win11 acrylic (active)", width=520, height=230, x=60, y=600, transparent=True, frameless=True, on_top=True),
]
webview.start(work, (wins,))
