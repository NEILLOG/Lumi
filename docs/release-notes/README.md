# 發佈說明

使用者在 Lumi 裡看到的「更新內容」就是 GitHub Release 的說明文字（Markdown）。

發版前在這個資料夾新增 `v{版本}.md`（例如 `v1.1.0.md`），`release.yml` 建立 Release 時會用它當說明；
沒有這個檔案就退回 GitHub 自動產生的 PR／commit 清單（英文、對一般使用者不好讀）。

寫給一般同事看：用繁體中文、一句話講「對你有什麼好處」，不要寫程式細節。範例：

```markdown
## 新功能
- 可以設定開機後自動開啟 Lumi（設定 › 外觀與操作 › 啟動與更新）
- 工作台右上角多了圖釘，按一下就能讓 Lumi 浮在其他視窗上方

## 修正
- 修正某某情況下回答會被截斷的問題
```

提醒視窗只顯示前約 6000 字，其餘請使用者到 Release 頁面看。

## 發版步驟

1. 寫好 `docs/release-notes/v{版本}.md`，連同其他變更 commit 並 push 到 `main`。
2. 執行 `scripts/release.sh {版本}`（例如 `scripts/release.sh 1.1.0`）。腳本會檢查在 `main`、工作目錄乾淨、與遠端同步、tag 不重複，確認後打 tag 並推送。
3. GitHub Actions 跑測試、打包，完成後在 Releases 出現：
   - `Lumi-{版本}-win-x64.zip`（裡面是 `Lumi.exe`）
   - `Lumi-{版本}-osx-arm64.zip`（裡面是 `Lumi.app`）

版本號含 `-`（`0.0.1-test`、`1.1.0-rc1`）會標成預覽版：不會成為最新版，程式內的更新檢查也不會提醒。第一次建議先用預覽版跑一遍整條流程。
