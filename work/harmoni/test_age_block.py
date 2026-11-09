"""الأغاني المقيدة بالعمر: تنحفظ وما تطلع بالنتائج مرة ثانية، والتشغيل/التحميل ما يحاولها عالفاضي."""
import os, sys, tempfile, unittest
os.environ.setdefault("HARMONI_DATA_DIR", tempfile.mkdtemp())
os.environ["MUSIC_HEADLESS"] = "1"
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import server
from flask import jsonify

AGE = "https://www.youtube.com/watch?v=AGEAGEAGE01"
OK = "https://www.youtube.com/watch?v=OKOKOKOKOK1"


@server.app.route("/api/search_age_probe")
def _probe():
    return jsonify({"results": [{"web_url": AGE, "title": "x"}, {"web_url": OK, "title": "y"}]})


class AgeTests(unittest.TestCase):
    def setUp(self):
        server._AGE_URLS.discard(AGE)
        self.c = server.app.test_client()

    def test_marked_song_is_hidden_and_never_resolved(self):
        self.assertEqual(len(self.c.get("/api/search_age_probe").get_json()["results"]), 2)
        server._mark_age(AGE)
        got = self.c.get("/api/search_age_probe").get_json()["results"]
        self.assertEqual([t["web_url"] for t in got], [OK])
        self.assertIsNone(server.resolve_stream_url(AGE))
        self.assertTrue(os.path.exists(server._AGE_FILE))
        self.assertTrue(server._is_age_error("ERROR: Sign in to confirm your age. This video may be inappropriate"))

    def test_audio_cache_settings(self):
        j = self.c.get("/api/audio_cache").get_json()
        self.assertEqual(j["options"], [0, 300, 700, 2000])
        self.assertEqual(self.c.post("/api/audio_cache", json={"limit_mb": 300}).get_json()["limit_mb"], 300)
        self.assertEqual(server._cache_max_bytes(), 300 * 1048576)
        self.assertEqual(self.c.post("/api/audio_cache", json={"limit_mb": 12345}).get_json()["limit_mb"], 700)
        self.assertEqual(self.c.post("/api/audio_cache", json={"limit_mb": 0}).get_json()["limit_mb"], 0)
        self.assertTrue(self.c.post("/api/audio_cache", json={"clear": True}).status_code == 200)
        self.c.post("/api/audio_cache", json={"limit_mb": 700})


if __name__ == "__main__":
    unittest.main()
