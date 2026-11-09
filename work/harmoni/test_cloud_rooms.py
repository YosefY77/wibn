"""غرف الاستماع المشتركة: hub/worker.js الحقيقي (RoomCore) على node + عميل WebSocket حق هارموني.
جهازين (أو أكثر) يسوون غرفة ويدخلونها بدون ما يكون جهاز صاحب البرنامج شغال."""
import json, os, socket, subprocess, sys, tempfile, time, unittest, urllib.request
os.environ.setdefault("HARMONI_DATA_DIR", tempfile.mkdtemp())
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import cloud_rooms
import harmoni_ws
from test_hub import NODE, _free_port


class Bus:
    def __init__(self):
        self.ev = {}
        self.fail = set()

    def emit(self, event, data, sid):
        self.ev.setdefault(sid, []).append((event, json.loads(json.dumps(data))))

    def resolve(self, tr, refresh):
        if tr.get("web_url") in self.fail:
            return None, ""
        return "https://audio.example/" + (tr.get("web_url") or tr.get("title")), "youtube"

    def last(self, sid, event="state"):
        for e, d in reversed(self.ev.get(sid, [])):
            if e == event:
                return d
        return None

    def wait(self, sid, cond, event="state", timeout=6):
        end = time.time() + timeout
        while time.time() < end:
            for e, d in reversed(self.ev.get(sid, [])):
                if e == event and cond(d):
                    return d
            time.sleep(0.03)
        raise AssertionError("timeout waiting for %s on %s; got %s" % (event, sid, [e for e, _ in self.ev.get(sid, [])][-8:]))


def tr(n, dur=200):
    return {"title": "Song %d" % n, "artist": "A", "web_url": "https://youtu.be/v%d" % n, "duration": dur, "stream_url": "https://evil/"}


@unittest.skipUnless(NODE, "node 22.5+ (node:sqlite) not available")
class CloudRoomTests(unittest.TestCase):
    def setUp(self, grace=(300, 0.2)):
        self.port = _free_port()
        self.proc = subprocess.Popen([NODE, "--no-warnings", os.path.join(HERE, "hub", "dev-server.mjs"), str(self.port)],
                                     stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                     env=dict(os.environ, HUB_EMPTY_MS=str(grace[0]), HUB_EMPTY_S=str(grace[1])))
        self.assertIn("hub ready", self.proc.stdout.readline().decode())
        self.url = "http://127.0.0.1:%d" % self.port
        self.bus = Bus()
        cloud_rooms.init(self.bus.emit, self.bus.resolve, lambda t: {k: v for k, v in t.items() if k != "stream_url"},
                         lambda: "ws://127.0.0.1:%d" % self.port)

    def tearDown(self):
        for sid in list(cloud_rooms.LINKS):
            cloud_rooms.drop(sid)
        self.proc.kill()
        self.proc.wait()
        self.proc.stdout.close()

    def get(self, path):
        with urllib.request.urlopen(self.url + path, timeout=5) as r:
            return json.loads(r.read())

    def new_code(self):
        req = urllib.request.Request(self.url + "/rooms/new", data=b"{}", method="POST", headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(req, timeout=5) as r:
            return json.loads(r.read())["id"]

    def test_two_pcs_share_a_room(self):
        b = self.bus
        code = self.new_code()
        self.assertRegex(code, r"^\d{6}$")
        joined = []
        avatar = "data:image/jpeg;base64," + "Q" * 250000   # صورة كبيرة (كانت تطيّح الغرفة: حد كلاودفلير 2KB للـ attachment)
        a = cloud_rooms.connect("sidA", code, {"uid": "", "name": "Ahmed", "avatar": avatar}, create={
            "name": "سهرة", "can_add": True, "carry": {"current": tr(1), "position": 30, "playing": True, "queue": [tr(2)]}},
            on_joined=joined.append)
        st = b.wait("sidA", lambda s: s["current"] and s["current"].get("stream_url"))
        self.assertEqual(joined, ["sidA"])
        self.assertEqual(st["owner_sid"], "sidA")              # المالك يشوف نفسه بالـ sid المحلي حقه
        self.assertEqual(st["members"][0]["id"], "sidA")
        self.assertEqual(st["members"][0]["avatar"], avatar)
        self.assertEqual(st["current"]["title"], "Song 1")
        self.assertEqual(st["current"]["stream_url"], "https://audio.example/https://youtu.be/v1")   # الصوت من جهازه هو
        self.assertNotIn("evil", json.dumps(st))
        self.assertGreater(st["position"], 29)
        self.assertLess(abs(st["updated_at"] - time.time()), 3)   # بوقت الجهاز المحلي
        self.assertEqual([x["title"] for x in st["queue"]], ["Song 2"])
        self.assertTrue(a.joined)
        rooms = self.get("/rooms")["rooms"]
        self.assertEqual([(r["id"], r["name"], r["members"]) for r in rooms], [(code, "سهرة", 1)])

        # كلمة سر من المالك
        a.update_settings("sidA", {"password": "1234", "max_members": 2})
        b.wait("sidA", lambda s: s["has_password"])
        self.assertFalse(a.update_settings("someone", {"name": "x"}))
        cloud_rooms.connect("sidB", code, {"name": "Badr"})
        self.assertEqual(b.wait("sidB", lambda d: True, "join_error")["reason"], "need_password")
        cloud_rooms.connect("sidB", code, {"name": "Badr"}, password="nope")
        b.wait("sidB", lambda d: d["reason"] == "wrong_password", "join_error")
        bl = cloud_rooms.connect("sidB", code, {"name": "Badr"}, password="1234")
        sb = b.wait("sidB", lambda s: len(s["members"]) == 2)
        self.assertNotEqual(sb["owner_sid"], "sidB")
        b.wait("sidA", lambda s: len(s["members"]) == 2)
        cloud_rooms.connect("sidC", code, {"name": "Full"}, password="1234")
        b.wait("sidC", lambda d: d["reason"] == "full", "join_error")

        # صاحبه يضيف أغنية ويتخطى → الكل يشوف نفس الشي
        self.assertTrue(bl.add_track(tr(3))["pending"])
        b.wait("sidB", lambda d: True, "add_ok")
        b.wait("sidA", lambda s: [x["title"] for x in s["queue"]] == ["Song 2", "Song 3"])
        bl.skip(sb["current"]["id"])
        sa = b.wait("sidA", lambda s: s["current"]["title"] == "Song 2" and s["current"].get("stream_url"))
        b.wait("sidB", lambda s: s["current"]["title"] == "Song 2")
        self.assertLess(sa["position"], 1)
        a.play_pause()
        b.wait("sidB", lambda s: not s["playing"])
        a.seek(50)
        b.wait("sidB", lambda s: abs(s["position"] - 50) < 0.01)
        a.previous()
        b.wait("sidB", lambda s: s["current"]["title"] == "Song 1")
        # «انتهت» قبل نهايتها ما يتخطى على الكل
        a.ended(b.last("sidA")["current"]["id"])
        time.sleep(0.3)
        self.assertEqual(b.last("sidB")["current"]["title"], "Song 1")

        # الطرد
        self.assertTrue(a.kick("sidA", b.last("sidA")["members"][1]["id"]))
        b.wait("sidB", lambda d: True, "kicked")
        b.wait("sidA", lambda s: len(s["members"]) == 1)
        self.assertIsNone(cloud_rooms.get("sidB"))

        # المالك يطلع والغرفة تفضى → تختفي من القائمة
        cloud_rooms.drop("sidA")
        end = time.time() + 5
        while time.time() < end and self.get("/rooms")["rooms"]:
            time.sleep(0.05)
        self.assertEqual(self.get("/rooms")["rooms"], [])
        time.sleep(0.8)   # الغرفة الفاضية تنتظر شوي (لو أحد انقطع عنه النت) وبعدين تنحذف
        cloud_rooms.connect("sidD", code, {"name": "Late"})
        b.wait("sidD", lambda d: d["reason"] == "not_found", "join_error")

    def test_empty_room_survives_a_short_disconnect(self):
        self.tearDown()
        self.setUp(grace=(6000, 5))
        b = self.bus
        code = self.new_code()
        a = cloud_rooms.connect("a1", code, {"name": "A"}, create={"name": "r", "carry": {"current": tr(1)}})
        b.wait("a1", lambda s: s["current"])
        a.ws.close()          # النت انقطع: يرجع يتصل لحاله لنفس الغرفة
        b.wait("a1", lambda s: s["current"] and s["owner_sid"] == "a1" and len(b.ev["a1"]) > 2)
        self.assertTrue(cloud_rooms.get("a1").joined)
        # انقطاع صامت (السيرفر ما درى): الاتصال الجديد بنفس client يشيل القديم — ما يطلع العضو مرتين
        u = "ws://127.0.0.1:%d/room/%s/ws" % (self.port, code)
        hello = json.dumps({"t": "hello", "member": {"name": "Z"}, "client": "same-pc"})
        w1 = harmoni_ws.WebSocket(u); w1.send(hello)
        self.assertEqual(len(json.loads(w1.recv())["state"]["members"]), 2)
        w2 = harmoni_ws.WebSocket(u); w2.send(hello)
        st = json.loads(w2.recv())
        self.assertEqual(sorted(m["name"] for m in st["state"]["members"]), ["A", "Z"])
        w1.close(); w2.close()

    def test_owner_leaves_and_failed_tracks(self):
        b = self.bus
        code = self.new_code()
        cloud_rooms.connect("o", code, {"name": "Owner"}, create={"name": "r"})
        b.wait("o", lambda s: s["owner_sid"] == "o")
        m = cloud_rooms.connect("m", code, {"name": "Member"})
        b.wait("m", lambda s: len(s["members"]) == 2)
        cloud_rooms.drop("o")
        b.wait("m", lambda s: s["owner_sid"] == "m" and len(s["members"]) == 1)
        # أغنية ما تشتغل عند أحد → تنتقل للي بعدها للكل
        b.fail.add("https://youtu.be/v9")
        m.add_track(tr(9))
        m.add_track(tr(4))
        b.wait("m", lambda d: True, "track_failed")
        st = b.wait("m", lambda s: s["current"] and s["current"]["title"] == "Song 4" and s["current"].get("stream_url"))
        self.assertEqual(st["queue"], [])
        self.assertEqual(self.get("/rooms")["rooms"][0]["owner"], "Member")

    def test_unknown_room_and_ping(self):
        cloud_rooms.connect("x", "123456", {"name": "x"})
        self.bus.wait("x", lambda d: d["reason"] == "not_found", "join_error")
        with urllib.request.urlopen(self.url + "/r/654321", timeout=5) as r:
            page = r.read().decode()
        self.assertIn("654321", page)
        self.assertIn("music.harmoni.click/?room=654321", page)
        ws = harmoni_ws.WebSocket("ws://127.0.0.1:%d/room/111111/ws" % self.port)
        ws.send("ping")
        self.assertEqual(ws.recv(), "pong")
        big = "x" * 70000
        ws.send(json.dumps({"t": "hello", "member": {"name": big}, "create": {"name": "big"}}))
        st = json.loads(ws.recv())
        self.assertEqual(st["t"], "state")
        self.assertEqual(st["state"]["members"][0]["name"], "x" * 300)
        ws.close()

    def test_server_socket_handlers_use_cloud_rooms(self):
        import server, hub
        b = self.bus
        hub.init(tempfile.mkdtemp())
        hub._state["url"] = self.url
        old = server._CLOUD_ROOMS_ENABLED
        server._CLOUD_ROOMS_ENABLED = True
        try:
            app = server.app
            # عنده غرفة شخصية محلية (تشتغل له الأغنية) → يبدأ غرفة مشتركة تكمل نفس الأغنية
            # (نسويها مباشرة: join_room الحقيقي حق flask-socketio يحتاج اتصال socket.io فعلي)
            personal = server.Room("987654", "personal", hidden=True)
            personal.current = {"id": "t1", "title": "Song 1", "web_url": "https://youtu.be/v1", "duration": 200}
            personal.playing = True
            with server.ROOMS_LOCK:
                server.ROOMS[personal.rid] = personal
                server.SID_ROOM["pc1"] = personal.rid
            personal.members["pc1"] = {"id": "pc1", "uid": "", "name": "Me"}
            self.assertIs(server.current_room("pc1"), personal)
            with app.test_request_context("/"):
                server.request.sid = "pc1"
                server.request.namespace = "/"
                server.on_create_room({"name": "Me", "room_name": "Shared", "carry": True})
            st = b.wait("pc1", lambda s: s.get("cloud") and s["current"] and s["current"].get("stream_url"))
            link = server.current_room("pc1")
            self.assertIsInstance(link, cloud_rooms.CloudLink)
            self.assertEqual(st["current"]["title"], "Song 1")
            end = time.time() + 3
            while time.time() < end and server.SID_ROOM.get("pc1"):
                time.sleep(0.03)
            self.assertIsNone(server.SID_ROOM.get("pc1"))          # طلع من الغرفة الشخصية
            code = st["rid"]
            listed = self.c_rooms(server)
            self.assertIn(code, [r["id"] for r in listed])
            # جهاز ثاني يدخل بالكود (الغرفة مو موجودة عنده محلياً)
            with app.test_request_context("/"):
                server.request.sid = "pc2"
                server.request.namespace = "/"
                server.on_join({"room": code, "name": "Friend"})
            b.wait("pc2", lambda s: len(s["members"]) == 2)
            self.assertIs(server._room_of_uid(""), None)
            with app.test_request_context("/"):
                server.request.sid = "pc2"
                server.request.namespace = "/"
                server.on_add({"track": tr(5)})
                server.on_disconnect()
            b.wait("pc1", lambda s: len(s["members"]) == 1 and [x["title"] for x in s["queue"]] == ["Song 5"])
        finally:
            server._CLOUD_ROOMS_ENABLED = old
            hub._state["url"] = ""

    def test_create_falls_back_to_local_room(self):
        import server
        fell = []
        link = cloud_rooms.connect("pcX", "222333", {"name": "x"}, create={"name": "r"}, on_failed=fell.append)
        self.proc.kill(); self.proc.wait()          # السيرفر المشترك طاح قبل ما يرد
        end = time.time() + 15
        while time.time() < end and not fell:
            time.sleep(0.05)
        self.assertEqual(fell, ["pcX"])
        self.assertIsNone(cloud_rooms.get("pcX"))
        server._create_local_room("pcX", {"name": "x", "room_name": "local"}, None, in_request=False)
        self.assertIsInstance(server.current_room("pcX"), server.Room)
        server._leave_local("pcX", in_request=False)

    def c_rooms(self, server):
        server._CLOUD_LIST["at"] = 0
        return server.app.test_client().get("/api/rooms").get_json()["rooms"]


if __name__ == "__main__":
    unittest.main()
