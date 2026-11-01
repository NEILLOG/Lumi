using System.Reflection;
using System.Text.RegularExpressions;
using Lumi.Core.Tools;

namespace Lumi.Architecture.Tests;

/// <summary>
/// M25：TarotMcp.Core（git submodule）只能由 Lumi.Tarot 引用（測試專案 Lumi.Tarot.Tests 除外）；
/// Lumi.Tarot 裡實作 <see cref="ITool"/> 的只有 TarotDrawTool、TarotCardTool。
/// </summary>
public class TarotLibraryRuleTests
{
    private const string TarotCore = "TarotMcp.Core";
    private const string TarotProject = "Lumi.Tarot";

    private static readonly Regex ProjectReference = new("<ProjectReference\\s+Include=\"([^\"]+)\"", RegexOptions.Compiled);

    private static IEnumerable<string> ProjectFiles()
    {
        foreach (var folder in new[] { "src", "tests" })
        {
            var dir = Path.Combine(RepoPaths.Root, folder);
            foreach (var file in Directory.EnumerateFiles(dir, "*.csproj", SearchOption.AllDirectories))
            {
                var parts = file.Split(Path.DirectorySeparatorChar);
                if (parts.Contains("bin") || parts.Contains("obj"))
                    continue;
                yield return file;
            }
        }
    }

    private static bool ReferencesTarotCore(string projectFile) =>
        ProjectReference.Matches(File.ReadAllText(projectFile)).Any(m => m.Groups[1].Value.Contains(TarotCore, StringComparison.Ordinal));

    [Fact]
    public void Only_Lumi_Tarot_references_TarotMcp_core()
    {
        var ownProject = Path.Combine(RepoPaths.Root, "src", TarotProject, $"{TarotProject}.csproj");
        Assert.True(ReferencesTarotCore(ownProject));   // 對照組

        var offenders = ProjectFiles()
            .Where(f => f != ownProject && !f.Contains($"{TarotProject}.Tests"))
            .Where(ReferencesTarotCore)
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("Lumi.Core")]
    [InlineData("Lumi.Tools")]
    [InlineData("Lumi.Providers")]
    [InlineData("Lumi.Infrastructure")]
    [InlineData("Lumi.Platform")]
    [InlineData("Lumi.Speech")]
    [InlineData("Lumi.Documents")]
    [InlineData("Lumi.Mcp")]
    [InlineData("Lumi.Web")]
    [InlineData("Lumi.Browser")]
    [InlineData("Lumi.App")]
    public void Other_assemblies_do_not_reference_TarotMcp_core(string name)
    {
        var refs = Assembly.Load(name).GetReferencedAssemblies().Select(a => a.Name!);

        Assert.DoesNotContain(TarotCore, refs);
    }

    [Fact]
    public void Only_the_two_tarot_tools_implement_ITool()
    {
        var tools = Assembly.Load(TarotProject).GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ITool).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToArray();

        Assert.Equal(["TarotCardTool", "TarotDrawTool"], tools.Order());
    }
}
