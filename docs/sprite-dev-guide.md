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

不用寫程式：準備圖片和一份 `manifest.json`，放進「角色」資料夾就會自動載入。素材型角色不會執行任何程式碼。

### 資料夾結構

```
sprites/cat/
  manifest.json
  preview.png          設定頁圖鑑用的預覽圖
  idle.png             sprite sheet（多格排成一張）
  listening.png
  poke_press.png
  ...
```

### manifest.json 範例

```json
{
  "schemaVersion": 1,
  "id": "example.cat",
  "name": "小貓",
  "author": "作者名稱",
  "version": "1.0.0",
  "kind": "frames",

  "box": { "width": 100, "height": 120 },
  "effectMargin": 0.3,
  "hitShape": "ellipse",
  "bubbleAnchor": { "x": 0.5, "y": 0.05 },

  "voice": "句尾偶爾加「喵」，語氣有點傲嬌。",

  "animations": {
    "idle":         { "source": "idle.png",       "frames": 12, "columns": 4, "fps": 8,  "loop": true },
    "listening":    { "source": "listening.png",  "frames": 8,  "columns": 4, "fps": 12, "loop": true },
    "thinking":     { "source": "thinking.png",   "frames": 10, "columns": 5, "fps": 10, "loop": true },
    "done":         { "source": "done.png",       "frames": 6,  "columns": 6, "fps": 12, "loop": false, "next": "idle" },
    "poke.press":   { "source": "poke_press.png", "frames": 4,  "columns": 4, "fps": 16, "loop": false, "hold": "last" },
    "poke.release": { "source": "poke_rel.png",   "frames": 8,  "columns": 4, "fps": 16, "loop": false, "next": "idle" },
    "drag":         { "source": "drag.png",       "frames": 6,  "columns": 6, "fps": 12, "loop": true }
  },

  "fallback": {
    "transcribing": "listening",
    "tool": "thinking",
    "streaming": "idle",
    "attention": "done",
    "error": "idle",
    "sleep": "idle"
  }
}
```

### 欄位說明

| 欄位 | 說明 |
|---|---|
| `box` | 「中」檔位的角色框大小（DIP），必須符合第 5 節的限制 |
| `effectMargin` | 特效外擴比例，0～0.4 |
| `hitShape` | `ellipse` 或 `rect`，點擊範圍的形狀 |
| `bubbleAnchor` | 氣泡位置，相對於角色框的比例座標（0～1） |
| `voice` | 角色語氣，選填，見第 8 節 |
| `animations.*.frames / columns` | sprite sheet 的總格數與每列格數，由左到右、由上到下排列 |
| `loop` | 是否循環 |
| `next` | 播完後接哪個動畫（`loop: false` 時使用） |
| `hold` | `last`：播完後停在最後一格（例如按住戳的時候） |
| `fallback` | 沒提供動畫的狀態要改用哪一個 |

### 美術素材規格

- 格式：PNG，帶透明背景
- 解析度：以「大」檔位的 **2 倍**繪製（Retina 用），例如框寬 100 時，大檔約 130 DIP，每格畫 260 px
- 每格大小必須相同，角色在每格中的對位要一致，避免播放時抖動
- 光暈、陰影畫在 `effectMargin` 允許的範圍內
- 第一版只支援 PNG 連續圖或 sprite sheet。Lottie 之後再評估。

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
- 只描述語氣，不要寫指令性的內容（例如「忽略之前的規則」），這類內容會在載入時被拒絕。

## 9. 上線前檢查清單

- [ ] 支援 `Idle`；其他狀態有動畫或已設定退化規則
- [ ] 符合 `SpriteLimits`（長寬比、最長邊、特效外擴）
- [ ] `HitTest` 只涵蓋角色本體，透明的地方可以點擊穿透
- [ ] `BubbleAnchor` 設在合理位置，氣泡不會蓋住角色的臉
- [ ] 小、中、大三個尺寸都清楚，Retina 螢幕上不模糊
- [ ] 待機與休眠時有降低幀率；待機 30 秒的 CPU 平均和光球相近
- [ ] 在 Windows 與 macOS 都看過一次
- [ ] 設定頁的圖鑑中，每個狀態都能預覽

## 10. 版本紀錄

| Abstractions 版本 | 變更 |
|---|---|
| 1.0 | 2026-10-21 初版：狀態、互動、`ISpriteFactory`、`ISpriteView`、manifest schemaVersion 1 |
