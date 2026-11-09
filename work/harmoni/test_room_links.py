"""غرف أصحابك: رابط عام للغرفة (نفق سريع) + فتح رابط دعوة غرفة على جهاز ثاني."""
import os, sys, tempfile, unittest
from unittest.mock import patch
os.environ.setdefault("HARMONI_DATA_DIR", tempfile.mkdtemp())
os.environ["MUSIC_HEADLESS"] = "1"
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import server

REMOTE = {"Cf-Connecting-Ip": "1.2.3.4"}


class RoomLinkTests(unittest.TestCase):
    def setUp(self):
        self.c = server.app.test_client()

    def test_remote_cannot_start_tunnel_or_open_windows(self):
        self.assertEqual(self.c.post("/api/room_tunnel", headers=REMOTE).status_code, 403)
        self.assertEqual(self.c.post("/api/open_room", json={"url": "https://a-b.trycloudflare.com/?room=123456"}, headers=REMOTE).status_code, 403)

    def test_tunnel_ready_when_url_known(self):
        with patch.object(server, "TUNNEL_URL", "https://seo-lamp.trycloudflare.com"):
            j = self.c.get("/api/room_tunnel").get_json()
        self.assertEqual(j["state"], "ready")
        self.assertEqual(j["url"], "https://seo-lamp.trycloudflare.com")

    def test_tunnel_starts_in_background(self):
        with patch.object(server, "TUNNEL_URL", ""), patch.object(server.threading, "Thread") as th:
            server._ROOM_TUNNEL.update(state="idle")
            j = self.c.post("/api/room_tunnel").get_json()
        self.assertEqual(j["state"], "starting")
        th.assert_called_once()

    def test_open_room_validates_links(self):
        for bad in ("http://192.168.1.5:5000/?room=123456", "https://evil.com/?x=1", "javascript:alert(1)",
                    "https://a.trycloudflare.com/?room=12", "file:///C:/x?room=123456"):
            self.assertEqual(self.c.post("/api/open_room", json={"url": bad}).status_code, 400, bad)
        opened = []
        with patch("webbrowser.open", side_effect=lambda u: opened.append(u)):
            r = self.c.post("/api/open_room", json={"url": "https://seo-lamp-fiber.trycloudflare.com/?room=123456&guest=Yousef"})
        self.assertEqual(r.status_code, 200)
        self.assertEqual(opened, ["https://seo-lamp-fiber.trycloudflare.com/?room=123456&guest=Yousef"])

    def test_focus_main_local_only(self):
        self.assertEqual(self.c.post("/api/focus_main", headers=REMOTE).status_code, 403)
        self.assertEqual(self.c.post("/api/focus_main").get_json()["app"], "harmoni")

    def test_quick_tunnel_regex_ignores_api_host(self):
        import re
        src = open(server.__file__, encoding="utf-8-sig").read()
        pat = re.search(r'url_re = re\.compile\(r"([^"]+)"\)', src).group(1)
        rx = re.compile(pat)
        self.assertIsNone(rx.search("ERR failed to request https://api.trycloudflare.com/tunnel"))
        self.assertEqual(rx.search("|  https://seo-lamp-fiber-wealthy.trycloudflare.com  |").group(0),
                         "https://seo-lamp-fiber-wealthy.trycloudflare.com")


if __name__ == "__main__":
    unittest.main()
