using System.Text.RegularExpressions;

namespace Lumi.Architecture.Tests;

/// <summary>元件 XAML 只能用主題 token，不能寫死色碼（agent.md 第 12 節）。</summary>
public partial class ThemeTokenRuleTests
{
    [GeneratedRegex(@"#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{3,4})\b")]
    private static partial Regex HexColor();

    [Fact]
    public void Axaml_OutsideTokens_HasNoHardCodedColors()
    {
        var appDir = Path.Combine(RepoPaths.Root, "src", "Lumi.App");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(appDir, "*.axaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;
            if (Path.GetFileName(file) == "Tokens.axaml")
                continue;

            var lineNo = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNo++;
                if (HexColor().IsMatch(line))
                    offenders.Add($"{Path.GetRelativePath(RepoPaths.Root, file)}:{lineNo}: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0, "發現寫死色碼：\n" + string.Join("\n", offenders));
    }
}
