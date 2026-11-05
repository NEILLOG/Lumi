using System.Reflection;
using System.Text.RegularExpressions;
using Lumi.App.Resources;
using Lumi.App.ViewModels;
using Lumi.Core.Tools;

namespace Lumi.Architecture.Tests;

/// <summary>
/// 使用說明（docs/guide）的規則（agent.md 第 3 節、計畫 28 第 6 節）：格式、大小、內部用語、連結，
/// 以及設定頁與工具是否都有寫到。「每篇」指 docs/guide/*.md 去掉 README.md；內部用語與連結也掃 README.md。
/// </summary>
public partial class UserGuideRuleTests
{
    private const string ReadmeName = "README.md";
    private const int MaxGuideLength = 8000;
    private const int MaxSummaryLength = 80;

    private static readonly string GuideDir = Path.Combine(RepoPaths.Root, "docs", "guide");

    private static readonly string[] InternalTerms =
        ["plan/", "agent.md", "ui-spec", "Phase 0", "TODO", "<!--"];

    /// <summary>設定頁的畫面名稱對應的文字（列舉值 → Strings 屬性）。新增分類時要一起加。</summary>
    private static readonly Dictionary<SettingsCategory, Func<string>> CategoryNames = new()
    {
        [SettingsCategory.Home] = () => Strings.Settings_CategoryHome,
        [SettingsCategory.Connections] = () => Strings.Settings_CategoryConnections,
        [SettingsCategory.Usage] = () => Strings.Settings_CategoryUsage,
        [SettingsCategory.Persona] = () => Strings.Settings_CategoryPersona,
        [SettingsCategory.Memory] = () => Strings.Settings_CategoryMemory,
        [SettingsCategory.Reminders] = () => Strings.Settings_CategoryReminders,
        [SettingsCategory.Tools] = () => Strings.Tools_MenuTitle,
        [SettingsCategory.General] = () => Strings.Settings_CategoryGeneral,
        [SettingsCategory.Sprite] = () => Strings.Settings_CategorySprite,
        [SettingsCategory.Speech] = () => Strings.Settings_CategorySpeech,
        [SettingsCategory.Network] = () => Strings.Settings_CategoryNetwork,
        [SettingsCategory.Privacy] = () => Strings.Settings_CategoryPrivacy,
    };

    [GeneratedRegex(@"(?<![A-Za-z0-9])M\d{1,2}(?![A-Za-z0-9])")]
    private static partial Regex MilestonePattern();

    [GeneratedRegex(@"!?\[[^\]]*\]\(([^)\s]+)")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"設定\s*›\s*")]
    private static partial Regex SettingsPathPattern();

    [GeneratedRegex(@"Descriptor\s*\{\s*get;\s*\}\s*=\s*new\(\s*(""[^""]*""|[A-Za-z_][\w.]*)\s*,\s*(""[^""]*""|[A-Za-z_][\w.]*)\s*,")]
    private static partial Regex DescriptorPattern();

    [GeneratedRegex(@"const\s+string\s+(\w+)\s*=\s*""([^""]*)""")]
    private static partial Regex ConstPattern();

    private static IEnumerable<string> Guides() =>
        Directory.EnumerateFiles(GuideDir, "*.md")
            .Where(f => Path.GetFileName(f) != ReadmeName)
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal);

    private static IEnumerable<string> AllPages() => Guides().Append(Path.Combine(GuideDir, ReadmeName));

    private static string Name(string file) => Path.GetFileName(file);

    private static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
        || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");

    private static IEnumerable<string> SourceFiles(string pattern) =>
        new[] { "Lumi.Tools", "Lumi.Tarot" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "src", p), pattern, SearchOption.AllDirectories))
            .Where(f => !IsBuildOutput(f));

    private sealed record BuiltInTool(string File, string Name, string DisplayName);

    /// <summary>
    /// 從原始碼解析內建工具的名稱與顯示名稱（Descriptor 要依賴注入才拿得到，所以只能讀原始碼）。
    /// 名稱可能是字串常數或 <c>const string</c> 的識別字；解析不了的記在 <c>Failures</c>，測試會明確失敗。
    /// </summary>
    private static (List<BuiltInTool> Tools, List<string> Failures) ParseBuiltInTools()
    {
        var failures = new List<string>();

        // 所有 const string 的值（依名稱）；同名但值不同的記成衝突，被引用到才算失敗
        var consts = new Dictionary<string, string>(StringComparer.Ordinal);
        var conflicts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in SourceFiles("*.cs"))
        {
            foreach (Match m in ConstPattern().Matches(File.ReadAllText(file)))
            {
                var key = m.Groups[1].Value;
                if (consts.TryGetValue(key, out var existing) && existing != m.Groups[2].Value)
                    conflicts.Add(key);
                consts[key] = m.Groups[2].Value;
            }
        }

        string? Resolve(string token, string file)
        {
            if (token.StartsWith('"'))
                return token[1..^1];

            var key = token.Split('.')[^1];
            if (conflicts.Contains(key) || !consts.TryGetValue(key, out var value))
            {
                failures.Add($"{Name(file)}：無法解析名稱「{token}」");
                return null;
            }
            return value;
        }

        var tools = new List<BuiltInTool>();
        foreach (var file in SourceFiles("*Tool.cs"))
        {
            var match = DescriptorPattern().Match(File.ReadAllText(file));
            if (!match.Success)
            {
                failures.Add($"{Name(file)}：找不到 Descriptor");
                continue;
            }

            var name = Resolve(match.Groups[1].Value, file);
            var display = Resolve(match.Groups[2].Value, file);
            if (name is null || display is null)
                continue;

            tools.Add(new BuiltInTool(Name(file), name, display));
        }

        return (tools, failures);
    }

    private static int ConcreteToolCount() =>
        new[] { typeof(Lumi.Tools.ToolsServiceCollectionExtensions).Assembly, typeof(Lumi.Tarot.TarotToolNames).Assembly }
            .SelectMany(a => a.GetTypes())
            .Count(t => t is { IsClass: true, IsAbstract: false } && typeof(ITool).IsAssignableFrom(t));

    [Fact]
    public void EveryGuide_HasTitleAndSummary()
    {
        var offenders = new List<string>();
        foreach (var file in Guides())
        {
            var lines = File.ReadAllLines(file);
            if (lines.Length == 0 || !lines[0].StartsWith("# ", StringComparison.Ordinal))
            {
                offenders.Add($"{Name(file)}：第一行不是 # 標題");
                continue;
            }

            var next = lines.Skip(1).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            if (next is null || !next.StartsWith("> ", StringComparison.Ordinal))
            {
                offenders.Add($"{Name(file)}：標題下一行不是 > 摘要");
                continue;
            }

            var summaryLength = next[2..].Trim().Length;
            if (summaryLength is < 1 or > MaxSummaryLength)
                offenders.Add($"{Name(file)}：摘要 {summaryLength} 字元（應為 1～{MaxSummaryLength}）");
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void EveryGuide_IsWithinTheSizeLimit()
    {
        var offenders = Guides()
            .Select(f => (Name: Name(f), Length: File.ReadAllText(f).Length))
            .Where(x => x.Length > MaxGuideLength)
            .Select(x => $"{x.Name}：{x.Length} 字元");

        Assert.Empty(offenders);
    }

    [Fact]
    public void Guides_HaveNoInternalTerms()
    {
        var offenders = new List<string>();
        foreach (var file in AllPages())
        {
            var text = File.ReadAllText(file);
            foreach (var term in InternalTerms.Where(t => text.Contains(t, StringComparison.Ordinal)))
                offenders.Add($"{Name(file)}：{term}");
            foreach (Match m in MilestonePattern().Matches(text))
                offenders.Add($"{Name(file)}：里程碑編號 {m.Value}");
        }

        Assert.Empty(offenders);
    }

    [Fact(Skip = "等文件完成")]
    public void EverySettingsCategory_IsMentioned()
    {
        // 對應表要涵蓋全部列舉值，新增分類時漏掉會在這裡失敗
        Assert.Equal(
            Enum.GetValues<SettingsCategory>().OrderBy(c => c).ToArray(),
            CategoryNames.Keys.OrderBy(c => c).ToArray());

        var text = string.Concat(Guides().Select(f => File.ReadAllText(f)));
        var missing = CategoryNames.Where(kv => !text.Contains(kv.Value(), StringComparison.Ordinal)).Select(kv => kv.Key.ToString());

        Assert.Empty(missing);
    }

    [Fact(Skip = "等文件完成")]
    public void EveryBuiltInToolGroup_IsMentioned()
    {
        var text = string.Concat(Guides().Select(f => File.ReadAllText(f)));
        var missing = ToolGroups.BuiltIn.Where(g => !text.Contains(g.Name, StringComparison.Ordinal)).Select(g => g.Name);

        Assert.Empty(missing);
    }

    [Fact(Skip = "等文件完成")]
    public void EveryBuiltInTool_IsListed()
    {
        var (tools, failures) = ParseBuiltInTools();
        Assert.Empty(failures);

        var toolsDoc = File.ReadAllText(Path.Combine(GuideDir, "tools.md"));
        var missing = tools.Where(t => !toolsDoc.Contains(t.DisplayName, StringComparison.Ordinal)).Select(t => $"{t.Name}（{t.DisplayName}）");

        Assert.Empty(missing);
    }

    [Fact]
    public void ToolNameParser_FindsEveryBuiltInTool()
    {
        var (tools, failures) = ParseBuiltInTools();

        Assert.Empty(failures);
        Assert.Equal(ConcreteToolCount(), tools.Count);
        Assert.Equal(tools.Count, tools.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void SettingsPaths_PointToRealCategories()
    {
        var realNames = CategoryNames.Values.Select(f => f()).ToList();
        var offenders = new List<string>();
        foreach (var file in AllPages())
        {
            foreach (var line in File.ReadLines(file))
            {
                foreach (Match m in SettingsPathPattern().Matches(line))
                {
                    var rest = line[(m.Index + m.Length)..];
                    if (!realNames.Any(n => rest.StartsWith(n, StringComparison.Ordinal)))
                        offenders.Add($"{Name(file)}：{line.Trim()}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void RelativeLinks_Resolve()
    {
        var offenders = new List<string>();
        foreach (var file in AllPages())
        {
            var dir = Path.GetDirectoryName(file)!;
            foreach (Match m in LinkPattern().Matches(File.ReadAllText(file)))
            {
                var target = m.Groups[1].Value;
                if (target.StartsWith('#') || target.Contains("://", StringComparison.Ordinal) || target.StartsWith("mailto:", StringComparison.Ordinal))
                    continue;

                var path = Uri.UnescapeDataString(target.Split('#')[0]);
                if (path.Length == 0)
                    continue;

                if (!File.Exists(Path.GetFullPath(Path.Combine(dir, path))))
                    offenders.Add($"{Name(file)} → {target}");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Readme_ListsEveryGuide()
    {
        var readme = File.ReadAllText(Path.Combine(GuideDir, ReadmeName));
        var missing = Guides().Select(Name).Where(n => !readme.Contains($"]({n})", StringComparison.Ordinal));

        Assert.Empty(missing);
    }
}
