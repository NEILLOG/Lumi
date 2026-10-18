using System.Reflection;
using Lumi.Core.Tools;

namespace Lumi.Architecture.Tests;

/// <summary>工具的歸屬與寫法（agent.md 第 8 節）：Core 只放介面與基底類別，內建工具都在 Lumi.Tools 並繼承 <c>ToolBase&lt;&gt;</c>。</summary>
public class ToolRuleTests
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
        Lumi.Tools.ToolsServiceCollectionExtensions.AddLumiTools(services);
        var registered = services.Where(d => d.ServiceType == typeof(ITool)).Select(d => d.ImplementationType).ToHashSet();

        var all = ConcreteTools(typeof(Lumi.Tools.ToolsServiceCollectionExtensions).Assembly).ToList();

        Assert.Equal(all.Count, registered.Count);
        Assert.All(all, t => Assert.Contains(t, registered));
    }
}
