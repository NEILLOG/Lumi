# 小精靈角色開發指南

這份指南說明怎麼為 Lumi 的小精靈模式做一個新角色，以及角色必須遵守的限制。
適用對象：寫程式的開發者（程式型角色），以及提供美術素材的人（素材型角色）。

---

## 1. 核心觀念：殼與角色

小精靈分成兩部分。**做新角色時，只需要處理「角色」這一邊。**

| | 殼（Shell，只有一份） | 角色（Sprite，可以有很多種） |
|---|---|---|
| 負責 | 透明無框視窗、點擊穿透、靠近偵測、戳與拖曳的判定、對話氣泡、輸入列、右鍵選單、位置記憶、幀率控制 | 畫出自己，並對「狀態」與「互動」做出反應 |
| 知道對話嗎 | 知道，負責把對話事件翻譯成狀態 | 不知道，只會收到「現在是思考中」這類訊號 |
| 碰平台 API 嗎 | 會 | **完全不碰** |

角色**不可以**做的事：
- 開視窗、移動視窗、改視窗大小
- 呼叫 P/Invoke 或任何平台 API
- 讀取對話內容、呼叫模型或工具
- 自己開計時器驅動動畫

## 2. 狀態

殼會呼叫 `SetState(...)`，角色要用動畫表現對應的狀態。

| 狀態 | 何時發生 | 光球的參考表現 | 退化時改用 |
|---|---|---|---|
| `Idle` 待機 | 沒有任務 | 暖色、4 秒呼吸、眼睛偶爾探出來 | —（必須支援） |
| `Listening` 聆聽 | 收音中 | 冰藍、漣漪擴散、眼睛睜大 | `Idle` |
| `Transcribing` 轉寫 | Whisper 處理中 | 藍往靛色漂移、眼睛左右掃視 | `Listening` |
| `Thinking` 思考 | 等待第一個 token | 紫藍流轉、粒子繞行、眼睛往上飄 | `Idle` |
| `Tool` 呼叫工具 | 執行工具中 | 琥珀色旋轉弧線、瞇眼 | `Thinking` |
| `Streaming` 回覆中 | 文字串流中 | 青綠閃動、眨眼變頻繁 | `Idle` |
| `Done` 完成 | 回覆結束（數秒後回到待機） | 綠光彈一下、單眼 wink | `Idle` |
| `Attention` 需要確認 | 工具需要使用者同意 | 橘色上下彈跳 | `Done` |
| `Error` 錯誤 | API 失敗、斷線 | 紅色搖頭、眼睛擠成線 | `Idle` |
| `Sleep` 休眠 | 閒置一段時間 | 灰藍、8 秒慢呼吸、閉眼 | `Idle` |

只有 `Idle` 是必要的，其他狀態可以在 manifest 裡用退化規則對應到別的狀態。
退化規則可以串接，例如 `Transcribing → Listening → Idle`，殼會一路往下找。

## 3. 互動

殼會呼叫 `OnInteraction(...)` 通知角色：

| 互動 | 說明 | 光球的參考表現 |
|---|---|---|
| `Near(true / false)` | 游標靠近或離開 | 光暈變亮、睜眼 |
| `Look(vector)` | 游標方向，範圍 -1..1 | 眼睛跟著看 |
| `PokeBegin(point)` | 在同一點長按 0.45 秒以上 | 從按點凹陷、>_<、腮紅 |
| `PokeEnd` | 放開 | QQ 果凍回彈 |
| `DragBegin` / `DragEnd` | 拖曳開始、結束 | 果凍晃動、眼睛轉圈 |

戳和拖曳的判定（時間、位移門檻）由殼負責，角色只負責表現。

## 4. 角色介面

```csharp
public interface ISpriteFactory
{
    SpriteManifest Manifest { get; }
    ISpriteView Create(SpriteOptions options);
}

// 殼建立角色時給的尺寸，已經過 SpriteLimits.Fit 縮放
public sealed record SpriteOptions(double Diameter, Size Box, Size Outer)
{
    public Rect BoxRect { get; }      // 角色框在 Outer 裡的位置（正中央）
}

public interface ISpriteView
{
    Control Visual { get; }           // 大小必須是 SpriteOptions.Outer
    void SetState(SpriteState state); // 只會收到 Manifest.Supported 裡的狀態（殼已依退化規則解析）
    void OnInteraction(SpriteInteraction e);
    void Advance(TimeSpan dt);        // 由殼的 FrameDriver 統一呼叫
    bool HitTest(Point p);            // 角色本體的範圍，用於點擊穿透與戳；角色框外一律 false
    Point BubbleAnchor { get; }       // 氣泡接在哪裡（通常是頭頂）
    int PreferredFps { get; }         // 角色目前需要的幀率
}
```

- **座標**：`HitTest`、`BubbleAnchor`、`PokeBegin` 的點，原點都是 `Visual` 的左上角。
- **`Visual` 的大小是 `Outer`**（角色框加上特效外擴），角色框在正中央。光暈、粒子畫在框外那一圈；不要畫到 `Visual` 之外，Avalonia 不會替你清掉畫在控制項外面的殘影。
- **`HitTest` 不可以依賴排版**：殼可能在 `Visual` 排版之前就呼叫它，座標一律用 `SpriteOptions` 算。

註冊：`services.AddSprite<MySpriteFactory>()`（會同時提供 `ISpriteCatalog`，設定頁的角色清單從這裡來）。
`Lumi.Sprites.Abstractions` 帶有版本號（`SpriteAbstractionsVersion.Current`），介面若有變動，必須升版並在本文件末尾記錄。

## 5. 尺寸與比例限制（`SpriteLimits`）

這些上限由開發者定義在 `Lumi.Sprites.Abstractions` 的 `SpriteLimits` 類別中，角色不能覆寫。

| 項目 | 上限 | 說明 |
|---|---|---|
| 長寬比 | 1:2 到 2:1 | 角色框的寬除以高，必須介於 0.5 和 2.0 之間 |
| 最長邊 | 該尺寸檔位光球直徑的 1.6 倍 | 例如「中」檔光球是 80，角色最長邊上限就是 128 DIP |
| 特效外擴 | 角色框的 40% | 光暈、粒子可以畫在框外，但不能超過這個範圍 |
| 點擊範圍 | 必須在角色框內 | `HitTest` 在框外一律回傳 false |
| 角色語氣 | 200 字以內 | 見第 8 節 |

尺寸檔位：小 64、中 80（預設）、大 104，以光球直徑為基準（`SpriteLimits.Tiers`）。角色在 manifest 中宣告「中」檔的框大小，其他檔位由殼等比例縮放。
特效外擴是**每邊**，大小是角色框對應邊長乘以 `effectMargin`（最多 0.4）：框 96×96、外擴 0.4 時，`Visual` 是 172.8×172.8。

**不合規的處理**：殼載入時會自動把角色縮放到上限內（`SpriteLimits.Fit`），並在 log 中警告，不會讓角色撐大視窗。長寬比超出就縮窄或縮矮；最長邊超出就整個等比例縮小；外擴超出就限制在 0.4。
殼的視窗大小由「角色框 + 特效外擴 + 氣泡 + 輸入列」自動計算。

## 6. 程式型角色

適合用程式繪製、需要精細互動的角色，例如光球。

1. 建立專案 `Lumi.Sprites.<名稱>`，只引用 `Lumi.Sprites.Abstractions`
2. 實作 `ISpriteFactory` 與 `ISpriteView`
3. 在 `Lumi.App` 註冊：`services.AddSprite<MySpriteFactory>()`

**繪製注意事項**（POC 實測得到的教訓）：
- `RadialGradientBrush` 不要讓 `GradientOrigin` 和 `Center` 不同。Skia 會畫出硬邊圓盤。需要偏心的亮面時，改用同心漸層、移動圓心並放大半徑，再裁切。
- 柔邊效果用多個色標，邊緣平滑收到 0。只有 2～3 個色標會出現「斷層圈」。
- 不要使用 `BlurEffect`，常駐畫面很吃 GPU。
- 不要自己開計時器，一律在 `Advance(dt)` 裡推進時間。
- 閒置時降低 `PreferredFps`：待機建議 30，休眠建議 12，互動中才用 60。
- 尺寸用比例計算（以光球半徑為基準），不要寫死像素。

參考實作：`Lumi.Sprites.Orb`（來源是 `poc/Lumi.OrbPoc/OrbControl.cs`）。它的角色框是光球直徑的 1.2 倍（本體加一圈點擊範圍），外擴 0.4。測試的寫法（每個狀態都畫得出東西、`HitTest` 不超出角色框）見 `tests/Lumi.Sprites.Tests/OrbSpriteTests.cs`。

## 7. 素材型角色

不用寫程式：準備圖片和一份 `manifest.json`，放進「角色」資料夾，在設定 ›「小精靈」按「重新載入」就能選用。
素材型角色**不會執行任何程式碼**；載入的檔案一律當作不可信的輸入，所以路徑、大小、圖片尺寸、語氣都會檢查（見「載入檢查」）。
範例：`samples/sprites/corgi-pixel/`（像素柯基，13 個動畫，也是自動測試的資料）。

### 安裝

- 角色資料夾：Windows `%APPDATA%\Lumi\sprites`、macOS `~/Library/Application Support/Lumi/sprites`（和 `settings.json` 同一層）。設定 ›「小精靈」有「開啟角色資料夾」鈕，資料夾不存在會先建立。
- **一個角色一個子資料夾**，裡面是 `manifest.json` 與圖片；只掃描第一層。把整個資料夾複製進去，按「重新載入」。啟動時也會掃描一次，不監看檔案變動。
- 載入失敗的角色不會出現在清單裡，原因（哪個資料夾、哪個欄位、為什麼）列在設定頁的「載入失敗與警告」；有警告的角色照常可以使用。
- 修改素材之後按「重新載入」，目前選用的角色會用新圖重建，位置不動。選用的角色被移走時小精靈暫時退回光球，設定裡的選擇不變，資料夾放回去、重新載入就自動換回來。

### 資料夾結構

```
sprites/corgi-pixel/
  manifest.json
  preview.png          設定頁清單的縮圖（選填，≤ 1 MB、≤ 512×512）
  idle.png             sprite sheet（多格排成一張）
  listening.png
  poke_press.png
  ...
```

### manifest.json 範例

```json
{
  "schemaVersion": 1,
  "id": "neillog.corgi-pixel",
  "name": "柯基（像素）",
  "author": "作者名稱",
  "version": "0.1.0",
  "kind": "frames",

  "box": { "width": 112, "height": 112 },
  "effectMargin": 0,
  "hitShape": "alpha",
  "bubbleAnchor": { "x": 0.5, "y": 0.12 },
  "rendering": "pixelated",
  "pixelGrid": 28,

  "voice": "句尾偶爾加「汪」，語氣熱情、容易興奮。",

  "animations": {
    "idle":         { "source": "idle.png",       "frames": 12, "columns": 4, "fps": 8,  "loop": true },
    "listening":    { "source": "listening.png",  "frames": 8,  "columns": 4, "fps": 10, "loop": true },
    "thinking":     { "source": "thinking.png",   "frames": 10, "columns": 5, "fps": 8,  "loop": true },
    "done":         { "source": "done.png",       "frames": 8,  "columns": 4, "fps": 12, "loop": false, "next": "idle" },
    "error":        { "source": "error.png",      "frames": 8,  "columns": 4, "fps": 10, "loop": false, "hold": "last" },
    "poke.press":   { "source": "poke_press.png", "frames": 4,  "columns": 4, "fps": 16, "loop": false, "hold": "last" },
    "poke.release": { "source": "poke_rel.png",   "frames": 8,  "columns": 4, "fps": 16, "loop": false, "next": "idle" },
    "drag":         { "source": "drag.png",       "frames": 6,  "columns": 6, "fps": 12, "loop": true }
  },

  "fallback": {
    "transcribing": "listening",
    "tool": "thinking"
  }
}
```

（`animations` 只列出部分；沒有動畫的狀態走 `fallback` 與預設的退化規則。）

### 欄位說明

| 欄位 | 必要 | 說明 |
|---|---|---|
| `schemaVersion` | 是 | 目前只有 `1` |
| `id` | 是 | 小寫英數與 `. _ -`，3～64 字元，英數開頭。**`lumi.` 前綴保留給內建角色**；不能和已有的角色重複（重複時依資料夾名稱排序先到先得） |
| `name`、`author`、`version` | 是 | 各最多 100 字，設定頁顯示用 |
| `kind` | 否 | 目前只有 `"frames"`（sprite sheet），沒寫就是 `frames` |
| `box` | 是 | 「中」檔位的角色框大小（DIP），超出第 5 節的限制時殼會自動縮放並警告 |
| `effectMargin` | 否 | 特效外擴比例，0～0.4（超出會被限制並警告），預設 0 |
| `hitShape` | 否 | 點擊範圍：`ellipse`（預設）、`rect`、`alpha`（見下） |
| `bubbleAnchor` | 否 | 氣泡位置，相對於角色框的比例座標（0～1），預設頭頂中央 |
| `rendering` | 否 | `smooth`（預設，高品質縮放）或 `pixelated`（最近鄰，像素風用） |
| `pixelGrid` | 否 | 像素風角色：每一格在寬度方向有幾個邏輯像素（見「像素風角色」） |
| `voice` | 否 | 角色語氣，見第 8 節的規則 |
| `animations` | 是 | 至少要有 `idle`；key 是十個狀態（小寫：`idle`、`listening`、`transcribing`、`thinking`、`tool`、`streaming`、`done`、`attention`、`error`、`sleep`）或互動動畫 `poke.press`、`poke.release`、`drag` |
| `fallback` | 否 | 沒提供動畫的狀態改用哪一個（狀態名稱對狀態名稱）；沒寫時用預設規則（第 2 節） |

每個動畫：

| 欄位 | 說明 |
|---|---|
| `source` | sprite sheet 的檔名（相對於角色資料夾，可以在子資料夾；只能是 `.png`） |
| `frames` | 總格數，1～120 |
| `columns` | 每列幾格，1～`frames`；格子由左到右、由上到下排列，最後一列可以不滿 |
| `fps` | 每秒幾格，1～60（可以是小數） |
| `loop` | 是否循環，沒寫就是 `false` |
| `next` | 播完後接哪個動畫（`loop: false` 時使用） |
| `hold` | `"last"`：播完停在最後一格 |

### 動畫怎麼播

- **狀態動畫**：殼設定狀態時從該動畫的第 0 格開始；同一個狀態重複設定不會重來。
- **播完不循環的動畫**：有 `next` 就接那個動畫（從頭播）；有 `hold: "last"` 就停在最後一格；**兩個都沒有就回到 `idle`**（`idle` 本身停在最後一格）。`loop: true` 的動畫不會結束，`next` 沒有意義（會被忽略並警告）。
- **互動動畫**（覆蓋在狀態動畫上，互動期間狀態動畫的時間凍結，結束後從原來的位置繼續）：
  - `poke.press`：按住戳時播放，播完停在最後一格等放開。
  - `poke.release`：放開時播放，播完回到**目前的狀態**（所以它的 `next` 不用寫，寫了也不影響）。
  - `drag`：拖曳時循環播放，放開就結束。
  - 角色沒有某個互動動畫就不反應；`Near`（靠近）與 `Look`（游標方向）沒有對應的動畫，直接忽略（要眼睛跟著游標需要程式型角色）。
- **幀率**：殼用動畫自己的 fps 驅動，畫面停住（停在最後一格、單格動畫）時降到最低。所以像素角色的待機 8 fps、休眠 4 fps，比光球省電。

### 格子與尺寸

- **每一格涵蓋的是 `Outer`（角色框加上特效外擴）**：`effectMargin` 是 0 時就是角色框，大於 0 時格子要把光暈、特效那一圈也畫進去。格子的長寬比要和 `Outer` 一致（差超過 2% 警告）；不一致時殼會等比例縮放並**底部置中**，不會拉伸。
- 一個角色所有動畫的格子大小必須相同（以 `idle` 為準；不同的動畫會被丟掉並警告）。每張圖的大小必須是「欄數×格寬」、「列數×格高」，不是整數倍的動畫會被丟掉。
- 角色框是「中」檔（光球直徑 80）的大小；小 64、大 104 由殼等比例縮放（柯基框 112：小 89.6、大 145.6）。
- 解析度：以「大」檔的 **2 倍**畫（Retina），柯基框 112、大檔 145.6 DIP，格子 280 px。

### 像素風角色

像素圖在非整數倍縮放時，最近鄰會讓有的邏輯像素 5 px、有的 6 px，看起來粗細不均。解法是兩個選填欄位：

- `"rendering": "pixelated"`：縮放時不做平滑（沒有混色）。
- `"pixelGrid": N`：每一格在**寬度方向**有 N 個邏輯像素（邏輯像素是正方形，所以高度方向的個數由長寬比決定，必須剛好是整數；柯基是 28：每個邏輯像素 10 px、整格 280 px）。有宣告時，殼把每個邏輯像素畫成**整數個裝置像素**（向下取整、至少 1），整張圖**底部置中**貼齊角色框（腳底的基準線不動）。

柯基在各檔位、各螢幕縮放下每個邏輯像素是：中檔 1 倍 4 px、2 倍 8 px、Windows 125% 5 px、150% 6 px（剛好填滿框）；小檔 1 倍 3 px、2 倍 6 px（整張 84 DIP）；大檔 1 倍 5 px、2 倍 10 px（整張 140 DIP）。小、大檔會比框略小，不會超出。作者的邏輯像素大小最好讓「中檔框 ÷ pixelGrid」剛好是整數 DIP（柯基 112 ÷ 28 = 4）。

### 點擊範圍

- `ellipse`：角色框內接的橢圓；`rect`：整個角色框。
- `alpha`：看角色**目前這一格**的透明度，不透明處與其周圍一個邏輯像素內才算命中（沒有 `pixelGrid` 時以格寬的 1/28 為準）。適合輪廓不規則的角色：柯基是瘦高的輪廓、腳在左右下角，橢圓會漏掉腳，矩形又包含大片透明區、會擋住下方的桌面。
- 三種都一律限制在角色框內。

### 美術素材規格

- 格式：PNG，帶透明背景。像素風不要有半透明像素（邊緣硬邊），調色盤保持精簡。
- 每格大小相同，角色在每格中的對位一致（腳底基準線不變），避免播放時抖動。
- **特效不要貼著格子的左、上、右邊緣**：離邊緣至少留 1 個邏輯像素（柯基是 10 px），否則邊緣會被切掉（載入時只能警告）。腳貼底邊沒問題，那是設計。
- 光暈、陰影畫在 `effectMargin` 允許的範圍內（格子涵蓋 `Outer`）。
- 第一版只支援 sprite sheet；PNG 連續圖（每格一個檔案）、Lottie 之後再評估。

### 載入檢查

整個角色**拒載**：找不到或超過 64 KB 的 `manifest.json`、不是合法 JSON、`schemaVersion` 不是 1、`kind` 不是 `frames`、`id` 不合規或用了保留前綴或重複、缺 `name`／`author`／`version`、`box` 不是正數、沒有 `idle` 或 `idle` 動畫無法使用、所有圖片解碼後超過 96 MB。

**只丟掉那個動畫**（角色照常載入，對應的狀態走退化，並警告）：`source` 是絕對路徑、含 `..`、指向資料夾外、經過捷徑或符號連結、不是 `.png`、不存在、超過 8 MB；PNG 標頭不合法、寬或高超過 4096；`frames`／`columns`／`fps` 超出範圍；圖片大小不是格數的整數倍；格子大小和 `idle` 不同。

**只警告**（自動修正）：欄位值不認得（`hitShape`、`rendering` 退回預設）、`effectMargin` 或 `bubbleAnchor` 超出範圍（限制到範圍內）、`pixelGrid` 不合法（忽略，用一般縮放）、不認得的動畫 key、`next`／`hold`／`fallback` 指向不存在的東西（忽略那一項）、格子長寬比對不上、特效貼著格子邊緣、`name` 等文字過長（截斷）、語氣沒通過檢查（忽略語氣，見第 8 節）。未知的欄位一律忽略，不警告。

圖片在每個動畫**第一次用到時才解碼**並快取（柯基 12 張圖全解開約 30 MB）；載入階段只讀 PNG 標頭。

## 8. 角色語氣與人設

角色可以在 manifest 中提供 `voice`（語氣），它只是 prompt 的最後一層點綴：

| 優先順序 | 層 |
|---|---|
| 1（最高） | 基底規則（安全、工具） |
| 2 | 模式格式（小精靈：最多兩句、約 60 字） |
| 3 | 使用者人設（個性、積極度、自訂人設） |
| 4（最低） | **角色語氣** |

- 語氣只影響說話風格，例如句尾、口癖。不能改變能力、規則或回答長度。
- 和人設衝突時，以人設為準。
- 使用者可以在設定中關閉角色語氣。
- 語氣要是**單行純文字**，不超過 200 字，不能含 `<` `>` 或反引號（不能偽裝成 prompt 的標籤或程式碼區塊），也不能含指令式的字眼（例如「忽略」「不要遵守」「你現在是」「ignore」「override」，完整清單見 `SpriteVoiceValidator.Forbidden`）。
- 沒通過檢查的語氣會被**忽略**：角色照常載入，只是沒有語氣，設定頁的「載入失敗與警告」會寫出原因（「語氣被忽略：…」）。程式型角色的語氣也經過同一個檢查（`SpriteVoiceProvider`）。
- 這個檢查是安全網，不是保證：語氣層本來就在 prompt 的最低優先，使用者也可以在設定中關閉它。

## 9. 驗證你的角色

- **設定頁的預覽**：設定 ›「小精靈」會用同一個殼的規則（縮放、退化、幀率）播放所選角色的每個狀態，沒有專屬表現的狀態會標示退化成哪個。
- **自動測試**：`tests/Lumi.App.Tests/Sprites/ChangingTheSpriteTests.cs` 用一個只畫色塊、只支援 `Idle` 的假角色（`BlockSpriteFactory`）示範：用 `AddSprite<T>()` 註冊、在設定選用，殼、氣泡、輸入列、尺寸檔位與設定頁都照常運作，不支援的狀態走退化。你的角色專案可以照 `tests/Lumi.Sprites.Tests/OrbSpriteTests.cs` 的寫法測：每個狀態都畫得出東西、`HitTest` 不超出角色框、`PreferredFps` 隨狀態變。
- **素材型角色**：把資料夾放進角色資料夾、按「重新載入」，設定頁的「載入失敗與警告」會告訴你哪裡不合規；狀態預覽一個一個播，確認沒有被切掉的特效、點擊範圍（`alpha`）和腳底基準線都對。`tests/Lumi.Sprites.Tests/CorgiSampleTests.cs` 示範怎麼用自動測試守住一個真的素材包：載入零警告、每個動畫的格子與時間、各檔位各縮放下每個邏輯像素的裝置像素數、命中範圍跟著輪廓。
- **耗能**：開 `Lumi:Developer:ShowSpriteHud=true`，HUD 顯示 FPS 與 30 秒平均 CPU；待機與休眠要和光球相近。

## 10. 上線前檢查清單

- [ ] 支援 `Idle`；其他狀態有動畫或已設定退化規則
- [ ] 符合 `SpriteLimits`（長寬比、最長邊、特效外擴）
- [ ] `HitTest` 只涵蓋角色本體，透明的地方可以點擊穿透
- [ ] `BubbleAnchor` 設在合理位置，氣泡不會蓋住角色的臉
- [ ] 小、中、大三個尺寸都清楚，Retina 螢幕上不模糊
- [ ] 待機與休眠時有降低幀率；待機 30 秒的 CPU 平均和光球相近
- [ ] 在 Windows 與 macOS 都看過一次
- [ ] 設定頁的圖鑑中，每個狀態都能預覽
- [ ] 素材型：重新載入後「載入失敗與警告」是空的；特效離格子左、上、右邊緣至少 1 個邏輯像素；`id` 不用 `lumi.` 開頭
- [ ] 像素風：宣告 `rendering: pixelated` 與 `pixelGrid`，小、中、大三檔與 1 倍、2 倍螢幕上像素粗細一致
- [ ] 輪廓不規則的角色用 `hitShape: alpha`，腳與邊角都點得到、旁邊的透明處會穿透

## 11. 版本紀錄

| Abstractions 版本 | 變更 |
|---|---|
| 1.1 | 2026-10-22 新增（選填、不影響既有角色）：`SpriteManifest.Rendering`（`smooth`／`pixelated`）、`SpriteManifest.PixelGrid`、`SpriteHitShape.Alpha`、`SpriteVoiceValidator`（語氣的載入檢查）。素材型角色的載入器與播放器在 `Lumi.Sprites.Assets`（第 7 節），不是 Abstractions 的一部分 |
| 1.0 | 2026-10-21 初版：狀態、互動、`ISpriteFactory`、`ISpriteView`、manifest schemaVersion 1 |
