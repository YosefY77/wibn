from pathlib import Path
p=Path('work/harmoni/server.py')
s=p.read_text(encoding='utf-8')
pos=s.index('\ndef _sp_import_work(')
s=s[:pos]+'''
_SP_LINK_LOCK = threading.RLock()
_SP_SYNC_BUSY = set()
_SP_SYNC_LAST = {}


def _sp_links():
    try:
        with open(os.path.join(BASE_DIR, "spotify_links.json"), encoding="utf-8") as f:
            return json.load(f)
    except FileNotFoundError:
        return {}


def _sp_write_links(links):
    path = os.path.join(BASE_DIR, "spotify_links.json")
    temp = path + ".tmp"
    with open(temp, "w", encoding="utf-8") as f:
        json.dump(links, f)
    os.replace(temp, path)


def _sp_link_id(key):
    return hashlib.sha256(key.encode()).hexdigest()


@app.route("/api/spotify_sync", methods=["POST"])
def api_spotify_sync():
    key = str((request.get_json(silent=True) or {}).get("sync_key") or "")
    with _SP_LINK_LOCK:
        if not key or _sp_link_id(key) not in _sp_links():
            return jsonify({"error": "أعد ربط حساب سبوتيفاي لتفعيل التحديث التلقائي"}), 401
        if key in _SP_SYNC_BUSY or time.time() - _SP_SYNC_LAST.get(key, 0) < 295:
            return jsonify({"status": "busy"}), 429
        _SP_SYNC_BUSY.add(key)
        _SP_SYNC_LAST[key] = time.time()
    ticket = uuid.uuid4().hex
    _SPOTIFY_TICKETS[ticket] = {"status": "pending", "exp": time.time() + 600}
    threading.Thread(target=_sp_import_work, args=(ticket, "", {"sync_key": key}), daemon=True).start()
    return jsonify({"ticket": ticket})


@app.route("/api/spotify_unlink", methods=["POST"])
def api_spotify_unlink():
    key = str((request.get_json(silent=True) or {}).get("sync_key") or "")
    with _SP_LINK_LOCK:
        links = _sp_links()
        links.pop(_sp_link_id(key), None)
        _sp_write_links(links)
    return jsonify({"ok": True})

''' + s[pos:]
a=s.index('        redirect_uri = _sp_redirect_uri()',s.index('def _sp_import_work('))
b=s.index('        me = _sp_api_get(tok, "/me")',a)
s=s[:a]+'''        sync_key = st.get("sync_key")
        if sync_key:
            with _SP_LINK_LOCK:
                link = _sp_links().get(_sp_link_id(sync_key))
            if not link:
                return _sp_fail(ticket, "أعد ربط حساب سبوتيفاي")
            token_body = {"grant_type": "refresh_token", "refresh_token": link["refresh_token"], "client_id": cid, "client_secret": sec}
        else:
            token_body = {"grant_type": "authorization_code", "code": code, "redirect_uri": _sp_redirect_uri(), "client_id": cid, "client_secret": sec, "code_verifier": st["verifier"]}
        req = urllib.request.Request(
            "https://accounts.spotify.com/api/token", data=urllib.parse.urlencode(token_body).encode(),
            headers={"Content-Type": "application/x-www-form-urlencoded"}, method="POST")
        with urllib.request.urlopen(req, timeout=15) as r:
            tokens = json.loads(r.read().decode())
        tok = tokens["access_token"]
        refresh = tokens.get("refresh_token") or (link["refresh_token"] if sync_key else None)
        if refresh:
            key = sync_key or (uuid.uuid4().hex + uuid.uuid4().hex)
            with _SP_LINK_LOCK:
                links = _sp_links()
                if sync_key and _sp_link_id(key) not in links:
                    return _sp_fail(ticket, "تم إلغاء الربط")
                links[_sp_link_id(key)] = {"refresh_token": refresh}
                _sp_write_links(links)
            sync_key = key
''' + s[b:]
s=s.replace('playlists.append({"name": name, "img": pimg, "tracks": trs})','playlists.append({"spotify_id": pl.get("id"), "name": name, "img": pimg, "tracks": trs})')
s=s.replace('        if not likes and not playlists:\n','        if not likes and not playlists and errs:\n')
s=s.replace('payload = {"user": display, "likes": likes, "playlists": playlists}','payload = {"user": display, "likes": likes, "playlists": playlists, "sync_key": sync_key}')
s=s.replace('        _sp_fail(ticket, "فشل الاستيراد: %s" % str(e)[:200])','        _sp_fail(ticket, "تعذر تحديث سبوتيفاي؛ تحقق من الاتصال أو أعد ربط الحساب")\n    finally:\n        with _SP_LINK_LOCK:\n            _SP_SYNC_BUSY.discard(st.get("sync_key"))')
p.write_text(s,encoding='utf-8')
p=Path('work/harmoni/static/refined.js');s=p.read_text(encoding='utf-8')
s=s.replace('<span class="hm-symbol" aria-hidden="true"><i></i><i></i><i></i><i></i></span>','')
s=s.replace('<div class="hm-art" aria-hidden="true"><div class="hm-record"><span>h.</span></div><span class="hm-art-note">PLAY YOUR<br>OWN WAY.</span></div>','')
p.write_text(s,encoding='utf-8')
