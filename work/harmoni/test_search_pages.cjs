const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
const source=fs.readFileSync(__dirname+'/static/app.js','utf8');
const elements={'search-input':{value:'music'},'source-select':{value:'youtube'},'search-msg':{},'more-results':{classList:{toggle(){}},setAttribute(){}}};
const pending=[],rendered=[];
const ctx=vm.createContext({Map,JSON,document:{getElementById:id=>elements[id]},fetchWithTimeout:()=>new Promise(resolve=>pending.push(resolve)),t:k=>k,L:a=>a,renderResults:(rows,append)=>rendered.push({rows,append}),preResolve(){},closeOpenedPlaylist(){}});
vm.runInContext(source.slice(source.indexOf('const searchCache ='),source.indexOf('\nfunction canIAdd()')),ctx);
(async()=>{
 const first=ctx.doSearch(false);pending[0]({ok:true,json:async()=>({results:[{title:'first'}],has_more:true})});await new Promise(setImmediate);
 assert.equal(rendered.length,1); // Preview can be played before the page completes.
 pending[1]({ok:true,json:async()=>({results:[{title:'first'}],has_more:true})});await first;
 assert.equal(pending.length,3); // The next page is prepared once.
 const more=ctx.doSearch(true);assert.equal(pending.length,3);
 pending[2]({ok:false,json:async()=>({error:'temporary'})});await more;
 assert.equal(vm.runInContext('searchPage',ctx),0);
 const retry=ctx.doSearch(true);assert.equal(pending.length,4);
 pending[3]({ok:true,json:async()=>({results:[{title:'second'}],has_more:false})});await retry;
 assert.equal(rendered.length,3);assert.equal(rendered[2].append,true);
 assert.equal(vm.runInContext('searchPage',ctx),1);assert.equal(elements['more-results'].disabled,false);
 console.log('PASS: More shares prefetch, retries failed pages, appends and unlocks');
})().catch(error=>{console.error(error);process.exitCode=1});
