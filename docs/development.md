# Lumi 開發者文件

這份收錄原本放在 README 的開發資訊。介紹與下載請看 [README](../README.md)。

程式註解裡提到的 `agent.md`、`plan/`、`docs/ui-spec.md` 是另外維護的內部工程文件，不在這個 repo。

## 維護提醒：兩個需要常改的位置（用量與費用）

| 要做的事 | 檔案 | 說明 |
|---|---|---|
| **增減地端模型名單** | [`src/Lumi.Core/Usage/LocalModelList.cs`](../src/Lumi.Core/Usage/LocalModelList.cs)（`Keywords` 陣列） | model ID 符合名單（qwen、llama、gemma、gpt-oss…）就視為地端，在模型設定自動勾選「不計費」，費用算 0。公司 AI Gateway 後面新增了開源模型家族，或有雲端服務被誤判成地端（例如 mistral、deepseek、glm 的雲端版），改這裡。 |
| **LiteLLM 價目表（模型單價）** | [`src/Lumi.Web/Pricing/litellm-model-prices.json`](../src/Lumi.Web/Pricing/litellm-model-prices.json)＋同資料夾的 `litellm-model-prices.date.txt` | 打包進 App 的內嵌快照（約 3 MB），App 啟動後不會自己上網查。使用者在設定按「更新價目表」才會下載新版存到使用者資料夾（`model-prices.json`），之後優先讀那份。發新版前想更新內建快照：重新下載 [LiteLLM 的 `model_prices_and_context_window.json`](https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json) 覆蓋這個檔案，並把今天的日期寫進 `.date.txt`。 |

## 專案結構

```
docs/
  release-notes/            每一版的發佈說明（使用者在程式裡看到的更新內容）
  sprite-dev-guide.md       小精靈角色開發指南
src/
  Lumi.Core/                資料模型、核心介面、對話迴圈（不依賴 UI）
    Network/                系統網路狀態（只在失敗後補說明文字）
  Lumi.Providers/           Anthropic（官方 SDK）、OpenAI 相容（M2）
  Lumi.Infrastructure/      SQLite（對話與訊息）、設定檔
    Logging/                寫進使用者資料夾 logs/ 的記錄檔
    Startup/                啟動失敗的原因分類（資料庫被鎖、權限、磁碟、較新 schema、設定檔）
  Lumi.Platform/            原生 API、金鑰安全儲存（DPAPI、Keychain）、麥克風錄音（waveIn、AudioQueue）
  Lumi.Speech/              Whisper 代理客戶端（上傳、回應解析、錯誤對應、測試連線）
  Lumi.Tools/               內建工具（時間、計算、範圍內檔案讀寫、筆記、提醒與計時器、文件讀取）
  Lumi.Documents/          讀取 Word、Excel、PowerPoint、PDF 的文字（OpenXml、PdfPig；只透過 Core 的 IDocumentReader 對外）
  Lumi.Browser/            瀏覽器自動化（Playwright 只在這裡引用）：偵測 Edge／Chrome、啟動參數、瀏覽器程序與分頁管理
  Lumi.Web/                Proxy（含 PAC）、網路工具的 HTTP 用戶端、HTML 轉 Markdown、RSS、網路診斷（解析套件只在這裡引用）
  Lumi.Mcp/                MCP 伺服器的連線與工具 Adapter（官方 SDK 只在這裡引用；只透過 Core 的 IMcpToolSource 對外）
  Lumi.App/                 Avalonia 介面與 DI 組裝（唯一的 composition root）
tests/
  Lumi.Core.Tests/
  Lumi.Platform.Tests/      金鑰儲存的契約測試（記憶體、DPAPI、Keychain）
  Lumi.Providers.Tests/     Anthropic Provider 的 fixture 測試（不呼叫真實 API）
  Lumi.Speech.Tests/        Whisper 客戶端與解析器（假的 HttpMessageHandler，不呼叫真實服務）
  Lumi.Infrastructure.Tests/ SQLite 實作（暫存資料夾的真資料庫檔）
  Lumi.Tools.Tests/         工具與運算式解析器（含惡意輸入、路徑逃逸）
  Lumi.Documents.Tests/    四種文件格式的讀取（測試檔由程式產生，不提交二進位檔）
  Lumi.Browser.Tests/      偵測與啟動參數的單元測試；真瀏覽器測試需要 LUMI_BROWSER_TESTS=1（本機 HTTP 站，不連真實網站）
  Lumi.Web.Tests/          Proxy／PAC、HTTP 用戶端、內容轉換（本機 TcpListener 與假的 HttpMessageHandler，不連真實網站）
  Lumi.Mcp.Tests/          MCP 管理員、工具 Adapter、結果格式（用假連線與記憶體內的 SDK 伺服器）
  Lumi.App.Tests/           ViewModel、Markdown 解析與渲染（含 Avalonia.Headless）、EmojiText、字串資源
  Lumi.Architecture.Tests/  相依規則、色碼檢查、P/Invoke 與供應商 SDK 的歸屬
poc/
  Lumi.OrbPoc/              光球 POC（驗證小精靈視窗的可行性，不在 Lumi.sln 內）
  Lumi.EmojiPoc/            Emoji 驗證頁（Windows 國旗判定，不在 Lumi.sln 內）
  Lumi.BrowserPoc/          瀏覽器 POC（Playwright 驅動 Edge／Chrome 的關鍵假設，不在 Lumi.sln 內；公司 Windows＋Edge 的驗證步驟見 README）
```

## 執行工作台

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)。塔羅牌的核心庫是 git submodule（`external/TarotMcp`），取得原始碼要用 `git clone --recurse-submodules`，或 clone 後執行 `git submodule update --init`；少了它建置會找不到 `TarotMcp.Core.csproj`。

```bash
dotnet build            # 零警告
dotnet test
dotnet run --project src/Lumi.App
```

## 試跑光球 POC

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)。

```bash
cd poc/Lumi.OrbPoc
dotnet run -c Release
```

操作方式：
- 游標靠近時睜眼，並浮出輸入列
- 點一下切換示範狀態
- 長按可以「戳」，拖曳可以移動
- 右鍵開啟選單（大小、眼睛、診斷 HUD 等）

完整的驗證清單見 `poc/Lumi.OrbPoc/README.md`。

## 已知限制

- **Windows 國旗 emoji 待驗證**：Segoe UI Emoji 沒有國旗字形，預期會顯示成兩個字母（TW、JP）。判定由 Windows 實機執行 `dotnet run --project poc/Lumi.EmojiPoc` 完成，結果填進 `poc/Lumi.EmojiPoc/README.md`；結果出來之前不附 emoji 字型。
- 離線判斷只在請求失敗後才補說明，不主動偵測網路。
- 歷史搜尋只比對標題與訊息文字（不分大小寫，英文以外的大小寫折疊不做），命中字只在清單中加粗，不捲動到內文。

## 第三方資料

- 模型價目表：[LiteLLM](https://github.com/BerriAI/litellm) 的 `model_prices_and_context_window.json`（MIT 授權），快照放在 `src/Lumi.Web/Pricing/`，用來自動帶入模型單價與上下文上限；快照日期見同資料夾的 `litellm-model-prices.date.txt`。
- 塔羅牌：`external/TarotMcp`，自家的塔羅牌核心庫，含牌義資料與牌面圖片（git submodule，取得原始碼時要一併拉下）。授權尚未決定。

