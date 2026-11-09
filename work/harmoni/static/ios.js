/* آيفون: التشغيل بالخلفية وشاشة القفل.
   - نقول لسفاري إن الصوت «تشغيل موسيقى» (يكمل مع زر الصامت وبالخلفية) — iOS 16.4 وأحدث.
   - لو فاتح الموقع من سفاري عادي (مو من أيقونة الشاشة الرئيسية) نطلع شريط صغير يشرح كيف يضيفه. */
(function () {
  "use strict";
  var ua = navigator.userAgent || "";
  var ios = /iP(hone|ad|od)/.test(ua) || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);
  if (!ios) return;
  function L(ar, en) { return (typeof settings !== "undefined" && settings.lang === "en") ? en : ar; }
  try { if (navigator.audioSession) navigator.audioSession.type = "playback"; } catch (e) {}
  document.documentElement.classList.add("hm-ios");

  var standalone = window.navigator.standalone === true ||
    (window.matchMedia && window.matchMedia("(display-mode: standalone)").matches);
  if (standalone) { document.documentElement.classList.add("hm-pwa"); return; }
  var KEY = "hm_ios_tip";
  try { if (localStorage.getItem(KEY)) return; } catch (e) {}
  function show() {
    if (document.getElementById("ios-tip")) return;
    var d = document.createElement("div");
    d.id = "ios-tip";
    d.setAttribute("role", "dialog");
    d.innerHTML = '<div class="ios-tip-t"></div><div class="ios-tip-d"></div><button type="button" class="ios-tip-x" aria-label="close">✕</button>';
    d.querySelector(".ios-tip-t").textContent = L("خل الأغاني تكمل والجوال مقفل", "Keep music playing with the screen locked");
    d.querySelector(".ios-tip-d").textContent = L(
      "اضغط زر المشاركة ⬆️ تحت، بعدين «إضافة إلى الشاشة الرئيسية». افتح هارموني من الأيقونة الجديدة دايماً.",
      "Tap the Share button ⬆️ below, then “Add to Home Screen”. Always open Harmoni from that icon.");
    d.querySelector(".ios-tip-x").onclick = function () {
      d.remove();
      try { localStorage.setItem(KEY, "1"); } catch (e) {}
    };
    document.body.appendChild(d);
  }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", function () { setTimeout(show, 1500); });
  else setTimeout(show, 1500);
})();
