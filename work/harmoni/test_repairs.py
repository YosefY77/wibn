import json
import os
import tempfile
import threading
import time
import unittest
from unittest.mock import patch, MagicMock

import server


class Repairs(unittest.TestCase):
    def setUp(self):
        self.client = server.app.test_client()
        server._STREAM_CACHE.clear()
        server._AUDIO_FILES.clear()

    def test_empty_stream_metadata(self):
        self.assertEqual(server.pick_stream_url(None), (None, None))

    def test_spotify_host_detection(self):
        self.assertTrue(server.is_spotify('https://open.spotify.com/track/123'))
        self.assertFalse(server.is_spotify('https://example.com/spotify'))

    def test_packaged_spotify_config_fallback(self):
        with tempfile.TemporaryDirectory(dir=os.path.dirname(__file__)) as directory:
            bundle = os.path.join(directory, 'bundle')
            os.makedirs(bundle)
            with open(os.path.join(bundle, 'spotify_config.json'), 'w') as config:
                json.dump({'client_id': 'fixture-id', 'client_secret': 'fixture-secret'}, config)
            with patch.object(server, 'BASE_DIR', directory), patch.object(server, '_BUNDLE_DIR', bundle), patch.dict(os.environ, {'SPOTIFY_CLIENT_ID': '', 'SPOTIFY_CLIENT_SECRET': ''}):
                self.assertEqual(server._spotify_credentials(), ('fixture-id', 'fixture-secret'))

    def test_refresh_replaces_broken_cached_stream(self):
        server._STREAM_CACHE['https://example.com/track'] = ('expired', time.time())
        with patch.object(server, 'resolve_stream_url', return_value='fresh') as resolve:
            response = self.client.post('/api/resolve_stream', json={
                'web_url': 'https://example.com/track', 'refresh': True})
        self.assertEqual(response.json['stream_url'], 'fresh')
        resolve.assert_called_once()

    def test_prefetch_does_not_download_every_result(self):
        with patch.object(server, 'resolve_stream_url', return_value='fresh'), patch.object(server.threading, 'Thread') as thread:
            self.client.post('/api/resolve_stream', json={'web_url': 'https://example.com/track'})
            thread.assert_not_called()

    def test_imported_track_without_url(self):
        with patch.object(server, 'search', return_value=[{'web_url': 'https://example.com/track'}]), patch.object(server, 'resolve_stream_url', return_value='fresh'):
            response = self.client.post('/api/resolve_stream', json={'title': 'Fixture song', 'artist': 'Fixture artist'})
        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.json['stream_url'], 'fresh')

    def test_failed_download_can_retry(self):
        server._AUDIO_FILES['test'] = {'ready': False}
        server._AUDIO_DOWNLOAD_SLOTS.acquire()
        with patch.object(server.yt_dlp, 'YoutubeDL', side_effect=RuntimeError('fixture failure')):
            server._download_audio('test', 'https://example.com/track')
        self.assertNotIn('test', server._AUDIO_FILES)

    def test_webm_keeps_real_extension_and_mime(self):
        with tempfile.TemporaryDirectory(dir=os.path.dirname(__file__)) as directory:
            path = os.path.join(directory, 'test.webm')
            with open(path, 'wb') as f:
                f.write(b'fixture audio')
            downloader = MagicMock()
            downloader.__enter__.return_value = downloader
            downloader.prepare_filename.return_value = path
            server._AUDIO_DOWNLOAD_SLOTS.acquire()
            with patch.object(server.yt_dlp, 'YoutubeDL', return_value=downloader):
                server._download_audio('test', 'https://example.com/track')
            self.assertEqual(server._AUDIO_FILES['test']['path'], path)
            response = self.client.get('/api/audio_file?k=test', headers={'Range': 'bytes=0-3'})
            self.assertEqual(response.status_code, 206)
            self.assertIn('webm', response.content_type)
            self.assertEqual(response.data, b'fixt')
            response.close()

    def test_profile_failure_preserves_previous_file(self):
        with tempfile.TemporaryDirectory(dir=os.path.dirname(__file__)) as directory:
            path = os.path.join(directory, 'profiles.json')
            with open(path, 'w') as f:
                json.dump({'existing': True}, f)
            with patch.object(server, '_PROFILES_FILE', path), patch.object(server, 'PROFILES', {'new': True}), patch.object(server.os, 'replace', side_effect=OSError('fixture failure')):
                with self.assertRaises(OSError):
                    server._save_profiles()
            with open(path) as f:
                self.assertEqual(json.load(f), {'existing': True})

    def test_profile_concurrent_saves(self):
        with tempfile.TemporaryDirectory(dir=os.path.dirname(__file__)) as directory:
            path = os.path.join(directory, 'profiles.json')
            with patch.object(server, '_PROFILES_FILE', path), patch.object(server, 'PROFILES', {'fixture': {'name': 'Test'}}):
                threads = [threading.Thread(target=server._save_profiles) for _ in range(10)]
                for thread in threads: thread.start()
                for thread in threads: thread.join()
                with open(path) as f:
                    self.assertEqual(json.load(f), server.PROFILES)

    def test_proxy_closes_upstream(self):
        upstream = MagicMock(status_code=206)
        upstream.headers = {'Content-Type': 'audio/mp4', 'Content-Range': 'bytes 0-2/3'}
        upstream.iter_content.return_value = iter([b'abc'])
        session = MagicMock()
        session.get.return_value = upstream
        # الجلسة صارت مشتركة (اتصالات يعاد استخدامها للتقديم السريع) — المهم يتسكر اتصال الأغنية نفسه
        with patch.object(server, '_proxy_session', return_value=session):
            response = self.client.get('/api/audio_proxy?url=https://rr1---sn-test.googlevideo.com/audio')
            self.assertEqual(response.data, b'abc')
            response.close()
        self.assertTrue(upstream.close.called)
        self.assertFalse(session.close.called)

    def test_busy_port_gets_separate_server(self):
        first = server.create_http_server(0)
        try:
            second = server.create_http_server(first.server_port)
            try:
                self.assertNotEqual(first.server_port, second.server_port)
            finally:
                second.server_close()
        finally:
            first.server_close()

    def test_ipv4_for_media_connections(self):
        adapter = server._IPv4Adapter()
        self.assertEqual(adapter.poolmanager.connection_pool_kw['source_address'], ('0.0.0.0', 0))
        self.assertEqual(server.YD_BASE['source_address'], '0.0.0.0')

    def test_duplicate_end_does_not_skip_second_track(self):
        room = server.Room('fixture')
        room.current = {'id': 'one'}
        room.queue = [{'id': 'two'}, {'id': 'three'}]
        with patch.object(room, 'broadcast'):
            threads = [threading.Thread(target=room.ended, args=('one',)) for _ in range(10)]
            for thread in threads: thread.start()
            for thread in threads: thread.join()
        self.assertEqual(room.current['id'], 'two')
        self.assertEqual(len(room.queue), 1)


if __name__ == '__main__':
    unittest.main(verbosity=2)
