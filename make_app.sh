#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"

swift build -c release
BIN="$(swift build -c release --show-bin-path)/AIUsageBar"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
STAGE="$TMP/AI Usage.app"

mkdir -p "$STAGE/Contents/MacOS"
cp "$BIN" "$STAGE/Contents/MacOS/AIUsageBar"
cat > "$STAGE/Contents/Info.plist" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>AI Usage</string>
  <key>CFBundleIdentifier</key><string>com.chaelimi.aiusagebar</string>
  <key>CFBundleExecutable</key><string>AIUsageBar</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>LSUIElement</key><true/>
</dict></plist>
EOF

plutil -lint "$STAGE/Contents/Info.plist"
# 비시스템 dylib 의존 확인 (작업공간 경로·@rpath 사용자 lib 있으면 배포 불가)
if otool -L "$STAGE/Contents/MacOS/AIUsageBar" | tail -n +2 | grep -vE '^\s+(/usr/lib|/System)'; then
    echo "ERROR: 비시스템 dylib 의존 발견" >&2; exit 1
fi
codesign --force --sign - "$STAGE"
codesign --verify --strict "$STAGE"

# 기존 검증본 백업→교체 (실패 시 이전본 보존)
mkdir -p dist
if [ -d "dist/AI Usage.app" ]; then
    rm -rf "dist/AI Usage.app.bak"
    mv "dist/AI Usage.app" "dist/AI Usage.app.bak"
fi
mv "$STAGE" "dist/AI Usage.app"
rm -rf "dist/AI Usage.app.bak"
echo "OK: dist/AI Usage.app"
