"""تحديث سريع لهارموني على جهازك (بدل update-harmoni-v12.bat الطويل).

- لو تغيّرت ملفات الواجهة بس (static/): ننسخها للبرنامج المثبّت مباشرة — ثواني.
- لو تغيّر كود (py/spec): الاختبارات تشتغل مع بعض (بالتوازي)، وبناء PyInstaller يستخدم الكاش،
  وننسخ البناء للبرنامج المثبّت مباشرة (بدون نسخة احتياطية كاملة ولا Inno Setup).
- الاختبارات اللي تحتاج نت (بحث يوتيوب/سبوتيفاي/السرعة) تنتخطى هنا — تشتغل في publish-update.bat قبل النشر.

    python fast_update.py            ← تلقائي (يعرف وش تغيّر من آخر مرة)
    python fast_update.py --full     ← يبني من جديد حتى لو ما تغيّر كود
"""
import concurrent.futures
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
WORK = os.path.dirname(HERE)
APP = os.path.join(os.environ.get("LOCALAPPDATA") or "", "Harmoni")
APP_STATIC = os.path.join(APP, "_internal", "static")
STAMP = os.path.join(WORK, ".fast-update.json")
BUILD_CACHE = os.path.join(WORK, "build-discord")
PY = sys.executable

# اختبارات تحتاج نت حقيقي (بطيئة وممكن تتأخر) — تشتغل وقت النشر بس
ONLINE = {"test_search_quality.py", "test_spotify_2026.py", "test_spotify_public.py", "test_speed.py",
          "test_stream_speed.py", "test_music_search.py", "test_ui_smoke.py", "test_youtube_link.py"}
NODE_TESTS = ["test_hub_do.mjs", "test_frontend.cjs", "test_search_pages.cjs", "test_appearance.cjs",
              "test_discord.cjs", "test_volume_sync.cjs"]
CODE_EXT = (".py", ".spec", ".json")
TOOLS = {"fast_update.py", "mac_publish.py", "set_version.py", "update_ytdlp.py", "hub.json"}   # مو داخل البرنامج
SKIP_DIRS = {"__pycache__", "audio_cache", "vendor", "hub", "pi", "mac", "work", "build", "dist", "downloads", "uploads"}
DATA_FILES = {"profiles.json", "profiles.json.bak", "search_cache.json", "stream_cache.json", "artist_cache.json",
              "spotify_links.json", "discord_config.json", "region.json", "panel_config.json", "badges.json",
              "version.json", "bubble_config.json", "cache_config.json"}


def say(msg):
    print(msg, flush=True)


def load_stamp():
    try:
        with open(STAMP, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return {}


def changed_since(ts):
    code, static = [], []
    for root, dirs, files in os.walk(HERE):
        rel_root = os.path.relpath(root, HERE)
        top = rel_root.split(os.sep)[0]
        if rel_root == ".":
            dirs[:] = [d for d in dirs if d not in SKIP_DIRS and not d.startswith(".")]
        dirs[:] = [d for d in dirs if d != "__pycache__"]
        for f in files:
            p = os.path.join(root, f)
            try:
                if os.path.getmtime(p) <= ts:
                    continue
            except OSError:
                continue
            rel = os.path.relpath(p, HERE)
            if top == "static":
                static.append(rel)
            elif rel_root == "." and f.endswith(CODE_EXT) and not f.startswith("test_") and f not in DATA_FILES \
                    and f not in TOOLS:
                code.append(rel)
    return code, static


def kill_app():
    subprocess.run(["taskkill", "/IM", "Harmoni.exe", "/F"], capture_output=True)
    time.sleep(1.2)


def start_app():
    exe = os.path.join(APP, "Harmoni.exe")
    if os.path.isfile(exe):
        subprocess.Popen([exe], cwd=APP, close_fds=True, creationflags=0x00000008)   # DETACHED_PROCESS
        say("   Harmoni started.")


def copy_static(files):
    n = 0
    for rel in files:
        src = os.path.join(HERE, rel)
        dst = os.path.join(APP, "_internal", rel)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copy2(src, dst)
        n += 1
    return n


def run_one(cmd, name):
    env = dict(os.environ, HARMONI_REGION="none", HARMONI_DATA_DIR=tempfile.mkdtemp(prefix="hm-test-"),
               PYTHONIOENCODING="utf-8")
    t0 = time.time()
    try:
        r = subprocess.run(cmd, cwd=HERE, env=env, capture_output=True, text=True, encoding="utf-8",
                           errors="replace", timeout=300)
        ok, out = r.returncode == 0, (r.stdout or "") + (r.stderr or "")
    except subprocess.TimeoutExpired:
        ok, out = False, "timed out after 300s"
    return name, ok, out, time.time() - t0


def run_tests(interface_only=False):
    py = [] if interface_only else sorted(f for f in os.listdir(HERE)
                                          if f.startswith("test_") and f.endswith(".py") and f not in ONLINE)
    jobs = [([PY, f], f) for f in py]
    if shutil.which("node"):
        jobs += [(["node", f], f) for f in NODE_TESTS if os.path.isfile(os.path.join(HERE, f))]
    say("[2/4] Running %d tests at the same time (online-only tests skipped)..." % len(jobs))
    t0 = time.time()
    failed = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=min(8, (os.cpu_count() or 4))) as ex:
        for name, ok, out, secs in ex.map(lambda j: run_one(*j), jobs):
            if not ok:
                failed.append((name, out))
    if failed:
        for name, out in failed:
            say("\n----- %s FAILED -----\n%s" % (name, out[-3000:]))
        say("\nTESTS FAILED - Harmoni was not changed.")
        return False
    say("   all %d passed (%.0fs)" % (len(jobs), time.time() - t0))
    return True


def build_and_install():
    t = time.localtime()
    ver = "%d.%d.%d.%d%02d" % (t.tm_year, t.tm_mon, t.tm_mday, t.tm_hour, t.tm_min)   # نفس شكل publish-update.bat
    subprocess.run([PY, "set_version.py", ver], cwd=HERE, capture_output=True)
    say("[3/4] Building (uses the build cache, usually under a minute)...")
    dist = os.path.join(tempfile.gettempdir(), "harmoni-fast-dist")
    shutil.rmtree(dist, ignore_errors=True)
    log = os.path.join(WORK, "build-fast.log")
    t0 = time.time()
    with open(log, "w", encoding="utf-8", errors="replace") as lf:
        r = subprocess.run([PY, "-m", "PyInstaller", "HarmoniApp.spec", "--noconfirm", "--distpath", dist,
                            "--workpath", BUILD_CACHE], cwd=HERE, stdout=lf, stderr=subprocess.STDOUT)
    if r.returncode != 0:
        say("BUILD FAILED - see work\\build-fast.log. Harmoni was not changed.")
        return False
    say("   built in %.0fs" % (time.time() - t0))
    say("[4/4] Installing into %s ..." % APP)
    src = os.path.join(dist, "Harmoni")
    kill_app()
    for root, dirs, files in os.walk(src):
        rel = os.path.relpath(root, src)
        out_dir = os.path.join(APP, rel) if rel != "." else APP
        os.makedirs(out_dir, exist_ok=True)
        if rel == ".":
            dirs[:] = [d for d in dirs if d != "audio_cache"]
        for f in files:
            if rel == "." and (f in DATA_FILES - {"version.json"} or f.endswith(".log") or f == ".harmoni_secret"):
                continue
            s, d = os.path.join(root, f), os.path.join(out_dir, f)
            try:
                if os.path.exists(d) and os.path.getsize(d) == os.path.getsize(s) and \
                        abs(os.path.getmtime(d) - os.path.getmtime(s)) < 1:
                    continue
                shutil.copy2(s, d)
            except PermissionError:
                for _ in range(8):   # بقايا WebView2 لسا ماسكة الملف ثواني بعد ما قفلنا البرنامج
                    time.sleep(1)
                    try:
                        shutil.copy2(s, d)
                        break
                    except PermissionError:
                        continue
                else:
                    raise
    say("   installed version " + ver)
    return True


def main():
    t_start = time.time()
    if not os.path.isfile(os.path.join(APP, "Harmoni.exe")):
        say("Harmoni isn't installed in %s yet - run update-harmoni-v12.bat once first." % APP)
        return 1
    st = load_stamp()
    full = "--full" in sys.argv or not st.get("ts")
    code, static = changed_since(st.get("ts", 0))
    say("[1/4] Changes since last update: %d code file(s), %d interface file(s)%s" %
        (len(code), len(static), " (full build)" if full else ""))
    for f in (code + static)[:12]:
        say("      " + f)
    if not code and not static and not full:
        say("Nothing changed - starting Harmoni.")
        start_app()
        return 0
    if not run_tests(interface_only=not (code or full)):
        return 1
    stamp_ts = time.time()
    if code or full:
        if not build_and_install():
            return 1
    else:
        say("[3/4] Only the interface changed - no rebuild needed.")
        kill_app()
        say("[4/4] Copied %d interface file(s) into Harmoni." % copy_static(static))
    with open(STAMP, "w", encoding="utf-8") as f:
        json.dump({"ts": stamp_ts}, f)
    start_app()
    say("\nDone in %.0f seconds." % (time.time() - t_start))
    return 0


if __name__ == "__main__":
    sys.exit(main())
