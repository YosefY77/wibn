"""ملف الحسابات: نسخة ثانية من البرنامج ما تمسح حسابات غيرها + استرجاع الحسابات من profiles.restore.json."""
import json, os, sys, tempfile, time, unittest
from unittest.mock import patch
os.environ.setdefault("HARMONI_DATA_DIR", tempfile.mkdtemp())
os.environ["MUSIC_HEADLESS"] = "1"
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import server


class ProfilesMergeTests(unittest.TestCase):
    def setUp(self):
        self.d = tempfile.mkdtemp()
        self.f = os.path.join(self.d, "profiles.json")
        for name, val in (("_PROFILES_FILE", self.f), ("BASE_DIR", self.d)):
            p = patch.object(server, name, val)
            p.start()
            self.addCleanup(p.stop)
        server._PROFILES_DELETED.clear()

    def write(self, data, path=None):
        with open(path or self.f, "w", encoding="utf-8") as fh:
            json.dump(data, fh)

    def test_stale_copy_does_not_wipe_accounts_written_by_others(self):
        self.write({"me": {"uid": "me", "username": "hiro"}})
        server._load_profiles()
        time.sleep(0.05)
        self.write({"me": {"uid": "me", "username": "hiro"}, "new": {"uid": "new", "username": "friend"}})   # نسخة ثانية أضافت حساب
        server.PROFILES["me"]["name"] = "Hiro"
        server._save_profiles()
        disk = json.load(open(self.f, encoding="utf-8"))
        self.assertIn("new", disk)
        self.assertEqual(disk["me"]["name"], "Hiro")

    def test_restore_file_brings_back_accounts_and_following(self):
        self.write({"me": {"uid": "me", "username": "hiro", "following": ["b"]}, "b": {"uid": "b", "username": "boo"}})
        self.write({"me": {"uid": "me", "username": "hiro", "following": ["b", "x", "gone"]},
                    "x": {"uid": "x", "username": "unqni"},
                    "dup": {"uid": "dup", "username": "boo"}},
                   os.path.join(self.d, "profiles.restore.json"))
        server._load_profiles()
        self.assertIn("x", server.PROFILES)
        self.assertNotIn("dup", server.PROFILES)          # يوزر محجوز
        self.assertEqual(server.PROFILES["me"]["following"], ["b", "x"])
        self.assertFalse(os.path.exists(os.path.join(self.d, "profiles.restore.json")))
        self.assertIn("x", json.load(open(self.f, encoding="utf-8")))


if __name__ == "__main__":
    unittest.main()
