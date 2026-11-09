"""تحديث yt-dlp التلقائي: ينزل الـ wheel، يتأكد من البصمة، يستخدم النسخة الجديدة، ويرجع للقديمة لو خربت."""
import hashlib, io, json, os, sys, tempfile, types, unittest, zipfile
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import ytdlp_live as L


def wheel(ver, broken=False, extra=None):
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as z:
        z.writestr("yt_dlp/__init__.py", "raise ImportError('boom')\n" if broken else
                   "from . import version\nclass YoutubeDL:\n    pass\n")
        z.writestr("yt_dlp/version.py", "__version__ = %r\n" % ver)
        for k, v in (extra or {}).items():
            z.writestr(k, v)
    return buf.getvalue()


class Fake:
    def __init__(self, files):
        self.files = files   # pkg -> (ver, bytes)

    def get(self, url):
        pkg = url.split("/pypi/")[1].split("/")[0]
        if pkg not in self.files:
            raise IOError("nope")
        ver, data = self.files[pkg]
        return {"info": {"version": ver}, "urls": [{"packagetype": "bdist_wheel",
                "filename": "%s-%s-py3-none-any.whl" % (pkg.replace("-", "_"), ver), "url": "mem://" + pkg,
                "digests": {"sha256": hashlib.sha256(data).hexdigest()}}]}

    def download(self, url, path):
        with open(path, "wb") as f:
            f.write(self.files[url[6:]][1])


class LiveTests(unittest.TestCase):
    def setUp(self):
        self.base = tempfile.mkdtemp()
        self.saved_mods = {k: v for k, v in sys.modules.items() if k.partition(".")[0] in L.PKGS}
        bundled = types.ModuleType("yt_dlp")
        bundled.__path__ = []
        bundled.YoutubeDL = object
        ver = types.ModuleType("yt_dlp.version")
        ver.__version__ = "2025.01.01"
        bundled.version = ver
        self.bundled = bundled
        L._state.update({"base": self.base, "roots": {}, "checked": 0.0, "busy": False, "on_swap": None})
        self._bundle()
        if L._state["finder"] in sys.meta_path:
            sys.meta_path.remove(L._state["finder"])
        L._state["finder"] = None
        self.swapped = []

    def _bundle(self):
        sys.modules["yt_dlp"] = self.bundled
        sys.modules["yt_dlp.version"] = self.bundled.version

    def tearDown(self):
        if L._state["finder"] in sys.meta_path:
            sys.meta_path.remove(L._state["finder"])
        L._state["finder"] = None
        L._state["roots"] = {}
        L._purge()
        sys.modules.update(self.saved_mods)

    def run_check(self, fake, test=None):
        # «import yt_dlp» بدون نسخة محدثة → نرجّع النسخة اللي داخل البرنامج
        orig = L._use

        def use(roots):
            orig(roots)
            if not roots:
                self._bundle()
        L._use = use
        try:
            return L.check(fake.get, fake.download, test, force=True)
        finally:
            L._use = orig

    def test_updates_and_loads_new_version(self):
        L._state["on_swap"] = self.swapped.append
        fake = Fake({"yt-dlp": ("2026.10.10", wheel("2026.10.10"))})
        self.assertEqual(self.run_check(fake), "2026.10.10")
        import yt_dlp
        self.assertEqual(yt_dlp.version.__version__, "2026.10.10")
        self.assertTrue(yt_dlp.__file__.startswith(self.base))
        self.assertEqual(len(self.swapped), 1)
        cfg = json.load(open(os.path.join(self.base, "pylib", "active.json")))
        self.assertEqual(cfg["yt-dlp"]["version"], "2026.10.10")
        # نفس النسخة: ما يسوي شي
        self.assertIsNone(self.run_check(fake))

    def test_bad_hash_is_rejected(self):
        fake = Fake({"yt-dlp": ("2026.10.10", wheel("2026.10.10"))})
        good = fake.get
        fake.get = lambda u: dict(good(u), urls=[dict(good(u)["urls"][0], digests={"sha256": "0" * 64})])
        self.assertIsNone(self.run_check(fake))
        self.assertIn("sha256", L._state["last"])
        import yt_dlp
        self.assertIs(yt_dlp, self.bundled)

    def test_broken_version_falls_back_and_is_remembered(self):
        fake = Fake({"yt-dlp": ("2026.10.11", wheel("2026.10.11", broken=True))})
        self.assertIsNone(self.run_check(fake))
        import yt_dlp
        self.assertIs(yt_dlp, self.bundled)
        cfg = json.load(open(os.path.join(self.base, "pylib", "active.json")))
        self.assertIn("2026.10.11", cfg["bad"])

    def test_failed_live_test_keeps_old_but_retries_later(self):
        fake = Fake({"yt-dlp": ("2026.10.12", wheel("2026.10.12"))})
        self.assertIsNone(self.run_check(fake, test=lambda m: False))
        import yt_dlp
        self.assertIs(yt_dlp, self.bundled)
        self.assertTrue(L._state["last"].startswith("test_failed"))
        self.assertEqual(self.run_check(fake, test=lambda m: True), "2026.10.12")

    def test_zip_slip_rejected(self):
        fake = Fake({"yt-dlp": ("2026.10.13", wheel("2026.10.13", extra={"../evil.py": "x"}))})
        self.assertIsNone(self.run_check(fake))
        self.assertFalse(os.path.exists(os.path.join(self.base, "evil.py")))

    def test_init_uses_saved_version_after_restart(self):
        fake = Fake({"yt-dlp": ("2026.10.14", wheel("2026.10.14"))})
        self.assertEqual(self.run_check(fake), "2026.10.14")
        L._state["roots"] = {}
        L._purge()
        argv = sys.argv[0]
        sys.argv[0] = "harmoni.exe"
        try:
            L.init(self.base)
        finally:
            sys.argv[0] = argv
        import yt_dlp
        self.assertEqual(yt_dlp.version.__version__, "2026.10.14")


if __name__ == "__main__":
    unittest.main()
