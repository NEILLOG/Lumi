using System.Reflection;

namespace Lumi.Architecture.Tests;

/// <summary>供應商 SDK（目前是 Anthropic 官方 SDK）只能出現在 Lumi.Providers（agent.md 第 5 節）。</summary>
public class ProviderSdkRuleTests
{
    private static string[] ReferencedAssemblyNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    [Fact]
    public void Providers_does_reference_the_anthropic_sdk()
    {
        // 對照組：確認偵測方式真的找得到
        var refs = ReferencedAssemblyNames(typeof(Lumi.Providers.ProvidersServiceCollectionExtensions).Assembly);

        Assert.Contains(refs, n => n == "Anthropic");
    }

    [Theory]
    [InlineData("Lumi.Core")]
    [InlineData("Lumi.Infrastructure")]
    [InlineData("Lumi.Platform")]
    [InlineData("Lumi.App")]
    public void Other_assemblies_do_not_reference_the_anthropic_sdk(string name)
    {
        var refs = ReferencedAssemblyNames(Assembly.Load(name));

        Assert.DoesNotContain(refs, n => n == "Anthropic");
    }
}
