const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
const src=fs.readFileSync(__dirname+'/static/app.js','utf8');
function fn(name){const a=src.indexOf('function '+name+'('); const tail=src.slice(a+1); const match=/\n(?:async )?function /.exec(tail); const b=match ? a+1+match.index : src.length;return src.slice(a,b);}
let saved=null,likes=[],playlists=[{id:'local',name:'Mix',tracks:[{title:'Old',artist:'A'}]}],linked={};
const ctx=vm.createContext({load:()=>saved,getLikes:()=>likes,getPlaylists:()=>playlists,store:(key,val)=>{if(key==='mu_likes')likes=val;},pushUserData(){},savePlaylists:x=>playlists=x,plId:()=> 'new',localStorage:{setItem:(k,v)=>linked=JSON.parse(v)}});
// savedVolume is immediately followed by rememberVolume, so it can be isolated.
vm.runInContext(fn('savedVolume'),ctx);
assert.equal(ctx.savedVolume(),25);saved=0;assert.equal(ctx.savedVolume(),0);saved=72;assert.equal(ctx.savedVolume(),72);saved='bad';assert.equal(ctx.savedVolume(),25);saved=150;assert.equal(ctx.savedVolume(),100);
vm.runInContext(fn('mergeSpotifyImport'),ctx);
const data={sync_key:'key',likes:[{title:'Like',artist:'A'}],playlists:[{spotify_id:'sp1',name:'Mix',tracks:[{title:'Old',artist:'A'},{title:'New',artist:'A'}]}]};
ctx.mergeSpotifyImport(data);ctx.mergeSpotifyImport(data);
assert.equal(playlists.length,1);assert.equal(playlists[0].tracks.length,2);assert.equal(playlists[0].id,'local');assert.equal(likes.length,1);assert.equal(linked.sync_key,'key');
console.log('PASS: default volume, saved zero, saved levels and invalid data');
console.log('PASS: Spotify refresh adds songs to existing lists without duplicates');
const sliders = {volume:{style:{setProperty(){}},value:0}, 'mp-volume':{style:{setProperty(){}},value:0}};
let stored;
const volumeContext = vm.createContext({audio:{volume:.37},soloAudio:{volume:1},volume:sliders.volume,document:{getElementById:id=>sliders[id]},store:(k,v)=>stored=v});
const begin=src.indexOf('function rememberVolume('), end=src.indexOf('\naudio.volume = savedVolume()',begin);
vm.runInContext(src.slice(begin,end),volumeContext);
volumeContext.rememberVolume();assert.equal(stored,37);assert.equal(volumeContext.soloAudio.volume,.37);assert.equal(sliders['mp-volume'].value,37);
volumeContext.audio.volume=0;volumeContext.rememberVolume();assert.equal(stored,0);
console.log('PASS: volume changes persist and update both players and sliders');
