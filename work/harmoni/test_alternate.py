"""أغنية ما تشتغل (تأكيد عمر/محذوفة): نجرب نسخة ثانية لنفس الأغنية."""
import os, sys, tempfile, unittest
from unittest.mock import patch
os.environ.setdefault("HARMONI_DATA_DIR", tempfile.mkdtemp())
os.environ["MUSIC_HEADLESS"] = "1"
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import server

ORIG = "https://www.youtube.com/watch?v=bm2nivsyPbs"
ROWS = [
    {"title": "Some Other Song", "web_url": "https://www.youtube.com/watch?v=other000001", "duration": 200},
    {"title": "Fasla - El Joker (Official Audio)", "web_url": "https://www.youtube.com/watch?v=goodalt0001", "duration": 182},
    {"title": "Fasla El Joker", "web_url": ORIG, "duration": 180},
]


class AlternateTests(unittest.TestCase):
    def setUp(self):
        server._ALT_CACHE.clear()
        # بدون إنترنت: بحث يوتيوب ميوزك وساوند كلاود فاضيين إلا لو الاختبار حدد غير كذا
        for name, obj, val in (("_ytmusic_songs", server, []), ("_sc_stream", server.Room, None)):
            p = patch.object(obj, name, return_value=val)
            p.start()
            self.addCleanup(p.stop)

    def test_picks_matching_alternate(self):
        def fake_stream(url, *a, **k):
            return None if url == ORIG else "https://rr.googlevideo.com/" + url[-11:]
        with patch.object(server, "search", return_value=ROWS), patch.object(server, "cached_stream", side_effect=fake_stream):
            url, alt = server._alternate_stream(ORIG, "El Joker - Fasla (Official Video)", "El Joker", 180)
        self.assertEqual(alt, ROWS[1]["web_url"])
        self.assertTrue(url.endswith("goodalt0001"))

    def test_rejects_different_duration(self):
        rows = [{"title": "Fasla El Joker live", "web_url": "https://www.youtube.com/watch?v=liveversion", "duration": 900}]
        with patch.object(server, "search", return_value=rows), patch.object(server, "cached_stream", return_value="x"):
            self.assertEqual(server._alternate_stream(ORIG, "El Joker - Fasla", "", 180), (None, None))

    def test_endpoint_uses_alternate(self):
        with patch.object(server, "cached_stream", side_effect=lambda u, *a, **k: None if u == ORIG else "https://rr.googlevideo.com/ok"), \
             patch.object(server, "search", return_value=ROWS):
            j = server.app.test_client().post("/api/resolve_stream", json={"web_url": ORIG, "title": "El Joker - Fasla", "duration": 180}).get_json()
        self.assertEqual(j["stream_url"], "https://rr.googlevideo.com/ok")
        self.assertEqual(j["alternate"], ROWS[1]["web_url"])

    def test_prefers_youtube_music_official_audio(self):
        music = [{"title": "Fasla", "web_url": "https://www.youtube.com/watch?v=topicaudio1", "duration": 181, "artist": "El Joker - Topic", "music": True},
                 {"title": "Fasla", "web_url": "https://www.youtube.com/watch?v=wrongartist", "duration": 181, "artist": "Someone Else", "music": True}]
        with patch.object(server, "_ytmusic_songs", return_value=music), patch.object(server, "search", return_value=ROWS) as srch, \
             patch.object(server, "cached_stream", side_effect=lambda u, *a, **k: None if u == ORIG else "https://rr.googlevideo.com/" + u[-11:]):
            url, alt = server._alternate_stream(ORIG, "El Joker - Fasla (Official Video)", "El Joker", 180)
        self.assertEqual(alt, "https://www.youtube.com/watch?v=topicaudio1")
        srch.assert_not_called()

    def test_music_rejects_other_artist_and_far_duration(self):
        music = [{"title": "Fasla", "web_url": "https://www.youtube.com/watch?v=wrongartist", "duration": 181, "artist": "Someone Else - Topic", "music": True},
                 {"title": "Fasla", "web_url": "https://www.youtube.com/watch?v=remix000001", "duration": 260, "artist": "El Joker - Topic", "music": True}]
        with patch.object(server, "_ytmusic_songs", return_value=music), patch.object(server, "search", return_value=[]), \
             patch.object(server.Room, "_sc_stream", return_value=None), patch.object(server, "cached_stream", return_value="x"):
            self.assertEqual(server._alternate_stream(ORIG, "El Joker - Fasla", "El Joker", 180), (None, None))

    def test_age_skips_soundcloud_guess(self):
        age = RuntimeError("Sign in to confirm your age")
        with patch.object(server, "_extract_base", side_effect=age), \
             patch.object(server.Room, "_sc_stream", return_value="https://sndcdn.com/wrong") as sc:
            self.assertIsNone(server.resolve_stream_url(ORIG, "youtube", "Fasla", "El Joker"))
            sc.assert_not_called()


if __name__ == "__main__":
    unittest.main()
