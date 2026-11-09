"""تحميل الأغاني للتشغيل بدون نت: إضافة للطابور، التشغيل من الجهاز، الحذف، والحماية."""
import os, sys, tempfile, time, types, unittest
os.environ.setdefault("HARMONI_DATA_DIR", tempfile.mkdtemp())
os.environ["MUSIC_HEADLESS"] = "1"
os.environ.setdefault("HARMONI_REGION", "none")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import server


class _FakeYDL:
    calls = []

    def __init__(self, opts):
        self.opts = opts

    def __enter__(self):
        return self

    def __exit__(self, *a):
        return False

    def extract_info(self, url, download=False):
        _FakeYDL.calls.append(url)
        self.path = self.opts["outtmpl"].replace("%(ext)s", "m4a")
        with open(self.path, "wb") as f:
            f.write(b"\x00\x00\x00\x18ftypM4A " + b"x" * 5000)
        for h in self.opts.get("progress_hooks") or []:
            h({"status": "downloading", "downloaded_bytes": 2500, "total_bytes": 5000})
        return {"ext": "m4a"}

    def prepare_filename(self, info):
        return self.path


class DownloadTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        server.yt_dlp = types.SimpleNamespace(YoutubeDL=_FakeYDL)
        server.search = lambda q, src, n=1: [{"web_url": "https://www.youtube.com/watch?v=found123"}]

    def setUp(self):
        self.c = server.app.test_client()
        self.c.post("/api/downloads/remove", json={"all": True})

    def wait(self):
        for _ in range(100):
            j = self.c.get("/api/downloads").get_json()
            if not j["queue"] and not j["current"]:
                return j
            time.sleep(0.05)
        self.fail("downloads did not finish")

    def test_download_play_offline_and_remove(self):
        tracks = [{"web_url": "https://www.youtube.com/watch?v=aaa", "title": "Song A", "artist": "X", "duration": 200},
                  {"title": "Song B", "artist": "Y"},                       # بدون رابط ⇒ نبحث عنه
                  {"web_url": "local:abc", "title": "Mine"}]                # ملف المستخدم نفسه ⇒ نتخطاه
        j = self.c.post("/api/downloads/add", json={"tracks": tracks}).get_json()
        self.assertEqual(j["added"], 2)
        self.assertEqual(j["skipped"], 1)
        j = self.wait()
        self.assertEqual(j["count"], 2)
        # نفس الأغنية مرة ثانية ما تنضاف
        again = self.c.post("/api/downloads/add", json={"tracks": tracks[:1]}).get_json()
        self.assertEqual((again["added"], again["already"]), (0, 1))
        # التشغيل من الجهاز (بالرابط، وبالاسم للي ما لها رابط)
        res = self.c.post("/api/resolve_stream", json={"web_url": tracks[0]["web_url"], "title": "Song A"}).get_json()
        self.assertTrue(res["offline"])
        r = self.c.get(res["stream_url"], headers={"Range": "bytes=0-3"})
        self.assertEqual(r.status_code, 206)
        res_b = self.c.post("/api/resolve_stream", json={"title": "song b", "artist": "y"}).get_json()
        self.assertTrue(res_b.get("offline"))
        # الحذف
        j = self.c.post("/api/downloads/remove", json={"web_urls": [tracks[0]["web_url"]]}).get_json()
        self.assertEqual(j["count"], 1)
        self.assertEqual(self.c.get(res["stream_url"]).status_code, 404)

    def test_index_survives_restart(self):
        self.c.post("/api/downloads/add", json={"tracks": [{"web_url": "https://www.youtube.com/watch?v=keep", "title": "Keep"}]})
        self.wait()
        server._DL["items"].clear()
        server._DL["loaded"] = False
        self.assertEqual(self.c.get("/api/downloads").get_json()["count"], 1)

    def test_remote_cannot_add_or_remove(self):
        env = {"REMOTE_ADDR": "10.0.0.5"}
        self.assertEqual(self.c.post("/api/downloads/add", json={"tracks": []}, environ_base=env).status_code, 403)
        self.assertEqual(self.c.post("/api/downloads/remove", json={"all": True}, environ_base=env).status_code, 403)
        self.assertEqual(self.c.get("/api/downloads/file?k=../../x").status_code, 404)


if __name__ == "__main__":
    unittest.main()
