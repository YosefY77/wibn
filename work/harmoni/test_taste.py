"""استبيان الذوق: تنظيف البيانات + استخدامها في الاقتراحات."""
import os, sys, tempfile, unittest
from unittest.mock import patch
os.environ.setdefault("HARMONI_DATA_DIR", tempfile.mkdtemp())
os.environ["MUSIC_HEADLESS"] = "1"
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import server


class TasteTests(unittest.TestCase):
    def test_clean_taste_drops_unknown_values(self):
        t = server._clean_taste({"genres": ["khaleeji", "bad", 5], "moods": ["focus", "x"], "artists": [" Drake ", "", "x" * 99], "langs": ["ar", "fr"]})
        self.assertEqual(t["genres"], ["khaleeji"])
        self.assertEqual(t["moods"], ["focus"])
        self.assertEqual(t["artists"][0], "Drake")
        self.assertEqual(len(t["artists"][1]), 60)
        self.assertEqual(t["langs"], ["ar"])
        self.assertIsNone(server._clean_taste({"genres": []}))
        self.assertIsNone(server._clean_taste("nope"))

    def test_suggest_uses_taste_without_history(self):
        seen = []

        def fake(q):
            seen.append(q)
            return [{"title": q + " song", "artist": "A", "duration": 200, "web_url": "https://y/" + q, "source": "youtube"}]
        with patch.object(server, "_search_cached", side_effect=fake):
            j = server.app.test_client().post("/api/suggest", json={"taste": {"genres": ["khaleeji"], "artists": ["محمد عبده"]}}).get_json()
        self.assertIn("محمد عبده music", seen)
        self.assertIn(server.TASTE_GENRES["khaleeji"], seen)
        self.assertTrue(j["results"])


if __name__ == "__main__":
    unittest.main()
