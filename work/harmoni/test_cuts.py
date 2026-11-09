"""قص أجزاء من الأغنية: تنظيف الأجزاء + الغرفة تقفزها للكل."""
import os, sys, tempfile, time, unittest
from unittest.mock import patch
os.environ.setdefault("HARMONI_DATA_DIR", tempfile.mkdtemp())
os.environ["MUSIC_HEADLESS"] = "1"
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import server


class CutTests(unittest.TestCase):
    def test_clean_cuts(self):
        self.assertEqual(server._clean_cuts([[30, 40], [10, 20], [15, 25], [50, 50.2], ["x", 1], [-1, 5]], 200),
                         [[10.0, 25.0], [30.0, 40.0]])
        self.assertEqual(server._clean_cuts([[190, 400]], 200), [[190.0, 200.0]])
        self.assertEqual(server._clean_cuts("bad"), [])

    def _room(self, cuts, dur=200):
        r = server.Room("t" + str(time.time()))
        r.broadcast = lambda: None
        r._ensure_resolving = lambda *a, **k: None
        r._prepare_next = lambda: None
        r.add_track({"title": "A", "web_url": "https://www.youtube.com/watch?v=aaaaaaaaaaa", "duration": dur, "cuts": cuts})
        return r

    def test_room_jumps_over_cut(self):
        r = self._room([[10, 20]])
        r.position, r.updated_at = 12.0, time.time()
        r._apply_cuts()
        self.assertAlmostEqual(r.position, 20.0, places=1)

    def test_cut_to_end_goes_next(self):
        r = self._room([[190, 200]])
        r.queue.append({"id": "n", "title": "Next", "duration": 100})
        r.position, r.updated_at = 191.0, time.time()
        r._apply_cuts()
        self.assertEqual(r.current["title"], "Next")

    def test_outside_cut_untouched(self):
        r = self._room([[10, 20]])
        r.position, r.updated_at = 30.0, time.time()
        r._apply_cuts()
        self.assertAlmostEqual(r.position, 30.0, places=0)


if __name__ == "__main__":
    unittest.main()
