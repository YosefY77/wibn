import json
import unittest
import mini_window as mw


class FakeMain:
    def __init__(self):
        self.js = []
    def evaluate_js(self, code):
        self.js.append(code)
        return json.dumps({"title": "Song", "playing": True, "volume": 40})


class BubbleTests(unittest.TestCase):
    def setUp(self):
        self.main = FakeMain()
        mw._BUBBLE.update(main=self.main, visible=True)

    def test_state_reads_main_window(self):
        self.assertEqual(mw.BubbleApi().state()["title"], "Song")

    def test_state_is_free_when_hidden(self):
        mw._BUBBLE["visible"] = False
        self.assertEqual(mw.BubbleApi().state(), {"hidden": True})
        self.assertEqual(self.main.js, [])

    def test_commands_are_whitelisted_and_escaped(self):
        api = mw.BubbleApi()
        self.assertTrue(api.cmd("volume", 55))
        self.assertIn('cmd("volume", 55.0, "', self.main.js[-1])
        self.assertFalse(api.cmd("alert(1)"))
        self.assertTrue(api.cmd("play_pause"))
        self.assertIn('cmd("play_pause", null, "', self.main.js[-1])

    def test_falls_back_to_pushed_state(self):
        mw._BUBBLE["main"] = None
        mw.push_state({"title": "Pushed", "playing": False})
        self.assertEqual(mw.BubbleApi().state()["title"], "Pushed")

    def test_cmd_queued_when_main_unavailable(self):
        mw._BUBBLE["main"] = None
        mw.BubbleApi().cmd("skip")
        cmds = mw.push_state(None)
        self.assertEqual([c["action"] for c in cmds], ["skip"])


if __name__ == "__main__":
    unittest.main()
