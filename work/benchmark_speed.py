import sys,time,json
from pathlib import Path
sys.path.insert(0,str(Path('work/harmoni').resolve()))
import server
results={}
for limit in [36,12]:
 t=time.monotonic();rows=server._do_search('piano relaxing music','youtube',limit)
 results['search_'+str(limit)]=round(time.monotonic()-t,3)
 print('search',limit,results['search_'+str(limit)],'results',len(rows),flush=True)
 if limit==12 and rows:
  t=time.monotonic();url=server.resolve_stream_url(rows[0]['web_url'],title=rows[0]['title'],artist=rows[0]['artist']);results['resolve']=round(time.monotonic()-t,3);print('resolve',results['resolve'],'success',bool(url),flush=True)
Path('outputs/performance-baseline.json').write_text(json.dumps(results))
