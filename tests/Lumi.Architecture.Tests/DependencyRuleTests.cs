using System.Reflection;
using Lumi.Core;

namespace Lumi.Architecture.Tests;

/// <summary>agent.md 第 5 節的相依方向。</summary>
public class DependencyRuleTests
{
    private static string[] References(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    [Fact]
    public void Core_DoesNotReferenceAvalonia()
    {
        var refs = References(typeof(CoreServiceCollectionExtensions).Assembly);

        Assert.DoesNotContain(refs, n => n.StartsWith("Avalonia", StringComparison.Ordinal));
    }

    [Fact]
    public void Core_HasNoLumiProjectReferences()
    {
        var refs = References(typeof(CoreServiceCollectionExtensions).Assembly);

        Assert.DoesNotContain(refs, n => n.StartsWith("Lumi.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Lumi.Providers")]
    [InlineData("Lumi.Infrastructure")]
    public void Library_ReferencesOnlyCoreAmongLumiProjects(string name)
    {
        var assembly = Assembly.Load(name);

        var lumiRefs = References(assembly).Where(n => n.StartsWith("Lumi.", StringComparison.Ordinal));

        Assert.All(lumiRefs, n => Assert.Equal("Lumi.Core", n));
    }

    [Theory]
    [InlineData("Lumi.Providers")]
    [InlineData("Lumi.Infrastructure")]
    public void Library_DoesNotReferenceAvalonia(string name)
    {
        var refs = References(Assembly.Load(name));

        Assert.DoesNotContain(refs, n => n.StartsWith("Avalonia", StringComparison.Ordinal));
    }
}
