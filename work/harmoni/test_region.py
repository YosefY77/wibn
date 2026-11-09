import os
import tempfile
import unittest
from unittest.mock import patch
os.environ["HARMONI_DATA_DIR"] = tempfile.mkdtemp()
os.environ.pop("HARMONI_REGION", None)
import server


class RegionTests(unittest.TestCase):
    def setUp(self):
        server._SEARCH_CACHE.clear()
        server._REGION_STATE["detected"] = "SA"
        try:
            os.remove(server._region_file())
        except OSError:
            pass

    def test_auto_region_used_in_youtube_search_key(self):
        with patch.object(server, "search", return_value=[]) as s:
            server._search_work("عبدالمجيد عبدالله", "youtube", 49)
            s.assert_called_once_with("عبدالمجيد عبدالله اغنية", "youtube@SA", 49)

    def test_region_is_always_auto(self):
        c = server.app.test_client()
        self.assertEqual(c.post("/api/region", json={"region": "eg"}).status_code, 405)
        with open(server._region_file(), "w", encoding="utf-8") as f:
            f.write('{"region": "EG"}')
        r = c.get("/api/region").get_json()
        self.assertEqual((r["setting"], r["region"]), ("auto", "SA"))

    def test_region_reaches_youtube_context(self):
        class FakeDL:
            params = {"harmoni_gl": "SA"}

        class FakeIE:
            _downloader = FakeDL()
        with patch.object(server, "_orig_extract_context", return_value={"client": {"hl": "en"}}):
            ctx = server._harmoni_extract_context(FakeIE())
        self.assertEqual(ctx["client"]["gl"], "SA")
        self.assertEqual(ctx["client"]["hl"], "en")

    def test_split_source_ignores_region(self):
        self.assertEqual(server._split_source("youtube:popular@SA"), ("youtube", "popular"))
        self.assertEqual(server._source_region("youtube:popular@SA"), "SA")

    def test_arabic_query_prefers_arabic_titles(self):
        rows = [{"title": "Love Song", "artist": "Someone", "duration": 200},
                {"title": "أغنية حب", "artist": "مطرب", "duration": 200}]
        self.assertEqual(server.rank_music_results(rows, "اغنية حب")[0]["title"], "أغنية حب")


if __name__ == "__main__":
    unittest.main()
