"""تخفيض الصوت أثناء الكلام: منطق الكشف والإعدادات (بدون ويندوز)."""
import os, sys, tempfile, unittest
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ducking


class DuckingTests(unittest.TestCase):
    def setUp(self):
        ducking.init(tempfile.mkdtemp())

    def test_defaults_and_save(self):
        c = ducking.config()
        self.assertFalse(c["enabled"])
        self.assertEqual(c["amount"], 60)
        ducking._state["available"] = False   # ما نشغل الخيط بالاختبار
        c = ducking.set_config({"enabled": True, "amount": 500, "source": "weird", "sensitivity": 0, "release": 99})
        self.assertEqual((c["amount"], c["source"], c["sensitivity"], c["release"]), (95, "voice", 1, 5.0))
        ducking._state["cfg"] = None
        self.assertTrue(ducking.config()["enabled"])   # انحفظ بالملف

    def test_voice_detection(self):
        cfg = ducking.clean({"enabled": True, "source": "voice", "sensitivity": 50})
        th = ducking.threshold(50)
        self.assertEqual(ducking.should_duck([("Discord.exe", 10, th * 2, False)], cfg), (True, "Discord.exe"))
        self.assertFalse(ducking.should_duck([("Discord.exe", 10, th / 3, False)], cfg)[0])     # هدوء
        self.assertFalse(ducking.should_duck([("game.exe", 11, 0.9, False)], cfg)[0])          # لعبة: مو برنامج مكالمات
        self.assertFalse(ducking.should_duck([("Harmoni.exe", 12, 0.9, True)], cfg)[0])        # صوتنا نفسه
        self.assertFalse(ducking.should_duck([("", 0, 0.9, False)], cfg)[0])                   # تنبيهات ويندوز
        anycfg = ducking.clean({"enabled": True, "source": "any", "sensitivity": 50})
        self.assertTrue(ducking.should_duck([("game.exe", 11, 0.9, False)], anycfg)[0])

    def test_levels(self):
        cfg = ducking.clean({"amount": 70})
        self.assertAlmostEqual(ducking.target_level(True, cfg), 0.3)
        self.assertEqual(ducking.target_level(False, cfg), 1.0)
        self.assertTrue(ducking.is_voice_app("DiscordPTB.exe"))
        self.assertTrue(ducking.threshold(100) < ducking.threshold(1))


if __name__ == "__main__":
    unittest.main()
