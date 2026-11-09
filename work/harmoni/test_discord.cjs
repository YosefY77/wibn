const fs = require('node:fs'), vm = require('node:vm'), assert = require('node:assert/strict');
const requests = [], timers = [];
const player = () => ({paused:false, ended:false, currentTime:12, events:{}, addEventListener(k,f){this.events[k]=f;}});
const ctx = vm.createContext({
  location:{hostname:'127.0.0.1'}, window:{}, settings:{lang:'ar'},
  document:{getElementById(){return null;}}, setInterval(f){timers.push(f);},
  solo:{active:true,track:{title:'Solo',artist:'Artist',duration:90}}, soloAudio:player(), audio:player(),
  inRoom:true, state:{current:{title:'Room'}},
  fetch:async (path, opts) => {
    requests.push({path, opts});
    return {ok:true,json:async()=>({enabled:true,client_id:'123456789012345678',status:'connected'})};
  }
});
vm.runInContext(fs.readFileSync(__dirname+'/static/discord.js','utf8'),ctx);
(async () => {
  await new Promise(setImmediate);
  const latest = () => JSON.parse(requests.filter(r=>r.path.endsWith('/state')).at(-1).opts.body);
  assert.equal(latest().track.title,'Solo');
  assert.equal(latest().playing,true);
  ctx.soloAudio.paused = true;
  await ctx.soloAudio.events.pause();
  assert.equal(latest().playing,false);
  ctx.solo.active = false;
  await timers[0]();
  assert.equal(latest().track.title,'Room');
  ctx.inRoom = false;
  await timers[0]();
  assert.equal(latest().track,null);
  assert.equal(latest().playing,false);
  console.log('PASS Discord follows solo/room playback, pause and stop');
})().catch(e=>{console.error(e);process.exitCode=1;});
