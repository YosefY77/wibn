import hashlib
import os
from pathlib import Path
import subprocess
import threading
import time
import requests

root = Path(__file__).resolve().parent.parent
exe = root / 'outputs/Harmoni-search-update/Harmoni/Harmoni.exe'
base = 'http://127.0.0.1:5083'
result = []

def test():
    session = requests.Session()
    session.trust_env = False
    try:
        for attempt in range(25):
            try:
                info = session.get(base + '/api/info', timeout=1)
                info.raise_for_status()
                break
            except requests.RequestException:
                time.sleep(.3)
        else:
            raise RuntimeError('Packaged server did not become ready')
        assert info.json()['port'] == 5083
        print('PASS packaged server starts on requested port', flush=True)
        actual = session.get(base + '/static/app.js', timeout=3).content
        expected = (exe.parent / '_internal/static/app.js').read_bytes()
        assert hashlib.sha256(actual).digest() == hashlib.sha256(expected).digest()
        print('PASS packaged server serves its own updated interface', flush=True)
        response = session.post(base + '/api/search', json={
            'query': 'Beethoven Moonlight Sonata', 'source': 'youtube', 'limit': 49, 'offset': 0}, timeout=12)
        response.raise_for_status()
        tracks = response.json()['results']
        assert len(tracks) == 49
        next_page = session.post(base + '/api/search', json={'query':'Beethoven Moonlight Sonata','source':'youtube','limit':49,'offset':49}, timeout=20)
        next_page.raise_for_status()
        assert len(next_page.json()['results']) == 49
        assert next_page.json()['results'][0]['web_url'] != tracks[0]['web_url']
        print('PASS actual 49-song first and second pages', flush=True)
        print('PASS real YouTube search in packaged app', flush=True)
        response = session.post(base + '/api/resolve_stream', json=tracks[0], timeout=12)
        response.raise_for_status()
        url = response.json()['stream_url']
        print('PASS real stream resolution in packaged app', flush=True)
        with session.get(base + '/api/audio_proxy', params={'url': url},
                         headers={'Range': 'bytes=0-4095'}, timeout=10, stream=True) as audio:
            audio.raise_for_status()
            assert 'audio/' in audio.headers.get('Content-Type', '')
            assert len(next(audio.iter_content(4096))) == 4096
        print('PASS actual audio bytes from packaged app', flush=True)
        result.append(True)
    except Exception as error:
        print('FAIL', type(error).__name__, str(error)[:300], flush=True)
        result.append(False)
    finally:
        session.close()

env = os.environ.copy()
env['MUSIC_HEADLESS'] = '1'
env['MUSIC_PORT'] = '5083'
worker = threading.Thread(target=test, daemon=True)
worker.start()
try:
    completed = subprocess.run([str(exe)], cwd=exe.parent, env=env,
                               capture_output=True, timeout=35)
    print('Unexpected exit', completed.returncode,
          completed.stderr.decode('utf-8', 'replace')[-1400:], flush=True)
except subprocess.TimeoutExpired:
    print('Stopped temporary packaged test server.', flush=True)
worker.join(timeout=2)
raise SystemExit(0 if result == [True] else 1)
