using System.Reflection;
using System.Text.RegularExpressions;
using Lumi.Core.Tools;

namespace Lumi.Architecture.Tests;

/// <summary>
/// M17：解析網頁、PAC、RSS 的套件（SmartReader、ReverseMarkdown、AngleSharp、Jint、System.ServiceModel.Syndication）
/// 只能由 Lumi.Web 引用（測試專案 Lumi.Web.Tests 除外）；Lumi.Web 不實作任何工具（工具一律在 Lumi.Tools）。
/// </summary>
public class WebLibraryRuleTests
{
    private const string WebProject = "Lumi.Web";

    private static readonly string[] RestrictedPackages =
        ["SmartReader", "ReverseMarkdown", "AngleSharp", "Jint", "System.ServiceModel.Syndication"];

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

    private static IEnumerable<string> RestrictedIn(string projectFile) =>
        PackageReference.Matches(File.ReadAllText(projectFile)).Select(m => m.Groups[1].Value).Where(RestrictedPackages.Contains);

    [Fact]
    public void Only_the_web_projects_reference_the_parsing_packages()
    {
        var ownProject = Path.Combine(RepoPaths.Root, "src", WebProject, $"{WebProject}.csproj");
        Assert.NotEmpty(RestrictedIn(ownProject));   // 對照組

        var offenders = ProjectFiles()
            .Where(f => f != ownProject && !f.Contains($"{WebProject}.Tests"))
            .Where(f => RestrictedIn(f).Any())
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
    [InlineData("Lumi.App")]
    public void Other_assemblies_do_not_reference_the_parsing_packages(string name)
    {
        var refs = Assembly.Load(name).GetReferencedAssemblies().Select(a => a.Name!);

        Assert.DoesNotContain(refs, n => RestrictedPackages.Any(p => n == p || n.StartsWith(p + ".", StringComparison.Ordinal)));
    }

    [Fact]
    public void Lumi_Web_implements_no_tools()
    {
        var tools = Assembly.Load(WebProject).GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ITool).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .ToArray();

        Assert.Empty(tools);
    }
}
