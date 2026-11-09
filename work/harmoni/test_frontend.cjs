const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const src = fs.readFileSync(__dirname + '/static/app.js', 'utf8');
function fn(name) {
  const start = src.indexOf(`function ${name}(`);
  assert(start >= 0, name);
  const next = src.indexOf('\nfunction ', start + 1);
  return src.slice(start, next < 0 ? undefined : next);
}
async function run() {
  const pending = [];
  const played = [];
  const notices = [];
  const ctx = vm.createContext({
    Date, Array, setTimeout, clearTimeout,
    fetch: () => new Promise(resolve => pending.push(resolve)),
    fetchWithTimeout: () => new Promise(resolve => pending.push(resolve)),
    loadSoloTrack: track => played.push(track.title),
    addHistory: () => {}, toast: text => notices.push(text), t: key => key,
    solo: {active: true}, lastResults: [],
    getLikes: () => [{web_url: 'liked'}],
    trackKey: track => track.web_url || track.title,
    myProfile: {uid: 'me'}, socket: {id: 'my-socket'}, getUid: () => 'me',
  });
  vm.runInContext('let soloRequestSeq = 0;\n' + fn('streamFresh') + '\n' + fn('preparedTrack') + '\n' + fn('prepareSoloTrack') + '\n' + fn('playSolo') + '\n' + fn('isMyProfile') + '\n' + fn('soloPool'), ctx);
  vm.runInContext("playSolo({title:'old', web_url:'old'}); playSolo({title:'new', web_url:'new'});", ctx);
  pending[1]({json: async () => ({stream_url:'new-stream'})});
  await new Promise(setImmediate);
  pending[0]({json: async () => ({stream_url:'old-stream'})});
  await new Promise(setImmediate);
  assert.deepEqual(played, ['new']);
  console.log('PASS: delayed old track cannot replace latest selection');
  vm.runInContext("playSolo({title:'leaving'}); ++soloRequestSeq;", ctx);
  pending[2]({json: async () => ({stream_url:'late-stream'})});
  await new Promise(setImmediate);
  assert.deepEqual(played, ['new']);
  console.log('PASS: leaving solo cancels pending playback');
  assert(vm.runInContext("isMyProfile({uid:'me'}) && isMyProfile({id:'my-socket'}) && !isMyProfile({uid:'other'})", ctx));
  console.log('PASS: self detection by user and room connection');
  assert(vm.runInContext("solo.track={web_url:'search'}; lastResults=[{web_url:'search'}, {web_url:'next'}]; soloPool()===lastResults", ctx));
  console.log('PASS: next and previous retain current search results');
  const freshness = vm.createContext({Date});
  vm.runInContext(fn('streamFresh').split('\nfunction playSolo')[0], freshness);
  assert(vm.runInContext("!streamFresh({stream_url:'x',stream_ts:Date.now()-19*60*1000}) && streamFresh({stream_url:'x',stream_ts:Date.now()})", freshness));
  console.log('PASS: expired stream is rejected');
  const count = pending.length;
  vm.runInContext("const shared={web_url:'shared',title:'shared'}; prepareSoloTrack(shared); playSolo({...shared});", ctx);
  assert.equal(pending.length, count + 1);
  pending[count]({json: async () => ({stream_url:'shared-stream'})});
  await new Promise(setImmediate);
  assert.equal(played.at(-1), 'shared');
  console.log('PASS: prefetch and click share one network request');
  const before = played.length;
  vm.runInContext("playSolo({web_url:'shared',title:'shared-copy'});", ctx);
  assert.equal(played.length, before + 1);
  assert.equal(pending.length, count + 1);
  console.log('PASS: prepared track starts synchronously without a network request');
  vm.runInContext("playSolo({web_url:'retry',title:'retry'});", ctx);
  pending[count + 1]({json: async () => ({error:'unavailable'})});
  await new Promise(setImmediate);
  vm.runInContext("playSolo({web_url:'retry',title:'retry'});", ctx);
  assert.equal(pending.length, count + 3);
  pending[count + 2]({json: async () => ({stream_url:'retry-stream'})});
  await new Promise(setImmediate);
  assert.equal(played.at(-1), 'retry');
  console.log('PASS: failed preparation can retry normally');
  let sourceWrites = 0, loads = 0;
  const media = {
    _src: '/api/audio_proxy?url=prepared',
    getAttribute() { return this._src; },
    set src(value) { sourceWrites++; this._src = value; },
    pause() {}, load() { loads++; }, play() { return Promise.resolve(); }
  };
  const mediaContext = vm.createContext({
    solo: {active:false}, inRoom:false, soloAudio:media,
    audio: {pause() {}, volume:1, muted:false},
    destroySoloHls() {}, renderNowPlaying() {}, startSoloReporting() {},
    setTimeout() {}, window:{}, toast() {}, t:key=>key,
    streamFresh: () => true,
    soloPlayableUrl: url=>'/api/audio_proxy?url='+url
  });
  vm.runInContext(fn('loadSoloTrack') + '\n' + fn('primeSoloAudio'), mediaContext);
  vm.runInContext("loadSoloTrack({stream_url:'prepared'});", mediaContext);
  assert.equal(sourceWrites, 0);
  console.log('PASS: playback keeps already buffered audio instead of reloading');
  vm.runInContext("primeSoloAudio({stream_url:'other'});", mediaContext);
  assert.equal(sourceWrites, 0);
  console.log('PASS: preparation never interrupts active playback');
  vm.runInContext("solo.active=false; primeSoloAudio({stream_url:'next'}); primeSoloAudio({stream_url:'next'});", mediaContext);
  assert.equal(sourceWrites, 1);
  assert.equal(loads, 1);
  console.log('PASS: idle preparation buffers one selection without duplicate loads');
}
run().catch(error => {console.error(error); process.exitCode=1;});
