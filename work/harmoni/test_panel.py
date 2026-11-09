import os
import tempfile
import unittest
from unittest.mock import patch
os.environ["HARMONI_DATA_DIR"] = tempfile.mkdtemp()
import server
import side_panel
import mini_window


class PanelTests(unittest.TestCase):
    def test_config_saved_and_applied(self):
        c = server.app.test_client()
        r = c.post("/api/panel_config", json={"enabled": True, "side": "left"}).get_json()
        self.assertEqual((r["enabled"], r["side"], r["hotkey"]), (True, "left", "Alt+P"))
        self.assertEqual(side_panel.CONFIG, {"enabled": True, "side": "left"})
        self.assertEqual(server.load_panel_config()["side"], "left")
        c.post("/api/panel_config", json={"side": "nonsense"})
        self.assertEqual(server.load_panel_config()["side"], "left")

    def test_remote_cannot_change_or_open(self):
        c = server.app.test_client()
        env = {"REMOTE_ADDR": "203.0.113.9"}
        self.assertEqual(c.post("/api/panel_config", json={"enabled": True}, environ_base=env).status_code, 403)
        self.assertEqual(c.post("/api/panel_open", environ_base=env).status_code, 403)

    def test_toggle_respects_disabled(self):
        side_panel.set_config({"enabled": False})
        with patch.object(side_panel, "show") as show:
            side_panel.toggle()
            show.assert_not_called()

    def test_api_whitelists_commands(self):
        api = side_panel.PanelApi()
        with patch.object(mini_window, "_eval_main", return_value=("", True)) as ev:
            self.assertFalse(api.cmd("alert(1)"))
            self.assertFalse(api.play("evil", 1))
            self.assertFalse(api.play("likes", "x"))
            self.assertTrue(api.play("likes", 2))
            self.assertIn('play("likes", 2)', ev.call_args[0][0])
            self.assertTrue(api.cmd("volume", 30))
            self.assertIn('cmd("volume", 30.0)', ev.call_args[0][0])
            self.assertTrue(api.play("pl:abc123", 0))
            self.assertFalse(api.play("pl:x');alert(1);//", 0))
            self.assertFalse(api.play("current", 0))          # قائمة الانتظار انشالت من القائمة الجانبية
            self.assertIsNone(api.playlist("bad id!"))
            self.assertTrue(api.play_track({"title": "Song", "web_url": "https://www.youtube.com/watch?v=1", "evil": "x"}))
            self.assertNotIn("evil", ev.call_args[0][0])
            self.assertFalse(api.play_track({"artist": "no title"}))

    def test_glass_endpoint_local_only(self):
        c = server.app.test_client()
        with patch.object(mini_window, "set_glass") as sg:
            self.assertEqual(c.post("/api/window_glass", json={"on": True}, environ_base={"REMOTE_ADDR": "203.0.113.9"}).status_code, 403)
            sg.return_value = True
            j = c.post("/api/window_glass", json={"on": True}).get_json()
            self.assertTrue(j["on"] and j["native"])          # ويندوز رسم Acrylic → الصفحة تصير شفافة
            sg.return_value = False
            self.assertFalse(c.post("/api/window_glass", json={"on": True}).get_json()["native"])   # ما نجح → زجاج داخل التصميم بس
        self.assertEqual(mini_window.float_alpha(), 255)
        mini_window.GLASS["on"] = True
        self.assertEqual(mini_window.float_alpha(), 255)   # النص ما يصير شفاف أبداً حتى بالوضع الزجاجي
        mini_window.GLASS["on"] = False


if __name__ == "__main__":
    unittest.main()
