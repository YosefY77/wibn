"""السيرفر المشترك (hub): جهازين مختلفين يشوفون نفس الحسابات + الرجوع للوضع المحلي لو السيرفر طايح.
يشغّل hub/worker.js الحقيقي على node (يحتاج Node 22.5+ عشان node:sqlite) — لو مو موجود يتخطى الاختبار."""
import json, os, shutil, socket, subprocess, sys, tempfile, time, unittest
os.environ.setdefault("HARMONI_DATA_DIR", tempfile.mkdtemp())
os.environ["MUSIC_HEADLESS"] = "1"
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import server
import badges
import hub


def _free_port():
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    p = s.getsockname()[1]
    s.close()
    return p


def _node_ok():
    node = shutil.which("node")
    if not node:
        return None
    try:
        r = subprocess.run([node, "--no-warnings", "-e", "require('node:sqlite')"], capture_output=True, timeout=20)
        return node if r.returncode == 0 else None
    except Exception:
        return None


NODE = _node_ok()


def local_auth(pw):
    salt = os.urandom(16).hex()
    return {"salt": salt, "hash": server._hash_password(pw, salt)}


@unittest.skipUnless(NODE, "node 22.5+ (node:sqlite) not available")
class HubTests(unittest.TestCase):
    def setUp(self):
        cls = type(self)
        cls.port = _free_port()
        cls.proc = subprocess.Popen([NODE, "--no-warnings", os.path.join(HERE, "hub", "dev-server.mjs"), str(cls.port)],
                                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                    env=dict(os.environ, HUB_MEDIA_QUOTA=str(13 * 1024 * 1024)))   # مساحة صغيرة للاختبار
        line = cls.proc.stdout.readline().decode()
        assert "hub ready" in line, line
        cls.url = "http://127.0.0.1:%d" % cls.port
        cls.saved = {k: getattr(server, k) for k in ("BASE_DIR", "_PROFILES_FILE", "PROFILES")}
        cls.pcs = {}

    def tearDown(self):
        cls = type(self)
        cls.proc.kill()
        cls.proc.wait()
        for k, v in cls.saved.items():
            setattr(server, k, v)

    # ---- محاكاة أكثر من جهاز: كل جهاز له مجلد بيانات وسر ومستخدمين ----
    def pc(self, name, profiles=None, url=None):
        if name not in self.pcs:
            d = tempfile.mkdtemp()
            self.pcs[name] = {"dir": d, "profiles": profiles or {}, "secret": os.urandom(32).hex(),
                              "hubstate": {"pulled": 0.0, "ver": "", "badges": None, "badges_at": 0.0}}
        p = self.pcs[name]
        server.BASE_DIR = p["dir"]
        server._PROFILES_FILE = os.path.join(p["dir"], "profiles.json")
        server.PROFILES = p["profiles"]
        server._PROFILES_SEEN["mtime"] = server._profiles_mtime()
        server._SECRET[0] = p["secret"]
        server._HUB.update(p["hubstate"])
        p["hubstate"] = server._HUB   # نفس القاموس (التعديلات تنحفظ للجهاز)
        hub.init(p["dir"])
        hub._state["url"] = self.url if url is None else url
        badges.init(p["dir"])
        badges._state["top"] = (0.0, None)
        server._PROFILES_DELETED.clear()
        self.c = server.app.test_client()
        return p

    def leave(self, name):
        self.pcs[name]["hubstate"] = {k: server._HUB[k] for k in ("pulled", "ver", "badges", "badges_at")}

    def switch(self, name):
        for n in self.pcs:
            if self.pcs[n]["profiles"] is server.PROFILES and n != name:
                self.leave(n)
        return self.pc(name)

    def test_shared_accounts_between_pcs(self):
        # ===== جهاز المؤسس: حسابات موجودة من قبل =====
        founder = {
            "u_hiro": {"uid": "u_hiro", "username": "hiro", "name": "ابوسعد", "auth": local_auth("pass1"),
                       "playlists": [{"id": "pl1", "name": "Mix", "tracks": []}], "stats": {"secs": 100, "total": 5}},
            "u_md5n": {"uid": "u_md5n", "username": "md5n", "name": "md5n", "auth": local_auth("1234")},
            "u_boo": {"uid": "u_boo", "username": "boo", "name": "BOO", "auth": local_auth("boo1"),
                      "stats": {"secs": 9000, "total": 50}},
            "anon": {"uid": "anon", "name": "مستمع"},
        }
        self.pc("A", founder)
        badges.seed(server.PROFILES, server.norm_username)
        custom = badges.create("🎤", "مغني")
        badges.grant("u_md5n", custom)
        server._save_profiles()
        server._hub_sync_once()
        for uid in ("u_hiro", "u_md5n", "u_boo"):
            self.assertTrue(hub.token(uid), uid)          # انرفعت للسيرفر
        self.assertFalse(hub.token("anon"))                # الحسابات المجهولة تبقى محلية
        self.assertTrue(hub.meta("badges_imported"))
        b = self.c.get("/api/badges").get_json()
        self.assertIn("founder", b["assign"]["u_hiro"])
        self.assertIn(custom, b["assign"]["u_md5n"])
        self.assertIn("top_listener", b["assign"]["u_boo"])

        # ===== جهاز صاحبه: يسجل حساب جديد =====
        self.switch("B")
        r = self.c.post("/api/register", json={"username": "Friend1", "password": "abcd"})
        self.assertEqual(r.status_code, 200, r.get_json())
        friend = r.get_json()
        self.assertTrue(hub.token(friend["uid"]))
        self.assertEqual(self.c.post("/api/register", json={"username": "hiro", "password": "zzzz"}).status_code, 400)
        server._hub_sync_once()
        names = {p["username"] for p in self.c.get("/api/profiles").get_json()["profiles"]}
        self.assertTrue({"hiro", "md5n", "boo", "friend1"} <= names)   # يلقى المؤسس بالبحث
        hiro = next(p for p in server.all_profiles() if p["username"] == "hiro")
        self.assertIn("founder", hiro["badges"])
        # حساب من جهاز ثاني ما ينعدل بدون توكن
        self.assertFalse(server.uid_authorized("u_hiro", "") and not server._is_local_owner())

        # يتابع المؤسس
        server.register_profile({"uid": friend["uid"], "token": friend["token"], "following": ["u_hiro"], "name": "Friend"})
        self.assertTrue(server.PROFILES[friend["uid"]].get("hub_dirty"))
        server._hub_sync_once()
        self.assertFalse(server.PROFILES[friend["uid"]].get("hub_dirty"))

        # ===== جهاز المؤسس يشوف صاحبه ومتابعته =====
        self.switch("A")
        server._hub_sync_once()
        fp = server.PROFILES[friend["uid"]]
        self.assertEqual(fp["following"], ["u_hiro"])
        self.assertTrue(fp.get("hub"))
        self.assertFalse(self.c.get("/api/check_username?username=friend1&uid=x").get_json()["available"])
        # المؤسس يعطي صاحبه شارة → تظهر بالجهاز الثاني
        j = self.c.post("/api/badges/admin", json={"uid": "u_hiro", "token": server.auth_token("u_hiro"),
                                                    "action": "grant", "target": friend["uid"], "badge": "vip"}).get_json()
        self.assertIn("vip", j["assign"][friend["uid"]])

        # ===== المؤسس يدخل حسابه من جهاز صاحبه =====
        self.switch("B")
        self.assertEqual(self.c.post("/api/login", json={"username": "hiro", "password": "nope"}).status_code, 401)
        r = self.c.post("/api/login", json={"username": "@HIRO", "password": "pass1"})
        self.assertEqual(r.status_code, 200)
        j = r.get_json()
        self.assertEqual(j["uid"], "u_hiro")
        self.assertEqual(j["data"]["playlists"][0]["id"], "pl1")
        self.assertTrue(server.uid_authorized("u_hiro", j["token"]))
        server._hub_sync_once()
        self.assertIn("vip", next(p for p in server.all_profiles() if p["uid"] == friend["uid"])["badges"])

        # بيانات المستخدم تتزامن: يضيف بلاي لست من جهاز B → توصل جهاز A
        self.c.post("/api/userdata", json={"uid": "u_hiro", "token": j["token"], "playlists": [{"id": "pl2", "name": "New"}]})
        server._hub_sync_once()
        self.switch("A")
        got = self.c.get("/api/userdata?uid=u_hiro", headers={"X-Harmoni-Token": server.auth_token("u_hiro")}).get_json()
        self.assertEqual([p["id"] for p in got["playlists"]], ["pl2"])

    def test_forgot_password_and_founder_reset(self):
        self.pc("F", {"u_fnd1": {"uid": "u_fnd1", "username": "hiro", "name": "F", "auth": local_auth("pass1")}})
        server._hub_sync_once()
        self.assertTrue(hub.token("u_fnd1"), (hub.available(), hub.url(), server.PROFILES))
        self.leave("F")
        self.pc("G", {})
        uid = self.c.post("/api/register", json={"username": "forgetful", "password": "oldpw"}).get_json()["uid"]
        self.assertTrue(self.c.post("/api/forgot_password", json={"username": "forgetful"}).get_json()["ok"])
        self.leave("G")
        self.switch("F")
        badges.grant("u_fnd1", "founder")   # محلياً؛ على الهب ينعطى تلقائياً لـ @hiro
        adm = lambda body: self.c.post("/api/badges/admin", json=dict(body, uid="u_fnd1", token=server.auth_token("u_fnd1")))
        rr = adm({"action": "reset_requests"}); self.assertEqual(rr.status_code, 200, rr.get_data(as_text=True)); reqs = rr.get_json()["requests"]
        self.assertEqual([r["username"] for r in reqs], ["forgetful"])
        self.assertEqual(adm({"action": "reset_password", "target": uid, "password": "12"}).status_code, 400)
        r = adm({"action": "reset_password", "target": uid, "password": "1234"})
        self.assertEqual(r.status_code, 200, r.get_json())
        self.assertEqual(adm({"action": "reset_requests"}).get_json()["requests"], [])
        # غير المؤسس ما يقدر
        self.assertEqual(self.c.post("/api/badges/admin", json={"uid": uid, "token": "x", "action": "reset_password",
                                                                 "target": "u_fnd1", "password": "hack"}).status_code, 403)
        self.switch("G")
        self.assertEqual(self.c.post("/api/login", json={"username": "forgetful", "password": "oldpw"}).status_code, 401)
        self.assertEqual(self.c.post("/api/login", json={"username": "forgetful", "password": "1234"}).status_code, 200)

    def test_offline_fallback_and_conflict(self):
        # السيرفر طايح: ما نسمح بتسجيل جديد (ما نقدر نتأكد إن اليوزر فاضي) — الحسابات القديمة تنرفع أول ما يرجع
        self.pc("C", {"u_old1": {"uid": "u_old1", "username": "offline_guy", "name": "o", "auth": local_auth("abcd")}},
                url="http://127.0.0.1:%d" % _free_port())
        r = self.c.post("/api/register", json={"username": "new_guy", "password": "abcd"})
        self.assertEqual(r.status_code, 503)
        self.assertFalse(any(p.get("username") == "new_guy" for p in server.PROFILES.values()))
        self.assertEqual(self.c.post("/api/login", json={"username": "offline_guy", "password": "abcd"}).status_code, 200)
        self.assertFalse(hub.available())
        hub._state["url"] = self.url
        hub._state["down_until"] = 0
        server._hub_sync_once()
        self.assertTrue(hub.token("u_old1"))

        # نفس اليوزر بجهازين: الثاني ياخذ اسم جديد تلقائياً (ما فيه يوزرات مكررة) ونفس كلمة السر
        self.leave("C")
        self.pc("D", {})
        self.assertEqual(self.c.post("/api/register", json={"username": "samename", "password": "first"}).status_code, 200)
        self.leave("D")
        self.pc("E", {"u_local": {"uid": "u_local", "username": "samename", "name": "x", "auth": local_auth("second")}})
        server._hub_sync_once()
        server._hub_sync_once()
        self.assertEqual(server.PROFILES["u_local"]["username"], "samename2")
        self.assertEqual(server.PROFILES["u_local"]["renamed_from"], "samename")
        self.assertTrue(hub.token("u_local"))
        names = [p["username"] for p in server.all_profiles() if p.get("username")]
        self.assertEqual(len(names), len(set(names)), names)
        r = self.c.post("/api/login", json={"username": "samename2", "password": "second"})
        self.assertEqual(r.status_code, 200)
        self.assertEqual(r.get_json()["uid"], "u_local")

    def test_big_gif_upload(self):
        import urllib.request
        self.pc("M", {})
        j = self.c.post("/api/register", json={"username": "gifguy", "password": "abcd"}).get_json()
        gif = b"GIF89a" + os.urandom(3 * 1024 * 1024)   # 3MB — أكبر من حد البروفايل العادي
        up = lambda: self.c.post("/api/media_upload?uid=%s&kind=avatar" % j["uid"], data=gif,
                                 headers={"Content-Type": "image/gif", "X-Harmoni-Token": j["token"]})
        r = up()
        self.assertEqual(r.status_code, 200, r.get_data(as_text=True))
        url = r.get_json()["url"]
        self.assertTrue(url.startswith("https://hub.harmoni.click/media/" + j["uid"] + "/avatar-"))
        path = url.split("hub.harmoni.click", 1)[1]
        with urllib.request.urlopen(self.url + path) as resp:
            self.assertEqual(resp.headers["content-type"], "image/gif")
            self.assertEqual(resp.read(), gif)
        url2 = up().get_json()["url"]          # صورة جديدة → القديمة تنحذف
        self.assertNotEqual(url, url2)
        with self.assertRaises(urllib.error.HTTPError):
            urllib.request.urlopen(self.url + path)
        # من جهاز ثاني (مو صاحب الجهاز) بدون توكن صحيح
        self.assertEqual(self.c.post("/api/media_upload?uid=%s&kind=avatar" % j["uid"], data=gif, environ_base={"REMOTE_ADDR": "10.1.2.3"},
                                     headers={"Content-Type": "image/gif", "X-Harmoni-Token": "x"}).status_code, 403)
        self.assertEqual(self.c.post("/api/media_upload?uid=%s&kind=avatar" % j["uid"], data=b"x" * (21 * 1024 * 1024),
                                     headers={"Content-Type": "image/gif", "X-Harmoni-Token": j["token"]}).status_code, 413)

    def test_decorations_library(self):
        self.pc("D1", {})
        f = self.c.post("/api/register", json={"username": "hiro", "password": "abcd"}).get_json()     # المؤسس
        u = self.c.post("/api/register", json={"username": "friendx", "password": "abcd"}).get_json()
        server._DECO_CACHE.update(at=0, list=None)
        names = [d["name"] for d in self.c.get("/api/decorations").get_json()["decorations"]]
        self.assertIn("bonsai-discord", names)                          # اللي داخل البرنامج
        gif = b"GIF89a" + os.urandom(2000)
        add = lambda who, name: self.c.post("/api/decorations/add?uid=%s&name=%s" % (who["uid"], name), data=gif,
                                            headers={"Content-Type": "image/gif", "X-Harmoni-Token": who["token"]})
        self.assertEqual(add(u, "nope").status_code, 403)               # غير المؤسس ما يضيف
        r = add(f, "my ring")
        self.assertEqual(r.status_code, 200, r.get_data(as_text=True))
        d = r.get_json()["decoration"]
        self.assertRegex(d["url"], r"^https://hub\.harmoni\.click/media/deco/[0-9a-f]{16}\.gif$")
        lst = self.c.get("/api/decorations?force=1").get_json()["decorations"]
        self.assertEqual(lst[0]["name"], "my ring")
        # الكل يقدر يختاره، والرابط الغريب ينرفض
        server.register_profile({"uid": u["uid"], "token": u["token"], "name": "x", "decoration": d["url"]})
        self.assertEqual(server.PROFILES[u["uid"]]["decoration"], d["url"])
        server.register_profile({"uid": u["uid"], "token": u["token"], "name": "x", "decoration": "https://evil/x.gif"})
        self.assertEqual(server.PROFILES[u["uid"]]["decoration"], "")
        server.register_profile({"uid": u["uid"], "token": u["token"], "name": "x", "decoration": "/static/decorations/bonsai-discord.gif"})
        server._hub_sync_once()
        self.leave("D1")
        self.pc("D2", {})
        server._hub_sync_once()
        other = next(p for p in self.c.get("/api/profiles").get_json()["profiles"] if p["uid"] == u["uid"])
        self.assertEqual(other["decoration"], "/static/decorations/bonsai-discord.gif")

    def test_sticker_quota(self):
        self.pc("Q", {})
        j = self.c.post("/api/register", json={"username": "quota", "password": "abcd"}).get_json()
        h = {"Content-Type": "image/png", "X-Harmoni-Token": j["token"]}
        up = lambda kind: self.c.post("/api/media_upload?uid=%s&kind=%s" % (j["uid"], kind), data=b"\x89PNG" + os.urandom(4 * 1024 * 1024), headers=h)
        keys = []
        for _ in range(3):                                   # 12MB من 13MB
            r = up("sticker")
            self.assertEqual(r.status_code, 200, r.get_data(as_text=True))
            keys.append(r.get_json()["key"])
        r = up("sticker")
        self.assertEqual(r.status_code, 413)
        self.assertEqual(r.get_json()["error"], "quota")     # المساحة خلصت (تشمل الصورة والغلاف)
        self.assertEqual(up("avatar").status_code, 413)
        u = self.c.get("/api/media_usage?uid=" + j["uid"], headers=h).get_json()
        self.assertEqual(u["limit"], 13 * 1024 * 1024)
        self.assertGreater(u["used"], 12 * 1024 * 1024)
        self.assertEqual(sorted(u["stickers"]), sorted(keys))   # ملفات الملصقات (للاسترجاع)
        d = self.c.post("/api/media_delete", json={"uid": j["uid"], "keys": [keys[0], "someoneelse/sticker-0123456789abcdef"]},
                        headers={"X-Harmoni-Token": j["token"]}).get_json()
        self.assertEqual(d["deleted"], 1)                    # ملفات غيره ما تنحذف
        self.assertEqual(up("sticker").status_code, 200)     # رجعت المساحة

    def test_profile_stickers_sync(self):
        self.pc("S1", {})
        j = self.c.post("/api/register", json={"username": "stk", "password": "abcd"}).get_json()
        good = "https://hub.harmoni.click/media/%s/sticker1-0123456789abcdef" % j["uid"]
        server.register_profile({"uid": j["uid"], "token": j["token"], "name": "stk", "stickers": [
            {"url": good, "x": 120, "y": 30, "w": 2, "rot": -90},
            {"url": "https://evil.com/x.gif", "x": 1, "y": 1, "w": 10, "rot": 0},
            {"url": good, "where": "card", "u": "px", "a": "r", "x": 40, "y": 60, "w": 9999, "rot": 10, "shape": "star"},
            {"url": good, "shape": "hexagon"}, {"url": good}]})
        st = server.PROFILES[j["uid"]]["stickers"]
        self.assertEqual(len(st), 4)                      # الرابط الغريب انشال
        self.assertEqual(st[0], {"url": good, "where": "page", "shape": "none", "x": 100.0, "y": 30.0, "w": 4.0, "rot": 270.0})
        # قائمة فاضية (مثلاً بعد تسجيل دخول بنسخة قديمة) ما تمسح الملصقات — إلا لو المستخدم شالها بنفسه
        server.register_profile({"uid": j["uid"], "token": j["token"], "name": "stk", "stickers": []})
        self.assertEqual(len(server.PROFILES[j["uid"]]["stickers"]), 4)
        server.register_profile({"uid": j["uid"], "token": j["token"], "name": "stk", "stickers": [], "stickers_clear": True})
        self.assertEqual(server.PROFILES[j["uid"]]["stickers"], [])
        server.register_profile({"uid": j["uid"], "token": j["token"], "name": "stk", "stickers": st})
        server._hub_sync_once()
        self.leave("S1")
        self.pc("S2", {})
        server._hub_sync_once()
        other = next(p for p in self.c.get("/api/profiles").get_json()["profiles"] if p["uid"] == j["uid"])
        self.assertEqual([x["url"] for x in other["stickers"]], [good] * 4)
        self.assertEqual(other["stickers"][0]["rot"], 270)
        self.assertEqual(other["stickers"][1]["shape"], "star")
        self.assertEqual((other["stickers"][1]["where"], other["stickers"][1]["a"], other["stickers"][1]["w"]), ("card", "r", 3000))
        self.assertEqual(other["stickers"][2]["shape"], "none")


if __name__ == "__main__":
    unittest.main()
