"""تغييرات سبوتيفاي فبراير 2026: بحث 10 نتائج لكل طلب، و/playlists/{id}/items بدل /tracks."""
import io
import json
import os
import tempfile
import unittest
import urllib.parse
from unittest.mock import patch
os.environ.setdefault("HARMONI_DATA_DIR", tempfile.mkdtemp())
os.environ.setdefault("HARMONI_REGION", "none")
import server


def track(i):
    return {"type": "track", "name": "Song %d" % i, "artists": [{"name": "A"}], "duration_ms": 200000,
            "album": {"images": [{"url": "img"}]}, "external_urls": {"spotify": "https://open.spotify.com/track/%d" % i}}


class Spotify2026Tests(unittest.TestCase):
    def setUp(self):
        server._SPOTIFY_TOKEN, server._SPOTIFY_TOKEN_EXP = "tok", server.time.time() + 999

    def test_search_pages_of_ten(self):
        seen = []

        def fake_open(req, timeout=15):
            q = urllib.parse.parse_qs(urllib.parse.urlparse(req.full_url).query)
            lim, off = int(q["limit"][0]), int(q["offset"][0])
            seen.append((lim, off))
            self_lim = min(lim, 10)
            return io.BytesIO(json.dumps({"tracks": {"items": [track(off + k) for k in range(self_lim)]}}).encode())
        with patch.object(server, "_spotify_credentials", return_value=("id", "sec")), \
             patch.object(server.urllib.request, "urlopen", fake_open):
            rows = server.spotify_search("x", 25)
        self.assertEqual(sorted(seen), [(5, 20), (10, 0), (10, 10)])
        self.assertTrue(all(l <= 10 for l, _ in seen))
        self.assertEqual([r["title"] for r in rows][:3], ["Song 0", "Song 1", "Song 2"])
        self.assertEqual(len(rows), 25)

    def test_premium_message(self):
        import urllib.error

        def fake_open(req, timeout=15):
            raise urllib.error.HTTPError(req.full_url, 403, "Forbidden", {},
                                         io.BytesIO(b'{"error":{"status":403,"message":"Active premium subscription required for the owner of the app."}}'))
        with patch.object(server, "_spotify_credentials", return_value=("id", "sec")), \
             patch.object(server.urllib.request, "urlopen", fake_open):
            with self.assertRaises(RuntimeError) as cm:
                server.spotify_search("x", 5)
        self.assertIn("Premium", str(cm.exception))

    def test_playlist_items_new_shape(self):
        pages = {"/playlists/P/items": [{"item": track(1)}, {"item": {"type": "episode", "name": "Pod"}}, {"track": track(2)}]}

        def fake_items(tok, path, limit=50):
            if path not in pages:
                raise AssertionError("old endpoint used: " + path)
            yield from pages[path]
        with patch.object(server, "_sp_items", fake_items):
            shapes = [server._sp_track_shape(server._sp_item_track(it)) for it in server._sp_playlist_items("t", "P")]
        self.assertEqual([s["title"] for s in shapes if s], ["Song 1", "Song 2"])


if __name__ == "__main__":
    unittest.main()
