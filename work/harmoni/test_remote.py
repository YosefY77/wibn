"""Harmoni Connect: التحكم من الجوال بمفتاح سري، والأوامر توصل للكمبيوتر بس."""
import os, sys, tempfile, unittest
os.environ.setdefault("HARMONI_DATA_DIR", tempfile.mkdtemp())
os.environ["MUSIC_HEADLESS"] = "1"
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import server
import remote

REMOTE = {"Cf-Connecting-Ip": "5.6.7.8"}


class RemoteTests(unittest.TestCase):
    def setUp(self):
        remote._state["base"] = tempfile.mkdtemp()
        remote._state["key"] = None
        remote._cmds.clear()
        remote._state["host"] = None
        self.c = server.app.test_client()

    def test_flow(self):
        info = self.c.get("/api/remote/info").get_json()
        self.assertTrue(info["links"])
        k = info["links"][0]["url"].split("k=")[1]
        self.assertEqual(len(k), 32)
        self.assertEqual(self.c.get("/api/remote/info", headers=REMOTE).status_code, 403)       # من برا: ممنوع
        self.assertEqual(self.c.get("/api/remote/state?k=wrong", headers=REMOTE).status_code, 403)
        j = self.c.get("/api/remote/state?k=" + k, headers=REMOTE).get_json()
        self.assertFalse(j["online"])
        # الكمبيوتر يرسل حالته
        h = self.c.post("/api/remote/host", json={"state": {"title": "Song", "playing": True},
                                                  "lists": {"likes": {"items": []}, "playlists": []}}).get_json()
        self.assertEqual(h["cmds"], [])
        self.assertTrue(h["phone"])          # الجوال فتح الصفحة قبل شوي
        j = self.c.get("/api/remote/state?k=" + k + "&lists=1", headers=REMOTE).get_json()
        self.assertTrue(j["online"])
        self.assertEqual(j["state"]["title"], "Song")
        self.assertIn("likes", j["lists"])
        # أوامر من الجوال
        self.assertEqual(self.c.post("/api/remote/cmd", json={"k": k, "action": "rm -rf"}, headers=REMOTE).status_code, 400)
        self.assertEqual(self.c.post("/api/remote/cmd", json={"k": "x", "action": "skip"}, headers=REMOTE).status_code, 403)
        self.c.post("/api/remote/cmd", json={"k": k, "action": "skip"}, headers=REMOTE)
        self.c.post("/api/remote/cmd", json={"k": k, "action": "play", "value": 3, "kind": "pl:abc"}, headers=REMOTE)
        self.assertEqual(self.c.post("/api/remote/host", json={}, headers=REMOTE).status_code, 403)   # الأوامر للكمبيوتر بس
        cmds = self.c.post("/api/remote/host", json={}).get_json()["cmds"]
        self.assertEqual([(c["action"], c.get("kind")) for c in cmds], [("skip", None), ("play", "pl:abc")])
        # فصل كل الأجوال
        k2 = self.c.post("/api/remote/info", json={"reset": True}).get_json()["links"][0]["url"].split("k=")[1]
        self.assertNotEqual(k, k2)
        self.assertEqual(self.c.get("/api/remote/state?k=" + k, headers=REMOTE).status_code, 403)

    def test_page(self):
        r = self.c.get("/remote?k=abc")
        self.assertEqual(r.status_code, 200)
        self.assertIn(b"Harmoni Connect", r.data)


if __name__ == "__main__":
    unittest.main()
