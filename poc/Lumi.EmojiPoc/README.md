# Lumi emoji 驗證頁（9E）

只回答一個問題：**Windows 上國旗是畫成圖案，還是顯示成兩個字母**（Segoe UI Emoji 沒有國旗字形，預期會是字母，但要實機確認）。
頁面用的是 `Lumi.App` 的 `EmojiTextBlock`／`EmojiText` 與 `EmojiSamples`，以連結檔方式引用（見 `Lumi.EmojiPoc.csproj`），**沒有引用 Lumi.App，也沒有打包任何 emoji 字型**。

## 執行

需要 .NET 10 SDK。在 Windows 上：

```powershell
cd <repo>
dotnet run --project poc/Lumi.EmojiPoc
```

macOS 上可用 `dotnet run --project poc/Lumi.EmojiPoc` 作對照。

頁面上方會顯示作業系統與目前 emoji 字型名稱。每組字串各有三欄：一般內文、粗體、等寬。

## 判定流程

1. **國旗正常**（三欄都是彩色國旗圖案）→ 不附字型，9E 結案，把結果記進 `plan/09-history-polish.md` 的實作紀錄。
2. **國旗顯示成字母**（例如 TW、JP）→ 進入「評估 Twemoji COLR」，依下列步驟做，不要直接打包。
3. **不可行** → 保持現狀，把「Windows 國旗顯示為字母」記為已知限制（寫在這份 README 與計畫的實作紀錄），不再花時間。

## 驗證清單

在 Windows 與 macOS 各跑一次。Win 欄與 Mac 欄由執行的人填：彩色圖案填「彩色」，字母或黑白字形填「字母／黑白」，缺字（方框）填「缺字」，其他情況寫下實際看到的內容。

| # | 字串集 | 字串 | 情境 | Win | Mac |
|---|---|---|---|---|---|
| 1a | 國旗 | 🇹🇼🇯🇵🇺🇸🇬🇧 | 一般內文 | | |
| 1b | 國旗 | 🇹🇼🇯🇵🇺🇸🇬🇧 | 粗體 | | |
| 1c | 國旗 | 🇹🇼🇯🇵🇺🇸🇬🇧 | 等寬 | | |
| 2a | 膚色 | 👍🏽 👋🏿 | 一般內文 | | |
| 2b | 膚色 | 👍🏽 👋🏿 | 粗體 | | |
| 2c | 膚色 | 👍🏽 👋🏿 | 等寬 | | |
| 3a | ZWJ 組合 | 👨‍👩‍👧 🏳️‍🌈 ❤️‍🔥 | 一般內文 | | |
| 3b | ZWJ 組合 | 👨‍👩‍👧 🏳️‍🌈 ❤️‍🔥 | 粗體 | | |
| 3c | ZWJ 組合 | 👨‍👩‍👧 🏳️‍🌈 ❤️‍🔥 | 等寬 | | |
| 4a | 鍵帽 | 1️⃣ #️⃣ | 一般內文 | | |
| 4b | 鍵帽 | 1️⃣ #️⃣ | 粗體 | | |
| 4c | 鍵帽 | 1️⃣ #️⃣ | 等寬 | | |
| 5a | VS16 符號 | ☀️ ✈️ | 一般內文 | | |
| 5b | VS16 符號 | ☀️ ✈️ | 粗體 | | |
| 5c | VS16 符號 | ☀️ ✈️ | 等寬 | | |
| 6a | Unicode 15+ | 🫨 🪿 🫎 | 一般內文 | | |
| 6b | Unicode 15+ | 🫨 🪿 🫎 | 粗體 | | |
| 6c | Unicode 15+ | 🫨 🪿 🫎 | 等寬 | | |
| 7a | 混合句子 | 今天的天氣☀️很好 🇹🇼 Hello, world! 👋🏿 ❤️‍🔥 好嗎？ | 一般內文 | | |
| 7b | 混合句子 | 今天的天氣☀️很好 🇹🇼 Hello, world! 👋🏿 ❤️‍🔥 好嗎？ | 粗體 | | |
| 7c | 混合句子 | 今天的天氣☀️很好 🇹🇼 Hello, world! 👋🏿 ❤️‍🔥 好嗎？ | 等寬 | | |

判讀重點：

- 組合 emoji（ZWJ、膚色、鍵帽）整段要是同一個圖案，不能拆成基底字元加上另一個符號。
- 「Unicode 15+」若某作業系統字型較舊而缺字，顯示方框或字母不算 EmojiText 的錯；記下來即可，不影響國旗的判定。
- 混合句子中，文字部分應與一般文字一樣（中文、英文、標點不變形）。

## 評估 Twemoji COLR（只有國旗顯示成字母時才做）

依序做，前一步不通過就停下來記錄，不往下做。

1. **確認 Avalonia 能畫 COLR v0**：Avalonia 11.3 的 Skia 後端在 Windows 上繪製內嵌 COLR v0 字型。用一個只含 COLR 字型的最小 spike（本頁即可，把字型以 `AvaloniaResource` 加進 POC）測國旗與膚色，看是否彩色。這一步是最大的未知數。
2. **核對授權（待核對）**：Twemoji 字型檔的授權條款與歸屬要求，**必須以原始授權文字為準**，加入前逐字核對，不得憑記憶判斷。記錄授權名稱、版本、來源網址與核對日期。
3. **打包與標示**（只在 1、2 都通過時）：
   - 字型檔放進 `src/Lumi.App/Assets/Fonts/`，授權文字放進 `src/Lumi.App/Assets/Fonts/LICENSES/`。
   - 在關於頁標示授權與歸屬。
4. **接線範圍**：只讓 `EmojiText` 對「區域指示符叢集（國旗）」或「系統字型缺字的叢集」使用 Twemoji；其他 emoji 仍用系統字型（Apple Color Emoji／Segoe UI Emoji）。
5. **不可行** → 回到判定流程第 3 項。

## 已知限制（目前版本）

- 驗證頁只驗證「切段」與「字型指定」；字形是否彩色只能靠實機看。切段的單元測試在 `tests/Lumi.App.Tests/EmojiSegmentationTests.cs`。
- 等寬欄的字型名稱（Consolas／Menlo）只是示意，不是 Lumi 的程式碼字型（Lumi 打包 JetBrains Mono）。
- 這個 POC 沒有打包 emoji 字型，因此 Windows 上「國旗顯示成字母」是預期結果，不是 bug。
