#!/usr/bin/env bash
# 把 macOS 單檔執行檔包成 Lumi.app，ad-hoc 簽章後壓成 zip（M20）。
# 用法：scripts/make-mac-app.sh <版本> <單檔路徑> <icns 路徑> <輸出 zip 路徑>
set -euo pipefail

if [[ "$(uname)" != "Darwin" ]]; then
  echo "需要在 macOS 上執行" >&2
  exit 1
fi

if [[ $# -ne 4 ]]; then
  echo "用法：$0 <版本> <單檔路徑> <icns 路徑> <輸出 zip 路徑>" >&2
  exit 2
fi

VERSION="$1"
EXE="$2"
ICNS="$3"
OUT_ZIP="$4"

[[ -f "$EXE" ]] || { echo "找不到單檔執行檔：$EXE" >&2; exit 1; }
[[ -f "$ICNS" ]] || { echo "找不到圖示檔：$ICNS" >&2; exit 1; }

# 轉成絕對路徑（後面會切換工作目錄）
EXE="$(cd "$(dirname "$EXE")" && pwd)/$(basename "$EXE")"
ICNS="$(cd "$(dirname "$ICNS")" && pwd)/$(basename "$ICNS")"
OUT_ZIP="$(mkdir -p "$(dirname "$OUT_ZIP")" && cd "$(dirname "$OUT_ZIP")" && pwd)/$(basename "$OUT_ZIP")"

# CFBundleVersion 只取數字版本（去掉 -rc1 之類的預覽字尾）
BUNDLE_VERSION="${VERSION%%-*}"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

APP="$WORK/Lumi.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

cp "$EXE" "$APP/Contents/MacOS/Lumi"
chmod +x "$APP/Contents/MacOS/Lumi"
cp "$ICNS" "$APP/Contents/Resources/Lumi.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>Lumi</string>
    <key>CFBundleDisplayName</key>
    <string>Lumi</string>
    <key>CFBundleIdentifier</key>
    <string>io.github.neillog.lumi</string>
    <key>CFBundleExecutable</key>
    <string>Lumi</string>
    <key>CFBundleIconFile</key>
    <string>Lumi</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>${BUNDLE_VERSION}</string>
    <key>CFBundleVersion</key>
    <string>${BUNDLE_VERSION}</string>
    <key>LSMinimumSystemVersion</key>
    <string>13.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.productivity</string>
    <key>NSMicrophoneUsageDescription</key>
    <string>Lumi 需要使用麥克風，才能讓你用語音輸入訊息。</string>
</dict>
</plist>
PLIST

codesign --force --deep --sign - "$APP"

rm -f "$OUT_ZIP"
ditto -c -k --sequesterRsrc --keepParent "$APP" "$OUT_ZIP"

echo "已產出：${OUT_ZIP}（$(du -h "$OUT_ZIP" | cut -f1)）"
