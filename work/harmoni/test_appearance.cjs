const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const source = fs.readFileSync(__dirname + '/static/app.js','utf8');
function getFn(name) { const start = source.indexOf('function '+name+'('); const end = source.indexOf('\nfunction ',start+1); return source.slice(start,end); }
const props = new Map(); const events = [];
const ctx = vm.createContext({settings:{theme:'custom',accent:'#ffffff'},document:{documentElement:{setAttribute(){},style:{setProperty:(k,v)=>props.set(k,v),removeProperty:k=>props.delete(k)}}},lighten:c=>c,contrastText:()=> '#000',window:{dispatchEvent:e=>events.push(e.type)},Event:class{constructor(type){this.type=type}},localStorage:{setItem(){}}});
vm.runInContext(getFn('customPalette')+'\n'+getFn('applyTheme')+'\n'+getFn('store'),ctx);
const luminance = hex => {
 const cs = [1,3,5].map(i=>parseInt(hex.slice(i,i+2),16)/255).map(c=>c<=.04045?c/12.92:((c+.055)/1.055)**2.4);
 return cs[0]*.2126+cs[1]*.7152+cs[2]*.0722;
};
for(const color of ['#000000','#ffffff','#ff0000','#00ff00','#0000ff','#ffff00','#1db954','#8800ff']) {
 const p=ctx.customPalette(color); const surfaces=['--bg','--bg2','--bg3','--bg4'];
 assert.equal(new Set(surfaces.map(k=>p[k])).size,4);
 for(const bg of surfaces) for(const fg of ['--fg','--muted']) assert((luminance(p[fg])+.05)/(luminance(p[bg])+.05)>=4.5, color+' '+bg+' '+fg);
}
ctx.applyTheme(); assert(props.has('--bg'));
ctx.settings.theme='light'; ctx.applyTheme(); assert(!props.has('--bg')); assert(!props.has('--fg'));
ctx.store('mu_playlists',[]); ctx.store('mu_likes',[]); ctx.store('mu_history',[]); assert.deepEqual(events,['harmoni-library','harmoni-library']);
console.log('PASS: distinct custom surfaces and readable text across extreme colors');
console.log('PASS: switching themes removes custom surface overrides');
console.log('PASS: account library saves notify the sidebar');
