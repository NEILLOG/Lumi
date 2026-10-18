using System.Reflection;

namespace Lumi.Architecture.Tests;

/// <summary>SQLite 只在 Lumi.Infrastructure（agent.md 第 5 節：Infrastructure 實作 Core 的儲存介面）。</summary>
public class SqliteRuleTests
{
    private const string SqliteAssembly = "Microsoft.Data.Sqlite";

    private static string[] ReferencedAssemblyNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

    [Fact]
    public void Infrastructure_does_reference_sqlite()
    {
        var refs = ReferencedAssemblyNames(typeof(Lumi.Infrastructure.InfrastructureServiceCollectionExtensions).Assembly);

        Assert.Contains(SqliteAssembly, refs);
    }

    [Theory]
    [InlineData("Lumi.Core")]
    [InlineData("Lumi.Providers")]
    [InlineData("Lumi.Platform")]
    [InlineData("Lumi.Tools")]
    [InlineData("Lumi.App")]
    public void Other_assemblies_do_not_reference_sqlite(string name)
    {
        Assert.DoesNotContain(SqliteAssembly, ReferencedAssemblyNames(Assembly.Load(name)));
    }
}
