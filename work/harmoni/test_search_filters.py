import unittest
from unittest.mock import patch
import server


class FakeDL:
    calls = []
    entries = []
    def __init__(self, opts): self.opts = opts
    def __enter__(self): return self
    def __exit__(self, *a): pass
    def extract_info(self, target, download=False, process=False):
        FakeDL.calls.append((target, self.opts))
        def gen():
            for e in FakeDL.entries: yield e
            return
        return {"entries": gen()}

    def get_info_extractor(self, name):
        dl = self

        class IE:
            _API_V2_BASE = "https://api-v2.soundcloud.com/"
            _HEADERS = {}
            def initialize(self): pass
            def _call_api(self, url, *a, query=None, headers=None):
                FakeDL.calls.append(("sc-api:" + url.rsplit("/", 1)[-1] + ":" + query["q"], dl.opts))
                return {"collection": [{"title": e["title"], "user": {"username": "A"}, "duration": 200000,
                                        "permalink_url": "https://soundcloud.com/a/" + e["id"],
                                        "created_at": "2020-01-0%dT00:00:00Z" % (i + 1)}
                                       for i, e in enumerate(FakeDL.entries[:query["limit"]])]}
        return IE()


class SearchFilterTests(unittest.TestCase):
    def setUp(self):
        server._SEARCH_CURSORS.clear()
        server._SEARCH_CACHE.clear(); FakeDL.calls = []
        FakeDL.entries = [{"id": "v%d" % i, "title": "Song %d" % i, "channel": "A", "duration": 200,
                           "url": "https://www.youtube.com/watch?v=v%d" % i, "view_count": i * 10,
                           "timestamp": 2000 - i} for i in range(5)]

    def run_search(self, body):
        with patch.object(server.yt_dlp, "YoutubeDL", FakeDL, create=True), \
             patch.object(server, "_save_search_cache", lambda: None):
            return server.app.test_client().post("/api/search", json=body).get_json()

    def test_default_unchanged_single_request(self):
        self.run_search({"query": "adele", "limit": 3})
        self.assertEqual(len(FakeDL.calls), 1)
        self.assertTrue(FakeDL.calls[0][0].startswith("ytsearch980:adele"))

    def test_popular_one_request_sorted_by_views(self):
        r = self.run_search({"query": "adele", "limit": 4, "sort": "popular"})
        self.assertEqual(len(FakeDL.calls), 1)
        self.assertIn("sp=CAMSAhAB", FakeDL.calls[0][0])
        views = [x["views"] for x in r["results"]]
        self.assertEqual(views, sorted(views, reverse=True))

    def test_oldest_uses_approximate_date_no_extra_request(self):
        r = self.run_search({"query": "adele", "limit": 4, "sort": "oldest"})
        self.assertEqual(len(FakeDL.calls), 1)
        self.assertIn("approximate_date", FakeDL.calls[0][1]["extractor_args"]["youtubetab"])
        ts = [x["ts"] for x in r["results"]]
        self.assertEqual(ts, sorted(ts))

    def test_playlists_filter(self):
        FakeDL.entries = [{"_type": "url", "ie_key": "YoutubeTab", "id": "PL1", "title": "Best of",
                           "url": "https://www.youtube.com/playlist?list=PL1", "channel": "X"},
                          {"id": "v1", "title": "video", "url": "https://www.youtube.com/watch?v=v1"}]
        r = self.run_search({"query": "adele", "limit": 4, "sort": "playlists"})
        self.assertIn("sp=EgIQAw%3D%3D", FakeDL.calls[0][0])
        self.assertEqual([x["kind"] for x in r["results"]], ["playlist"])
        self.assertNotIn(" music", FakeDL.calls[0][0])

    def test_filters_cached_separately_and_reused(self):
        self.run_search({"query": "adele", "limit": 3, "sort": "popular"})
        self.run_search({"query": "adele", "limit": 3, "sort": "popular"})
        self.assertEqual(len(FakeDL.calls), 1)

    def test_soundcloud_sorted_locally_same_request(self):
        r = self.run_search({"query": "x", "source": "soundcloud", "limit": 3, "sort": "oldest"})
        self.assertEqual([c[0] for c in FakeDL.calls], ["sc-api:tracks:x"])   # طلب API واحد سريع
        ts = [x["ts"] for x in r["results"]]
        self.assertEqual(ts, sorted(ts))

    def test_bad_sort_ignored(self):
        self.run_search({"query": "adele", "limit": 3, "sort": "evil"})
        self.assertTrue(FakeDL.calls[0][0].startswith("ytsearch980:"))

    def test_more_consumes_continuation_without_restarting(self):
        first = self.run_search({"query": "adele", "limit": 2, "offset": 0})
        second = self.run_search({"query": "adele", "limit": 2, "offset": 2})
        self.assertEqual(len(FakeDL.calls), 1)
        self.assertEqual(len(first['results']), 2)
        self.assertEqual(len(second['results']), 2)
        self.assertTrue(set(r['web_url'] for r in first['results']).isdisjoint(
            r['web_url'] for r in second['results']))

    def test_spotify_popularity_and_date(self):
        rows = [{"title": "a", "pop": 10, "ts": 5}, {"title": "b", "pop": 90, "ts": 1}]
        self.assertEqual([r["title"] for r in server.sort_results(rows, "popular")], ["b", "a"])
        self.assertEqual([r["title"] for r in server.sort_results(rows, "oldest")], ["b", "a"])
        self.assertEqual(server._release_ts("1999"), 915148800)


if __name__ == "__main__":
    unittest.main()
