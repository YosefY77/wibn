"""تحديث آمن لـ yt-dlp (المكتبة اللي تجيب الأغاني من يوتيوب).

1. يتأكد إن فيه إنترنت (عشان ما نحكم على التحديث بالفشل بسبب النت).
2. ياخذ نسخة من مجلد vendor ويسجل النسخ الحالية.
3. يحدّث yt-dlp و yt-dlp-ejs مع بعض، وينسخ yt-dlp-ejs الجديد لمجلد vendor (البرنامج يستخدم هذي النسخة).
4. اختبار حقيقي: بحث عن أغنية + جلب رابط صوتها من يوتيوب بنفس طريقة البرنامج.
5. لو فشل الاختبار: يرجّع النسخ القديمة بالضبط.

أكواد الخروج: 0 = تحدّث ونجح (لازم إعادة بناء)، 3 = أصلاً آخر نسخة وشغال، 1 = فشل ورجعنا للقديم، 2 = ما فيه إنترنت.
"""
import glob
import os
import shutil
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
VENDOR = os.path.join(HERE, "vendor")
PY = sys.executable
PKGS = ("yt-dlp", "yt-dlp-ejs")

LIVE_CHECK = r'''
import os, sys, tempfile
os.environ["HARMONI_DATA_DIR"] = tempfile.mkdtemp(prefix="harmoni-ytdlp-check-")
os.environ["MUSIC_HEADLESS"] = "1"
sys.path.insert(0, %r)
import server, yt_dlp
rows = server._do_search("adele hello", "youtube", 3)
assert rows, "search returned nothing"
url = server.resolve_stream_url(rows[0]["web_url"])
assert url and url.startswith("http"), "could not get audio url"
print("LIVE OK yt-dlp", yt_dlp.version.__version__, "->", rows[0]["title"])
''' % HERE


def run(args, timeout=600):
    return subprocess.run(args, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout)


def versions():
    code = ("import importlib.metadata as m\n"
            "for p in %r:\n"
            "    try: print(p, m.version(p))\n"
            "    except Exception: print(p, '-')\n") % (PKGS,)
    out = run([PY, "-c", code]).stdout.split()
    return dict(zip(out[0::2], out[1::2]))


def online():
    try:
        import urllib.request
        urllib.request.urlopen("https://www.youtube.com/", timeout=8).read(100)
        return True
    except Exception:
        return False


def live_check():
    r = run([PY, "-c", LIVE_CHECK], timeout=180)
    ok = r.returncode == 0 and "LIVE OK" in r.stdout
    print(("   " + r.stdout.strip().splitlines()[-1]) if ok else "   live test failed: " + (r.stderr.strip().splitlines() or ["?"])[-1])
    return ok


def sync_vendor_ejs():
    """ينسخ yt_dlp_ejs (والـ dist-info حقه) من site-packages إلى vendor."""
    loc = run([PY, "-c", "import importlib.metadata as m, os; d=m.distribution('yt-dlp-ejs'); "
                         "print(os.path.dirname(os.path.dirname(str(d.locate_file('yt_dlp_ejs/__init__.py'))))); "
                         "print(d._path)"]).stdout.split("\n")
    site, distinfo = loc[0].strip(), loc[1].strip()
    src_pkg = os.path.join(site, "yt_dlp_ejs")
    if not os.path.isdir(src_pkg) or not os.path.isdir(distinfo):
        raise RuntimeError("yt_dlp_ejs not found in site-packages")
    os.makedirs(VENDOR, exist_ok=True)
    shutil.rmtree(os.path.join(VENDOR, "yt_dlp_ejs"), ignore_errors=True)
    for old in glob.glob(os.path.join(VENDOR, "yt_dlp_ejs-*.dist-info")):
        shutil.rmtree(old, ignore_errors=True)
    shutil.copytree(src_pkg, os.path.join(VENDOR, "yt_dlp_ejs"))
    shutil.copytree(distinfo, os.path.join(VENDOR, os.path.basename(distinfo)))


def main():
    print("[1] Checking internet...")
    if not online():
        print("   No internet / YouTube unreachable - nothing changed.")
        return 2
    before = versions()
    print("[2] Current:", ", ".join("%s %s" % kv for kv in before.items()))
    backup = os.path.join(tempfile.gettempdir(), "harmoni-vendor-backup-%d" % int(time.time()))
    if os.path.isdir(VENDOR):
        shutil.copytree(VENDOR, backup)
    print("[3] Updating yt-dlp + yt-dlp-ejs...")
    r = run([PY, "-m", "pip", "install", "-U", "--quiet", *PKGS])
    if r.returncode != 0:
        print("   pip failed:", (r.stderr.strip().splitlines() or ["?"])[-1])
        return 1
    after = versions()
    changed = after != before
    print("   Now:", ", ".join("%s %s" % kv for kv in after.items()), "" if changed else "(already latest)")
    try:
        if changed:
            sync_vendor_ejs()
    except Exception as e:
        print("   vendor sync failed:", e)
        changed = True
        ok = False
    else:
        print("[4] Live test (search + get audio from YouTube)...")
        ok = live_check()
    if ok:
        return 0 if changed else 3
    if not changed:
        print("   Already the newest yt-dlp but the live test fails - YouTube probably changed and a fix")
        print("   is not released yet. Try again in a day or two. Nothing was changed.")
        return 1
    print("[5] Update did not work - restoring previous versions...")
    pins = ["%s==%s" % (p, v) for p, v in before.items() if v and v != "-"]
    if pins:
        run([PY, "-m", "pip", "install", "--quiet", "--force-reinstall", "--no-deps", *pins])
    if os.path.isdir(backup):
        shutil.rmtree(VENDOR, ignore_errors=True)
        shutil.copytree(backup, VENDOR)
    print("   Restored:", ", ".join("%s %s" % kv for kv in versions().items()))
    return 1


if __name__ == "__main__":
    sys.exit(main())
