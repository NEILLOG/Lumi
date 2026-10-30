using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Lumi.Architecture.Tests;

/// <summary>
/// 間距只用 ui-guide.html 第 9 節的尺度 4／8／12／16／24／32（0 不算間距）：掃描 src/Lumi.App 的 axaml，
/// Margin／Padding／Spacing（含 ColumnSpacing、RowSpacing、ItemSpacing、LineSpacing）出現尺度外的值就失敗。
/// 無法收斂的例外寫在 <see cref="Exceptions"/>，一定要有原因；例外沒有被用到時也會失敗，避免名單越積越多。
/// </summary>
public class SpacingScaleRuleTests
{
    private static readonly HashSet<double> Scale = [0, 4, 8, 12, 16, 24, 32];

    private static readonly HashSet<string> SpacingProperties =
        ["Margin", "Padding", "Spacing", "ColumnSpacing", "RowSpacing", "ItemSpacing", "LineSpacing"];

    /// <summary>
    /// 例外名單：(相對於 src/Lumi.App 的路徑, 屬性值) → 原因。路徑以 / 分隔；結尾是 / 的項目代表整個資料夾。
    /// </summary>
    private static readonly (string Path, string? Value, string Reason)[] Exceptions =
    [
        // 小精靈氣泡與輸入列的尺寸（14,10／12,5／6、輸入列右側 6）決定氣泡與輸入列的大小，計畫規定小精靈版面不改（UE-1）
        ("Sprites/", null, "小精靈視窗與氣泡的版面不改：內距與按鈕間距決定氣泡尺寸（plan/12 範圍「不做」）"),
        // 工作台對話區 Wide 的左右 Padding／Composer 外框左右 Margin 是 64（計畫「頁面 Padding」），RWD 再覆寫成 24／16
        ("Views/ConversationView.axaml", "64,24", "對話區 Wide 左右 Padding 64（plan/12 設計 token：頁面 Padding）"),
        ("Views/ChatComposer.axaml", "64,8,64,16", "Composer 外框左右 Margin 64，與對話區左右 Padding 對齊"),
        ("Views/NotificationStrip.axaml", "64,8,64,0", "工作台通知列 Wide 的左右 Margin 64，與 Composer 外框對齊（RWD 在 Compact／Narrow 覆寫成 24／16）"),
        ("Views/UpdateBanner.axaml", "64,8,64,0", "新版提醒與通知列同一條版面，左右 Margin 64 與 Composer 外框對齊（RWD 在 Compact／Narrow 覆寫成 24／16）"),
        ("Views/BudgetBanner.axaml", "64,8,64,0", "預算提醒列與通知列、新版提醒同一條版面，左右 Margin 64 與 Composer 外框對齊（RWD 在 Compact／Narrow 覆寫成 24／16）"),
        // 輸入框、下拉選單的 Focus 光暈：3px 的外框用 -3 的負 Margin 畫在控制項外面
        ("Themes/Controls.axaml", "-3", "Focus 光暈是 3px 外框，用 -3 負 Margin 畫在輸入框外圈（plan/12 共用元件樣式）"),
        // 用量按鈕右上角的紅點：向外偏移 4px 蓋在按鈕邊角（ui-usage-and-topbar.md 2.1）
        ("Views/ChatToolbar.axaml", "0,-4,-4,0", "用量圖示的紅點外移 4px，不算版面間距"),
        // 設定首頁待處理清單：最後一列的下分隔線與容器下框重疊成一條線
        ("Views/HomePanel.axaml", "0,0,0,-1", "待處理清單最後一列的下線蓋在容器下框上，不算版面間距"),
        // ComboBox 的下拉箭頭覆蓋層：上下右各縮 1px 對齊 1px 的邊框
        ("Themes/Controls.axaml", "0,1,1,1", "ComboBox 下拉覆蓋層縮進 1px 邊框，不算版面間距"),
    ];

    private static IEnumerable<(string Relative, int Line, string Name, string Value)> Spacings()
    {
        var appDir = Path.Combine(RepoPaths.Root, "src", "Lumi.App");
        foreach (var file in Directory.EnumerateFiles(appDir, "*.axaml", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(appDir, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal))
                continue;

            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var element in doc.Descendants())
            {
                // Setter Property="Margin" Value="..."
                if (element.Name.LocalName == "Setter" && (string?)element.Attribute("Property") is { } property
                    && SpacingProperties.Contains(property) && (string?)element.Attribute("Value") is { } setterValue)
                {
                    yield return (relative, ((IXmlLineInfo)element).LineNumber, property, setterValue);
                }

                foreach (var attribute in element.Attributes())
                {
                    // 只看沒有命名空間的屬性（Margin="…"）；Grid.Row 之類的附加屬性不是間距
                    if (attribute.Name.Namespace == XNamespace.None && SpacingProperties.Contains(attribute.Name.LocalName))
                        yield return (relative, ((IXmlLineInfo)attribute).LineNumber, attribute.Name.LocalName, attribute.Value);
                }
            }
        }
    }

    private static bool IsOnScale(string value)
    {
        // 資源與綁定（{StaticResource LumiSpace8}、{Binding …}）不是字面值
        if (value.Contains('{'))
            return true;
        var parts = value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && Scale.Contains(number));
    }

    private static (string Path, string? Value, string Reason)? ExceptionFor(string relative, string value) =>
        Exceptions.Cast<(string Path, string? Value, string Reason)?>().FirstOrDefault(e =>
            (e!.Value.Path.EndsWith('/') ? relative.StartsWith(e.Value.Path, StringComparison.Ordinal) : relative == e.Value.Path)
            && (e.Value.Value is null || e.Value.Value == value.Trim()));

    [Fact]
    public void Axaml_UsesOnlyTheSpacingScale()
    {
        var offenders = Spacings()
            .Where(s => !IsOnScale(s.Value) && ExceptionFor(s.Relative, s.Value) is null)
            .Select(s => $"src/Lumi.App/{s.Relative}:{s.Line}: {s.Name}=\"{s.Value}\"")
            .ToList();

        Assert.True(offenders.Count == 0,
            "發現尺度（0／4／8／12／16／24／32）以外的間距值，請收斂，或在 SpacingScaleRuleTests.Exceptions 寫明原因：\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Exceptions_AreAllStillNeeded_AndHaveAReason()
    {
        var all = Spacings().Where(s => !IsOnScale(s.Value)).ToList();
        foreach (var (path, value, reason) in Exceptions)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{path} 的例外沒有寫原因");
            Assert.True(
                all.Any(s => (path.EndsWith('/') ? s.Relative.StartsWith(path, StringComparison.Ordinal) : s.Relative == path)
                             && (value is null || s.Value.Trim() == value)),
                $"例外已經不需要了，請從名單移除：{path} {value}");
        }
    }

    [Fact]
    public void TheScanner_SeesTheAxamlFiles()
    {
        // 防止路徑改了之後掃描變成空集合而永遠通過
        Assert.True(Spacings().Count() > 100);
    }
}
