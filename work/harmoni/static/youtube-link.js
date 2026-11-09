/* تنبيهات الأغاني المقيدة بالعمر في يوتيوب (بدون أي ربط حساب):
   البرنامج يشغّل النسخة الصوتية الرسمية من يوتيوب ميوزك بدالها، ولو ما لقى نسخة يقترح إضافتها من الجهاز. */
(function () {
  "use strict";
  var LOCAL = ["localhost", "127.0.0.1"].indexOf(location.hostname) >= 0;
  function L(ar, en) { return (typeof settings !== "undefined" && settings.lang === "en") ? en : ar; }
  var shown = {};

  window.harmoniYoutubeAgeHint = function (kind) {
    if (typeof toast !== "function") return;
    if (kind === "alternate") {
      if (shown.alt) return;
      shown.alt = 1;
      toast(L("الفيديو الأصلي عليه قيد عمر — شغّلنا النسخة الصوتية الرسمية بداله ✓", "The original video is age-restricted — playing the official audio version instead ✓"));
      return;
    }
    if (kind === "age") {
      toast(L("الأغنية مقيدة بالعمر من يوتيوب — شلناها وما بتطلع لك مرة ثانية", "This song is age-restricted on YouTube — it's been removed and won't show again"));
      return;
    }
    var canUpload = LOCAL && typeof window.harmoniUploadTrack === "function";
    toast(canUpload
      ? L("الأغنية عليها قيد عمر وما لقينا لها نسخة ثانية — اضغط هنا وأضفها من جهازك", "This song is age-restricted and no other version was found — click here to add it from your device")
      : L("الأغنية عليها قيد عمر من يوتيوب وما لقينا لها نسخة ثانية", "This song is age-restricted on YouTube and no other version was found"));
    var el = document.getElementById("toast");
    if (!el) return;
    try { clearTimeout(toastTimer); toastTimer = setTimeout(function () { el.classList.add("hidden"); el.onclick = null; el.classList.remove("yt-click"); }, 7000); } catch (e) {}
    if (canUpload) {
      el.classList.add("yt-click");
      el.onclick = function () {
        el.onclick = null; el.classList.remove("yt-click"); el.classList.add("hidden");
        window.harmoniUploadTrack({});
      };
    }
  };
})();
