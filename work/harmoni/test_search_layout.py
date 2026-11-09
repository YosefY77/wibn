import unittest
from unittest.mock import patch
import server
class SearchLayoutTests(unittest.TestCase):
 def setUp(self): self.client=server.app.test_client()
 def test_pages_continue_beyond_old_72_result_limit(self):
  rows=[{'title':str(i)} for i in range(150)]
  with patch.object(server,'_search_work',side_effect=lambda q,s,n:rows[:n]):
   first=self.client.post('/api/search',json={'query':'x','limit':49,'offset':0}).get_json()
   second=self.client.post('/api/search',json={'query':'x','limit':49,'offset':49}).get_json()
   last=self.client.post('/api/search',json={'query':'x','limit':49,'offset':147}).get_json()
  self.assertEqual(len(first['results']),49)
  self.assertEqual(second['results'][0]['title'],'49')
  self.assertEqual(second['results'][-1]['title'],'97')
  self.assertTrue(second['has_more'])
  self.assertEqual(len(last['results']),3)
  self.assertFalse(last['has_more'])
 def test_recommendations_keep_recent_artist_and_exclude_played(self):
  rows=[{'title':'New song','artist':'Recent Artist','duration':180,'web_url':'new'}, {'title':'Already played','artist':'Recent Artist','duration':180,'web_url':'old'}, {'title':'Other music','artist':'Older Artist','duration':180,'web_url':'other'}]
  with patch.object(server,'_search_cached',return_value=rows):
   result=self.client.post('/api/suggest',json={'artists':['Recent Artist','Older Artist'],'current':'Recent Artist','played':['Already played'],'recent':[{'artist':'Recent Artist','title':'Already played'},{'artist':'Older Artist','title':'Old'}]}).get_json()['results']
  self.assertEqual(result[0]['title'],'New song')
  self.assertNotIn('Already played',[r['title'] for r in result])
  self.assertEqual(len(result),2)
if __name__=='__main__':unittest.main()
