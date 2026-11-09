import sys,time
from pathlib import Path
sys.path.insert(0,str(Path('work/harmoni').resolve()))
import server
cid,secret=server._spotify_credentials()
with server._requests.Session() as session:
 session.trust_env=False
 session.mount('https://',server._IPv4Adapter())
 started=time.monotonic()
 r=session.post('https://accounts.spotify.com/api/token',data={'grant_type':'client_credentials'},auth=(cid,secret),timeout=(5,15))
 print('app_token_status',r.status_code,flush=True)
 if r.ok:
  token=r.json()['access_token']
  r=session.get('https://api.spotify.com/v1/search',params={'q':'piano','type':'track','limit':1},headers={'Authorization':'Bearer '+token},timeout=(5,15))
  print('app_only_search_status',r.status_code,flush=True)
  if not r.ok: print('spotify_error',r.text[:350],flush=True)
  print('spotify_response_date',r.headers.get('Date',''),flush=True)
 print('seconds',round(time.monotonic()-started,2),flush=True)
