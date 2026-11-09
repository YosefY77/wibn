import os
from pathlib import Path
import subprocess
import threading
import time
import requests
root=Path(__file__).resolve().parent.parent
exe=root/'outputs/Harmoni-discord/Harmoni/Harmoni.exe'
base='http://127.0.0.1:5084'
result=[]
def check():
 s=requests.Session();s.trust_env=False
 try:
  for _ in range(40):
   try:
    r=s.get(base+'/api/info',timeout=1);r.raise_for_status();break
   except requests.RequestException:time.sleep(.3)
  else:raise RuntimeError('Server unavailable')
  headers={'X-Harmoni-Desktop':'1'}
  r=s.get(base+'/api/discord/config',headers=headers,timeout=3)
  r.raise_for_status();assert r.json()['enabled'] is False
  assert s.post(base+'/api/discord/state',json={},timeout=3).status_code==403
  r=s.post(base+'/api/discord/state',headers=headers,json={'track':None,'playing':False},timeout=3)
  r.raise_for_status()
  html=s.get(base+'/',timeout=3).text
  assert '/static/discord.js' in html
  code=s.get(base+'/static/discord.js',timeout=3).text
  assert 'harmoniDiscordSettings' in code
  print('PASS packaged Discord routes, local access checks and settings assets',flush=True)
  result.append(True)
 except Exception as e:
  print('FAIL',str(e),flush=True);result.append(False)
 finally:s.close()
env=os.environ.copy();env.update(MUSIC_HEADLESS='1',MUSIC_PORT='5084')
t=threading.Thread(target=check,daemon=True);t.start()
p=subprocess.Popen([str(exe)],cwd=exe.parent,env=env,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
try:t.join(25)
finally:
 p.terminate()
 try:p.wait(5)
 except subprocess.TimeoutExpired:p.kill();p.wait()
raise SystemExit(0 if result==[True] else 1)
