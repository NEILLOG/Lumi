# Lumi 瀏覽器 POC（M18）

驗證 Playwright 驅動 Edge／Chrome 的關鍵假設（`plan/18-browser.md`）。**不在 `Lumi.sln` 內。** 它會在暫存資料夾建一個全新的瀏覽器設定檔，不會碰你平常的瀏覽器資料，跑完就刪除。

## 執行

需要 .NET SDK 10 與已安裝的 Edge 或 Chrome。第一次執行會還原 Playwright 套件（約 210 MB，公司網路請確認 NuGet 可用）。

```
cd poc/Lumi.BrowserPoc
dotnet run -- edge                       # 背景執行
dotnet run -- edge --visible             # 顯示視窗（會多測「背景 ↔ 視窗」重啟）
dotnet run -- edge --pac http://公司/proxy.pac
dotnet run -- edge --intranet https://公司內部網站/
dotnet run -- chrome                     # 或 chrome、或瀏覽器的完整路徑
```

## 在公司的 Windows＋Edge 請這樣跑

1. `dotnet run -- edge`（背景）
2. `dotnet run -- edge --visible`
3. `dotnet run -- edge --intranet https://需要 Windows 登入的內部網站/`（用系統 Proxy 設定，驗證整合驗證）
4. 有 PAC 網址的話：`dotnet run -- edge --pac http://…/proxy.pac`

把每次的完整輸出貼回來。每行是 `PASS`／`FAIL`／`SKIP`；有任何 `FAIL` 先回報，不要往下測。

## 各項在驗證什麼

| 項目 | 對應計畫 |
|---|---|
| 啟動持久設定檔失敗 | D15：公司的瀏覽器原則可能禁止自動化 |
| 快照有 ref、iframe 內也有 | D4 |
| aria-ref 點擊與輸入、辨識密碼欄位 | D4、D7 |
| 對話框、新分頁、下載、截圖 | D11、D12、D14 |
| 關閉後重開 Cookie 還在 | D2 |
| 背景 ↔ 視窗重啟 Cookie 還在 | D9 |
| http PAC 會導到指定 Proxy／你的 PAC／內部網站 | D8 |

## 結果記錄

（填寫日期、機器、瀏覽器版本、輸出）
