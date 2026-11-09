import re
from pathlib import Path
root=Path('work/harmoni');html=(root/'static/index.html').read_text(encoding='utf-8')
html=re.sub(r'<script[^>]*src="[^"]+"[^>]*></script>','',html)
html=html.replace('</head>','<style>.auth-overlay{display:none!important}</style></head>')
fixture='''<script>
const audio=document.getElementById('audio'),soloAudio=document.getElementById('solo-audio');const solo={active:false},state={current:null};
const t=k=>({'pl.title':'قوائم التشغيل','nav.likes':'المفضلة','pl.empty':'لا توجد قوائم','np.nothing':'لا شيء قيد التشغيل'}[k]||k);const L=(a,b)=>a;const getLikes=()=>[];const getPlaylists=()=>[];const showView=()=>{};const createPlaylistModal=()=>{};const fmt=()=>'';
const box=document.getElementById('results');for(let i=1;i<=49;i++){const card=document.createElement('div');card.className='track-card';card.innerHTML='<div class="track-thumb" style="display:grid;place-items:center;background:linear-gradient(135deg,#254d48,#19362f);color:#bdede0;font-size:36px">♫</div><div class="track-title">أغنية تجريبية رقم '+i+'</div><div class="track-artist">اسم الفنان</div><div class="track-actions"><button class="btn act-listen" title="استماع">▶</button><button class="btn act-add">+</button><button class="btn btn-like">♥</button></div>';box.append(card);}document.getElementById('more-results').classList.remove('hidden');
</script><script src="/static/refined.js"></script>'''
html=html.replace('</body>',fixture+'</body>');(root/'layout-preview.html').write_text(html,encoding='utf-8')
