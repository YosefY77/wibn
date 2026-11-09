/* إشعار التحديث: يشيك عند التشغيل وكل 6 ساعات، ويحدّث البرنامج بضغطة */
(function () {
  "use strict";
  if (!["localhost", "127.0.0.1"].includes(location.hostname)) return;   // البرنامج المثبت فقط
  const L2 = (ar, en) => (typeof settings !== "undefined" && settings.lang === "en") ? en : ar;
  const esc2 = s => String(s == null ? "" : s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  let info = null;
  const SKIP_KEY = "harmoni_update_skip";

  function skipped(v) { try { return localStorage.getItem(SKIP_KEY) === v; } catch (e) { return false; } }
  function skip(v) { try { localStorage.setItem(SKIP_KEY, v); } catch (e) {} }

  function check(force) {
    return fetch("/api/update/check" + (force ? "?force=1" : "")).then(r => r.json()).then(j => {
      info = j;
      renderSettingsGroup();
      if (j.available && j.can_install && (force || !skipped(j.latest))) showCard();
      return j;
    }).catch(() => null);
  }

  function card() {
    let c = document.getElementById("upd-card");
    if (!c) {
      c = document.createElement("div");
      c.id = "upd-card";
      c.className = "upd-card";
      c.setAttribute("role", "alert");
      document.body.appendChild(c);
    }
    return c;
  }

  function showCard() {
    const c = card();
    const notes = (info.notes || "").trim();
    c.innerHTML =
      '<div class="upd-head"><span class="upd-icon">⬆</span><div><div class="upd-title">' + esc2(L2("فيه تحديث جديد لـ Harmoni", "A Harmoni update is available")) + '</div>' +
      '<div class="upd-ver">' + esc2(info.current) + ' → ' + esc2(info.latest) + '</div></div>' +
      '<button class="upd-x" title="' + esc2(L2("إغلاق", "Close")) + '">✕</button></div>' +
      (notes ? '<div class="upd-notes">' + esc2(notes).replace(/\n/g, "<br>") + '</div>' : "") +
      '<div class="upd-bar hidden"><div class="upd-fill"></div></div>' +
      '<div class="upd-msg"></div>' +
      '<div class="upd-actions"><button class="btn btn-primary upd-go">' + esc2(L2("حدّث الحين", "Update now")) + '</button>' +
      '<button class="btn upd-later">' + esc2(L2("لاحقاً", "Later")) + '</button></div>';
    c.classList.add("show");
    const close = () => c.classList.remove("show");
    c.querySelector(".upd-x").onclick = close;
    c.querySelector(".upd-later").onclick = () => { skip(info.latest); close(); };
    c.querySelector(".upd-go").onclick = install;
  }

  function install() {
    const c = card();
    c.querySelector(".upd-actions").classList.add("hidden");
    c.querySelector(".upd-bar").classList.remove("hidden");
    const msg = c.querySelector(".upd-msg");
    msg.textContent = L2("جاري تحميل التحديث...", "Downloading the update...");
    fetch("/api/update/install", { method: "POST" }).then(r => r.json()).then(j => {
      if (j.error) throw new Error(j.error);
      poll();
    }).catch(e => fail(e && e.message));
  }

  function poll() {
    const c = card();
    fetch("/api/update/status").then(r => r.json()).then(s => {
      const fill = c.querySelector(".upd-fill");
      if (s.total) fill.style.width = Math.min(100, Math.round(s.done * 100 / s.total)) + "%";
      if (s.phase === "error") return fail(s.error);
      if (s.phase === "installing") {
        fill.style.width = "100%";
        c.querySelector(".upd-msg").textContent = L2("جاري التثبيت — البرنامج بيقفل ويرجع يفتح لحاله خلال ثواني", "Installing — Harmoni will close and reopen in a few seconds");
        return;
      }
      setTimeout(poll, 400);
    }).catch(() => setTimeout(poll, 1000));   // السيرفر يقفل وقت التثبيت — طبيعي
  }

  function fail(err) {
    const c = card();
    c.querySelector(".upd-msg").textContent = L2("ما قدرنا نحدّث: ", "Update failed: ") + (err || "?");
    c.querySelector(".upd-actions").classList.remove("hidden");
    c.querySelector(".upd-bar").classList.add("hidden");
  }

  /* قسم «التحديثات» في الإعدادات */
  function renderSettingsGroup() {
    const body = document.getElementById("settings-body");
    if (!body) return;
    let g = document.getElementById("update-settings");
    if (!g) {
      g = document.createElement("div");
      g.className = "settings-group";
      g.id = "update-settings";
      body.appendChild(g);
    }
    const cur = info ? info.current : "…";
    let status = "";
    if (info) {
      if (info.reason === "no_repo") status = L2("التحديث التلقائي غير مفعّل في هذي النسخة", "Auto-update isn't set up in this build");
      else if (info.reason === "offline") status = L2("ما قدرنا نوصل لسيرفر التحديثات", "Couldn't reach the update server");
      else if (info.available) status = L2("فيه نسخة أحدث: ", "Newer version: ") + info.latest;
      else status = L2("عندك آخر نسخة ✓", "You're up to date ✓");
    }
    g.innerHTML = '<div class="settings-head">' + esc2(L2("التحديثات", "Updates")) + '</div>' +
      '<div class="upd-set-row"><span>' + esc2(L2("النسخة الحالية:", "Current version:")) + ' <b dir="ltr">' + esc2(cur) + '</b></span>' +
      '<span class="upd-set-status">' + esc2(status) + '</span></div>' +
      '<div class="upd-set-actions"><button type="button" class="btn" id="upd-check">' + esc2(L2("تحقق من التحديثات", "Check for updates")) + '</button>' +
      (info && info.available && info.can_install ? '<button type="button" class="btn btn-primary" id="upd-now">' + esc2(L2("حدّث الحين", "Update now")) + '</button>' : "") + '</div>';
    g.querySelector("#upd-check").onclick = e => {
      e.target.disabled = true;
      e.target.textContent = L2("جاري التحقق...", "Checking...");
      check(true).then(j => { if (j && !j.available && !j.reason && typeof toast === "function") toast(L2("عندك آخر نسخة ✓", "You're up to date ✓")); });
    };
    const now = g.querySelector("#upd-now");
    if (now) now.onclick = () => { showCard(); install(); };
  }

  const orig = window.renderSettings;
  if (typeof orig === "function") {
    window.renderSettings = function () { const r = orig.apply(this, arguments); try { renderSettingsGroup(); } catch (e) {} return r; };
  }
  setTimeout(() => check(false), 6000);
  setInterval(() => check(false), 6 * 60 * 60 * 1000);
  window.__harmoniUpdate = { check: check };
  try { document.documentElement.setAttribute("data-updater-ready", "1"); } catch (e) {}
})();
