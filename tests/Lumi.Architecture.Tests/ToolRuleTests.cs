using System.Reflection;
using System.Text.RegularExpressions;
using Lumi.Core.Tools;

namespace Lumi.Architecture.Tests;

/// <summary>工具的歸屬與寫法（agent.md 第 8 節）：Core 只放介面與基底類別，內建工具都在 Lumi.Tools 並繼承 <c>ToolBase&lt;&gt;</c>。</summary>
public partial class ToolRuleTests
{
    private static IEnumerable<Type> ConcreteTools(Assembly assembly) =>
        assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ITool).IsAssignableFrom(t));

    private static bool InheritsToolBase(Type type)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ToolBase<>))
                return true;
        }
        return false;
    }

    [Fact]
    public void Tools_assembly_does_contain_tools_so_the_detector_works()
    {
        Assert.NotEmpty(ConcreteTools(typeof(Lumi.Tools.ToolsServiceCollectionExtensions).Assembly));
    }

    [Fact]
    public void Every_builtin_tool_inherits_ToolBase()
    {
        var offenders = ConcreteTools(typeof(Lumi.Tools.ToolsServiceCollectionExtensions).Assembly).Where(t => !InheritsToolBase(t));

        Assert.Empty(offenders.Select(t => t.FullName));
    }

    [Theory]
    [InlineData("Lumi.Core")]
    [InlineData("Lumi.Providers")]
    [InlineData("Lumi.Infrastructure")]
    [InlineData("Lumi.Platform")]
    [InlineData("Lumi.App")]
    public void Other_assemblies_implement_no_tools(string name)
    {
        Assert.Empty(ConcreteTools(Assembly.Load(name)).Select(t => t.FullName));
    }

    [Fact]
    public void Every_builtin_tool_is_registered_by_AddLumiTools()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Lumi.Tools.ToolsServiceCollectionExtensions.AddLumiTools(services, o => o.EnableDemoTools = true);   // 示範工具預設關；這裡要檢查所有工具
        var registered = services.Where(d => d.ServiceType == typeof(ITool)).Select(d => d.ImplementationType).ToHashSet();

        var all = ConcreteTools(typeof(Lumi.Tools.ToolsServiceCollectionExtensions).Assembly).ToList();

        Assert.Equal(all.Count, registered.Count);
        Assert.All(all, t => Assert.Contains(t, registered));
    }

    // ---- 檔案存取一律經過範圍（agent.md 第 8 節） ----

    [GeneratedRegex(@"\b(?:File|Directory|FileInfo|DirectoryInfo|FileStream)\.|new\s+(?:FileInfo|DirectoryInfo|FileStream)\(|TextFiles\.Open\(")]
    private static partial Regex FileSystemAccess();

    /// <summary>示範用的筆記工具有自己的資料夾規則（M10 有真正的寫入類工具後移除），不走範圍。</summary>
    private static readonly string[] DemoToolFiles = ["SaveNoteTool.cs", "DeleteNoteTool.cs"];

    private static IEnumerable<string> ToolSourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "src", "Lumi.Tools"), "*Tool.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    [Fact]
    public void Detector_finds_filesystem_access_in_the_file_tools()
    {
        // 對照組：偵測方式真的找得到，下面的「沒有」測試才有意義
        var read = File.ReadAllText(ToolSourceFiles().Single(f => f.EndsWith("ReadFileTool.cs", StringComparison.Ordinal)));

        Assert.Matches(FileSystemAccess(), read);
    }

    [Fact]
    public void Tools_that_touch_the_filesystem_resolve_paths_through_context_Scope()
    {
        var offenders = ToolSourceFiles()
            .Where(f => !DemoToolFiles.Contains(Path.GetFileName(f)))
            .Select(f => (File: f, Text: File.ReadAllText(f)))
            .Where(x => FileSystemAccess().IsMatch(x.Text) && !x.Text.Contains("context.Scope", StringComparison.Ordinal))
            .Select(x => Path.GetRelativePath(RepoPaths.Root, x.File));

        Assert.Empty(offenders);
    }
}
