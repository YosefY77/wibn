"""متابعة بلاي لستات سبوتيفاي العامة بدون تسجيل دخول."""
import os, sys, tempfile, unittest
from unittest.mock import patch
os.environ.setdefault("HARMONI_DATA_DIR", tempfile.mkdtemp())
os.environ["MUSIC_HEADLESS"] = "1"
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import server

A, B = "A" * 22, "B" * 22


def fake_pub(sid):
    return {A: ("Gym", "img-a", [{"title": "S1", "artist": "X"}]),
            B: ("Chill", "img-b", [{"title": "S2", "artist": "Y"}, {"title": "S3", "artist": "Z"}])}[sid]


class PublicSpotifyTests(unittest.TestCase):
    def setUp(self):
        server._SP_PUB_CACHE.clear()
        self.c = server.app.test_client()

    def test_resolve_link(self):
        with patch.object(server, "_sp_public_playlist", side_effect=fake_pub):
            j = self.c.post("/api/spotify_public/resolve", json={"url": "https://open.spotify.com/playlist/%s?si=1" % B}).get_json()
        self.assertEqual((j["id"], j["name"], j["count"]), (B, "Chill", 2))

    def test_resolve_rejects_track_link(self):
        r = self.c.post("/api/spotify_public/resolve", json={"url": "https://open.spotify.com/track/%s" % A})
        self.assertEqual(r.status_code, 400)

    def test_sync_puts_favorite_playlist_into_likes(self):
        with patch.object(server, "_sp_public_playlist", side_effect=fake_pub):
            j = self.c.post("/api/spotify_public/sync", json={"playlists": [A, B], "fav": B}).get_json()
        self.assertEqual([t["title"] for t in j["likes"]], ["S2", "S3"])
        self.assertEqual([p["name"] for p in j["playlists"]], ["Gym"])
        self.assertEqual(j["failed"], [])

    def test_sync_reports_unreadable(self):
        with patch.object(server, "_sp_public_playlist", side_effect=RuntimeError("private")):
            j = self.c.post("/api/spotify_public/sync", json={"playlists": [A]}).get_json()
        self.assertEqual(j["failed"], [A])

    def test_parse_user(self):
        self.assertEqual(server._sp_parse_user("https://open.spotify.com/user/31tkamn3kvuhkoy4sjasrcn4xf24?si=x"), "31tkamn3kvuhkoy4sjasrcn4xf24")
        self.assertEqual(server._sp_parse_user("bad user!"), "")

    def test_profile_link(self):
        prof = {"name": "hiro", "playlists": [{"id": A, "name": "Gym"}, {"id": B, "name": "Chill"}]}
        with patch.object(server, "_sp_read_profile", return_value=prof), \
             patch.object(server, "_sp_public_playlist", side_effect=fake_pub):
            j = self.c.post("/api/spotify_public/user", json={"user": "https://open.spotify.com/user/hiro"}).get_json()
        self.assertEqual(j["name"], "hiro")
        self.assertEqual([(p["name"], p["count"]) for p in j["playlists"]], [("Gym", 1), ("Chill", 2)])

    def test_sync_discovers_new_profile_playlists_but_respects_hidden(self):
        prof = {"name": "hiro", "playlists": [{"id": A, "name": "Gym"}, {"id": B, "name": "Chill"}]}
        with patch.object(server, "_sp_read_profile", return_value=prof), \
             patch.object(server, "_sp_public_playlist", side_effect=fake_pub):
            j = self.c.post("/api/spotify_public/sync", json={"user": "hiro", "playlists": [], "hidden": [A]}).get_json()
        self.assertEqual(j["discovered"], [B])
        self.assertEqual([p["name"] for p in j["playlists"]], ["Chill"])


if __name__ == "__main__":
    unittest.main()
