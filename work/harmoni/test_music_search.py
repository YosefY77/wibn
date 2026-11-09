import unittest
from unittest.mock import patch
import server
class MusicSearchTests(unittest.TestCase):
 def test_music_first_without_hiding_game_soundtracks(self):
  rows=[{'title':'كلاش اوف كلانس شرح تحديث','artist':'Gaming','duration':180}, {'title':'كلاش أغنية جديدة official audio','artist':'كلاش','duration':180}, {'title':'Clash of Clans Soundtrack','artist':'Music','duration':180}]
  ranked=server.rank_music_results(rows,'كلاش')
  self.assertEqual(ranked[0],rows[1]);self.assertEqual(ranked[-1],rows[0]);self.assertEqual(len(ranked),3)
 def test_ambiguous_artist_uses_only_one_search(self):
  with patch.object(server,'search',return_value=[]) as search:
   server._search_work('كلاش','youtube',50)
   search.assert_called_once_with('كلاش راب','youtube',50)
 def test_song_queries_and_other_platforms_remain_intact(self):
  self.assertEqual(server.music_search_query('أغنية كلاش'),'أغنية كلاش')
  with patch.object(server,'search',return_value=[]) as search:
   server._search_work('title','soundcloud',49)
   search.assert_called_once_with('title','soundcloud',49)
 def test_direct_url_does_not_gain_keywords(self):
  with patch.object(server,'_extract',return_value={}) as extract:
   server._search_work('https://www.youtube.com/watch?v=fixture','youtube',49)
   extract.assert_called_once_with('https://www.youtube.com/watch?v=fixture')
 def test_ranking_keeps_pages_separate(self):
  rows=[{'title':str(i),'artist':'Artist','duration':180} for i in range(100)]
  with patch.object(server,'_search_work',return_value=rows):
   page=server.app.test_client().post('/api/search',json={'query':'Artist','limit':49,'offset':49}).get_json()
  self.assertEqual([r['title'] for r in page['results']],[str(i) for i in range(49,98)])
if __name__=='__main__':unittest.main()
