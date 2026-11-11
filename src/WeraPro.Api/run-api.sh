#!/bin/sh
# ─────────────────────────────────────────────────────────────────────────────
#  run-api.sh — مشغل خادم WERA PRO HD API (إنتاج Linux/macOS)
#  يعمل بجانب WeraPro.Api في مجلد النشر. عند أول تشغيل:
#    1) يولّد مفتاح توقيع JWT عشوائياً (48 محرفاً) ويحفظه محلياً في
#       ${XDG_DATA_HOME:-$HOME/.local/share}/wera-pro-hd/api-signing.key
#       — لا يُوزّع أبداً مع الحزمة؛
#    2) يشغّل الخادم على المنفذ من appsettings.json (الافتراضي 5080)؛
#    3) يمكن تمرير أي وسائط للتنفيذي (مثل --no-console).
#  لتخصيص كلمة مرور المالك عند أول إقلاع: BOOTSTRAP__OWNERPASSWORD
#  (8 أحرف ورقم على الأقل — الافتراضي Wera@2026 غيّرها بعد أول دخول).
# ─────────────────────────────────────────────────────────────────────────────
set -eu

APP_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
KEY_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/wera-pro-hd"
KEY_FILE="$KEY_DIR/api-signing.key"

mkdir -p "$KEY_DIR"

if [ ! -f "$KEY_FILE" ]; then
    echo "[run-api] توليد مفتاح توقيع جديد لأول تشغيل..."
    # urandom → 48 محرفاً من مجموعة آمنة (بلا محارف ملتبسة)
    KEY=$(head -c 512 /dev/urandom | tr -dc -- '-ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789' | head -c 48 || true)
    if [ "${#KEY}" -lt 32 ]; then
        echo "[run-api] تعذر توليد المفتاح — اضبط TOKENS__SIGNINGKEYSECRET يدوياً." >&2
        exit 1
    fi
    umask 077
    printf '%s' "$KEY" > "$KEY_FILE"
    chmod 600 "$KEY_FILE"
fi

KEY=$(cat "$KEY_FILE")
export TOKENS__SIGNINGKEYSECRET="$KEY"
: "${ASPNETCORE_URLS:=http://localhost:5080}"
export ASPNETCORE_URLS

echo "[run-api] تشغيل WeraPro.Api على $ASPNETCORE_URLS ..."
exec "$APP_DIR/WeraPro.Api" "$@"
