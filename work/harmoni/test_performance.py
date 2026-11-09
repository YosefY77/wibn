import concurrent.futures
import threading
import time
import unittest
from unittest.mock import patch
import server

class PerformanceTests(unittest.TestCase):
    def setUp(self):
        server._SEARCH_CACHE.clear()
        server._SEARCH_INFLIGHT.clear()
        server._STREAM_CACHE.clear()
        server._STREAM_INFLIGHT.clear()
    def test_duplicate_searches_share_work(self):
        def work(*args):
            time.sleep(.1)
            return [{'title':'song'}]
        with patch.object(server,'_do_search',side_effect=work) as run:
            with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
                values=list(pool.map(lambda _: server.search('same','youtube',12),range(6)))
            self.assertEqual(run.call_count,1)
            self.assertTrue(all(v==values[0] for v in values))
    def test_larger_cached_search_satisfies_first_page(self):
        server._SEARCH_CACHE[('query','youtube',36)]=(time.time(),list(range(36)))
        with patch.object(server,'_do_search') as run:
            self.assertEqual(server.search('query','youtube',12),list(range(12)))
            run.assert_not_called()
    def test_duplicate_resolutions_share_work(self):
        def work(*args):
            time.sleep(.1)
            return 'stream'
        with patch.object(server,'resolve_stream_url',side_effect=work) as run:
            with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
                values=list(pool.map(lambda _:server.cached_stream('url'),range(6)))
            self.assertEqual(values,['stream']*6)
            self.assertEqual(run.call_count,1)
    def test_failed_resolve_can_retry(self):
        with patch.object(server,'resolve_stream_url',side_effect=[None,'ok']) as run:
            self.assertIsNone(server.cached_stream('url'))
            self.assertEqual(server.cached_stream('url'),'ok')
            self.assertEqual(run.call_count,2)

    def test_stream_prefers_direct_audio_over_video_and_manifest(self):
        info = {'formats': [
            {'url':'video', 'acodec':'aac', 'vcodec':'h264', 'abr':256, 'protocol':'https'},
            {'url':'hls', 'acodec':'aac', 'vcodec':'none', 'abr':256, 'protocol':'m3u8_native'},
            {'url':'audio', 'acodec':'opus', 'vcodec':'none', 'abr':128, 'protocol':'https'}]}
        self.assertEqual(server.pick_stream_url(info)[0], 'audio')

    def test_manifest_only_stream_uses_compatible_fallback(self):
        with patch.object(server, '_extract_base', side_effect=[RuntimeError('no direct audio'), {'url':'live.m3u8'}]) as run:
            self.assertEqual(server.resolve_stream_url('video'), 'live.m3u8')
            self.assertEqual(run.call_count, 2)

    def test_fast_extraction_keeps_full_default_for_other_callers(self):
        with patch.object(server.yt_dlp, 'YoutubeDL') as dl:
            server._extract_base('video', fast=True)
            opts = dl.call_args.args[0]
            self.assertEqual(opts['extractor_args']['youtube']['skip'], ['hls','dash'])
            server._extract_base('video')
            self.assertNotIn('extractor_args', dl.call_args.args[0])

if __name__=='__main__':unittest.main()
