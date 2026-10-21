using System.Xml.Linq;

namespace Lumi.Architecture.Tests;

/// <summary>
/// 圖示一律用 Themes/Icons.axaml 與 LumiIcon（ui-guide.html、agent.md 第 12 節），不用 Unicode 符號當按鈕或圖示。
/// 掃描 axaml 的屬性值與文字內容（註解不算）、Strings.resx。對話內容與使用者文字（Markdown 項目符號、emoji 處理）不在這裡。
/// 例外：「·」純裝飾分隔符、「…」省略號、用量說明「↑ 輸入 … · ↓ 輸出 …」的方向箭頭（跟著文字一起念，不是按鈕或圖示）。
/// </summary>
public class NoSymbolIconRuleTests
{
    private static readonly string[] Banned =
    [
        "✓", "✔", "✕", "✖", "×", "⚠", "⊘", "■", "□", "▾", "▸", "▶", "⌄", "›", "‹", "→", "←", "✦", "✨", "●", "○", "◆", "◇",
    ];

    private static IEnumerable<string> AxamlFiles()
    {
        var appDir = Path.Combine(RepoPaths.Root, "src", "Lumi.App");
        return Directory.EnumerateFiles(appDir, "*.axaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    [Fact]
    public void Axaml_HasNoSymbolCharactersAsTextOrContent()
    {
        var offenders = new List<string>();
        foreach (var file in AxamlFiles())
        {
            var relative = Path.GetRelativePath(RepoPaths.Root, file);
            foreach (var element in XDocument.Load(file).Descendants())
            {
                var values = element.Attributes().Select(a => a.Value).Concat(element.Nodes().OfType<XText>().Select(t => t.Value));
                foreach (var value in values.Where(v => Banned.Any(v.Contains)))
                    offenders.Add($"{relative}: <{element.Name.LocalName}> {value.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0, "發現 Unicode 符號當圖示或按鈕文字，請改用 LumiIcon：\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Strings_HasNoSymbolCharacters()
    {
        var resx = XDocument.Load(Path.Combine(RepoPaths.Root, "src", "Lumi.App", "Resources", "Strings.resx"));
        var offenders = resx.Descendants("data")
            .Select(d => (Name: (string?)d.Attribute("name"), Value: d.Element("value")?.Value ?? ""))
            .Where(d => Banned.Any(d.Value.Contains))
            .Select(d => $"{d.Name}: {d.Value}")
            .ToList();

        Assert.True(offenders.Count == 0, "Strings.resx 有 Unicode 符號：\n" + string.Join("\n", offenders));
    }
}
