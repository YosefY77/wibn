from pathlib import Path
p=Path('work/harmoni/static/app.js');s=p.read_text(encoding='utf-8')
a=s.index('const searchCache = {};');b=s.index('\nfunction canIAdd()',a)
s=s[:a]+'''const searchCache = new Map();
let searchSeq = 0;
let searchPage = -1;
let searchIdentity = "";
let searchBusy = false;
let searchHasMore = false;
const SEARCH_PAGE_SIZE = 49;
function searchPageData(query, source, page) {
  const key = JSON.stringify([query.toLowerCase(), source, page]);
  if (searchCache.has(key)) return searchCache.get(key);
  const pending = fetchWithTimeout("/api/search", {
    method: "POST", headers: {"Content-Type":"application/json"},
    body: JSON.stringify({query, source, limit:SEARCH_PAGE_SIZE, offset:page * SEARCH_PAGE_SIZE})
  }, 50000).then(async response => {
    const data = await response.json();
    if (!response.ok || data.error) throw new Error(data.error || t("search.fail"));
    return data;
  }).catch(error => { searchCache.delete(key); throw error; });
  searchCache.set(key, pending);
  while(searchCache.size > 30) searchCache.delete(searchCache.keys().next().value);
  return pending;
}
function updateMoreResults() {
  const button = document.getElementById("more-results");
  button.classList.toggle("hidden", !searchHasMore);
  button.disabled = searchBusy;
  button.setAttribute("aria-busy", String(searchBusy));
  button.textContent = searchBusy ? t("searching") : L("عرض المزيد", "Show more");
}
async function doSearch(more) {
  const query = document.getElementById("search-input").value.trim();
  const source = document.getElementById("source-select").value;
  if (!query) return;
  const identity = JSON.stringify([query.toLowerCase(), source]);
  if (more && (searchBusy || !searchHasMore)) return;
  if (identity !== searchIdentity) more = false;
  const page = more ? searchPage + 1 : 0;
  const seq = ++searchSeq;
  searchIdentity = identity;
  if (!more) { searchPage = -1; searchHasMore = false; }
  searchBusy = true;
  updateMoreResults();
  const message = document.getElementById("search-msg");
  message.textContent = t("searching");
  try {
    const data = await searchPageData(query, source, page);
    if (seq !== searchSeq) return;
    searchPage = page;
    searchHasMore = !!data.has_more;
    renderResults(data.results || [], !!more);
    preResolve(data.results || []);
    message.textContent = "";
    // Prepare one next page. Clicking More reuses this exact request.
    if(searchHasMore) searchPageData(query, source, page + 1).catch(() => {});
  } catch(error) {
    if(seq === searchSeq) message.textContent = error.message || t("search.fail");
  } finally {
    if(seq === searchSeq) { searchBusy = false; updateMoreResults(); }
  }
}
''' +s[b:]
a=s.index('  const mb = document.getElementById("more-results");',s.index('function renderResults('));b=s.index('\n}\n',a)
s=s[:a]+s[b:]
s=s.replace('  results.forEach(t => el.appendChild(trackCard(t)));','  results.forEach(t => el.appendChild(trackCard(t)));')
# Keep newer listening context from being overwritten by slower older requests.
s=s.replace('let lastSuggestions = null;','let lastSuggestions = null;\nlet suggestionSeq = 0;\nlet suggestionAbort = null;')
s=s.replace('async function loadSuggestions() {\n  const h = getHistory();','async function loadSuggestions() {\n  const seq = ++suggestionSeq;\n  if(suggestionAbort) suggestionAbort.abort();\n  const h = getHistory();')
s=s.replace('    const ctrl = new AbortController();\n    const tm = setTimeout(() => ctrl.abort(), 20000);\n    const res = await fetch("/api/suggest", {','    const ctrl = new AbortController();\n    suggestionAbort = ctrl;\n    const tm = setTimeout(() => ctrl.abort(), 20000);\n    const res = await fetch("/api/suggest", {')
s=s.replace('current: h[0].artist || "", played: played','current: h[0].artist || "", played: played, recent: h.slice(0, 12).map(track => ({title:track.title, artist:track.artist}))')
s=s.replace('    lastSuggestions = j.results || [];','    if(seq !== suggestionSeq) return;\n    lastSuggestions = j.results || [];')
s=s.replace('  sugShown = 36;\n  renderSuggestions();','  if(seq !== suggestionSeq) return;\n  sugShown = 36;\n  renderSuggestions();')
p.write_text(s,encoding='utf-8')
