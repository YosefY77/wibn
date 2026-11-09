import sys,time,json
from pathlib import Path
sys.path.insert(0,str(Path('work/harmoni').resolve()))
import server
result={}
server._SEARCH_CACHE.clear()
for name in ['first_search','repeat_search']:
 start=time.monotonic();rows=server.search('piano relaxing music','youtube',12)
 result[name]=round(time.monotonic()-start,4)
 print(name,result[name],flush=True)
track=rows[0]
for name in ['first_resolve','prepared_resolve']:
 start=time.monotonic();url=server.cached_stream(track['web_url'],title=track['title'],artist=track['artist'])
 result[name]=round(time.monotonic()-start,4);print(name,result[name],bool(url),flush=True)
with server._requests.Session() as session:
 session.trust_env=False;session.mount('https://',server._IPv4Adapter())
 start=time.monotonic()
 with session.get(url,headers={'Range':'bytes=0-16383'},stream=True,timeout=(5,15)) as response:
  response.raise_for_status();chunk=next(response.iter_content(16384));assert len(chunk)>0
 result['first_audio_chunk']=round(time.monotonic()-start,4)
 print('first_audio_chunk',result['first_audio_chunk'],flush=True)
Path('outputs/performance-after.json').write_text(json.dumps(result))
