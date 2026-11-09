import json,hashlib,os
from pathlib import Path
roots=[Path('C:/Users/YSIF/Documents/music_app'),Path('C:/Users/YSIF/AppData/Local/Harmoni'),Path('outputs/Harmoni-updated/Harmoni'),Path('outputs/Harmoni-link-fix/Harmoni')]
for root in roots:
 for rel in ['spotify_config.json','_internal/spotify_config.json']:
  p=root/rel
  if p.exists():
   try:
    d=json.loads(p.read_text(encoding='utf-8-sig'));cid=str(d.get('client_id') or '');sec=str(d.get('client_secret') or '')
    print(str(p), 'id_suffix='+cid[-6:], 'pair_fingerprint='+hashlib.sha256((cid+'|'+sec).encode()).hexdigest()[:12], 'secret_present='+str(bool(sec)))
   except Exception: print(str(p),'invalid_json')
print('environment_id_present',bool(os.environ.get('SPOTIFY_CLIENT_ID')),'environment_secret_present',bool(os.environ.get('SPOTIFY_CLIENT_SECRET')))
