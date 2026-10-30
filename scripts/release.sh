#!/usr/bin/env bash
# 發版：檢查沒問題後打 tag 並推送，GitHub Actions（.github/workflows/release.yml）接手測試、打包、建立 Release。
# 用法：scripts/release.sh <版本> [--yes]
#   正式版：1.1.0　　預覽版（Release 標成 prerelease，程式內的更新檢查不會提醒）：0.0.1-test、1.1.0-rc1
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

VERSION=""
YES=0
for arg in "$@"; do
  case "$arg" in
    --yes) YES=1 ;;
    -h|--help) sed -n '2,4p' "$0"; exit 0 ;;
    -*) echo "未知參數：$arg" >&2; exit 2 ;;
    *) VERSION="$arg" ;;
  esac
done

VERSION="${VERSION#v}"
[[ -n "$VERSION" ]] || { echo "用法：$0 <版本> [--yes]" >&2; exit 2; }
if ! [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ ]]; then
  echo "版本號格式不對：${VERSION}（例：1.1.0、1.1.0-rc1）" >&2
  exit 2
fi
TAG="v$VERSION"

[[ "$(git rev-parse --abbrev-ref HEAD)" == "main" ]] || { echo "請在 main 分支上發版" >&2; exit 1; }
[[ -z "$(git status --porcelain)" ]] || { echo "工作目錄有未 commit 的變更，請先處理" >&2; exit 1; }

git fetch --quiet origin main --tags
[[ "$(git rev-parse HEAD)" == "$(git rev-parse origin/main)" ]] || { echo "本機 main 與遠端不同步（請先 push 或 pull）" >&2; exit 1; }
if git rev-parse -q --verify "refs/tags/$TAG" >/dev/null; then
  echo "tag $TAG 已經存在" >&2
  exit 1
fi

NOTES="docs/release-notes/$TAG.md"
if [[ -f "$NOTES" ]]; then
  echo "發佈說明：${NOTES}"
elif [[ "$VERSION" != *-* ]]; then
  echo "警告：沒有 ${NOTES}，Release 會用自動產生的英文清單，使用者在程式裡看到的更新內容會很難讀。"
fi

echo "即將在 $(git rev-parse --short HEAD)（$(git log -1 --format=%s)）建立並推送 tag ${TAG}。"
if [[ $YES -ne 1 ]]; then
  read -r -p "確定要發版嗎？(y/N) " answer
  [[ "$answer" == "y" || "$answer" == "Y" ]] || { echo "已取消"; exit 1; }
fi

git tag -a "$TAG" -m "Lumi $VERSION"
git push origin "$TAG"
echo "已推送 ${TAG}。到 GitHub 的 Actions 頁面看進度，完成後 Release 會出現在 Releases。"
