(() => {
  if (!['localhost', '127.0.0.1'].includes(location.hostname)) return;
  const headers = {'Content-Type':'application/json', 'X-Harmoni-Desktop':'1'};
  let config = {enabled:false, client_id:'', status:'disabled'};
  let busy = false;
  const label = (ar, en) => settings.lang === 'ar' ? ar : en;
  async function api(path, body) {
    const response = await fetch('/api/discord/' + path, {
      method:body === undefined ? 'GET' : 'POST', headers,
      ...(body === undefined ? {} : {body:JSON.stringify(body)})
    });
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || 'unavailable');
    return data;
  }
  function statusText() {
    if (!config.enabled) return label('إظهار النشاط متوقف', 'Activity sharing is off');
    return config.status === 'connected'
      ? label('متصل بديسكورد', 'Connected to Discord')
      : label('بانتظار ديسكورد — افتح تطبيقه وتأكد من معرّف التطبيق', 'Waiting for Discord — open the desktop app and check the Application ID');
  }
  function updateStatus() {
    const el = document.getElementById('discord-status');
    if (el) el.textContent = statusText();
  }
  window.harmoniDiscordSettings = body => {
    const group = document.createElement('div');
    group.className = 'settings-group';
    group.innerHTML = `<div class="settings-head">Discord</div>
      <p class="sp-link-desc"></p>
      <label style="display:flex;align-items:center;gap:10px;margin:12px 0"><input id="discord-enabled" type="checkbox"><span></span></label>
      <label for="discord-client-id">Application ID</label>
      <input id="discord-client-id" class="search-input" dir="ltr" inputmode="numeric" maxlength="20" style="width:100%;margin:8px 0" autocomplete="off">
      <div id="discord-options" class="discord-options"></div>
      <button class="btn btn-primary" id="discord-save"></button>
      <p id="discord-status" class="sp-link-desc" role="status"></p>`;
    group.querySelector('.sp-link-desc').textContent = label(
      'اعرض الأغنية والفنان في نشاطك بديسكورد. يتوقف الظهور عند إيقاف الأغنية. يلزم تشغيل ديسكورد وتفعيل مشاركة النشاط فيه.',
      'Show the song and artist in your Discord activity. Pausing clears it. Discord desktop and activity sharing must be enabled.');
    group.querySelector('label span').textContent = label('إظهار ما أستمع إليه', 'Share what I am listening to');
    const enabled = group.querySelector('#discord-enabled');
    const id = group.querySelector('#discord-client-id');
    const save = group.querySelector('#discord-save');
    enabled.checked = config.enabled;
    enabled.onchange = async () => {
      // تشغيل/إيقاف الخاصية فوراً (لو فيه Application ID محفوظ)
      if (!id.value.trim()) return;
      try { config = await api('config', {enabled:enabled.checked, client_id:id.value.trim(), options:config.options}); updateStatus(); report(); } catch (e) {}
    };
    id.value = config.client_id;
    save.textContent = label('حفظ الربط', 'Save connection');
    // خيارات الظهور — تنحفظ أول ما تغيّرها
    const OPTS = [
      ['show_title', label('اسم الأغنية', 'Song title')],
      ['show_artist', label('اسم الفنان', 'Artist name')],
      ['show_cover', label('صورة الأغنية', 'Cover art')],
      ['show_time', label('الوقت المتبقي', 'Time remaining')],
      ['show_button', label('زر "Listen" برابط الأغنية', '"Listen" button with the song link')],
      ['show_in_rooms', label('أظهر النشاط وأنا في غرفة استماع', 'Show while I am in a listening room')],
    ];
    const box = group.querySelector('#discord-options');
    const opts = Object.assign({show_title:true, show_artist:true, show_cover:true, show_time:true, show_button:false, show_in_rooms:true}, config.options || {});
    box.innerHTML = '<div class="discord-opt-head">' + label('وش يظهر لأصحابك في ديسكورد:', 'What your friends see on Discord:') + '</div>' +
      OPTS.map(([key, text]) => '<label class="discord-opt"><input type="checkbox" data-opt="' + key + '"' + (opts[key] ? ' checked' : '') + '><span></span></label>').join('') +
      '<div class="discord-preview"></div>';
    box.querySelectorAll('.discord-opt span').forEach((span, i) => { span.textContent = OPTS[i][1]; });
    const preview = () => {
      const o = {};
      box.querySelectorAll('input[data-opt]').forEach(x => { o[x.dataset.opt] = x.checked; });
      const lines = [];
      lines.push(o.show_title ? label('🎵 اسم الأغنية', '🎵 Song title') : '🎵 Listening on Harmoni');
      if (o.show_artist) lines.push(label('👤 اسم الفنان', '👤 Artist'));
      if (o.show_time) lines.push('⏱ 1:23 / 3:45');
      if (o.show_cover) lines.push(label('🖼 مع صورة الأغنية', '🖼 with cover art'));
      if (o.show_button && o.show_title) lines.push(label('🔗 زر Listen', '🔗 Listen button'));
      box.querySelector('.discord-preview').textContent = label('معاينة: ', 'Preview: ') + lines.join('  •  ');
      return o;
    };
    preview();
    box.querySelectorAll('input[data-opt]').forEach(x => {
      x.onchange = async () => {
        const o = preview();
        try { config = await api('config', {enabled:config.enabled, client_id:config.client_id, options:o}); report(); } catch (e) {}
      };
    });
    group.querySelector('#discord-status').textContent = statusText();
    save.onclick = async () => {
      save.disabled = true;
      try {
        const o = {};
        group.querySelectorAll('input[data-opt]').forEach(x => { o[x.dataset.opt] = x.checked; });
        config = await api('config', {enabled:enabled.checked, client_id:id.value.trim(), options:o});
        updateStatus();
        report();
      } catch (e) {
        group.querySelector('#discord-status').textContent = label('تعذر الحفظ. أدخل Application ID الصحيح (رقم التطبيق فقط).', 'Could not save. Enter a valid numeric Application ID.');
      } finally { save.disabled = false; }
    };
    body.appendChild(group);
  };
  async function report() {
    if (!config.enabled || busy) return;
    busy = true;
    try {
      const player = solo.active ? soloAudio : audio;
      const track = solo.active ? solo.track : (inRoom ? state.current : null);
      await api('state', {playing:!!track && !player.paused && !player.ended,
        position:player.currentTime || 0, in_room:!solo.active && !!inRoom && !(state && state.personal),
        track:track ? {title:track.title, artist:track.artist, thumbnail:track.thumbnail, duration:track.duration, web_url:track.web_url || ''} : null});
    } catch (e) {} finally { busy = false; }
  }
  for (const player of [audio, soloAudio]) {
    for (const event of ['playing','pause','ended','seeked','emptied']) player.addEventListener(event, report);
  }
  setInterval(report, 5000);
  setInterval(async () => {
    try { config = await api('config'); updateStatus(); } catch (e) {}
  }, 10000);
  api('config').then(data => {
    config = data;
    const body = document.getElementById('settings-body');
    if (body && !document.getElementById('discord-enabled')) window.harmoniDiscordSettings(body);
    report();
  }).catch(() => {});
})();
