/* إعدادات القائمة الجانبية (Alt+P) داخل صفحة الإعدادات */
(function () {
  if (!["localhost", "127.0.0.1"].includes(location.hostname)) return;   // خاصية الكمبيوتر فقط
  let cfg = { enabled: false, side: "right", hotkey: "Alt+P" };
  const label = (ar, en) => (settings.lang === "ar" ? ar : en);
  function api(body) {
    return fetch("/api/panel_config", {
      method: body ? "POST" : "GET", headers: { "Content-Type": "application/json" },
      ...(body ? { body: JSON.stringify(body) } : {})
    }).then(r => r.json());
  }
  function add() {
    const body = document.getElementById("settings-body");
    if (!body || document.getElementById("panel-settings")) return;
    const g = document.createElement("div");
    g.className = "settings-group";
    g.id = "panel-settings";
    g.innerHTML =
      '<div class="settings-head"></div>' +
      '<p class="sp-link-desc ps-desc"></p>' +
      '<label class="discord-opt"><input type="checkbox" id="ps-enabled"><span class="ps-en"></span></label>' +
      '<div class="ps-row"><span class="ps-side-l"></span>' +
      '<div class="ps-seg"><button type="button" data-side="right" class="ps-side"></button><button type="button" data-side="left" class="ps-side"></button></div></div>' +
      '<div class="ps-row"><span class="ps-key-l"></span><kbd class="ps-kbd"></kbd></div>' +
      '<button type="button" class="btn btn-primary" id="ps-try"></button>';
    g.querySelector(".settings-head").textContent = label("القائمة الجانبية", "Side panel");
    g.querySelector(".ps-desc").textContent = label(
      "قائمة تطلع من طرف الشاشة فوق أي برنامج: الأغنية الحالية والـ CD، التحكم بالتشغيل والصوت، والبلاي لست الحالية أو المفضلة. تختفي لو ضغطت برّاها أو نفس الاختصار.",
      "A panel that slides in from the screen edge over any app: current song with the CD, playback and volume controls, and the current playlist or favorites. Click outside it or press the shortcut again to hide it.");
    g.querySelector(".ps-en").textContent = label("تفعيل القائمة الجانبية", "Enable the side panel");
    g.querySelector(".ps-side-l").textContent = label("مكانها:", "Position:");
    g.querySelector('[data-side="right"]').textContent = label("يمين الشاشة", "Right");
    g.querySelector('[data-side="left"]').textContent = label("يسار الشاشة", "Left");
    g.querySelector(".ps-key-l").textContent = label("الاختصار:", "Shortcut:");
    g.querySelector(".ps-kbd").textContent = cfg.hotkey || "Alt+P";
    g.querySelector("#ps-try").textContent = label("افتحها الحين", "Open it now");
    const sync = () => {
      g.querySelector("#ps-enabled").checked = !!cfg.enabled;
      g.querySelectorAll(".ps-side").forEach(b => b.classList.toggle("active", b.dataset.side === cfg.side));
      g.querySelector("#ps-try").disabled = !cfg.enabled;
    };
    sync();
    g.querySelector("#ps-enabled").onchange = e => api({ enabled: e.target.checked }).then(j => { cfg = j; sync(); }).catch(() => {});
    g.querySelectorAll(".ps-side").forEach(b => { b.onclick = () => api({ side: b.dataset.side }).then(j => { cfg = j; sync(); }).catch(() => {}); });
    g.querySelector("#ps-try").onclick = () => fetch("/api/panel_open", { method: "POST" }).catch(() => {});
    body.appendChild(g);
  }
  const orig = window.renderSettings;
  if (typeof orig === "function") {
    window.renderSettings = function () { const r = orig.apply(this, arguments); try { add(); } catch (e) {} return r; };
  }
  api().then(j => { cfg = j; add(); }).catch(() => {});
})();
try { document.documentElement.setAttribute("data-panel-settings-ready", "1"); } catch (e) {}
