from pathlib import Path
p=Path('work/harmoni/server.py');s=p.read_text(encoding='utf-8')
pos=s.index('\ndef _sp_track_shape(')
s=s[:pos]+'''
def _sp_items(tok, path, limit=50):
    offset = 0
    while True:
        page = _sp_api_get(tok, path, {"limit": limit, "offset": offset})
        items = page.get("items") or []
        yield from items
        if not page.get("next") or not items:
            break
        offset += len(items)

''' + s[pos:]
a=s.index('            for off in range(0, 300, 50):');b=s.index('        except urllib.error.HTTPError',a)
s=s[:a]+'''            for item in _sp_items(tok, "/me/tracks"):
                shape = _sp_track_shape(item.get("track"))
                if shape:
                    likes.append(shape)
''' + s[b:]
s=s.replace('            pj = _sp_api_get(tok, "/me/playlists", {"limit": 50})\n            for pl in ((pj.get("items") or []))[:20]:','            for pl in _sp_items(tok, "/me/playlists"):')
a=s.index('                for off in range(0, 400, 100):',s.index('def _sp_import_work('));b=s.index('                if ok_pl and trs:',a)
s=s[:a]+'''                try:
                    for it in _sp_items(tok, "/playlists/%s/tracks" % pl.get("id"), 100):
                        shape = _sp_track_shape((it or {}).get("track"))
                        if not shape or not shape["title"]:
                            continue
                        key = (shape["title"] + "|" + shape["artist"]).lower()
                        if key not in seen:
                            seen.add(key)
                            trs.append(shape)
                except Exception:
                    ok_pl = False
                    errs.append(name)
''' + s[b:]
s=s.replace('                if ok_pl and trs:', '                if ok_pl:')
s=s.replace('"سبوتيفاي رفض الوصول لبيانات حسابك (HTTP 403). "\n                "هذا لأن التطبيق بوضع Development. الحل: افتح Dashboard ← تطبيقك ← "\n                "User Management ← أضف إيميل حسابك سبوتيفاي ثم انتظر دقيقة وجرب."','"تعذر قراءة مكتبتك من سبوتيفاي. تحقق من الاتصال وصلاحيات الحساب ثم حاول مرة أخرى."')
p.write_text(s,encoding='utf-8')
p=Path('work/harmoni/static/index.html');s=p.read_text(encoding='utf-8').replace('app.js?v=87','app.js?v=88').replace('refined.js?v=4','refined.js?v=5');s=s.replace('id="volume" class="volume" type="range" min="0" max="100" step="1" value="100"','id="volume" class="volume" type="range" min="0" max="100" step="1" value="25"');p.write_text(s,encoding='utf-8')
