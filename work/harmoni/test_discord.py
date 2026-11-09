import os
import tempfile
import unittest
from unittest.mock import patch
from flask import Flask
from discord_presence import activity_for, Presence, install


class DiscordTests(unittest.TestCase):
    def test_activity_has_track_and_progress(self):
        result = activity_for({'playing':True, 'position':30,
                               'track':{'title':'Song', 'artist':'Artist', 'duration':180}}, now=1000)
        self.assertEqual(result['details'], 'Song')
        self.assertEqual(result['state'], 'Artist')
        self.assertEqual(result['type'], 2)
        self.assertEqual(result['status_display_type'], 1)   # اسم المغني بقائمة الأعضاء
        self.assertEqual(result['timestamps'], {'start':970, 'end':1150})

    def test_pause_and_stop_clear_activity(self):
        self.assertIsNone(activity_for({'playing':False, 'track':{'title':'Song'}}))
        self.assertIsNone(activity_for({}))

    def test_invalid_timing_and_untrusted_art(self):
        result = activity_for({'playing':True, 'position':'nan', 'track':{
            'title':'x'*200, 'duration':'bad', 'thumbnail':'file:///private'}}, now=1000)
        self.assertEqual(len(result['details']),128)
        self.assertEqual(result['timestamps'], {'start':1000})
        self.assertNotIn('assets',result)

    def test_local_routes_reject_remote_and_cross_origin_posts(self):
        with tempfile.TemporaryDirectory(dir=os.path.dirname(__file__)) as folder:
            app = Flask(__name__)
            presence = install(app, folder)
            client = app.test_client()
            self.assertEqual(client.post('/api/discord/state',json={}).status_code,403)
            headers={'X-Harmoni-Desktop':'1'}
            self.assertEqual(client.post('/api/discord/state',json={},headers=headers,
                                        environ_base={'REMOTE_ADDR':'192.168.1.2'}).status_code,403)
            self.assertEqual(client.post('/api/discord/state',json={},headers=headers,
                                        base_url='http://evil.test').status_code,403)
            self.assertEqual(client.post('/api/discord/state',json={},headers=headers).status_code,200)
            with patch.object(presence, 'start'):   # بدون Application ID: يستخدم معرّف هارموني الافتراضي
                self.assertEqual(client.post('/api/discord/config',json={'enabled':True},headers=headers).status_code,200)

    def test_config_persists_without_starting_discord_in_request(self):
        with tempfile.TemporaryDirectory(dir=os.path.dirname(__file__)) as folder:
            p = Presence(folder)
            with patch.object(p,'start'):
                p.configure({'enabled':True,'client_id':'123456789012345678'})
            other = Presence(folder)
            self.assertTrue(other.config['enabled'])
            self.assertEqual(other.config['client_id'],'123456789012345678')
            with self.assertRaises(ValueError):
                p.configure({'enabled':True,'client_id':'not a token'})

    def test_worker_clears_stale_activity(self):
        with tempfile.TemporaryDirectory(dir=os.path.dirname(__file__)) as folder:
            p = Presence(folder)
            p.config = {'enabled':True,'client_id':'123456789012345678'}
            p.state = {'playing':True,'track':{'title':'Song'}}
            p.last_seen = 0
            with patch('discord_presence.DiscordPipe') as pipe:
                pipe.return_value.update.side_effect=lambda _:p.stop.set()
                p.run()
                pipe.return_value.update.assert_called_once_with(None)
                pipe.return_value.close.assert_called_once()

    def test_failed_discord_does_not_break_worker(self):
        with tempfile.TemporaryDirectory(dir=os.path.dirname(__file__)) as folder:
            p = Presence(folder)
            p.config = {'enabled':True,'client_id':'123456789012345678'}
            def fail(_):
                p.stop.set()
                raise OSError('not running')
            with patch('discord_presence.DiscordPipe',side_effect=fail):
                p.run()
            self.assertEqual(p.status,'unavailable')


class DiscordOptionTests(unittest.TestCase):
    base = {'playing': True, 'position': 10, 'track': {'title': 'Song', 'artist': 'Artist', 'duration': 100,
            'thumbnail': 'https://i.ytimg.com/x.jpg', 'web_url': 'https://www.youtube.com/watch?v=abc'}}

    def test_hidden_details(self):
        a = activity_for(self.base, now=1000, options={'show_title': False, 'show_artist': False,
                                                         'show_cover': False, 'show_time': False})
        self.assertEqual(a['details'], 'Listening on Harmoni')
        self.assertNotIn('state', a)
        self.assertNotIn('assets', a)
        self.assertNotIn('timestamps', a)

    def test_button_and_rooms(self):
        a = activity_for(self.base, now=1000, options={'show_button': True})
        self.assertEqual(a['buttons'][0]['url'], 'https://www.youtube.com/watch?v=abc')
        room = dict(self.base, in_room=True)
        self.assertIsNone(activity_for(room, options={'show_in_rooms': False}))
        self.assertIsNotNone(activity_for(room, options={}))

    def test_options_persist(self):
        with tempfile.TemporaryDirectory(dir=os.path.dirname(os.path.abspath(__file__))) as folder:
            p = Presence(folder)
            with patch.object(p, 'start'):
                p.configure({'enabled': False, 'client_id': '', 'options': {'show_title': False}})
            self.assertFalse(Presence(folder).config['options']['show_title'])
            self.assertTrue(Presence(folder).config['options']['show_artist'])



class DefaultIdTests(unittest.TestCase):
    def test_works_out_of_the_box(self):
        from discord_presence import DEFAULT_CLIENT_ID
        with tempfile.TemporaryDirectory(dir=os.path.dirname(os.path.abspath(__file__))) as folder:
            p = Presence(folder)
            self.assertTrue(p.config['enabled'])
            self.assertEqual(p.config['client_id'], DEFAULT_CLIENT_ID)


if __name__ == '__main__':
    unittest.main()
