using System.Reflection;
using System.Text.RegularExpressions;
using Lumi.Core.Tools;

namespace Lumi.Architecture.Tests;

/// <summary>
/// Outlook 的 COM 套件（Microsoft.Office.Interop.Outlook）只能由 Lumi.Outlook 引用；
/// Lumi.Outlook 不實作任何工具（工具一律在 Lumi.Tools，經由 Core 的 <c>IOutlookClient</c> 使用它）。
/// 型別是嵌入的：專案檔只允許 Lumi.Outlook 引用，產物裡也不能留下對互通組件（含它相依的 office）的引用。
/// </summary>
public class OutlookLibraryRuleTests
{
    private const string OutlookProject = "Lumi.Outlook";
    private const string Package = "Microsoft.Office.Interop.Outlook";

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
    public void Only_the_outlook_project_references_the_interop_package()
    {
        var ownProject = Path.Combine(RepoPaths.Root, "src", OutlookProject, $"{OutlookProject}.csproj");
        Assert.True(References(ownProject));   // 對照組

        var offenders = ProjectFiles()
            .Where(f => f != ownProject)
            .Where(References)
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Lumi_Outlook_implements_no_tools()
    {
        var tools = Assembly.Load(OutlookProject).GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ITool).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .ToArray();

        Assert.Empty(tools);
    }

    private static readonly string[] ForbiddenAssemblies = ["Microsoft.Office.Interop.Outlook", "office"];

    [Fact]
    public void Lumi_Outlook_assembly_does_not_reference_the_interop_assemblies()
    {
        // 型別沒有嵌入的話，Windows 上載入時會找不到 office.dll，所有 Outlook 工具都會失敗
        var referenced = Assembly.Load(OutlookProject).GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => ForbiddenAssemblies.Contains(n, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(referenced);
    }

    [Fact]
    public void Outlook_output_and_dependency_list_do_not_ship_the_interop_assemblies()
    {
        var dir = Path.GetDirectoryName(Assembly.Load(OutlookProject).Location)!;

        var files = Directory.EnumerateFiles(dir, "*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => ForbiddenAssemblies.Contains(n!, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        Assert.Empty(files);

        foreach (var deps in Directory.EnumerateFiles(dir, "*.deps.json"))
        {
            var text = File.ReadAllText(deps);
            foreach (var name in ForbiddenAssemblies)
                Assert.DoesNotContain($"\"{name}/", text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
