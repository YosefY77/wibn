/* استبيان الذوق: يطلع أول مرة بعد إنشاء حساب جديد (ويقدر المستخدم يرجع له من الإعدادات).
   يحفظ في mu_taste: { genres, moods, artists, langs, at } — ويستخدم في «مقترحة لك». */
(function () {
  "use strict";
  var KEY = "mu_taste", PENDING = "mu_onboard";
  function L2(ar, en) { return (typeof settings !== "undefined" && settings.lang === "en") ? en : ar; }
  function esc2(s) { return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]; }); }

  var GENRES = [
    ["khaleeji", "🌴", "خليجي", "Khaleeji"], ["arabic_pop", "🎤", "عربي بوب", "Arabic pop"], ["tarab", "🎻", "طرب", "Tarab"],
    ["shilat", "🐪", "شيلات", "Shilat"], ["rap_ar", "🔥", "راب عربي", "Arabic rap"], ["pop", "✨", "بوب", "Pop"],
    ["hiphop", "🎧", "هيب هوب", "Hip-hop"], ["rnb", "💜", "R&B", "R&B"], ["rock", "🎸", "روك", "Rock"],
    ["electronic", "⚡", "إلكتروني / EDM", "Electronic / EDM"], ["indie", "🌿", "إندي", "Indie"], ["lofi", "☕", "لو-فاي", "Lo-fi"],
    ["classical", "🎹", "كلاسيك", "Classical"], ["jazz", "🎷", "جاز", "Jazz"], ["kpop", "💫", "كي-بوب", "K-pop"],
    ["latin", "💃", "لاتيني", "Latin"], ["metal", "🤘", "ميتال", "Metal"], ["afro", "🌍", "أفروبيتس", "Afrobeats"],
    ["ost", "🎬", "موسيقى أفلام", "Soundtracks"], ["nasheed", "🕊️", "أناشيد", "Nasheeds"]
  ];
  var MOODS = [
    ["workout", "💪", "تمرين", "Workout"], ["focus", "📚", "دراسة وتركيز", "Focus"], ["party", "🎉", "سهرة", "Party"],
    ["drive", "🚗", "سواقة", "Driving"], ["chill", "🌙", "استرخاء", "Chill"], ["sad", "💔", "مزاج حزين", "Sad"]
  ];
  var ARTISTS = {
    khaleeji: ["محمد عبده", "عبدالمجيد عبدالله", "راشد الماجد", "ماجد المهندس", "أصيل أبو بكر", "عايض"],
    arabic_pop: ["عمرو دياب", "نانسي عجرم", "إليسا", "تامر حسني", "ويجز"],
    tarab: ["أم كلثوم", "عبدالحليم حافظ", "فيروز", "وردة"],
    shilat: ["فهد بن فصلا", "منصور الوايلي"],
    rap_ar: ["ويجز", "مروان بابلو", "كلاش", "شب جديد"],
    pop: ["Taylor Swift", "The Weeknd", "Dua Lipa", "Ed Sheeran", "Billie Eilish"],
    hiphop: ["Drake", "Kendrick Lamar", "Travis Scott", "J. Cole"],
    rnb: ["SZA", "Frank Ocean", "Daniel Caesar", "Brent Faiyaz"],
    rock: ["Arctic Monkeys", "Linkin Park", "Imagine Dragons", "Queen"],
    electronic: ["Avicii", "Martin Garrix", "Calvin Harris", "Kygo"],
    indie: ["Tame Impala", "Cigarettes After Sex", "The Neighbourhood"],
    lofi: ["Lofi Girl"],
    classical: ["Ludovico Einaudi", "Chopin", "Beethoven", "Mozart"],
    jazz: ["Frank Sinatra", "Norah Jones", "Miles Davis"],
    kpop: ["BTS", "BLACKPINK", "Stray Kids", "NewJeans"],
    latin: ["Bad Bunny", "Shakira", "Karol G"],
    metal: ["Metallica", "System of a Down"],
    afro: ["Burna Boy", "Rema", "Wizkid"],
    ost: ["Hans Zimmer", "Ramin Djawadi"],
    nasheed: ["ماهر زين", "مشاري العفاسي", "حمود الخضر"]
  };

  function getTaste() { try { return JSON.parse(localStorage.getItem(KEY) || "null"); } catch (e) { return null; } }
  window.getTaste = getTaste;

  var st = null, step = 0, el = null;
  var STEPS = 4;

  function chip(kind, item, on) {
    return '<button type="button" class="onb-chip' + (on ? " on" : "") + '" data-kind="' + kind + '" data-k="' + esc2(item[0]) + '">' +
      '<span class="onb-emo">' + item[1] + "</span>" + esc2(L2(item[2], item[3])) + "</button>";
  }
  function suggestedArtists() {
    var out = [];
    st.genres.forEach(function (g) { (ARTISTS[g] || []).forEach(function (a) { if (out.indexOf(a) < 0) out.push(a); }); });
    return out.slice(0, 18);
  }

  function render() {
    var title = "", sub = "", body = "";
    if (step === 0) {
      title = L2("وش نوع الأغاني اللي تحبها؟", "What kind of music do you like?");
      sub = L2("اختار كم تصنيف تحبه — نستخدمها عشان نقترح عليك أغاني على ذوقك.", "Pick a few genres — we'll use them to suggest songs you'll like.");
      body = '<div class="onb-chips">' + GENRES.map(function (g) { return chip("genres", g, st.genres.indexOf(g[0]) >= 0); }).join("") + "</div>";
    } else if (step === 1) {
      title = L2("متى تسمع أغاني أكثر شي؟", "When do you listen the most?");
      sub = L2("اختياري — اختار اللي يناسبك.", "Optional — pick what fits you.");
      body = '<div class="onb-chips onb-moods">' + MOODS.map(function (m) { return chip("moods", m, st.moods.indexOf(m[0]) >= 0); }).join("") + "</div>" +
        '<div class="onb-sub2">' + esc2(L2("لغة الأغاني:", "Song language:")) + "</div>" +
        '<div class="onb-chips">' + [["ar", "🇸🇦", "عربي", "Arabic"], ["en", "🌎", "إنجليزي", "English"]].map(function (x) { return chip("langs", x, st.langs.indexOf(x[0]) >= 0); }).join("") + "</div>";
    } else if (step === 2) {
      title = L2("مين فنانينك المفضلين؟", "Who are your favorite artists?");
      sub = L2("اضغط على اللي تحبهم أو اكتب اسم أي فنان.", "Tap the ones you like or type any artist.");
      var sug = suggestedArtists();
      var all = sug.concat(st.artists.filter(function (a) { return sug.indexOf(a) < 0; }));
      body = '<div class="onb-row"><input class="search-input onb-artist-in" maxlength="60" placeholder="' + esc2(L2("اكتب اسم فنان واضغط Enter", "Type an artist and press Enter")) + '">' +
        '<button type="button" class="btn onb-add">' + esc2(L2("إضافة", "Add")) + "</button></div>" +
        '<div class="onb-chips">' + all.map(function (a) { return chip("artists", [a, "🎵", a, a], st.artists.indexOf(a) >= 0); }).join("") + "</div>";
    } else {
      var n = st.genres.length + st.artists.length;
      title = n ? L2("تمام! جهزنا لك اقتراحات على ذوقك 🎶", "All set! Your suggestions are ready 🎶") : L2("ولا يهمك 👌", "No problem 👌");
      sub = n ? L2("تلقاها في الرئيسية تحت «مقترحة لك»، وتتحسن كل ما سمعت أكثر.", "Find them on Home under “Suggested for you” — they get better the more you listen.")
              : L2("تقدر تعبي ذوقك بعدين من الإعدادات ← عام.", "You can set your taste later in Settings → General.");
      body = n ? '<div class="onb-summary">' + st.genres.map(function (k) { var g = GENRES.filter(function (x) { return x[0] === k; })[0]; return g ? "<span>" + g[1] + " " + esc2(L2(g[2], g[3])) + "</span>" : ""; }).join("") +
        st.artists.map(function (a) { return "<span>🎵 " + esc2(a) + "</span>"; }).join("") + "</div>" : "";
    }
    var dots = "";
    for (var i = 0; i < STEPS; i++) dots += '<i class="' + (i === step ? "on" : i < step ? "done" : "") + '"></i>';
    var last = step === STEPS - 1;
    el.querySelector(".onb-card").innerHTML =
      '<div class="onb-top"><div class="onb-dots">' + dots + "</div>" +
      (last ? "" : '<button type="button" class="onb-skip">' + esc2(L2("تخطي", "Skip")) + "</button>") + "</div>" +
      '<h2 class="onb-title">' + esc2(title) + '</h2><p class="onb-subtitle">' + esc2(sub) + "</p>" +
      '<div class="onb-body">' + body + "</div>" +
      '<div class="onb-foot">' + (step > 0 && !last ? '<button type="button" class="btn onb-back">' + esc2(L2("رجوع", "Back")) + "</button>" : "<span></span>") +
      '<button type="button" class="btn btn-primary onb-next"' + (step === 0 && !st.genres.length ? " disabled" : "") + ">" +
      esc2(last ? L2("يلا نبدأ", "Let's go") : step === 0 ? L2("التالي", "Next") + (st.genres.length ? " (" + st.genres.length + ")" : "") : L2("التالي", "Next")) + "</button></div>";
    wire();
  }

  function wire() {
    var card = el.querySelector(".onb-card");
    card.querySelectorAll(".onb-chip").forEach(function (b) {
      b.onclick = function () {
        var list = st[b.dataset.kind], k = b.dataset.k, i = list.indexOf(k);
        if (i >= 0) list.splice(i, 1); else list.push(k);
        render();
      };
    });
    var inp = card.querySelector(".onb-artist-in");
    if (inp) {
      var add = function () {
        var v = inp.value.trim();
        if (v && st.artists.indexOf(v) < 0) st.artists.push(v);
        render();
        var again = el.querySelector(".onb-artist-in");
        if (again) again.focus();
      };
      inp.onkeydown = function (e) { if (e.key === "Enter") { e.preventDefault(); add(); } };
      card.querySelector(".onb-add").onclick = add;
      setTimeout(function () { try { inp.focus(); } catch (e) {} }, 30);
    }
    var skip = card.querySelector(".onb-skip");
    if (skip) skip.onclick = function () { step = STEPS - 1; render(); };
    var back = card.querySelector(".onb-back");
    if (back) back.onclick = function () { step--; render(); };
    card.querySelector(".onb-next").onclick = function () {
      if (step < STEPS - 1) { step++; render(); return; }
      finish();
    };
  }

  function finish() {
    try { localStorage.removeItem(PENDING); } catch (e) {}
    if (st.genres.length || st.moods.length || st.artists.length) {
      st.at = Date.now();
      try { localStorage.setItem(KEY, JSON.stringify(st)); } catch (e) {}
      try { if (typeof pushUserData === "function") pushUserData(); } catch (e) {}
    }
    el.classList.remove("show");
    setTimeout(function () { if (el) el.remove(); el = null; }, 250);
    try { if (typeof showView === "function") showView("home"); } catch (e) {}
    try { if (typeof loadSuggestions === "function") loadSuggestions(); } catch (e) {}
    try { if (typeof renderSettings === "function" && document.getElementById("view-settings").classList.contains("active")) renderSettings(); } catch (e) {}
  }

  function open() {
    if (el) return;
    var cur = getTaste() || {};
    st = { genres: (cur.genres || []).slice(), moods: (cur.moods || []).slice(), artists: (cur.artists || []).slice(), langs: (cur.langs || []).slice() };
    step = 0;
    el = document.createElement("div");
    el.className = "onb";
    el.setAttribute("role", "dialog");
    el.setAttribute("aria-modal", "true");
    el.innerHTML = '<div class="onb-card"></div>';
    document.body.appendChild(el);
    render();
    requestAnimationFrame(function () { el.classList.add("show"); });
  }
  window.harmoniOnboarding = { open: open };

  /* قسم في الإعدادات ← عام */
  function settingsGroup() {
    var body = document.getElementById("settings-body");
    if (!body || document.getElementById("taste-settings")) return;
    var g = document.createElement("div");
    g.className = "settings-group";
    g.id = "taste-settings";
    g.dataset.key = "taste";
    var t = getTaste();
    var parts = [];
    if (t) {
      (t.genres || []).forEach(function (k) { var x = GENRES.filter(function (y) { return y[0] === k; })[0]; if (x) parts.push(x[1] + " " + L2(x[2], x[3])); });
      (t.artists || []).slice(0, 6).forEach(function (a) { parts.push("🎵 " + a); });
    }
    g.innerHTML = '<div class="settings-head">' + esc2(L2("ذوقك في الأغاني", "Your music taste")) + "</div>" +
      '<p class="sp-link-desc">' + esc2(parts.length ? L2("نستخدمه في «مقترحة لك».", "Used for “Suggested for you”.") : L2("ما حددت ذوقك للحين — حدده عشان تطلع لك اقتراحات أحسن.", "You haven't set your taste yet — set it for better suggestions.")) + "</p>" +
      (parts.length ? '<div class="onb-summary onb-summary-sm">' + parts.map(function (p) { return "<span>" + esc2(p) + "</span>"; }).join("") + "</div>" : "") +
      '<button type="button" class="btn btn-primary" id="taste-edit">' + esc2(parts.length ? L2("عدّل ذوقك", "Edit your taste") : L2("حدد ذوقك", "Set your taste")) + "</button>";
    body.appendChild(g);
    g.querySelector("#taste-edit").onclick = open;
  }
  var orig = window.renderSettings;
  if (typeof orig === "function") {
    window.renderSettings = function () { var r = orig.apply(this, arguments); try { settingsGroup(); } catch (e) {} return r; };
  }

  function start() {
    var pending = false;
    try { pending = localStorage.getItem(PENDING) === "pending"; } catch (e) {}
    if (pending && !/[?&]selftest=1/.test(location.search)) setTimeout(open, 900);
    // صفحة الإعدادات تنبني مرة وحدة عند التشغيل (قبل ما يتحمل هذا الملف) — نضيف القسم الحين
    try { settingsGroup(); } catch (e) {}
    try { document.documentElement.setAttribute("data-onboarding-ready", "1"); } catch (e) {}
  }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start); else start();
})();
