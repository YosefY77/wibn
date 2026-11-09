import io
import os
import tempfile
import unittest
from unittest import mock

os.environ.setdefault("HARMONI_DATA_DIR", tempfile.mkdtemp())
import server
import youtube_link

GOOD = ("# Netscape HTTP Cookie File\n"
        ".youtube.com\tTRUE\t/\tTRUE\t1999999999\t__Secure-3PSID\tabc\n"
        "#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t1999999999\tLOGIN_INFO\txyz\n"
        ".youtube.com\tTRUE\t/\tFALSE\t1999999999\tPREF\t\n"
        ".google.com\tTRUE\t/\tTRUE\t1999999999\tSID\tgoogle-secret\n"
        ".facebook.com\tTRUE\t/\tTRUE\t1999999999\tc_user\tsecret\n")
AGE = RuntimeError("ERROR: Sign in to confirm your age. This video may be inappropriate for some users.")


class YoutubeLinkTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        youtube_link.init(self.tmp)
        youtube_link.unlink()
        self.c = server.app.test_client()
        server._AGE_URLS.clear()
        del youtube_link._uses[:]

    def test_clean_keeps_only_youtube(self):
        text, n = youtube_link.clean(GOOD)
        self.assertEqual(n, 3)
        self.assertNotIn("facebook", text)
        self.assertNotIn("google-secret", text)
        self.assertIn("PREF\t\n", text)

    def test_rejects_signed_out_and_foreign(self):
        with self.assertRaises(ValueError) as e:
            youtube_link.clean(".youtube.com\tTRUE\t/\tFALSE\t1999999999\tPREF\tx\n")
        self.assertEqual(str(e.exception), "not_signed_in")
        with self.assertRaises(ValueError) as e:
            youtube_link.clean(".facebook.com\tTRUE\t/\tTRUE\t1\tc_user\tx\n")
        self.assertEqual(str(e.exception), "no_youtube")

    def test_store_is_not_plain_and_temp_is_removed(self):
        youtube_link.save(GOOD)
        raw = open(os.path.join(self.tmp, "youtube_link.bin"), "rb").read()
        self.assertNotIn(b"abc", raw)
        youtube_link.set_requester(True)
        with youtube_link.cookie_file() as path:
            with open(path) as fh:
                self.assertIn("__Secure-3PSID\tabc", fh.read())
        self.assertFalse(os.path.exists(path))
        self.assertEqual(youtube_link.status()["uses"], 1)

    def test_legacy_plain_file_migrated(self):
        open(os.path.join(self.tmp, "youtube_cookies.txt"), "w").write(GOOD)
        youtube_link.init(self.tmp)
        self.assertTrue(youtube_link.linked())
        self.assertFalse(os.path.exists(os.path.join(self.tmp, "youtube_cookies.txt")))

    def test_only_owner_unless_shared(self):
        youtube_link.save(GOOD)
        youtube_link.set_requester(False)
        self.assertFalse(youtube_link.allowed())
        youtube_link.set_share(True)
        self.assertTrue(youtube_link.allowed())

    def test_hourly_limit(self):
        youtube_link.save(GOOD)
        youtube_link.set_requester(True)
        for _ in range(youtube_link.HOURLY_LIMIT):
            with youtube_link.cookie_file():
                pass
        with self.assertRaises(PermissionError):
            with youtube_link.cookie_file():
                pass

    def test_upload_status_share_unlink(self):
        r = self.c.post("/api/youtube_link", data={"file": (io.BytesIO(GOOD.encode()), "cookies.txt")},
                        content_type="multipart/form-data")
        self.assertEqual(r.status_code, 200, r.get_json())
        self.assertTrue(r.get_json()["linked"])
        st = self.c.get("/api/youtube_link")
        self.assertNotIn("abc", st.get_data(as_text=True))
        self.assertTrue(self.c.post("/api/youtube_link", json={"share_friends": True}).get_json()["share_friends"])
        self.assertFalse(self.c.delete("/api/youtube_link").get_json()["linked"])

    def test_remote_cannot_touch(self):
        self.assertEqual(self.c.get("/api/youtube_link", headers={"Cf-Connecting-Ip": "1.2.3.4"}).status_code, 403)
        r = self.c.post("/api/youtube_link", json={"text": GOOD}, headers={"X-Forwarded-For": "1.2.3.4"})
        self.assertEqual(r.status_code, 403)

    def test_cookies_used_only_for_age_errors_and_owner(self):
        youtube_link.save(GOOD)
        youtube_link.set_requester(True)
        with mock.patch.object(server, "_extract_base", side_effect=AGE), \
             mock.patch.object(server, "_extract_age", return_value={"url": "https://x.googlevideo.com/a"}) as ex, \
             mock.patch.object(server.Room, "_sc_stream", return_value=None):
            self.assertEqual(server.resolve_stream_url("https://youtube.com/watch?v=a"), "https://x.googlevideo.com/a")
            ex.assert_called_once()
        with mock.patch.object(server, "_extract_base", side_effect=RuntimeError("Video unavailable")), \
             mock.patch.object(server, "_extract_age") as ex, \
             mock.patch.object(server.Room, "_sc_stream", return_value=None):
            self.assertIsNone(server.resolve_stream_url("https://youtube.com/watch?v=b"))
            ex.assert_not_called()
        youtube_link.set_requester(False)   # صديق عبر النفق
        with mock.patch.object(server, "_extract_base", side_effect=AGE), \
             mock.patch.object(server, "_extract_age") as ex, \
             mock.patch.object(server.Room, "_sc_stream", return_value=None):
            self.assertIsNone(server.resolve_stream_url("https://youtube.com/watch?v=c"))
            ex.assert_not_called()

    def test_age_hint_when_not_linked(self):
        with mock.patch.object(server, "_extract_base", side_effect=AGE), \
             mock.patch.object(server.Room, "_sc_stream", return_value=None), \
             mock.patch.object(server, "_alternate_stream", return_value=(None, None)):
            r = self.c.post("/api/resolve_stream", json={"web_url": "https://youtube.com/watch?v=d", "title": "x", "refresh": True})
        self.assertEqual(r.status_code, 502)
        self.assertEqual(r.get_json()["hint"], "age")

    def test_browser_cookies_to_netscape(self):
        ck = [{"name": "LOGIN_INFO", "value": "a", "domain": ".youtube.com", "path": "/", "secure": True, "httpOnly": True, "expires": 1999999999.5},
              {"name": "SAPISID", "value": "b", "domain": ".youtube.com", "path": "/", "secure": True, "expires": -1},
              {"name": "SID", "value": "g", "domain": ".google.com", "path": "/", "secure": True, "expires": 1999999999}]
        self.assertTrue(youtube_link._logged_in(ck))
        self.assertFalse(youtube_link._logged_in(ck[1:]))
        text = youtube_link._netscape(ck)
        self.assertIn("#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t1999999999\tLOGIN_INFO\ta", text)
        self.assertIn(".youtube.com\tTRUE\t/\tTRUE\t0\tSAPISID\tb", text)
        self.assertNotIn("google.com", text)
        self.assertEqual(youtube_link.clean(text)[1], 2)

    def test_login_endpoint_local_only(self):
        r = self.c.post("/api/youtube_link/login", headers={"Cf-Connecting-Ip": "1.2.3.4"})
        self.assertEqual(r.status_code, 403)
        with mock.patch.object(youtube_link, "find_browser", return_value=None):
            r = self.c.post("/api/youtube_link/login")
        self.assertEqual(r.get_json()["error"], "no_browser")


if __name__ == "__main__":
    unittest.main()
