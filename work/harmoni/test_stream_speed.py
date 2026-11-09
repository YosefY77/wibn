"""تسريع التشغيل: أول الأغنية من الذاكرة + اختيار m4a."""
import os, sys, tempfile, unittest, urllib.parse
from unittest.mock import patch
os.environ.setdefault("HARMONI_DATA_DIR", tempfile.mkdtemp())
os.environ["MUSIC_HEADLESS"] = "1"
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import server

URL = "https://rr1---sn-abc.googlevideo.com/videoplayback?mime=audio%2Fmp4&clen=1000000"


class StreamSpeedTests(unittest.TestCase):
    def setUp(self):
        server._HEAD_CACHE.clear()
        server._HEAD_CACHE[URL] = (b"x" * 1000, 1000000, "audio/mp4", 0)
        self.c = server.app.test_client()

    def get(self, rng):
        with patch.object(server, "_proxy_allowed", return_value=True):
            return self.c.get("/api/audio_proxy?url=" + urllib.parse.quote(URL, safe=""), headers={"Range": rng})

    def test_head_served_from_memory(self):
        r = self.get("bytes=0-")
        self.assertEqual(r.status_code, 206)
        self.assertEqual(r.headers["Content-Range"], "bytes 0-999/1000000")
        self.assertEqual(len(r.data), 1000)
        r = self.get("bytes=0-99")
        self.assertEqual((r.headers["Content-Range"], len(r.data)), ("bytes 0-99/1000000", 100))

    def test_m4a_preferred_and_webm_cache_skipped(self):
        info = {"formats": [
            {"url": "w", "acodec": "opus", "vcodec": "none", "ext": "webm", "abr": 160, "protocol": "https"},
            {"url": "m", "acodec": "mp4a.40.2", "vcodec": "none", "ext": "m4a", "abr": 129, "protocol": "https"}]}
        self.assertEqual(server.pick_stream_url(info)[0], "m")
        self.assertTrue(server._is_webm_stream("https://x.googlevideo.com/v?mime=audio%2Fwebm"))
        self.assertFalse(server._is_webm_stream(URL))


if __name__ == "__main__":
    unittest.main()
