import os
import tempfile
import time
import unittest
from unittest.mock import patch
import server


class SpeedTests(unittest.TestCase):
    def setUp(self):
        server._STREAM_CACHE.clear()
        self.tmp = tempfile.mkdtemp()
        server._STREAM_CACHE_FILE = os.path.join(self.tmp, "stream_cache.json")
        server._PERF_FILE = os.path.join(self.tmp, "perf.log")

    def test_ttl_from_expire(self):
        url = "https://rr1.googlevideo.com/videoplayback?expire=%d&x=1" % (time.time() + 6 * 3600)
        self.assertGreater(server._stream_ttl(url), 4 * 3600)
        self.assertEqual(server._stream_ttl("https://x/y"), server._STREAM_TTL)

    def test_stream_cache_persists(self):
        url = "https://rr1.googlevideo.com/videoplayback?expire=%d" % (time.time() + 3 * 3600)
        with patch.object(server, "resolve_stream_url", return_value=url) as r:
            self.assertEqual(server.cached_stream("https://youtube.com/watch?v=a"), url)
            self.assertEqual(server.cached_stream("https://youtube.com/watch?v=a"), url)
            self.assertEqual(r.call_count, 1)
        server._STREAM_SAVE_TIMER[0].cancel()
        server._STREAM_SAVE_TIMER[0].function()
        server._STREAM_CACHE.clear()
        server._load_stream_cache()
        self.assertIn("https://youtube.com/watch?v=a", server._STREAM_CACHE)

    def test_resolve_endpoint_logs_timing(self):
        with patch.object(server, "cached_stream", return_value="https://s/x"):
            j = server.app.test_client().post("/api/resolve_stream", json={"web_url": "https://youtube.com/watch?v=b", "title": "T"}).get_json()
        self.assertEqual(j["stream_url"], "https://s/x")
        self.assertIn("stream", open(server._PERF_FILE, encoding="utf-8").read())

    def test_prefetch_skips_spotify_and_duplicates(self):
        server._PREFETCH_SEEN.clear()
        with patch.object(server._PREFETCH_EXEC, "submit") as sub:
            c = server.app.test_client()
            j = c.post("/api/prefetch", json={"tracks": [{"web_url": "https://youtube.com/watch?v=c"}, {"web_url": "https://open.spotify.com/track/x"}]}).get_json()
            j2 = c.post("/api/prefetch", json={"tracks": [{"web_url": "https://youtube.com/watch?v=c"}]}).get_json()
        self.assertEqual((j["queued"], j2["queued"]), (1, 0))

    def test_cache_audio_only_supported_sites(self):
        with patch.object(server.threading, "Thread"):
            j = server.app.test_client().post("/api/cache_audio", json={"tracks": [{"web_url": "https://www.youtube.com/watch?v=d"}, {"web_url": "https://evil.example/x"}]}).get_json()
        self.assertEqual(j["queued"], 1)
        server._AUDIO_QUEUE.clear(); server._AUDIO_WORKER[0] = False

    def test_artist_guess(self):
        import harmoni_artists
        with patch.object(harmoni_artists, "_deezer", return_value={}):
            self.assertEqual(server.resolve_real_artist("Klash Ft. L.K. | قفلة", "Psycho")["artist"], "Klash")
            self.assertEqual(server.resolve_real_artist("song", "Someone - Topic")["artist"], "Someone")


if __name__ == "__main__":
    unittest.main()
