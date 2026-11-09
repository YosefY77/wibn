"""أغاني من جهاز المستخدم: رفع ملف صوت + تشغيله + الحماية."""
import io, os, sys, tempfile, unittest
os.environ.setdefault("HARMONI_DATA_DIR", tempfile.mkdtemp())
os.environ["MUSIC_HEADLESS"] = "1"
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import server


class UploadTests(unittest.TestCase):
    def setUp(self):
        self.c = server.app.test_client()

    def up(self, name, data=b"ID3fake-mp3-data" * 100, addr="127.0.0.1"):
        return self.c.post("/api/upload_track", data={"file": (io.BytesIO(data), name)},
                           content_type="multipart/form-data", environ_base={"REMOTE_ADDR": addr})

    def test_upload_and_play(self):
        j = self.up("song.mp3").get_json()
        self.assertTrue(j["web_url"].startswith("local:"))
        r = self.c.get(j["stream_url"], headers={"Range": "bytes=0-9"})
        self.assertEqual(r.status_code, 206)
        self.assertEqual(r.data, b"ID3fake-mp")
        res = self.c.post("/api/resolve_stream", json={"web_url": j["web_url"]}).get_json()
        self.assertEqual(res["stream_url"], j["stream_url"])

    def test_rejects_non_audio_and_remote(self):
        self.assertEqual(self.up("evil.exe").status_code, 400)
        self.assertEqual(self.up("song.mp3", addr="10.1.2.3").status_code, 403)

    def test_bad_ids(self):
        self.assertEqual(self.c.get("/api/local_audio/..%2F..%2Fserver.py").status_code, 404)
        self.assertIsNone(server._local_track_path("local:../../x"))
        self.assertEqual(self.c.post("/api/resolve_stream", json={"web_url": "local:" + "0" * 32}).status_code, 404)


if __name__ == "__main__":
    unittest.main()
