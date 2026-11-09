"""نسخة الماك: يرفع الكود لمستودع خاص (harmoni-src) ويطلب من GitHub يبنيها على جهاز ماك ويرفع Harmoni-mac.dmg
على نفس إصدار ويندوز (الإصدار يكون مسودة لين تجهز النسختين، بعدين publish-update.bat ينشره مرة وحدة).
    python mac_publish.py start <gh.exe> <version> <owner/harmoni-app>   ← يبدأ البناء (يشتغل مع بناء ويندوز بنفس الوقت)
    python mac_publish.py wait  <gh.exe> <version> <owner/harmoni-app>   ← ينتظر لين يخلص ويتأكد إن الـ dmg انرفع
ما يحتاج git — كله عن طريق gh."""
import base64
import json
import os
import subprocess
import sys
import tempfile
import time
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
SRC_REPO_NAME = "harmoni-src"
WORKFLOW = ".github/workflows/mac-build.yml"
# ما نرفع بيانات المستخدمين ولا الأسرار ولا الكاش
SKIP_DIRS = {"__pycache__", "audio_cache", "work", "pi", "vendor", "hub", ".git", "bin", "build", "dist"}
SKIP_FILES = {"profiles.json", "profiles.json.bak", ".harmoni_secret", "discord_config.json", "spotify_links.json",
              "search_cache.json", "stream_cache.json", "artist_cache.json", "region.json", "panel_config.json",
              "badges.json", "bubble_config.json"}
LOG = os.path.join(os.path.dirname(HERE), "mac-build.log")   # work\mac-build.log — للتشخيص


def log(msg, show=True):
    if show:
        print(msg, flush=True)
    try:
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(time.strftime("%Y-%m-%d %H:%M:%S ") + str(msg) + "\n")
    except Exception:
        pass


SKIP_EXT = (".log", ".txt", ".bat", ".vbs", ".pyc", ".tmp", ".part")


def gh(*args, input=None, check=True):
    r = subprocess.run([GH] + list(args), input=input, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if r.returncode != 0:
        log("gh %s -> %s: %s" % (" ".join(a for a in args[:4]), r.returncode, (r.stderr or r.stdout).strip()[:600]), show=False)
    if check and r.returncode != 0:
        raise RuntimeError("gh %s failed: %s" % (" ".join(args[:3]), (r.stderr or r.stdout).strip()[:400]))
    return r


def make_zip(path):
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        for root, dirs, files in os.walk(HERE):
            # المجلدات المستثناة بس على المستوى الأول (static/vendor فيه مكتبات الواجهة — لازم ينرفع)
            top = os.path.abspath(root) == os.path.abspath(HERE)
            dirs[:] = [d for d in dirs if not (top and d in SKIP_DIRS) and d not in ("__pycache__", ".git") and not d.startswith("tmp")]
            for f in files:
                if f in SKIP_FILES or f.endswith(SKIP_EXT) or f.startswith("tmp"):
                    if f != "requirements-mac.txt" and f != "requirements.txt":
                        continue
                full = os.path.join(root, f)
                z.write(full, os.path.relpath(full, HERE).replace("\\", "/"))


def put_workflow(src):
    with open(os.path.join(HERE, "mac", "mac-build.yml"), "rb") as f:
        body = f.read().replace(b"\r\n", b"\n")
    cur = gh("api", "repos/%s/contents/%s" % (src, WORKFLOW), check=False)
    sha = ""
    if cur.returncode == 0:
        j = json.loads(cur.stdout or "{}")
        if base64.b64decode(j.get("content") or "") == body:
            return False
        sha = j.get("sha") or ""
    payload = {"message": "mac build workflow", "content": base64.b64encode(body).decode()}
    if sha:
        payload["sha"] = sha
    r = gh("api", "-X", "PUT", "repos/%s/contents/%s" % (src, WORKFLOW), "--input", "-",
           input=json.dumps(payload), check=False)
    if r.returncode != 0:
        if "workflow" in (r.stderr + r.stdout).lower():
            log("   GitHub needs one more permission (workflow). Your browser opens - approve it.")
            subprocess.run([GH, "auth", "refresh", "-h", "github.com", "-s", "workflow"])
            gh("api", "-X", "PUT", "repos/%s/contents/%s" % (src, WORKFLOW), "--input", "-", input=json.dumps(payload))
        else:
            raise RuntimeError("could not upload workflow: " + (r.stderr or r.stdout)[:300])
    return True


def label_windows(release_repo, version):
    """نكتب «Harmoni for Windows» على ملف ويندوز بقائمة الإصدار (لو ما كان مكتوب)."""
    r = gh("api", "repos/%s/releases/tags/v%s" % (release_repo, version), check=False)
    if r.returncode != 0:
        return
    for a in (json.loads(r.stdout or "{}").get("assets") or []):
        if str(a.get("name", "")).lower().endswith(".exe") and a.get("label") != "Harmoni for Windows":
            gh("api", "-X", "PATCH", "repos/%s/releases/assets/%s" % (release_repo, a["id"]),
               "-f", "label=Harmoni for Windows", check=False)


def wait(src, version, release_repo):
    title = "mac " + version
    run_id = None
    for _ in range(40):
        r = gh("run", "list", "-R", src, "--workflow", "mac-build.yml", "--limit", "15",
               "--json", "databaseId,displayTitle", check=False)
        try:
            run_id = next((x["databaseId"] for x in json.loads(r.stdout or "[]") if x.get("displayTitle") == title), None)
        except Exception:
            run_id = None
        if run_id:
            break
        time.sleep(3)
    if not run_id:
        raise RuntimeError("Mac build not found on GitHub")
    t0 = time.time()
    last = ""
    while True:
        r = gh("run", "view", str(run_id), "-R", src, "--json", "status,conclusion", check=False)
        try:
            j = json.loads(r.stdout or "{}")
        except Exception:
            j = {}
        st = j.get("status") or "?"
        mins = int(time.time() - t0) // 60
        secs = int(time.time() - t0) % 60
        line = "   Mac build: %s  (%d:%02d)" % (st.replace("_", " "), mins, secs)
        if st != last or secs < 15:
            log(line)
            last = st
        if st == "completed":
            if j.get("conclusion") != "success":
                fl = gh("run", "view", str(run_id), "-R", src, "--log-failed", check=False)
                log("FAILED STEP LOG (last 150 lines):\n" + "\n".join((fl.stdout or fl.stderr or "").splitlines()[-150:]), show=False)
                raise RuntimeError("Mac build failed: https://github.com/%s/actions/runs/%s" % (src, run_id))
            break
        if time.time() - t0 > 40 * 60:
            raise RuntimeError("Mac build took too long")
        time.sleep(15)
    # نتأكد إن ملف الماك موجود بالإصدار (المسودات تطلع بس بالقائمة)
    r = gh("api", "repos/%s/releases?per_page=20" % release_repo, check=False)
    for rel in json.loads(r.stdout or "[]"):
        if rel.get("tag_name") == "v" + version:
            if any(str(a.get("name", "")).lower().endswith(".dmg") for a in rel.get("assets") or []):
                log("   Mac version ready - OK")
                return
    raise RuntimeError("Mac file is missing from the release")


def main():
    global GH
    mode = sys.argv[1]
    log("---- %s %s" % (mode, " ".join(sys.argv[3:5])), show=False)
    GH, version, release_repo = sys.argv[2], sys.argv[3], sys.argv[4]
    owner = release_repo.split("/")[0]
    src = "%s/%s" % (owner, SRC_REPO_NAME)
    if mode == "wait":
        wait(src, version, release_repo)
        return
    if mode == "label":
        label_windows(release_repo, version)
        return

    if gh("repo", "view", src, check=False).returncode != 0:
        gh("repo", "create", SRC_REPO_NAME, "--private", "--add-readme", "--description", "Harmoni source (private, used to build the Mac version)")
        log("   Created private repo " + src)
        time.sleep(3)
    new_wf = put_workflow(src)

    # التوكن اللي يرفع الـ dmg على صفحة الإصدارات العامة
    tok = gh("auth", "token").stdout.strip()
    gh("secret", "set", "RELEASE_TOKEN", "-R", src, input=tok)

    tag = "src-" + version
    tmp = os.path.join(tempfile.gettempdir(), "harmoni-src.zip")
    make_zip(tmp)
    gh("release", "delete", tag, "-R", src, "--yes", "--cleanup-tag", check=False)
    gh("release", "create", tag, tmp, "-R", src, "--title", tag, "--notes", "source for mac build")
    # نخلي آخر 3 بس
    lst = gh("release", "list", "-R", src, "--limit", "50", "--json", "tagName,createdAt", check=False)
    try:
        rels = sorted(json.loads(lst.stdout or "[]"), key=lambda r: r["createdAt"], reverse=True)
        for r in rels[3:]:
            if r["tagName"].startswith("src-"):
                gh("release", "delete", r["tagName"], "-R", src, "--yes", "--cleanup-tag", check=False)
    except Exception:
        pass

    for i in range(10):
        r = gh("workflow", "run", "mac-build.yml", "-R", src, "-f", "version=" + version,
               "-f", "repo=" + release_repo, "-f", "src_tag=" + tag, check=False)
        if r.returncode == 0:
            break
        time.sleep(6 if new_wf else 3)
    else:
        raise RuntimeError("could not start the Mac build: " + (r.stderr or r.stdout)[:300])
    log("   Mac build started on GitHub (runs while Windows builds): https://github.com/%s/actions" % src)


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        log("   MAC: " + str(e))
        sys.exit(1)
