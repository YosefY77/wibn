import os
import tempfile
import unittest
from unittest.mock import patch
os.environ.setdefault("HARMONI_DATA_DIR", tempfile.mkdtemp())
os.environ.setdefault("HARMONI_REGION", "none")
import server


def T(title, url="https://www.youtube.com/watch?v=abc"):
    return {"title": title, "artist": "A", "web_url": url, "duration": 200}


class RoomTests(unittest.TestCase):
    def setUp(self):
        self.p = [patch.object(server.Room, "_ensure_resolving", lambda self, *a, **k: None),
                  patch.object(server.Room, "broadcast", lambda self: None),
                  patch.object(server, "leave_room", lambda *a, **k: None),
                  patch.object(server, "register_profile", lambda *a, **k: None)]
        for p in self.p:
            p.start()

    def tearDown(self):
        for p in self.p:
            p.stop()
        server.ROOMS.clear()
        server.SID_ROOM.clear()

    def make(self, owner="s1", uid="u1", auto=False):
        r = server.Room("123456", "test")
        r.create_room(owner, {"uid": uid, "name": "Owner", "auto": auto})
        r.add_member(owner, {"uid": uid, "name": "Owner"})
        return r

    def test_personal_room_is_private(self):
        r = self.make(auto=True)
        self.assertTrue(r.state()["personal"])
        self.assertEqual(r.can_join("x", {"uid": "other"}), "not_found")
        self.assertIsNone(r.can_join("x", {"uid": "u1"}))   # صاحبها يرجع لها عادي

    def test_take_over_solo_track_keeps_position(self):
        r = server.Room("111111")
        r.take_over(dict(T("Song"), position=42.5, playing=True, stream_url="http://evil"), None)
        self.assertEqual(r.current["title"], "Song")
        self.assertNotIn("stream_url", r.current)
        self.assertAlmostEqual(r.position, 42.5)
        self.assertTrue(r.playing)

    def test_take_over_copies_previous_queue(self):
        src = self.make(auto=True)
        src.add_track(T("one"))
        src.add_track(T("two"))
        r = server.Room("222222")
        r.take_over(None, src)
        self.assertEqual(r.current["title"], "one")
        self.assertEqual([t["title"] for t in r.queue], ["two"])

    def test_settings_owner_only(self):
        r = self.make()
        self.assertFalse(r.update_settings("s2", {"name": "x"}))
        self.assertTrue(r.update_settings("s1", {"name": "  Chill ", "password": "pw", "can_add": False, "max_members": 1}))
        st = r.state()
        self.assertEqual((st["name"], st["has_password"], st["can_add"], st["max_members"]), ("Chill", True, False, 2))
        self.assertNotIn("password", st)
        r.update_settings("s1", {"password": ""})
        self.assertFalse(r.state()["has_password"])

    def test_kick_bans_and_cannot_kick_self(self):
        r = self.make()
        r.add_member("s2", {"uid": "u2", "name": "Guest"})
        self.assertFalse(r.kick("s2", "s1"))
        self.assertFalse(r.kick("s1", "s1"))
        self.assertTrue(r.kick("s1", "s2"))
        self.assertNotIn("s2", r.members)
        self.assertEqual(r.can_join("s9", {"uid": "u2"}), "kicked")

    def test_play_track_plays_for_everyone_now(self):
        r = self.make()
        r.add_track(T("first"))
        r.add_track(T("queued"))
        r.play_track(dict(T("now"), stream_url="http://evil"))
        self.assertEqual(r.current["title"], "now")
        self.assertNotIn("stream_url", r.current)
        self.assertEqual([t["title"] for t in r.queue], ["queued"])
        self.assertEqual(r.history[-1]["title"], "first")

    def test_add_track_ignores_client_stream_url(self):
        r = self.make()
        r.add_track(dict(T("x"), stream_url="http://evil"))
        self.assertNotIn("stream_url", r.current)

    def test_owner_leaving_hands_over_ownership(self):
        r = self.make()
        r.add_member("s2", {"uid": "u2", "name": "Guest"})
        server.ROOMS[r.rid] = r
        server.SID_ROOM["s1"] = r.rid
        server.SID_ROOM["s2"] = r.rid
        server._leave_current("s1")
        self.assertEqual(r.owner_sid, "s2")
        self.assertEqual(r.owner_uid, "u2")
        server._leave_current("s2")
        self.assertNotIn(r.rid, server.ROOMS)

    def test_double_skip_only_skips_once(self):
        r = self.make()
        for n in ("a", "b", "c"):
            r.add_track(T(n))
        cur = r.current["id"]
        r.skip(cur)
        r.skip(cur)          # صاحبك ضغط تخطي بنفس اللحظة
        self.assertEqual(r.current["title"], "b")

    def test_early_ended_does_not_remove_song(self):
        r = self.make()
        r.add_track(T("a"))
        r.add_track(T("b"))
        r.ended(r.current["id"])                     # انقطع الصوت بعد ثواني
        self.assertEqual(r.current["title"], "a")
        r.position, r.updated_at = 195.0, server.time.time()
        r.ended(r.current["id"])
        self.assertEqual(r.current["title"], "b")

    def test_play_now_does_not_requeue_current(self):
        r = self.make()
        for n in ("a", "b", "c"):
            r.add_track(T(n))
        r.play_now(r.queue[1]["id"])
        self.assertEqual(r.current["title"], "c")
        self.assertEqual([t["title"] for t in r.queue], ["b"])

    def test_stream_failed_refreshes_at_most_twice(self):
        r = self.make()
        r.add_track(T("a"))
        tid = r.current["id"]
        self.assertTrue(r.stream_failed(tid))
        self.assertFalse(r.stream_failed(tid))       # أكثر من مرة خلال ثواني
        r.current["_refresh_at"] = 0
        self.assertTrue(r.stream_failed(tid))
        r.current["_refresh_at"] = 0
        self.assertFalse(r.stream_failed(tid))
        self.assertEqual(r.current["title"], "a")    # ما انحذفت

    def test_resolve_failure_moves_on_success_restarts_clock(self):
        r = self.make()
        r.add_track(T("a"))
        r.add_track(T("b"))
        a = r.current
        r.updated_at -= 20
        with patch.object(server.Room, "_resolve_url", lambda self, t, refresh=False: ("https://x.googlevideo.com/a", "youtube")):
            server.Room._bg_resolve(r, a)
        self.assertEqual(a["stream_url"], "https://x.googlevideo.com/a")
        self.assertLess(server.time.time() - r.updated_at, 2)
        b = r.queue[0]
        r.current, r.queue = b, []
        with patch.object(server.Room, "_resolve_url", lambda self, t, refresh=False: (None, "")), \
             patch.object(server.socketio, "emit", lambda *x, **k: None):
            server.Room._bg_resolve(r, b)
        self.assertTrue(b.get("stream_failed"))
        self.assertIsNone(r.current)


    def test_remove_button_rules(self):
        r = self.make()
        r.add_member("s2", {"uid": "u2", "name": "Guest"})
        for n in ("a", "b", "c"):
            r.add_track(T(n))
        server.ROOMS[r.rid] = r
        server.SID_ROOM["s1"] = r.rid
        server.SID_ROOM["s2"] = r.rid
        errors = []
        def call(sid, tid):
            with patch.object(server, "request", type("R", (), {"sid": sid})), \
                 patch.object(server, "emit", lambda ev, d=None, **k: errors.append(ev)):
                server.on_remove({"id": tid})
        call("s2", r.queue[0]["id"])                 # عضو يحذف أغنية من القائمة
        self.assertEqual([t["title"] for t in r.queue], ["c"])
        call("s1", r.current["id"])                  # حذف الشغالة = تنتقل للي بعدها
        self.assertEqual(r.current["title"], "c")
        r.can_add = False
        r.add_track(T("d"))
        call("s2", r.queue[0]["id"])                 # المالك قفل الإضافة ⇒ العضو ما يحذف
        self.assertEqual([t["title"] for t in r.queue], ["d"])
        self.assertIn("add_error", errors)


if __name__ == "__main__":
    unittest.main()
