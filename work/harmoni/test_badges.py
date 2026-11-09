"""الشارات: البذرة الأولى، الأكثر استماعاً، وإدارة المؤسس فقط."""
import json, os, sys, tempfile, unittest
from unittest.mock import patch
os.environ.setdefault("HARMONI_DATA_DIR", tempfile.mkdtemp())
os.environ["MUSIC_HEADLESS"] = "1"
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import server
import badges

PROFILES = {
    "uh": {"uid": "uh", "username": "Hiro", "name": "ابوسعد", "stats": {"secs": 900, "total": 30}},
    "um": {"uid": "um", "username": "md5n", "name": "md5n"},
    "ub": {"uid": "ub", "username": "boo", "name": "BOO", "stats": {"secs": 5000, "total": 3}},
}
REMOTE = {"Cf-Connecting-Ip": "1.2.3.4"}


class BadgeTests(unittest.TestCase):
    def setUp(self):
        badges.init(tempfile.mkdtemp())
        badges._state["top"] = (0.0, None)
        p = patch.object(server, "PROFILES", json.loads(json.dumps(PROFILES)))
        p.start(); self.addCleanup(p.stop)
        badges.seed(server.PROFILES, server.norm_username)
        self.c = server.app.test_client()

    def post(self, body, uid="uh", headers=None):
        body = dict(body, uid=uid, token=server.auth_token(uid))
        return self.c.post("/api/badges/admin", json=body, headers=headers or {})

    def test_seed_and_top_listener(self):
        a = self.c.get("/api/badges").get_json()["assign"]
        self.assertEqual(a["uh"], ["founder"])
        self.assertEqual(a["um"], ["first_user"])
        self.assertEqual(a["ub"], ["top_listener"])       # أكثر وقت استماع
        prof = {p["uid"]: p for p in self.c.get("/api/profiles").get_json()["profiles"]}
        self.assertEqual(prof["uh"]["badges"], ["founder"])

    def test_only_founder_from_his_pc(self):
        self.assertTrue(self.c.get("/api/badges/admin?uid=uh&token=" + server.auth_token("uh")).get_json()["admin"])
        self.assertFalse(self.c.get("/api/badges/admin?uid=um&token=" + server.auth_token("um")).get_json()["admin"])
        self.assertFalse(self.c.get("/api/badges/admin?uid=uh&token=wrong").get_json()["admin"])
        self.assertEqual(self.post({"action": "grant", "target": "ub", "badge": "vip"}, headers=REMOTE).status_code, 403)
        self.assertEqual(self.post({"action": "grant", "target": "ub", "badge": "vip"}, uid="um").status_code, 403)

    def test_grant_revoke_create_delete(self):
        j = self.post({"action": "grant", "target": "ub", "badge": "vip"}).get_json()
        self.assertIn("vip", j["assign"]["ub"])
        self.assertEqual(self.post({"action": "grant", "target": "ub", "badge": "top_listener"}).status_code, 400)
        self.assertEqual(self.post({"action": "revoke", "target": "uh", "badge": "founder"}).get_json()["error"], "keep_founder")
        j = self.post({"action": "create", "emoji": "🎤", "ar": "مغني"}).get_json()
        key = j["badge"]
        self.assertIn(key, j["defs"])
        self.post({"action": "grant", "target": "um", "badge": key})
        j = self.post({"action": "delete", "badge": key}).get_json()
        self.assertNotIn(key, j["defs"])
        self.assertEqual(j["assign"]["um"], ["first_user"])
        self.assertEqual(self.post({"action": "delete", "badge": "founder"}).status_code, 400)
        self.assertNotIn("vip", self.post({"action": "revoke", "target": "ub", "badge": "vip"}).get_json()["assign"].get("ub", []))


if __name__ == "__main__":
    unittest.main()
