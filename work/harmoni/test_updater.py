import json
import os
import tempfile
import unittest
from unittest.mock import patch
os.environ.setdefault("HARMONI_DATA_DIR", tempfile.mkdtemp())
os.environ.setdefault("HARMONI_REGION", "none")
import server
import harmoni_updater as up


class FakeResp:
    def __init__(self, data, status=200):
        self._d, self.status_code = data, status

    def json(self):
        return self._d

    def raise_for_status(self):
        if self.status_code >= 400:
            raise RuntimeError(self.status_code)


RELEASE = {"tag_name": "v2026.10.13.900", "body": "- ميزة جديدة",
           "assets": [{"name": "Harmoni-Setup.exe",
                       "browser_download_url": "https://github.com/me/harmoni-app/releases/download/v2026.10.13.900/Harmoni-Setup.exe"}]}


class UpdaterTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        up._CFG["bundle_dir"] = self.dir
        up._CACHE.update(at=0, data=None)
        self.write("2026.10.12.1530", "me/harmoni-app")

    def write(self, v, repo):
        with open(os.path.join(self.dir, "version.json"), "w", encoding="utf-8") as f:
            json.dump({"version": v, "repo": repo}, f)

    def test_version_compare(self):
        self.assertGreater(up.vtuple("v2026.10.13.900"), up.vtuple("2026.10.12.1530"))
        self.assertGreater(up.vtuple("2026.10.12.1531"), up.vtuple("2026.10.12.1530"))
        self.assertGreater(up.vtuple("2026.11.1.5"), up.vtuple("2026.10.31.2359"))

    def test_newer_release_is_available(self):
        with patch.object(up.requests, "get", return_value=FakeResp(RELEASE)):
            out = up.check()
        self.assertTrue(out["available"])
        self.assertEqual(out["latest"], "2026.10.13.900")
        self.assertIn("ميزة", out["notes"])

    def test_same_or_older_is_not_available(self):
        self.write("2026.10.13.900", "me/harmoni-app")
        with patch.object(up.requests, "get", return_value=FakeResp(RELEASE)):
            self.assertFalse(up.check()["available"])

    def test_no_repo_or_offline(self):
        self.write("1", "")
        self.assertEqual(up.check()["reason"], "no_repo")
        self.write("1", "me/harmoni-app")
        with patch.object(up.requests, "get", side_effect=OSError("offline")):
            self.assertEqual(up.check()["reason"], "offline")

    def test_only_this_repos_installer_is_accepted(self):
        self.assertTrue(up._allowed_url(RELEASE["assets"][0]["browser_download_url"], "me/harmoni-app"))
        self.assertFalse(up._allowed_url("https://github.com/evil/x/releases/download/v1/Harmoni-Setup.exe", "me/harmoni-app"))
        self.assertFalse(up._allowed_url("https://example.com/Harmoni-Setup.exe", "me/harmoni-app"))

    def test_install_is_local_only(self):
        c = server.app.test_client()
        r = c.post("/api/update/install", headers={"X-Forwarded-For": "1.2.3.4"})
        self.assertEqual(r.status_code, 403)

    def test_check_endpoint(self):
        with patch.object(up.requests, "get", return_value=FakeResp(RELEASE)):
            j = server.app.test_client().get("/api/update/check").get_json()
        self.assertTrue(j["available"])
        self.assertFalse(j["can_install"])   # مو نسخة مثبتة (من الكود)

    def test_failed_update_is_reported(self):
        d = tempfile.mkdtemp()
        with patch.object(up, "_pending_path", lambda: os.path.join(d, "p.json")), \
                patch.object(up, "local_info", lambda: {"version": "2026.10.12.1530", "repo": "me/harmoni-app"}), \
                patch.object(up, "_latest", lambda repo, force=False: {"version": "2026.10.13.900", "url": "https://x/a.exe"}):
            with open(os.path.join(d, "p.json"), "w") as f:
                json.dump({"target": "2026.10.13.900", "ts": __import__("time").time()}, f)
            out = up.check()
            self.assertEqual(out["failed_update"], "2026.10.13.900")
            self.assertIn("releases/latest", out["releases"])
            with open(os.path.join(d, "p.json"), "w") as f:
                json.dump({"target": "2026.10.12.1530", "ts": __import__("time").time()}, f)
            self.assertNotIn("failed_update", up.check())          # انطبق → ينمسح
            self.assertFalse(os.path.exists(os.path.join(d, "p.json")))


if __name__ == "__main__":
    unittest.main()
