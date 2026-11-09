"""جودة نتائج البحث: تطابق الكلمات، حذف التكرار، النسخة الأصلية قبل الكفر/الريمكس، الشورتس تحت."""
import os
import tempfile
import unittest
os.environ.setdefault("HARMONI_DATA_DIR", tempfile.mkdtemp())
os.environ.setdefault("HARMONI_REGION", "none")
import server


def R(title, artist="X", duration=200, views=0):
    return {"title": title, "artist": artist, "duration": duration, "views": views}


class SearchQualityTests(unittest.TestCase):
    def test_all_words_beat_partial_match(self):
        rows = [R("محمد عبده - الأماكن ريمكس"), R("طلال مداح الاماكن"), R("محمد عبده الأماكن", "Mohammed Abdu - Topic")]
        out = server.rank_music_results(rows, "محمد عبده الاماكن")
        self.assertEqual(out[0]["artist"], "Mohammed Abdu - Topic")   # كل الكلمات + قناة الفنان + أصلية
        self.assertEqual(out[1]["title"], "محمد عبده - الأماكن ريمكس")   # الريمكس بعد الأصلية
        self.assertEqual(out[-1]["title"], "طلال مداح الاماكن")          # فنان ثاني آخر شي

    def test_alef_and_taa_marbuta_folding(self):
        rows = [R("Some other song"), R("أغنية الليلة")]
        out = server.rank_music_results(rows, "اغنيه الليله")
        self.assertEqual(out[0]["title"], "أغنية الليلة")

    def test_duplicates_removed(self):
        rows = [R("Adele - Hello (Official Music Video)", "Adele", 366), R("Adele - Hello [Lyrics]", "Lyrics Hub", 362),
                R("Adele - Hello", "Adele - Topic", 295), R("Adele - Someone Like You", "Adele", 285)]
        out = server.rank_music_results(rows, "adele hello")
        titles = [r["title"] for r in out]
        self.assertEqual(sum("Hello" in t for t in titles), 2)   # الفيديو (366ث) والصوت (295ث) مدتهم مختلفة
        self.assertIn("Adele - Someone Like You", titles)
        self.assertNotIn("Adele - Hello [Lyrics]", titles)        # نفس مدة الفيديو تقريباً = تكرار

    def test_shorts_and_long_mixes_go_down(self):
        rows = [R("song name #shorts", duration=30), R("song name 1 hour loop", duration=3600), R("song name", duration=210)]
        out = server.rank_music_results(rows, "song name")
        self.assertEqual(out[0]["duration"], 210)

    def test_asking_for_remix_keeps_remix(self):
        rows = [R("Track A"), R("Track A remix")]
        self.assertEqual(server.rank_music_results(rows, "track a remix")[0]["title"], "Track A remix")


if __name__ == "__main__":
    unittest.main()
