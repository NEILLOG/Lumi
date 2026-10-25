using System.Reflection;
using System.Text.RegularExpressions;
using Lumi.Core.Tools;

namespace Lumi.Architecture.Tests;

/// <summary>
/// M18：瀏覽器自動化套件（Microsoft.Playwright）只能由 Lumi.Browser 引用（測試專案 Lumi.Browser.Tests 除外）；
/// Lumi.Browser 不實作任何工具（工具一律在 Lumi.Tools，經由 Core 的 <c>IBrowserSessions</c> 使用它）。
/// </summary>
public class BrowserLibraryRuleTests
{
    private const string BrowserProject = "Lumi.Browser";
    private const string Package = "Microsoft.Playwright";

    private static readonly Regex PackageReference = new("<PackageReference\\s+Include=\"([^\"]+)\"", RegexOptions.Compiled);

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

    private static bool References(string projectFile) =>
        PackageReference.Matches(File.ReadAllText(projectFile)).Any(m => m.Groups[1].Value == Package);

    [Fact]
    public void Only_the_browser_projects_reference_playwright()
    {
        var ownProject = Path.Combine(RepoPaths.Root, "src", BrowserProject, $"{BrowserProject}.csproj");
        Assert.True(References(ownProject));   // 對照組

        var offenders = ProjectFiles()
            .Where(f => f != ownProject && !f.Contains($"{BrowserProject}.Tests"))
            .Where(References)
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
    [InlineData("Lumi.App")]
    public void Other_assemblies_do_not_reference_playwright(string name)
    {
        var refs = Assembly.Load(name).GetReferencedAssemblies().Select(a => a.Name!);

        Assert.DoesNotContain(refs, n => n == Package);
    }

    [Fact]
    public void Lumi_Browser_implements_no_tools()
    {
        var tools = Assembly.Load(BrowserProject).GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ITool).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .ToArray();

        Assert.Empty(tools);
    }
}
