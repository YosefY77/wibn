import os
import tempfile
import unittest
from unittest.mock import patch
os.environ.setdefault("HARMONI_DATA_DIR", tempfile.mkdtemp())
os.environ.setdefault("HARMONI_REGION", "none")
import server


class SearchRetryTests(unittest.TestCase):
    def setUp(self):
        server._SEARCH_CACHE.clear()

    def test_403_is_retried(self):
        calls = []

        def once(q, s, limit, attempt):
            calls.append(attempt)
            if attempt == 0:
                raise Exception("HTTP Error 403: Forbidden")
            return [{"title": "ok"}]
        with patch.object(server, "_do_search_once", once), patch.object(server.time, "sleep", lambda *_: None):
            self.assertEqual(server._do_search("x", "youtube", 5), [{"title": "ok"}])
        self.assertEqual(calls, [0, 1])

    def test_gives_up_after_three_and_message_is_arabic(self):
        def once(q, s, limit, attempt):
            raise Exception("HTTP Error 403: Forbidden")
        with patch.object(server, "_do_search_once", once), patch.object(server.time, "sleep", lambda *_: None):
            j = server.app.test_client().post("/api/search", json={"query": "test song"}).get_json()
        self.assertEqual(j["results"], [])
        self.assertNotIn("403", j["error"])
        self.assertIn("جرّب", j["error"])

    def test_non_transient_error_not_retried(self):
        calls = []

        def once(q, s, limit, attempt):
            calls.append(attempt)
            raise ValueError("bad input")
        with patch.object(server, "_do_search_once", once):
            with self.assertRaises(ValueError):
                server._do_search("x", "youtube", 5)
        self.assertEqual(calls, [0])

    def test_continuation_403_keeps_first_page(self):
        def gen():
            for i in range(3):
                yield {"id": "v%d" % i, "title": "Song %d" % i, "url": "https://www.youtube.com/watch?v=v%d" % i, "duration": 200}
            raise Exception("HTTP Error 403: Forbidden")

        class FakeDL:
            def __init__(self, *a, **k): pass
            def __enter__(self): return self
            def __exit__(self, *a): return False
            def extract_info(self, *a, **k): return {"entries": gen()}
        server._SEARCH_CURSORS.clear()
        with patch.object(server.yt_dlp, "YoutubeDL", FakeDL):
            rows = server._youtube_search_rows("q", "youtube", {}, "ytsearch10:q", 10)
        self.assertEqual(len(rows), 3)

    def test_spotify_403_gets_new_token_then_works(self):
        import io, json, urllib.error
        server._SPOTIFY_TOKEN, server._SPOTIFY_TOKEN_EXP = "old", server.time.time() + 999
        calls = []

        def fake_open(req, timeout=15):
            url = req.full_url
            calls.append((url, req.headers.get("Authorization")))
            if "accounts.spotify.com" in url:
                return io.BytesIO(json.dumps({"access_token": "new", "expires_in": 3600}).encode())
            if req.headers.get("Authorization") == "Bearer old":
                raise urllib.error.HTTPError(url, 403, "Forbidden", {}, io.BytesIO(b'{"error":"x"}'))
            return io.BytesIO(json.dumps({"tracks": {"items": [{"name": "Song", "artists": [{"name": "A"}],
                                                                  "duration_ms": 200000, "external_urls": {"spotify": "https://open.spotify.com/track/1"}}]}}).encode())
        with patch.object(server, "_spotify_credentials", return_value=("id", "sec")), \
             patch.object(server.urllib.request, "urlopen", fake_open), patch.object(server.time, "sleep", lambda *_: None):
            rows = server.spotify_search("song", 5)
        self.assertEqual(rows[0]["title"], "Song")
        self.assertTrue(any("accounts.spotify.com" in u for u, _ in calls))

    def test_spotify_down_falls_back_to_youtube_with_notice(self):
        with patch.object(server, "_spotify_search_work", side_effect=Exception("HTTP Error 403: Forbidden")), \
             patch.object(server, "search", return_value=[{"title": "yt", "web_url": "https://www.youtube.com/watch?v=1", "source": "youtube"}]):
            j = server.app.test_client().post("/api/search", json={"query": "song", "source": "spotify"}).get_json()
        self.assertEqual(j["results"][0]["title"], "yt")
        self.assertIn("يوتيوب", j["notice"])
        self.assertNotIn("error", j)

    def test_soundcloud_uses_one_api_request(self):
        calls = []

        class IE:
            _API_V2_BASE = "https://api-v2.soundcloud.com/"
            _HEADERS = {}
            def initialize(self): pass
            def _call_api(self, url, *a, query=None, headers=None):
                calls.append((url, query))
                return {"collection": [{"title": "S%d" % i, "user": {"username": "U"}, "duration": 180000,
                                        "permalink_url": "https://soundcloud.com/u/s%d" % i, "playback_count": i,
                                        "created_at": "2020-01-0%dT00:00:00Z" % (i + 1)} for i in range(query["limit"])],
                        "next_href": None}

        class DL:
            def __init__(self, *a, **k): pass
            def __enter__(self): return self
            def __exit__(self, *a): return False
            def get_info_extractor(self, name): return IE()
        with patch.object(server.yt_dlp, "YoutubeDL", DL):
            rows = server._do_search("x", "soundcloud", 5)
        self.assertEqual(len(calls), 1)
        self.assertTrue(calls[0][0].endswith("search/tracks"))
        self.assertEqual(len(rows), 5)
        self.assertEqual(rows[0]["source"], "soundcloud")
        self.assertEqual(rows[4]["views"], 4)
        self.assertTrue(rows[0]["ts"])


if __name__ == "__main__":
    unittest.main()
