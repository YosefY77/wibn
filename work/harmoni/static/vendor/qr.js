/* مولّد QR صغير (بايت، تصحيح M، الإصدارات 1-10) — بدون مكتبات خارجية.
   HarmoniQR.matrix(text) → مصفوفة true/false ، HarmoniQR.svg(text, px) → نص SVG */
(function (root) {
  "use strict";
  // [مجموع الكود ووردز، تصحيح لكل بلوك، [عدد بلوكات، بيانات]...]  — مستوى M
  var TABLE = {
    1: [10, [[1, 16]]], 2: [16, [[1, 28]]], 3: [26, [[1, 44]]], 4: [18, [[2, 32]]], 5: [24, [[2, 43]]],
    6: [16, [[4, 27]]], 7: [18, [[4, 31]]], 8: [22, [[2, 38], [2, 39]]], 9: [22, [[3, 36], [2, 37]]],
    10: [26, [[4, 43], [1, 44]]]
  };
  var ALIGN = { 1: [], 2: [6, 18], 3: [6, 22], 4: [6, 26], 5: [6, 30], 6: [6, 34], 7: [6, 22, 38],
                8: [6, 24, 42], 9: [6, 26, 46], 10: [6, 28, 50] };

  // ===== GF(256) =====
  var EXP = new Array(512), LOG = new Array(256);
  (function () { var x = 1; for (var i = 0; i < 255; i++) { EXP[i] = x; LOG[x] = i; x <<= 1; if (x & 0x100) x ^= 0x11d; } for (i = 255; i < 512; i++) EXP[i] = EXP[i - 255]; })();
  function mul(a, b) { return a && b ? EXP[LOG[a] + LOG[b]] : 0; }
  function rsGen(n) {
    var g = [1];
    for (var i = 0; i < n; i++) {
      var ng = new Array(g.length + 1).fill(0);
      for (var j = 0; j < g.length; j++) { ng[j] ^= g[j]; ng[j + 1] ^= mul(g[j], EXP[i]); }
      g = ng;
    }
    return g;
  }
  function rsEc(data, n) {
    var g = rsGen(n), res = data.concat(new Array(n).fill(0));
    for (var i = 0; i < data.length; i++) {
      var c = res[i];
      if (c) for (var j = 0; j < g.length; j++) res[i + j] ^= mul(g[j], c);
    }
    return res.slice(data.length);
  }

  function utf8(s) {
    var out = [];
    s = unescape(encodeURIComponent(s));
    for (var i = 0; i < s.length; i++) out.push(s.charCodeAt(i));
    return out;
  }
  function dataCap(v) { var t = TABLE[v][1], n = 0; t.forEach(function (g) { n += g[0] * g[1]; }); return n; }

  function encode(bytes, v) {
    var bits = [];
    function put(val, len) { for (var i = len - 1; i >= 0; i--) bits.push((val >>> i) & 1); }
    put(4, 4);
    put(bytes.length, v < 10 ? 8 : 16);
    bytes.forEach(function (b) { put(b, 8); });
    var cap = dataCap(v) * 8;
    for (var t = 0; t < 4 && bits.length < cap; t++) bits.push(0);
    while (bits.length % 8) bits.push(0);
    var cw = [];
    for (var i = 0; i < bits.length; i += 8) { var b = 0; for (var k = 0; k < 8; k++) b = (b << 1) | bits[i + k]; cw.push(b); }
    for (var p = 0; cw.length < dataCap(v); p++) cw.push(p % 2 ? 0x11 : 0xec);
    // بلوكات + تصحيح، وبعدين ندمجها
    var ecn = TABLE[v][0], blocks = [], off = 0;
    TABLE[v][1].forEach(function (g) {
      for (var n = 0; n < g[0]; n++) { var d = cw.slice(off, off + g[1]); off += g[1]; blocks.push({ d: d, e: rsEc(d, ecn) }); }
    });
    var out = [], maxD = Math.max.apply(null, blocks.map(function (b) { return b.d.length; }));
    for (i = 0; i < maxD; i++) blocks.forEach(function (b) { if (i < b.d.length) out.push(b.d[i]); });
    for (i = 0; i < ecn; i++) blocks.forEach(function (b) { out.push(b.e[i]); });
    return out;
  }

  function bch(data, poly, polyLen) {
    var d = data << (polyLen - 1);
    for (var i = 31; i >= polyLen - 1; i--) if ((d >>> i) & 1) d ^= poly << (i - (polyLen - 1));
    return (data << (polyLen - 1)) | d;
  }
  var MASKS = [
    function (r, c) { return (r + c) % 2 === 0; }, function (r) { return r % 2 === 0; },
    function (r, c) { return c % 3 === 0; }, function (r, c) { return (r + c) % 3 === 0; },
    function (r, c) { return (Math.floor(r / 2) + Math.floor(c / 3)) % 2 === 0; },
    function (r, c) { return (r * c) % 2 + (r * c) % 3 === 0; },
    function (r, c) { return ((r * c) % 2 + (r * c) % 3) % 2 === 0; },
    function (r, c) { return ((r + c) % 2 + (r * c) % 3) % 2 === 0; }
  ];

  function build(v, codewords, mask) {
    var n = 17 + 4 * v, m = [], fn = [];
    for (var i = 0; i < n; i++) { m.push(new Array(n).fill(false)); fn.push(new Array(n).fill(false)); }
    function set(r, c, val) { m[r][c] = !!val; fn[r][c] = true; }
    function finder(r0, c0) {
      for (var r = -1; r <= 7; r++) for (var c = -1; c <= 7; c++) {
        var rr = r0 + r, cc = c0 + c;
        if (rr < 0 || cc < 0 || rr >= n || cc >= n) continue;
        var on = (r >= 0 && r <= 6 && (c === 0 || c === 6)) || (c >= 0 && c <= 6 && (r === 0 || r === 6)) || (r >= 2 && r <= 4 && c >= 2 && c <= 4);
        set(rr, cc, on);
      }
    }
    finder(0, 0); finder(0, n - 7); finder(n - 7, 0);
    for (i = 8; i < n - 8; i++) { set(6, i, i % 2 === 0); set(i, 6, i % 2 === 0); }
    var al = ALIGN[v];
    al.forEach(function (r) { al.forEach(function (c) {
      var last = al[al.length - 1], first = al[0];
      if ((r === first && c === first) || (r === first && c === last) || (r === last && c === first)) return;
      for (var dr = -2; dr <= 2; dr++) for (var dc = -2; dc <= 2; dc++) set(r + dr, c + dc, Math.max(Math.abs(dr), Math.abs(dc)) !== 1);
    }); });
    // أماكن معلومات الصيغة (نحجزها) + النقطة الثابتة
    var fmt = bch((0 << 3) | mask, 0x537, 11) ^ 0x5412;   // M = 00
    for (i = 0; i <= 5; i++) set(i, 8, (fmt >>> i) & 1);
    set(7, 8, (fmt >>> 6) & 1); set(8, 8, (fmt >>> 7) & 1); set(8, 7, (fmt >>> 8) & 1);
    for (i = 9; i < 15; i++) set(8, 14 - i, (fmt >>> i) & 1);
    for (i = 0; i < 8; i++) set(8, n - 1 - i, (fmt >>> i) & 1);
    for (i = 8; i < 15; i++) set(n - 15 + i, 8, (fmt >>> i) & 1);
    set(n - 8, 8, 1);
    if (v >= 7) {
      var vi = bch(v, 0x1f25, 13);
      for (i = 0; i < 18; i++) { var bit = (vi >>> i) & 1, a = n - 11 + (i % 3), b = Math.floor(i / 3); set(a, b, bit); set(b, a, bit); }
    }
    // البيانات (زقزاق من تحت يمين)
    var bitsArr = [];
    codewords.forEach(function (cw) { for (var k = 7; k >= 0; k--) bitsArr.push((cw >>> k) & 1); });
    var idx = 0;
    for (var right = n - 1; right >= 1; right -= 2) {
      if (right === 6) right = 5;
      for (var vert = 0; vert < n; vert++) {
        for (var j = 0; j < 2; j++) {
          var c = right - j, up = ((right + 1) & 2) === 0, r = up ? n - 1 - vert : vert;
          if (fn[r][c]) continue;
          var val = idx < bitsArr.length ? bitsArr[idx] : 0;
          idx++;
          m[r][c] = !!val !== MASKS[mask](r, c);
        }
      }
    }
    return m;
  }

  function penalty(m) {
    var n = m.length, p = 0, r, c, k;
    for (r = 0; r < n; r++) {
      for (var dir = 0; dir < 2; dir++) {
        var run = 1;
        for (c = 1; c < n; c++) {
          var a = dir ? m[c][r] : m[r][c], b = dir ? m[c - 1][r] : m[r][c - 1];
          if (a === b) { run++; if (run === 5) p += 3; else if (run > 5) p++; } else run = 1;
        }
      }
    }
    for (r = 0; r < n - 1; r++) for (c = 0; c < n - 1; c++) {
      var x = m[r][c];
      if (x === m[r][c + 1] && x === m[r + 1][c] && x === m[r + 1][c + 1]) p += 3;
    }
    var pat = [true, false, true, true, true, false, true, false, false, false, false];
    for (r = 0; r < n; r++) for (c = 0; c + 11 <= n; c++) {
      var h1 = true, h2 = true, v1 = true, v2 = true;
      for (k = 0; k < 11; k++) {
        if (m[r][c + k] !== pat[k]) h1 = false;
        if (m[r][c + k] !== pat[10 - k]) h2 = false;
        if (m[c + k][r] !== pat[k]) v1 = false;
        if (m[c + k][r] !== pat[10 - k]) v2 = false;
      }
      p += 40 * (h1 + h2 + v1 + v2);
    }
    var dark = 0;
    for (r = 0; r < n; r++) for (c = 0; c < n; c++) if (m[r][c]) dark++;
    p += Math.floor(Math.abs(dark * 20 - n * n * 10) / (n * n)) * 10;
    return p;
  }

  function matrix(text, forceMask) {
    var bytes = utf8(String(text));
    var v = 1;
    while (v <= 10 && bytes.length + (v < 10 ? 2 : 3) > dataCap(v)) v++;
    if (v > 10) throw new Error("QR: النص طويل");
    var cw = encode(bytes, v);
    if (forceMask != null) return build(v, cw, forceMask);
    var best = null, bestP = Infinity;
    for (var mask = 0; mask < 8; mask++) {
      var m = build(v, cw, mask), p = penalty(m);
      if (p < bestP) { bestP = p; best = m; }
    }
    return best;
  }

  function svg(text, px) {
    var m = matrix(text), n = m.length, q = 4, size = n + q * 2, d = "";
    for (var r = 0; r < n; r++) for (var c = 0; c < n; c++) if (m[r][c]) d += "M" + (c + q) + " " + (r + q) + "h1v1h-1z";
    return '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ' + size + " " + size + '" width="' + (px || 220) + '" height="' + (px || 220) +
      '" shape-rendering="crispEdges"><rect width="100%" height="100%" fill="#fff"/><path d="' + d + '" fill="#000"/></svg>';
  }

  var api = { matrix: matrix, svg: svg };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else root.HarmoniQR = api;
})(this);
