using System.Reflection;

namespace Lumi.Architecture.Tests;

/// <summary>Markdown 解析（Markdig）是 UI 專用的：只有 Lumi.App 引用，Core 與其他層不需要知道 Markdown。</summary>
public class MarkdownRuleTests
{
    private static string[] ReferencedAssemblyNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    [Fact]
    public void App_does_reference_markdig()
    {
        Assert.Contains("Markdig", ReferencedAssemblyNames(typeof(Lumi.App.Markdown.MarkdownParser).Assembly));
    }

    [Theory]
    [InlineData("Lumi.Core")]
    [InlineData("Lumi.Providers")]
    [InlineData("Lumi.Infrastructure")]
    [InlineData("Lumi.Platform")]
    public void Other_assemblies_do_not_reference_markdig(string name)
    {
        Assert.DoesNotContain("Markdig", ReferencedAssemblyNames(Assembly.Load(name)));
    }
}
