import json
import tempfile
import time
import unittest
from unittest.mock import patch, MagicMock
import server

class SpotifyRefreshTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(dir=server.BASE_DIR)
        self.base = patch.object(server, 'BASE_DIR', self.tmp.name)
        self.base.start()
        self.client = server.app.test_client()
        server._SP_SYNC_BUSY.clear()
        server._SP_SYNC_LAST.clear()
    def tearDown(self):
        self.base.stop()
        self.tmp.cleanup()
    def test_sync_requires_secret_and_unlink_revokes_it(self):
        server._sp_write_links({server._sp_link_id('secret'): {'refresh_token':'fixture'}})
        self.assertEqual(self.client.post('/api/spotify_sync',json={'sync_key':'wrong'}).status_code,401)
        with patch.object(server.threading,'Thread') as thread:
            self.assertEqual(self.client.post('/api/spotify_sync',json={'sync_key':'secret'}).status_code,200)
            self.assertEqual(self.client.post('/api/spotify_sync',json={'sync_key':'secret'}).status_code,429)
            self.assertEqual(thread.call_count,1)
        self.client.post('/api/spotify_unlink',json={'sync_key':'secret'})
        self.assertEqual(server._sp_links(),{})
    def test_refresh_retains_refresh_token_and_accepts_empty_library(self):
        server._sp_write_links({server._sp_link_id('secret'): {'refresh_token':'fixture'}})
        server._SPOTIFY_TICKETS['test']={'status':'pending'}
        response=MagicMock()
        response.__enter__.return_value.read.return_value=json.dumps({'access_token':'access'}).encode()
        def api(token,path,params=None):
            return {'id':'me','display_name':'Test'} if path=='/me' else {'items':[], 'next':None}
        with patch.object(server,'_spotify_credentials',return_value=('id','secret')), patch.object(server.urllib.request,'urlopen',return_value=response) as request, patch.object(server,'_sp_api_get',side_effect=api):
            server._sp_import_work('test','',{'sync_key':'secret'})
        self.assertIn(b'grant_type=refresh_token',request.call_args.args[0].data)
        self.assertEqual(server._sp_links()[server._sp_link_id('secret')]['refresh_token'],'fixture')
        self.assertEqual(server._SPOTIFY_TICKETS['test']['status'],'done')
        self.assertNotIn('refresh_token',server._SPOTIFY_TICKETS['test']['data'])
    def test_premium_rejection_is_explained_without_returning_raw_response(self):
        message = server._sp_access_message(403, 'Active premium subscription required for the owner of the app. secret-fixture')
        self.assertIn('Premium', message)
        self.assertNotIn('secret-fixture', message)
        server._SPOTIFY_TICKETS['rejected'] = {'status': 'pending'}
        response = MagicMock()
        response.__enter__.return_value.read.return_value = b'{"access_token":"fixture"}'
        with patch.object(server, '_spotify_credentials', return_value=('id', 'secret')), patch.object(server.urllib.request, 'urlopen', return_value=response), patch.object(server, '_sp_api_get', side_effect=server.SpotifyAccessError(message)):
            server._sp_import_work('rejected', 'code', {'verifier':'fixture'})
        self.assertEqual(server._SPOTIFY_TICKETS['rejected']['error'], message)

    def test_pagination_reads_later_additions(self):
        with patch.object(server,'_sp_api_get',side_effect=[{'items':[1,2],'next':'more'},{'items':[3],'next':None}]) as get:
            self.assertEqual(list(server._sp_items('token','/fixture')), [1,2,3])
            self.assertEqual(get.call_args.args[2]['offset'],2)

if __name__=='__main__': unittest.main()
