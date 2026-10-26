#!/usr/bin/env bash
# 發佈腳本（M20）：產出 artifacts/release/ 下的發佈檔。
# 用法：scripts/publish-release.sh [--version x.y.z] [--only win|mac]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

VERSION=""
ONLY=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) VERSION="${2:?--version 需要參數}"; shift 2 ;;
    --only) ONLY="${2:?--only 需要參數}"; shift 2 ;;
    -h|--help) sed -n '2,3p' "$0"; exit 0 ;;
    *) echo "未知參數：$1" >&2; exit 2 ;;
  esac
done

if [[ -n "$ONLY" && "$ONLY" != "win" && "$ONLY" != "mac" ]]; then
  echo "--only 只能是 win 或 mac" >&2
  exit 2
fi

if [[ -z "$VERSION" ]]; then
  TAG="$(git describe --tags --abbrev=0 2>/dev/null || true)"
  if [[ -n "$TAG" ]]; then
    VERSION="$TAG"
  else
    VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -n 1)"
  fi
fi
VERSION="${VERSION#v}"
[[ -n "$VERSION" ]] || { echo "無法取得版本號" >&2; exit 1; }

OUT="$ROOT/artifacts/release"
rm -rf "$OUT"
mkdir -p "$OUT"

echo "發佈版本：$VERSION"

if [[ -z "$ONLY" || "$ONLY" == "win" ]]; then
  echo "==> 發佈 Windows x64"
  dotnet publish src/Lumi.App -p:PublishProfile=win-x64 -p:Version="$VERSION" --nologo
  cp "artifacts/publish/win-x64/Lumi.App.exe" "$OUT/Lumi-$VERSION-win-x64.exe"
fi

if [[ -z "$ONLY" || "$ONLY" == "mac" ]]; then
  if [[ "$(uname)" == "Darwin" ]]; then
    echo "==> 發佈 macOS Apple Silicon"
    dotnet publish src/Lumi.App -p:PublishProfile=osx-arm64 -p:Version="$VERSION" --nologo
    "$ROOT/scripts/make-mac-app.sh" "$VERSION" \
      "artifacts/publish/osx-arm64/Lumi.App" \
      "src/Lumi.App/Assets/Icons/Lumi.icns" \
      "$OUT/Lumi-$VERSION-osx-arm64.zip"
  else
    echo "略過 macOS：需要在 macOS 上執行（目前是 $(uname)）"
  fi
fi

echo "==> artifacts/release/ 內容："
ls -lh "$OUT"
