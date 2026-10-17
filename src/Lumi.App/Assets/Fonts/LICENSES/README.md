# 字型來源與授權

三套字型皆為 SIL Open Font License 1.1，授權全文在本資料夾的 `*-OFL.txt`。

| 檔案 | 來源 | 備註 |
|---|---|---|
| `IBMPlexSans-Regular/Medium/Bold.ttf` | https://github.com/IBM/plex 的 `packages/plex-sans/fonts/complete/ttf/`（v3.005） | 原樣使用 |
| `NotoSansTC-Regular/Medium/Bold.ttf` | https://github.com/google/fonts 的 `ofl/notosanstc/NotoSansTC[wght].ttf` | 用 fonttools `varLib.instancer` 切出 wght=400／500／700 的靜態檔 |
| `JetBrainsMono-Regular/Medium/Bold.ttf` | https://github.com/google/fonts 的 `ofl/jetbrainsmono/JetBrainsMono[wght].ttf` | 同上，切出 wght=400／500／700 |

## 為什麼是靜態檔

Avalonia 11.3 的 Skia 後端不會套用可變字型的 `wght` 軸：直接放可變字型時，中文與等寬字型在 500／700 都仍是 400 的粗細（2026-10-17 在 macOS 實測）。
改用靜態字重檔後，400／500／700 都正確。

## 重新產生靜態檔

```bash
python3 -m venv venv && venv/bin/pip install fonttools
venv/bin/fonttools varLib.instancer NotoSansTC-VF.ttf wght=700 -o NotoSansTC-Bold.ttf --update-name-table
```

（工具只在產生字型時使用，不屬於建置流程。）

## SHA-256

```
9e6c74a889a700d707613d24548fe4ffa6bc59559a0689d2cf9e133bdcdafb2f  IBMPlexSans-Bold.ttf
331c8639d7598b2cde62a911a71db195e30cb655cd6bdf2e324a7e984955f907  IBMPlexSans-Medium.ttf
975dcda37d80f038dcd143c22e33ca2d97a0cc5a929aace1c749153b0fe1afa5  IBMPlexSans-Regular.ttf
5b3124a1395c61ce45ee2c4b50ac0669ccd75ad28eaafdf38318ab90862c6463  JetBrainsMono-Bold.ttf
8ec4ed38a1c005fac92c011b955b9edb9abd2ac8f26f4511bfec2744629b156a  JetBrainsMono-Medium.ttf
11ffa32af8f894ce3e4828d65431c4c893210b9c331d40eaa9a4eaafca341f57  JetBrainsMono-Regular.ttf
64819712ea9581ad56b8212830918a80c685fe32402fd58076180ef4729727bc  NotoSansTC-Bold.ttf
1c1000fff7a95c5fbce1860ba31c2b187835855caab81334e863d24e41442ab9  NotoSansTC-Medium.ttf
120637170e4786aacaf0b5726a8dc62415f562ece7ec31118afcdf82e6c5bc25  NotoSansTC-Regular.ttf
```
