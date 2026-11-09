import sys,json,time
from pathlib import Path
sys.path.insert(0,str(Path('work/harmoni').resolve()))
import server
links=json.loads(Path('outputs/Harmoni-updated/Harmoni/spotify_links.json').read_text())
entry=list(links.values())[-1]
cid,secret=server._spotify_credentials()
s=server._requests.Session();s.trust_env=False;s.mount('https://',server._IPv4Adapter())
t=time.monotonic()
r=s.post('https://accounts.spotify.com/api/token',data={'grant_type':'refresh_token','refresh_token':entry['refresh_token'],'client_id':cid,'client_secret':secret},timeout=(5,15))
print('token_status',r.status_code,'seconds',round(time.monotonic()-t,2),flush=True)
if r.ok:
 data=r.json()
 if data.get('refresh_token'):
  entry['refresh_token']=data['refresh_token'];Path('outputs/Harmoni-updated/Harmoni/spotify_links.json').write_text(json.dumps(links))
 for path in ['/me','/me/tracks?limit=1','/me/playlists?limit=1']:
  t=time.monotonic();q=s.get('https://api.spotify.com/v1'+path,headers={'Authorization':'Bearer '+data['access_token']},timeout=(5,15))
  print(path,'status',q.status_code,'seconds',round(time.monotonic()-t,2),'error',q.text[:250] if not q.ok else None,flush=True)
