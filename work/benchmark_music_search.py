import sys,time,json
from pathlib import Path
sys.path.insert(0,str(Path('work/harmoni').resolve()))
import server
report={}
for label,query in [('previous','كلاش'),('music_first',server.music_search_query('كلاش'))]:
 start=time.perf_counter();rows=server._do_search(query,'youtube',50);elapsed=time.perf_counter()-start
 start=time.perf_counter();ranked=server.rank_music_results(rows[:49],'كلاش');rank_ms=(time.perf_counter()-start)*1000
 report[label]={'search_seconds':round(elapsed,3),'ranking_ms':round(rank_ms,3),'top_titles':[r['title'] for r in (ranked if label=='music_first' else rows)[:7]]}
 print(label,json.dumps(report[label],ensure_ascii=False),flush=True)
Path('outputs/music-search-measurements.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
