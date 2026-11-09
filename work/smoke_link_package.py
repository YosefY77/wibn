import os
from pathlib import Path
import subprocess
import threading
import time
import requests
root=Path(__file__).resolve().parent.parent
exe=root/'outputs/Harmoni-link-fix/Harmoni/Harmoni.exe'
results=[]
def check():
    session=requests.Session(); session.trust_env=False
    try:
        for attempt in range(35):
            try:
                r=session.get('http://127.0.0.1:5086/',timeout=1)
                if r.status_code==200: break
            except requests.RequestException: pass
            time.sleep(.5)
        else: raise RuntimeError('Local server did not start')
        assert 'app.js?v=89' in r.text
        script=session.get('http://127.0.0.1:5086/static/refined.js',timeout=3).text
        assert 'hm-art-note' not in script and 'hm-symbol' not in script
        assert 'hm-cd' in script
        res=session.post('http://127.0.0.1:5086/api/spotify_sync',json={},timeout=3)
        assert res.status_code==401
        results.append(True)
        print('PASS: packaged UI, retained CD and protected Spotify sync endpoint',flush=True)
    except Exception as error:
        results.append(False); print(type(error).__name__,str(error),flush=True)
    finally: session.close()
env=os.environ.copy();env['MUSIC_HEADLESS']='1';env['MUSIC_PORT']='5086'
worker=threading.Thread(target=check,daemon=True);worker.start()
try: subprocess.run([str(exe)],cwd=exe.parent,env=env,capture_output=True,timeout=25)
except subprocess.TimeoutExpired: pass
worker.join(timeout=2)
raise SystemExit(0 if results==[True] else 1)
