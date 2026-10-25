using System.Reflection;
using System.Text.RegularExpressions;
using Lumi.Core.Tools;

namespace Lumi.Architecture.Tests;

/// <summary>
/// M16：MCP 的 SDK（ModelContextProtocol）只能由 Lumi.Mcp 引用（測試專案 Lumi.Mcp.Tests 除外）；
/// Lumi.Mcp 裡只有 <c>McpTool</c> 一個型別實作 <see cref="ITool"/>。
/// </summary>
public class McpLibraryRuleTests
{
    private const string SdkPackagePrefix = "ModelContextProtocol";
    private const string McpProject = "Lumi.Mcp";

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

    [Fact]
    public void Only_the_mcp_projects_reference_the_sdk_package()
    {
        var ownProject = Path.Combine(RepoPaths.Root, "src", McpProject, $"{McpProject}.csproj");
        var own = PackageReference.Matches(File.ReadAllText(ownProject)).Select(m => m.Groups[1].Value).ToArray();
        Assert.Contains(own, n => n.StartsWith(SdkPackagePrefix, StringComparison.Ordinal));   // 對照組

        var offenders = ProjectFiles()
            .Where(f => f != ownProject && !f.Contains($"{McpProject}.Tests"))
            .Where(f => PackageReference.Matches(File.ReadAllText(f)).Any(m => m.Groups[1].Value.StartsWith(SdkPackagePrefix, StringComparison.Ordinal)))
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
    [InlineData("Lumi.App")]
    public void Other_assemblies_do_not_reference_the_sdk(string name)
    {
        var refs = Assembly.Load(name).GetReferencedAssemblies().Select(a => a.Name!);

        Assert.DoesNotContain(refs, n => n.StartsWith(SdkPackagePrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void Only_McpTool_implements_ITool_in_the_mcp_assembly()
    {
        var tools = Assembly.Load(McpProject).GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ITool).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToArray();

        Assert.All(tools, name => Assert.Equal("McpTool", name));
    }
}
