"""اختبار واجهة تلقائي قبل البناء: يشغّل السيرفر ويفتح الصفحة في متصفح مخفي (Edge/Chrome)
ويتأكد إن كل السكربتات اشتغلت بدون أخطاء وإن الصفحة الرئيسية تعرض الأغاني."""
import os
import re
import shutil
import subprocess
import sys
import tempfile
import threading

DATA = tempfile.mkdtemp(prefix="harmoni-ui-test-")
os.environ["HARMONI_DATA_DIR"] = DATA
os.environ["MUSIC_HEADLESS"] = "1"
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import server  # noqa: E402


def find_browser():
    cands = [os.environ.get("HARMONI_BROWSER", "")]
    for base in (os.environ.get("ProgramFiles(x86)", ""), os.environ.get("ProgramFiles", ""), os.environ.get("LOCALAPPDATA", "")):
        if base:
            cands += [os.path.join(base, "Microsoft", "Edge", "Application", "msedge.exe"),
                      os.path.join(base, "Google", "Chrome", "Application", "chrome.exe")]
    cands += [shutil.which("msedge") or "", shutil.which("chrome") or "", shutil.which("chromium") or ""]
    return next((c for c in cands if c and os.path.isfile(c)), None)


def main():
    browser = find_browser()
    if not browser:
        print("SKIP: no Edge/Chrome found for UI smoke test")
        return 0
    srv = server.HarmoniHTTPServer("127.0.0.1", 0, server.app)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    url = "http://127.0.0.1:%d/?selftest=1" % srv.server_port
    profile = tempfile.mkdtemp(prefix="harmoni-ui-profile-")
    try:
        extra = os.environ.get("HARMONI_BROWSER_ARGS", "").split()
        out = subprocess.run([browser] + extra + ["--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
                              "--user-data-dir=" + profile, "--virtual-time-budget=8000", "--dump-dom", url],
                             capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=90).stdout
    finally:
        srv.shutdown()
        shutil.rmtree(profile, ignore_errors=True)
    html_tag = (re.search(r"<html[^>]*>", out) or [""])[0]
    problems = []
    for attr in ("data-app-ready", "data-filters-ready", "data-playlist-ready", "data-stats-ready", "data-accent-ready", "data-panel-settings-ready", "data-room-ready", "data-updater-ready", "data-color-picker-ready"):
        if attr + '="1"' not in html_tag:
            problems.append("script did not finish: " + attr)
    m = re.search(r'data-js-errors="(\d+)"', html_tag)
    if not m or m.group(1) != "0":
        err = re.search(r'data-js-error-list="([^"]*)"', html_tag)
        problems.append("JavaScript errors: " + (err.group(1) if err else "unknown"))
    hist = re.search(r'id="history"[^>]*>(.*?)</div>\s*</section>|id="history"[^>]*>(.*)', out, re.S)
    if "Selftest Song A" not in out or "Selftest Song B" not in out:
        problems.append("home page did not render recently played songs")
    for elem in ('id="search-filters"', 'id="view-playlist"', 'id="my-stats"'):
        if elem not in out:
            problems.append("missing element " + elem)
    if problems:
        print("UI SMOKE TEST FAILED:")
        for p in problems:
            print("  - " + p)
        return 1
    print("PASS: UI loads with all scripts, no JS errors, home shows songs")
    return 0


if __name__ == "__main__":
    code = main()
    shutil.rmtree(DATA, ignore_errors=True)
    sys.exit(code)
