"""قراءة البلاي لستات العامة من بروفايل سبوتيفاي (بدون تسجيل دخول).

سبوتيفاي (2026) قفل الطلب الرسمي لبلاي لستات المستخدم، وصفحة البروفايل ما تعرض شي إلا بعد ما
يشتغل مشغل الويب حقهم. فنفتح الصفحة العامة نفسها في نافذة متصفح مخفية (برّا الشاشة) ونقرا
روابط البلاي لستات اللي تظهر فيها — نفس اللي يشوفه أي أحد يفتح البروفايل — وبعدين نسكرها.
"""
import json
import threading
import time

_LOCK = threading.Lock()
_CACHE = {}            # uid -> (time, result)
CACHE_TTL = 25 * 60    # البروفايل نقراه كل 25 دقيقة بالكثير (الأغاني نفسها تتحدث كل 5 دقايق من صفحات البلاي لستات)
TITLE = "Harmoni · Spotify reader"

# يجمع الروابط (ويتراكم بين المحاولات) وينزل تحت عشان لو القائمة طويلة تحمّل الباقي
_JS = r"""
(function () {
  var seen = window.__hpl || (window.__hpl = {});
  document.querySelectorAll('a[href^="/playlist/"]').forEach(function (a) {
    var m = (a.getAttribute('href') || '').match(/^\/playlist\/([A-Za-z0-9]{22})/);
    if (!m || m[1].indexOf('37i9dQZF') === 0) return;   // بلاي لستات سبوتيفاي الرسمية مو حقته
    var name = (a.textContent || '').trim();
    if (!seen[m[1]] || (!seen[m[1]].name && name)) seen[m[1]] = { id: m[1], name: name };
  });
  var sc = document.querySelector('[data-overlayscrollbars-viewport]') || document.scrollingElement;
  try { sc.scrollTop = sc.scrollHeight; } catch (e) {}
  var h1 = document.querySelector('main h1');
  var notFound = /couldn.t find|page not found|لم نتمكن|غير موجود/i.test((document.body && document.body.innerText || '').slice(0, 3000));
  var showAll = !!document.querySelector('main a[href$="/playlists"]');
  var hasContent = !!document.querySelector('main h1');
  return JSON.stringify({ items: Object.keys(seen).map(function (k) { return seen[k]; }), name: h1 ? h1.textContent.trim() : '',
                          title: document.title, notFound: notFound, showAll: showAll, hasContent: hasContent });
})()
"""


def _hide_from_taskbar(title):
    try:
        import mini_window as mw
        for _ in range(30):
            h = mw._find_own_window(title)
            if h:
                u = mw._u32()
                ex = u.GetWindowLongW(mw._h(h), -20)
                u.SetWindowLongW(mw._h(h), -20, (ex | 0x80 | 0x08000000) & ~0x40000)   # TOOLWINDOW|NOACTIVATE, بدون APPWINDOW
                return
            time.sleep(0.1)
    except Exception:
        pass


def _collect(w, timeout):
    """نقرا الصفحة كل شوي لين يثبت عدد البلاي لستات."""
    deadline = time.time() + timeout
    last_n, stable, data = -1, 0, {}
    time.sleep(1.5)
    while time.time() < deadline:
        try:
            raw = w.evaluate_js(_JS)
            data = json.loads(raw) if isinstance(raw, str) else {}
        except Exception:
            data = {}
        if data.get("notFound") and not data.get("items"):
            raise LookupError("user")
        n = len(data.get("items") or [])
        if n and n == last_n:
            stable += 1
            if stable >= 3:
                break
        elif not n and data.get("hasContent") and time.time() > deadline - timeout + 8:
            break   # الصفحة تحملت وما فيها بلاي لستات عامة
        else:
            stable = 0
        last_n = n
        time.sleep(0.8)
    return data


def read_profile(uid, timeout=20.0):
    """يرجع {"name": ..., "playlists": [{"id", "name"}]} أو يرمي LookupError/RuntimeError."""
    hit = _CACHE.get(uid)
    if hit and time.time() - hit[0] < CACHE_TTL:
        return hit[1]
    import webview   # متوفر بس في البرنامج المثبت (نافذة)
    with _LOCK:
        hit = _CACHE.get(uid)
        if hit and time.time() - hit[0] < CACHE_TTL:
            return hit[1]
        base = "https://open.spotify.com/user/%s" % uid
        kw = dict(width=1200, height=900, x=-12000, y=-12000, frameless=True)
        if _supports_focus():
            kw["focus"] = False
        w = webview.create_window(TITLE, base, **kw)
        threading.Thread(target=_hide_from_taskbar, args=(TITLE,), daemon=True).start()
        try:
            w.events.loaded.wait(timeout=15)
            data = _collect(w, timeout)
            items = {p["id"]: p for p in (data.get("items") or [])}
            name = (data.get("title") or "").replace("Spotify –", "").replace("Spotify -", "").strip()
            if data.get("showAll"):
                # البروفايل يعرض جزء بس — نفتح «عرض الكل»
                try:
                    w.load_url(base + "/playlists")
                    time.sleep(1.0)
                    more = _collect(w, timeout)
                    for p in more.get("items") or []:
                        items.setdefault(p["id"], p)
                except Exception:
                    pass
            if name.lower() in ("web player", "spotify", ""):
                name = ""
            res = {"name": name, "playlists": list(items.values())[:80]}
            if not res["playlists"]:
                raise RuntimeError("no public playlists found")
            _CACHE[uid] = (time.time(), res)
            return res
        finally:
            try:
                w.destroy()
            except Exception:
                pass


def _supports_focus():
    try:
        import inspect
        import webview
        return "focus" in inspect.signature(webview.create_window).parameters
    except Exception:
        return False
