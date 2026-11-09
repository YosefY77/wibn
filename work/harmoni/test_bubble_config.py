"""إعدادات الفقاعة: المكان (يسار/وسط/يمين) والتثبيت تنحفظ وتتنظف."""
import os, sys, tempfile, unittest
os.environ["HARMONI_DATA_DIR"] = tempfile.mkdtemp()
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import mini_window as mw


class BubbleConfigTests(unittest.TestCase):
    def setUp(self):
        mw._BCFG["cfg"] = None
        try:
            os.remove(mw._bcfg_path())
        except OSError:
            pass

    def test_defaults_save_and_clean(self):
        self.assertEqual(mw.bubble_config(), {"auto": True, "position": "center", "pinned": False})
        c = mw.set_bubble_config({"position": "left", "pinned": True, "evil": 1})
        self.assertEqual(c, {"auto": True, "position": "left", "pinned": True})
        mw._BCFG["cfg"] = None
        self.assertEqual(mw.bubble_config()["position"], "left")           # انحفظ بالملف
        self.assertEqual(mw.set_bubble_config({"position": "top-ish"})["position"], "center")

    def test_trigger_zone_follows_position(self):
        mw.set_bubble_config({"position": "left"})
        self.assertEqual(mw._zone_center(0, 1920), mw._TRIGGER_HALF)
        mw.set_bubble_config({"position": "right"})
        self.assertEqual(mw._zone_center(0, 1920), 1920 - mw._TRIGGER_HALF)
        mw.set_bubble_config({"position": "center"})
        self.assertEqual(mw._zone_center(0, 1920), 960)


if __name__ == "__main__":
    unittest.main()
