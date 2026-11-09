/* تشخيص: لو صار خطأ JavaScript وقت فتح البرنامج (والواجهة ما اكتملت) نطلّع صندوق صغير فيه الخطأ
   ونرسله لسجل البرنامج (harmoni-perf.log) — عشان نعرف وش اللي خرب بدل «ما أقدر أضغط شي». */
(function () {
  "use strict";
  var errs = [];
  function add(msg) {
    msg = String(msg || "").slice(0, 400);
    if (!msg || errs.indexOf(msg) >= 0 || errs.length > 8) return;
    errs.push(msg);
    try {
      fetch("/api/client_error", { method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ msg: msg, ua: navigator.userAgent.slice(0, 160) }) }).catch(function () {});
    } catch (e) {}
    if (window.__harmoniReady) return;
    show();
  }
  function show() {
    if (!document.body) { document.addEventListener("DOMContentLoaded", show); return; }
    var b = document.getElementById("hm-errbox");
    if (!b) {
      b = document.createElement("div");
      b.id = "hm-errbox";
      b.style.cssText = "position:fixed;left:12px;right:12px;bottom:12px;z-index:2147483647;background:#3a0d12;color:#fff;" +
        "border:1px solid #ff5c6c;border-radius:10px;padding:10px 12px;font:12px/1.5 monospace;direction:ltr;text-align:left;" +
        "max-height:40vh;overflow:auto;white-space:pre-wrap;user-select:text;-webkit-user-select:text";
      document.body.appendChild(b);
    }
    b.textContent = "Harmoni error (send a screenshot of this):\n" + errs.join("\n");
  }
  window.addEventListener("error", function (e) {
    var t = e.target;
    if (t && (t.tagName === "AUDIO" || t.tagName === "VIDEO")) {   // الصوت ما اشتغل (رابط خربان/انقطع)
      try {
        fetch("/api/client_error", { method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ msg: "media error id=" + (t.id || "") + " code=" + (t.error ? t.error.code : "?") + " " +
            String(t.error && t.error.message || "").slice(0, 100) + " src=" + String(t.currentSrc || "").replace(location.origin, "").slice(0, 80) }) }).catch(function () {});
      } catch (x) {}
      return;
    }
    if (t && t !== window && (t.tagName === "SCRIPT" || t.tagName === "LINK")) {   // ملف ما انحمّل (سكربت/ستايل)
      add("failed to load " + String(t.src || t.href).replace(location.origin, ""));
      return;
    }
    add((e.message || "error") + (e.filename ? "  @ " + e.filename.replace(location.origin, "") + ":" + e.lineno + ":" + e.colno : ""));
  }, true);
  setTimeout(function () { if (!window.__harmoniReady) { add("Harmoni did not finish loading"); show(); } }, 8000);
  // تشغيل صوت انرفض (مثلاً المتصفح منع التشغيل التلقائي للأغنية الجاية) — نسجله بس بدون صندوق
  try {
    var origPlay = HTMLMediaElement.prototype.play;
    HTMLMediaElement.prototype.play = function () {
      var r = origPlay.apply(this, arguments), el = this;
      if (r && r.catch) r.catch(function (e) {
        if (!e || e.name === "AbortError") return;
        try {
          fetch("/api/client_error", { method: "POST", headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ msg: "play() " + e.name + ": " + String(e.message || "").slice(0, 120) + " id=" + (el.id || "") +
              " rs=" + el.readyState + " ns=" + el.networkState, ua: navigator.userAgent.slice(0, 160) }) }).catch(function () {});
        } catch (x) {}
      });
      return r;
    };
  } catch (e) {}
  window.addEventListener("unhandledrejection", function (e) {
    var r = e.reason;
    add("promise: " + (r && (r.stack || r.message) || r));
  });
})();
