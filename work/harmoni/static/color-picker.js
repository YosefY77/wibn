/* منتقي ألوان حديث بدل نافذة ويندوز القديمة: يشتغل تلقائياً مع أي <input type="color"> */
(function () {
  "use strict";
  const PRESETS = ["#1db954", "#22c55e", "#14b8a6", "#06b6d4", "#0ea5e9", "#3b82f6", "#6366f1", "#8b5cf6",
                   "#a855f7", "#d946ef", "#ec4899", "#f43f5e", "#ef4444", "#f97316", "#f59e0b", "#eab308",
                   "#84cc16", "#ffffff", "#9ca3af", "#111111"];
  const RECENT_KEY = "harmoni_recent_colors";
  const L3 = (ar, en) => (typeof settings !== "undefined" && settings.lang === "en") ? en : ar;

  function clamp(v, a, b) { return Math.max(a, Math.min(b, v)); }
  function hexToRgb(h) {
    h = String(h || "").replace("#", "");
    if (h.length === 3) h = h.split("").map(c => c + c).join("");
    if (!/^[0-9a-f]{6}$/i.test(h)) return null;
    return [parseInt(h.slice(0, 2), 16), parseInt(h.slice(2, 4), 16), parseInt(h.slice(4, 6), 16)];
  }
  function rgbToHex(r, g, b) { return "#" + [r, g, b].map(v => clamp(Math.round(v), 0, 255).toString(16).padStart(2, "0")).join(""); }
  function rgbToHsv(r, g, b) {
    r /= 255; g /= 255; b /= 255;
    const mx = Math.max(r, g, b), mn = Math.min(r, g, b), d = mx - mn;
    let h = 0;
    if (d) {
      if (mx === r) h = ((g - b) / d) % 6;
      else if (mx === g) h = (b - r) / d + 2;
      else h = (r - g) / d + 4;
      h *= 60; if (h < 0) h += 360;
    }
    return [h, mx ? d / mx : 0, mx];
  }
  function hsvToRgb(h, s, v) {
    const c = v * s, x = c * (1 - Math.abs((h / 60) % 2 - 1)), m = v - c;
    let r = 0, g = 0, b = 0;
    if (h < 60) [r, g, b] = [c, x, 0]; else if (h < 120) [r, g, b] = [x, c, 0];
    else if (h < 180) [r, g, b] = [0, c, x]; else if (h < 240) [r, g, b] = [0, x, c];
    else if (h < 300) [r, g, b] = [x, 0, c]; else [r, g, b] = [c, 0, x];
    return [(r + m) * 255, (g + m) * 255, (b + m) * 255];
  }
  function recent() { try { return JSON.parse(localStorage.getItem(RECENT_KEY) || "[]").slice(0, 8); } catch (e) { return []; } }
  function pushRecent(hex) {
    try {
      const list = [hex].concat(recent().filter(c => c !== hex)).slice(0, 8);
      localStorage.setItem(RECENT_KEY, JSON.stringify(list));
    } catch (e) {}
  }

  let pop = null, target = null, hsv = [140, .8, .7], startHex = "";

  function build() {
    pop = document.createElement("div");
    pop.className = "hcp";
    pop.innerHTML =
      '<div class="hcp-sv"><div class="hcp-sv-white"></div><div class="hcp-sv-black"></div><div class="hcp-sv-dot"></div></div>' +
      '<div class="hcp-row"><div class="hcp-preview"></div><div class="hcp-hue"><div class="hcp-hue-dot"></div></div></div>' +
      '<div class="hcp-hexrow"><span>HEX</span><input class="hcp-hex" maxlength="7" spellcheck="false" dir="ltr"></div>' +
      '<div class="hcp-label hcp-l-presets"></div><div class="hcp-swatches hcp-presets"></div>' +
      '<div class="hcp-label hcp-l-recent"></div><div class="hcp-swatches hcp-recent"></div>' +
      '<div class="hcp-actions"><button type="button" class="btn hcp-cancel"></button><button type="button" class="btn btn-primary hcp-ok"></button></div>';
    document.body.appendChild(pop);
    drag(pop.querySelector(".hcp-sv"), (x, y) => { hsv[1] = x; hsv[2] = 1 - y; update(true); });
    drag(pop.querySelector(".hcp-hue"), x => { hsv[0] = x * 359.9; update(true); });
    const hex = pop.querySelector(".hcp-hex");
    hex.addEventListener("input", () => {
      let v = hex.value.trim(); if (v[0] !== "#") v = "#" + v;
      const rgb = hexToRgb(v);
      if (rgb) { hsv = rgbToHsv(...rgb); update(true, true); }
    });
    hex.addEventListener("keydown", e => { if (e.key === "Enter") ok(); if (e.key === "Escape") cancel(); });
    pop.querySelector(".hcp-ok").onclick = ok;
    pop.querySelector(".hcp-cancel").onclick = cancel;
    document.addEventListener("mousedown", e => {
      if (pop.classList.contains("show") && !pop.contains(e.target) && e.target !== target) ok();
    }, true);
    document.addEventListener("keydown", e => { if (e.key === "Escape" && pop.classList.contains("show")) cancel(); });
  }

  function drag(el, cb) {
    const move = e => {
      const r = el.getBoundingClientRect();
      const p = e.touches ? e.touches[0] : e;
      cb(clamp((p.clientX - r.left) / r.width, 0, 1), clamp((p.clientY - r.top) / r.height, 0, 1));
    };
    el.addEventListener("pointerdown", e => {
      e.preventDefault(); move(e);
      const up = () => { window.removeEventListener("pointermove", move); window.removeEventListener("pointerup", up); };
      window.addEventListener("pointermove", move); window.addEventListener("pointerup", up);
    });
  }

  function swatches(box, list) {
    box.innerHTML = "";
    list.forEach(c => {
      const b = document.createElement("button");
      b.type = "button"; b.className = "hcp-sw"; b.style.background = c; b.title = c;
      b.onclick = () => { hsv = rgbToHsv(...hexToRgb(c)); update(true); };
      box.appendChild(b);
    });
  }

  function current() { return rgbToHex(...hsvToRgb(...hsv)); }

  function update(emit, fromHex) {
    const hex = current();
    const hue = rgbToHex(...hsvToRgb(hsv[0], 1, 1));
    pop.querySelector(".hcp-sv").style.background = hue;
    const dot = pop.querySelector(".hcp-sv-dot");
    dot.style.left = (hsv[1] * 100) + "%"; dot.style.top = ((1 - hsv[2]) * 100) + "%"; dot.style.background = hex;
    pop.querySelector(".hcp-hue-dot").style.left = (hsv[0] / 360 * 100) + "%";
    pop.querySelector(".hcp-hue-dot").style.background = hue;
    pop.querySelector(".hcp-preview").style.background = hex;
    if (!fromHex) pop.querySelector(".hcp-hex").value = hex.toUpperCase();
    pop.querySelectorAll(".hcp-sw").forEach(s => s.classList.toggle("on", s.title.toLowerCase() === hex));
    if (emit && target) setValue(hex);
  }

  function setValue(hex) {
    if (target.value === hex) return;
    target.value = hex;
    target.dispatchEvent(new Event("input", { bubbles: true }));
  }

  function open(input) {
    if (!pop) build();
    target = input;
    startHex = input.value || "#1db954";
    hsv = rgbToHsv(...(hexToRgb(startHex) || [29, 185, 84]));
    pop.querySelector(".hcp-l-presets").textContent = L3("ألوان جاهزة", "Presets");
    pop.querySelector(".hcp-l-recent").textContent = L3("آخر ألوان استخدمتها", "Recent");
    pop.querySelector(".hcp-ok").textContent = L3("تم", "Done");
    pop.querySelector(".hcp-cancel").textContent = L3("إلغاء", "Cancel");
    swatches(pop.querySelector(".hcp-presets"), PRESETS);
    const rc = recent();
    swatches(pop.querySelector(".hcp-recent"), rc);
    pop.querySelector(".hcp-l-recent").style.display = rc.length ? "" : "none";
    update(false);
    pop.classList.add("show");
    const r = input.getBoundingClientRect();
    const w = pop.offsetWidth, h = pop.offsetHeight;
    let left = r.left + r.width / 2 - w / 2, top = r.bottom + 8;
    if (top + h > innerHeight - 8) top = r.top - h - 8;
    pop.style.left = clamp(left, 8, innerWidth - w - 8) + "px";
    pop.style.top = clamp(top, 8, innerHeight - h - 8) + "px";
  }

  function close() { if (pop) pop.classList.remove("show"); target = null; }
  function ok() {
    if (!target) return close();
    const hex = current();
    setValue(hex);
    target.dispatchEvent(new Event("change", { bubbles: true }));
    pushRecent(hex);
    close();
  }
  function cancel() {
    if (target && startHex) { setValue(startHex); target.dispatchEvent(new Event("change", { bubbles: true })); }
    close();
  }

  // نعترض فتح نافذة ويندوز ونفتح منتقينا بدالها
  document.addEventListener("click", e => {
    const el = e.target && e.target.closest ? e.target.closest('input[type="color"]') : null;
    if (!el || el.disabled) return;
    e.preventDefault();
    if (pop && pop.classList.contains("show") && target === el) { ok(); return; }
    open(el);
  }, true);
  document.addEventListener("keydown", e => {
    const el = document.activeElement;
    if (el && el.matches && el.matches('input[type="color"]') && (e.key === "Enter" || e.key === " ")) { e.preventDefault(); open(el); }
  }, true);

  window.HarmoniColor = { open: open };
  try { document.documentElement.setAttribute("data-color-picker-ready", "1"); } catch (e) {}
})();
