# 範例角色

這個資料夾放素材型角色的範例（也是自動測試的資料）。使用方式：把整個角色資料夾（例如 `corgi-pixel/`）複製到 Lumi 的角色資料夾，
再到「設定 › 小精靈」按「重新載入」。角色資料夾位置：

- Windows：`%APPDATA%\Lumi\sprites`
- macOS：`~/Library/Application Support/Lumi/sprites`

規格與檢查清單見 `docs/sprite-dev-guide.md` 第 7 節。

## corgi-pixel（柯基，像素風）

作者：張仲毅。每格 280×280 px、一個邏輯像素 10 px（28×28 的像素網格），所以 `manifest.json` 宣告 `"pixelGrid": 28`；
輪廓不規則，所以點擊範圍用 `"hitShape": "alpha"`；`"rendering": "pixelated"` 讓縮放時不平滑。
