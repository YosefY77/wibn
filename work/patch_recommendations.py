from pathlib import Path
p=Path('work/harmoni/server.py');s=p.read_text(encoding='utf-8')
a=s.index('    cur_c = ',s.index('def api_suggest():'));b=s.index('    raw = []',a)
s=s[:a]+'''    recent = data.get("recent") or []
    recent = [r for r in recent[:12] if isinstance(r, dict)] if isinstance(recent, list) else []
    weights = {}
    names = {}
    for index, track in enumerate(recent):
        name = str(track.get("artist") or "").strip()
        if name:
            key = _norm(name)
            weights[key] = weights.get(key, 0) + (12 - index)
            names[key] = name
    for index, name in enumerate(([current] if current else []) + artists):
        key = _norm(name)
        if key and key not in weights:
            weights[key] = max(1, 6 - index)
            names[key] = name
    seeds = [names[key] for key in sorted(weights, key=weights.get, reverse=True)[:3]]
    queries = []
    for seed in seeds:
        queries.extend([seed + " music", "songs similar to " + seed])
    if not queries and recent:
        queries = [str(track.get("title") or "") + " similar songs" for track in recent[:2] if track.get("title")]
    queries = list(dict.fromkeys(queries))[:6]
''' +s[b:]
s=s.replace('        if current and _belongs_to(current, r):\n            continue\n','')
a=s.index('    def _score(r):',s.index('def api_suggest():'));b=s.index('\n\n\n@socketio.on',a)
s=s[:a]+'''    def _score(r):
        text = (r.get("title") or "").lower()
        affinity = max((weights[_norm(seed)] for seed in seeds if _belongs_to(seed, r)), default=0)
        quality = (3 if "official" in text else 0) + (2 if "audio" in text else 0)
        return (-affinity, -quality, text)

    collected.sort(key=_score)
    # Mix artists rather than filling the entire first page with one channel.
    selected, deferred, counts = [], [], {}
    for row in collected:
        key = _norm(row.get("artist") or "")
        if counts.get(key, 0) >= 6:
            deferred.append(row)
        else:
            selected.append(row)
            counts[key] = counts.get(key, 0) + 1
    return jsonify({"results": (selected + deferred)[:36]})
''' +s[b:]
p.write_text(s,encoding='utf-8')
p=Path('work/harmoni/static/index.html');s=p.read_text(encoding='utf-8').replace('app.js?v=90','app.js?v=91').replace('refined.css?v=4','refined.css?v=5');p.write_text(s,encoding='utf-8')
