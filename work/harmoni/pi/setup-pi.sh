#!/usr/bin/env bash
# Harmoni على الرازبيري — يشغّل الموقع (harmoni.click) 24/7 بدون الكمبيوتر.
# الاستخدام (داخل مجلد harmoni-pi بعد فك الضغط):   sudo bash setup-pi.sh
set -euo pipefail

say()  { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }
warn() { printf '\033[1;33m[!] %s\033[0m\n' "$*"; }
die()  { printf '\033[1;31m[x] %s\033[0m\n' "$*"; exit 1; }

[ "$(id -u)" = "0" ] || die "شغّله بـ sudo:  sudo bash setup-pi.sh"
PKG="$(cd "$(dirname "$0")" && pwd)"
U="${SUDO_USER:-pi}"
HOMEU="$(getent passwd "$U" | cut -d: -f6)"
APP="$HOMEU/harmoni-app"
DATA="$HOMEU/harmoni-data"
ARCH="$(uname -m)"
[ -f "$PKG/harmoni/server.py" ] || die "ما لقيت harmoni/server.py جنب السكربت — تأكد إنك فكيت الملف كامل."

say "الجهاز: $ARCH — المستخدم: $U"
MEM=$(awk '/MemTotal/{print int($2/1024)}' /proc/meminfo)
echo "    الرام: ${MEM}MB"

# ---------- 1) حزم النظام ----------
say "تثبيت حزم النظام (python3, venv, ffmpeg...)"
apt-get update -y
DEBIAN_FRONTEND=noninteractive apt-get install -y python3 python3-venv python3-pip curl ca-certificates xz-utils ffmpeg

PYV=$(python3 -c 'import sys;print("%d.%d"%sys.version_info[:2])')
python3 -c 'import sys;sys.exit(0 if sys.version_info>=(3,10) else 1)' \
  || die "بايثون $PYV قديم — yt-dlp يحتاج 3.10 أو أحدث. حدّث نظام الرازبيري (Raspberry Pi OS Bookworm) وأعد التشغيل."

# على الأجهزة اللي رامها قليل نزود الـ swap عشان التثبيت ما يعلق
if [ "$MEM" -lt 900 ] && [ -f /etc/dphys-swapfile ]; then
  if grep -q '^CONF_SWAPSIZE=\(100\|200\|512\)$' /etc/dphys-swapfile; then
    say "زيادة الـ swap إلى 1GB (الرام قليل)"
    sed -i 's/^CONF_SWAPSIZE=.*/CONF_SWAPSIZE=1024/' /etc/dphys-swapfile
    systemctl restart dphys-swapfile || true
  fi
fi

# ---------- 2) Node.js (يحتاجه yt-dlp لفك حماية يوتيوب) ----------
need_node=1
if command -v node >/dev/null 2>&1; then
  NV=$(node -p 'process.versions.node.split(".")[0]' 2>/dev/null || echo 0)
  [ "$NV" -ge 20 ] && need_node=0
fi
if [ $need_node = 1 ]; then
  say "تثبيت Node.js 22"
  case "$ARCH" in
    aarch64|arm64) NODE_ARCH=linux-arm64;  NODE_BASE=https://nodejs.org/dist ;;
    armv7l)        NODE_ARCH=linux-armv7l; NODE_BASE=https://nodejs.org/dist ;;
    armv6l)        NODE_ARCH=linux-armv6l; NODE_BASE=https://unofficial-builds.nodejs.org/download/release ;;
    x86_64)        NODE_ARCH=linux-x64;    NODE_BASE=https://nodejs.org/dist ;;
    *) die "معمارية غير معروفة: $ARCH" ;;
  esac
  NODE_VER=$(curl -fsSL "$NODE_BASE/index.json" | python3 -c '
import json,sys
want=sys.argv[1]
for r in json.load(sys.stdin):
    if r["version"].startswith("v22.") and want in r.get("files",[]):
        print(r["version"]); break' "$NODE_ARCH")
  [ -n "$NODE_VER" ] || die "ما قدرت أحدد نسخة Node المناسبة لـ $NODE_ARCH"
  TMPN=$(mktemp -d)
  curl -fL --retry 3 -o "$TMPN/node.tar.xz" "$NODE_BASE/$NODE_VER/node-$NODE_VER-$NODE_ARCH.tar.xz"
  rm -rf /opt/node && mkdir -p /opt/node
  tar -xJf "$TMPN/node.tar.xz" -C /opt/node --strip-components=1
  ln -sf /opt/node/bin/node /usr/local/bin/node
  rm -rf "$TMPN"
fi
node --version || warn "Node ما اشتغل — بعض أغاني يوتيوب ممكن ما تشتغل."

# ---------- 3) cloudflared ----------
if ! command -v cloudflared >/dev/null 2>&1; then
  say "تثبيت cloudflared"
  case "$ARCH" in
    aarch64|arm64) CF=cloudflared-linux-arm64 ;;
    armv7l|armv6l) CF=cloudflared-linux-arm ;;
    x86_64)        CF=cloudflared-linux-amd64 ;;
  esac
  curl -fL --retry 3 -o /usr/local/bin/cloudflared "https://github.com/cloudflare/cloudflared/releases/latest/download/$CF"
  chmod +x /usr/local/bin/cloudflared
fi
cloudflared --version || die "cloudflared ما يشتغل على هالجهاز ($ARCH)."

# ---------- 4) ملفات البرنامج + البيانات ----------
say "نسخ ملفات Harmoni إلى $APP"
systemctl stop harmoni 2>/dev/null || true
mkdir -p "$APP" "$DATA"
cp -a "$PKG/harmoni/." "$APP/"
if [ -d "$PKG/data" ]; then
  if [ -f "$DATA/profiles.json" ] && [ "${FORCE_DATA:-0}" != "1" ]; then
    warn "فيه بيانات قديمة في $DATA — ما بكتب فوقها. (لو تبي تستبدلها: sudo FORCE_DATA=1 bash setup-pi.sh)"
  else
    cp -a "$PKG/data/." "$DATA/"
  fi
fi
chown -R "$U:$U" "$APP" "$DATA"

say "بيئة بايثون + المكتبات (على Pi 3/Zero ممكن تاخذ 10-20 دقيقة)"
sudo -u "$U" python3 -m venv "$APP/.venv"
PIP="$APP/.venv/bin/pip"
sudo -u "$U" "$PIP" install --upgrade pip wheel
sudo -u "$U" "$PIP" install flask flask-socketio requests
sudo -u "$U" "$PIP" install -U yt-dlp yt-dlp-ejs
# إضافات yt-dlp — اختيارية، لو وحدة فشلت ما توقف التثبيت
for p in brotli mutagen pycryptodomex websockets certifi; do
  sudo -u "$U" "$PIP" install "$p" || warn "تخطيت $p"
done

# ---------- 5) النفق ----------
say "إعداد النفق (cloudflared)"
CFG_SRC="$PKG/cloudflared/config.yml"
[ -f "$CFG_SRC" ] || die "ما لقيت cloudflared/config.yml في الحزمة."
mkdir -p /etc/cloudflared
cp "$PKG"/cloudflared/*.json /etc/cloudflared/ 2>/dev/null || die "ما لقيت ملف بيانات النفق (*.json) في الحزمة."
CRED=$(grep -E '^\s*credentials-file:' "$CFG_SRC" | sed -E 's/.*[\\\/]//' | tr -d '\r"\047 ' || true)
[ -n "$CRED" ] && [ -f "/etc/cloudflared/$CRED" ] || CRED=$(cd /etc/cloudflared && ls *.json | head -1 || true)
TID=$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["TunnelID"])' "/etc/cloudflared/$CRED")
# نفس الإعداد بس بمسار الرازبيري
sed -E "s#^(\s*credentials-file:).*#\1 /etc/cloudflared/$CRED#; s/\r$//" "$CFG_SRC" > /etc/cloudflared/config.yml
grep -q '^\s*credentials-file:' /etc/cloudflared/config.yml || sed -i "1i credentials-file: /etc/cloudflared/$CRED" /etc/cloudflared/config.yml
chmod 600 /etc/cloudflared/*.json
HOSTS=$(grep -oE 'hostname:\s*\S+' /etc/cloudflared/config.yml | awk '{print $2}' | tr -d '"\r' || true)
PUB_HOST=$(echo "$HOSTS" | grep '^music\.' | head -1 || true)
[ -n "$PUB_HOST" ] || PUB_HOST=$(echo "$HOSTS" | head -1 || true)
echo "    النفق: $TID"
echo "    الروابط: $(echo $HOSTS)"

# ---------- 6) خدمات systemd (تشتغل تلقائي مع تشغيل الجهاز) ----------
say "إنشاء الخدمات"
cat > /etc/systemd/system/harmoni.service <<EOF
[Unit]
Description=Harmoni music server
After=network-online.target
Wants=network-online.target

[Service]
User=$U
WorkingDirectory=$APP
Environment=MUSIC_HEADLESS=1
Environment=HARMONI_DATA_DIR=$DATA
Environment=HARMONI_PUBLIC_URL=https://$PUB_HOST
Environment=PYTHONUNBUFFERED=1
Environment=PATH=/usr/local/bin:/usr/bin:/bin
ExecStart=$APP/.venv/bin/python $APP/server.py
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

cat > /etc/systemd/system/harmoni-tunnel.service <<EOF
[Unit]
Description=Harmoni Cloudflare tunnel
After=network-online.target harmoni.service
Wants=network-online.target

[Service]
ExecStart=/usr/local/bin/cloudflared --no-autoupdate --config /etc/cloudflared/config.yml tunnel run $TID
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

# تحديث yt-dlp كل يوم الساعة 5 الفجر (يوتيوب يغير أشياء كثير)
cat > /etc/cron.d/harmoni-ytdlp <<EOF
0 5 * * * $U $APP/.venv/bin/pip install -q -U yt-dlp yt-dlp-ejs && sudo /bin/systemctl restart harmoni
EOF
echo "$U ALL=(root) NOPASSWD: /bin/systemctl restart harmoni" > /etc/sudoers.d/harmoni
chmod 440 /etc/sudoers.d/harmoni

systemctl daemon-reload
systemctl enable --now harmoni.service
sleep 6
systemctl enable --now harmoni-tunnel.service

# ---------- 7) فحص ----------
say "فحص"
ok=0
for i in $(seq 1 30); do
  if curl -fsS -o /dev/null http://127.0.0.1:5000/; then ok=1; break; fi
  sleep 2
done
[ $ok = 1 ] && echo "    السيرفر شغال ✓" || { warn "السيرفر ما رد — شوف:  journalctl -u harmoni -n 50"; }
sleep 4
systemctl is-active --quiet harmoni-tunnel && echo "    النفق شغال ✓" || warn "النفق ما اشتغل — شوف:  journalctl -u harmoni-tunnel -n 50"

cat <<EOF

=====================================================
  خلصنا ✓  الموقع الحين يشتغل من الرازبيري:
     https://$PUB_HOST
  الخطوة الأخيرة: على الكمبيوتر شغّل  pc-tunnel-off.bat
  عشان الكمبيوتر يوقف يشارك بالنفق.

  أوامر مفيدة:
     sudo systemctl restart harmoni     إعادة تشغيل
     journalctl -u harmoni -f           السجل المباشر
     sudo systemctl status harmoni-tunnel
=====================================================
EOF
