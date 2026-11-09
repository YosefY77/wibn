import os
import tempfile
import unittest
from unittest.mock import patch
import server

REMOTE = {"REMOTE_ADDR": "203.0.113.5"}
TUNNEL = {"HTTP_CF_CONNECTING_IP": "198.51.100.7"}


class SecurityTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        server._PROFILES_FILE = os.path.join(self.tmp, "profiles.json")
        server._SECRET_FILE = os.path.join(self.tmp, ".secret")
        server._SECRET[0] = None
        server.PROFILES = {}
        self.c = server.app.test_client()
        r = self.c.post("/api/register", json={"username": "tester", "password": "pass1234"}).get_json()
        self.uid, self.token = r["uid"], r["token"]

    def test_remote_write_without_token_is_rejected(self):
        r = self.c.post("/api/userdata", json={"uid": self.uid, "likes": [{"title": "x"}]}, environ_base=REMOTE)
        self.assertEqual(r.status_code, 403)
        r = self.c.post("/api/userdata", json={"uid": self.uid, "likes": []}, environ_base={"REMOTE_ADDR": "127.0.0.1", **TUNNEL})
        self.assertEqual(r.status_code, 403)

    def test_remote_write_with_token_works(self):
        r = self.c.post("/api/userdata", json={"uid": self.uid, "likes": [{"title": "x"}]},
                        headers={"X-Harmoni-Token": self.token}, environ_base=REMOTE)
        self.assertEqual(r.status_code, 200)

    def test_local_owner_gets_token(self):
        r = self.c.get("/api/userdata?uid=" + self.uid, environ_base={"REMOTE_ADDR": "127.0.0.1"}).get_json()
        self.assertEqual(r.get("token"), self.token)

    def test_login_returns_token(self):
        r = self.c.post("/api/login", json={"username": "tester", "password": "pass1234"}).get_json()
        self.assertEqual(r["token"], self.token)

    def test_profile_backup_recovery(self):
        server._save_profiles(); server._save_profiles()
        with open(server._PROFILES_FILE, "w") as f:
            f.write("{broken")
        server._load_profiles()
        self.assertIn(self.uid, server.PROFILES)

    def test_proxy_allowlist(self):
        self.assertTrue(server._proxy_allowed("https://rr3---sn-x.googlevideo.com/videoplayback?a=1"))
        self.assertTrue(server._proxy_allowed("https://cf-media.sndcdn.com/x.mp3"))
        self.assertFalse(server._proxy_allowed("http://192.168.1.1/admin"))
        self.assertFalse(server._proxy_allowed("https://googlevideo.com.evil.example/x"))
        self.assertEqual(self.c.get("/api/audio_proxy?url=http://example.com/").status_code, 400)

    def test_spotify_best_match(self):
        rows = [{"title": "Song (Live)", "artist": "Artist", "duration": 260},
                {"title": "Song (Sped Up)", "artist": "x", "duration": 150},
                {"title": "Artist - Song", "artist": "Artist - Topic", "duration": 201}]
        best = server._best_match({"title": "Song", "artist": "Artist", "duration": 200}, rows)
        self.assertEqual(best["duration"], 201)


if __name__ == "__main__":
    unittest.main()
