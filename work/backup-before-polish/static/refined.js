/* Presentation only: retain the existing playback and account controls. */
(() => {
  const sidebar = document.querySelector('.sidebar');
  const brand = document.createElement('div');
  brand.className = 'hm-brand';
  brand.innerHTML = '<div><strong>harmoni</strong><small data-hm-ar="مساحتك الموسيقية" data-hm-en="Your listening space"></small></div>';
  sidebar.prepend(brand);
  const heading = document.querySelector('#view-home > .view-title');
  const hero = document.createElement('header');
  hero.className = 'hm-hero';
  hero.innerHTML = '<div class="hm-hero-copy"><span class="hm-eyebrow" data-hm-ar="على إيقاعك" data-hm-en="IN YOUR RHYTHM"></span><p data-hm-ar="كل موسيقاك في مكان واحد. اكتشف، احفظ، واستمع مع أصدقائك." data-hm-en="All your music, in one place. Discover, save, and listen together."></p><div class="hm-platforms" aria-label="YouTube, SoundCloud, Spotify"><span>YouTube</span><span>SoundCloud</span><span>Spotify</span></div></div>';
  heading.before(hero);
  hero.querySelector('.hm-eyebrow').after(heading);
  const icons = {
    home:'M3 10 12 3l9 7M5 9v12h5v-7h4v7h5V9',
    queue:'M4 6h16M4 12h10M4 18h10M18 11v9l5-4.5z',
    room:'M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8M20 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75',
    likes:'M20.8 4.6a5.5 5.5 0 0 0-7.8 0L12 5.7l-1.1-1.1a5.5 5.5 0 0 0-7.8 7.8L12 21l8.8-8.6a5.5 5.5 0 0 0 0-7.8Z',
    settings:'M4 6h16M4 12h16M4 18h16M8 3v6M16 9v6M10 15v6'
  };
  document.querySelectorAll('.nav-btn[data-view]').forEach(button => {
    const icon = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    icon.setAttribute('viewBox','0 0 24 24');
    icon.setAttribute('aria-hidden','true');
    icon.innerHTML = '<path d="'+icons[button.dataset.view]+'"/>';
    button.prepend(icon);
  });
  const me = document.getElementById('me-card');
  if (me) sidebar.append(me);
  const localize = () => document.querySelectorAll('[data-hm-ar]').forEach(el => {
    el.textContent = document.documentElement.lang === 'en' ? el.dataset.hmEn : el.dataset.hmAr;
  });
  localize();
  new MutationObserver(localize).observe(document.documentElement, {attributes:true,attributeFilter:['lang']});
})();

/* Left library: uses existing account data and playback controls. */
(() => {
  const rail = document.createElement('aside');
  rail.className = 'hm-library';
  rail.id = 'hm-library';
  rail.innerHTML = `<button class="hm-library-close" type="button">×</button>
    <section class="hm-disc-panel"><div class="hm-disc-stage"><div class="hm-cd"><img alt=""><span></span></div></div>
    <h3 class="hm-song-title"></h3><p class="hm-song-description"></p></section>
    <button class="hm-favorites" type="button"><span>♥</span><b></b><small></small></button>
    <div class="hm-library-heading"><h3></h3><button class="hm-new-playlist" type="button">+</button></div>
    <div class="hm-playlists"></div>`;
  document.querySelector('.app').append(rail);
  const toggle = document.createElement('button');
  toggle.className = 'hm-library-toggle';
  toggle.type = 'button';
  toggle.setAttribute('aria-controls', rail.id);
  toggle.setAttribute('aria-expanded', 'false');
  document.body.append(toggle);
  const shade = document.createElement('button');
  shade.className = 'hm-library-shade';
  shade.type = 'button';
  shade.hidden = true;
  document.body.append(shade);
  const close = () => { rail.classList.remove('is-open'); toggle.setAttribute('aria-expanded','false'); shade.hidden = true; };
  toggle.onclick = () => {
    const open = !rail.classList.contains('is-open');
    rail.classList.toggle('is-open',open); toggle.setAttribute('aria-expanded',String(open)); shade.hidden = !open;
    if (open) rail.querySelector('.hm-library-close').focus();
  };
  rail.querySelector('.hm-library-close').onclick = () => { close(); toggle.focus(); };
  shade.onclick = close;
  document.addEventListener('keydown', e => { if(e.key === 'Escape' && rail.classList.contains('is-open')) { close(); toggle.focus(); } });
  rail.querySelector('.hm-favorites').onclick = () => { showView('likes'); close(); };
  rail.querySelector('.hm-new-playlist').onclick = () => { close(); createPlaylistModal(); };
  const refreshLibrary = () => {
    rail.setAttribute('aria-label',t('pl.title'));
    rail.querySelector('.hm-library-heading h3').textContent = t('pl.title');
    rail.querySelector('.hm-favorites b').textContent = t('nav.likes');
    rail.querySelector('.hm-favorites small').textContent = getLikes().length;
    rail.querySelector('.hm-new-playlist').setAttribute('aria-label',t('pl.new'));
    const closeLabel = L('إغلاق المكتبة','Close library');
    rail.querySelector('.hm-library-close').setAttribute('aria-label',closeLabel);
    shade.setAttribute('aria-label',closeLabel);
    toggle.textContent = '♫ ' + L('مكتبتي','My library');
    const list = rail.querySelector('.hm-playlists');
    list.replaceChildren();
    const playlists = getPlaylists();
    if (!playlists.length) {
      const empty = document.createElement('p'); empty.className = 'hm-library-empty'; empty.textContent = t('pl.empty'); list.append(empty);
    }
    playlists.forEach(p => {
      const button = document.createElement('button'); button.type = 'button'; button.className = 'hm-playlist-link';
      const cover = document.createElement('span'); cover.className = 'hm-playlist-cover'; cover.textContent = '♫';
      if(p.img) { const img = document.createElement('img'); img.src = p.img; img.alt = ''; img.onerror = () => img.remove(); cover.append(img); }
      const meta = document.createElement('span'); meta.className = 'hm-playlist-meta';
      const name = document.createElement('b'); name.textContent = p.name;
      const count = document.createElement('small'); count.textContent = (p.tracks || []).length + ' ' + t('pl.count');
      meta.append(name,count); button.append(cover,meta);
      button.onclick = () => { close(); openPlaylist(p.id); }; list.append(button);
    });
  };
  const cd = rail.querySelector('.hm-cd');
  const cover = cd.querySelector('img');
  cover.onerror = () => { cover.hidden = true; };
  const refreshTrack = () => {
    const track = solo.active ? solo.track : state.current;
    const media = solo.active ? soloAudio : audio;
    cd.classList.toggle('is-playing', !!track && !media.paused && !media.ended && media.readyState >= 3);
    rail.querySelector('.hm-song-title').textContent = track ? track.title : t('np.nothing');
    const description = rail.querySelector('.hm-song-description');
    description.textContent = track ? [track.artist, track.duration ? fmt(track.duration) : '', track.source, track.description].filter(Boolean).join(' · ') : L('اختر أغنية لعرض غلافها ومعلوماتها هنا.','Choose a song to see its artwork and details here.');
    const src = track && track.thumbnail;
    if (src) { if(cover.getAttribute('src') !== src) { cover.hidden = false; cover.src = src; } }
    else { cover.removeAttribute('src'); cover.hidden = true; }
  };
  window.addEventListener('harmoni-library',refreshLibrary);
  window.addEventListener('storage',refreshLibrary);
  window.addEventListener('harmoni-track',refreshTrack);
  [audio,soloAudio].forEach(media => ['playing','play','pause','waiting','ended','emptied','loadeddata'].forEach(event => media.addEventListener(event,refreshTrack)));
  new MutationObserver(() => { refreshLibrary(); refreshTrack(); }).observe(document.documentElement,{attributes:true,attributeFilter:['lang']});
  refreshLibrary(); refreshTrack();
})();
