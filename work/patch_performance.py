from pathlib import Path
p=Path('work/harmoni/server.py');s=p.read_text(encoding='utf-8')
pos=s.index('@app.route("/api/resolve_stream"')
s=s[:pos]+'''def cached_stream(web_url, source="youtube", title="", artist="", refresh=False):
    with _STREAM_CACHE_LOCK:
        hit = _STREAM_CACHE.get(web_url)
        if hit and time.time() - hit[1] < _STREAM_TTL and not refresh:
            return hit[0]
        pending = _STREAM_INFLIGHT.get(web_url)
        leader = pending is None
        if leader:
            pending = concurrent.futures.Future()
            _STREAM_INFLIGHT[web_url] = pending
    if not leader:
        return pending.result(timeout=45)
    try:
        stream = resolve_stream_url(web_url, source, title, artist)
        if stream:
            with _STREAM_CACHE_LOCK:
                _STREAM_CACHE[web_url] = (stream, time.time())
                if len(_STREAM_CACHE) > 300:
                    for key, _ in sorted(_STREAM_CACHE.items(), key=lambda item: item[1][1])[:100]:
                        _STREAM_CACHE.pop(key, None)
        pending.set_result(stream)
        return stream
    except Exception as error:
        pending.set_exception(error)
        raise
    finally:
        with _STREAM_CACHE_LOCK:
            _STREAM_INFLIGHT.pop(web_url, None)


''' + s[pos:]
a=s.index('    with _STREAM_CACHE_LOCK:',s.index('def api_resolve_stream():'));b=s.index('    start_download = False',a)
s=s[:a]+'''    stream_url = cached_stream(web_url, data.get("source", "youtube"),
                               data.get("title", ""), data.get("artist", ""), bool(data.get("refresh")))
    if not stream_url:
        return jsonify({"error": "unavailable"}), 502
''' +s[b:]
p.write_text(s,encoding='utf-8')
p=Path('work/harmoni/static/app.js');s=p.read_text(encoding='utf-8')
s=s.replace('async function doSearch(more) {','const SEARCH_PAGE_SIZE = 12;\nasync function doSearch(more) {')
s=s.replace('limit: 36 * (myPage + 1)','limit: SEARCH_PAGE_SIZE * (myPage + 1)')
s=s.replace('.slice(36 * myPage)', '.slice(SEARCH_PAGE_SIZE * myPage)').replace('.slice(0, 36);','.slice(0, SEARCH_PAGE_SIZE);')
s=s.replace('results.length < 36','results.length < SEARCH_PAGE_SIZE')
p.write_text(s,encoding='utf-8')
p=Path('work/harmoni/static/index.html');s=p.read_text(encoding='utf-8').replace('app.js?v=89','app.js?v=90');p.write_text(s,encoding='utf-8')
