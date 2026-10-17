using System.Reflection;
using System.Text.RegularExpressions;

namespace Lumi.Architecture.Tests;

/// <summary>原生呼叫（P/Invoke）只能出現在 Lumi.Platform（agent.md 第 5、14 節）。</summary>
public partial class PlatformInteropRuleTests
{
    private const BindingFlags AllMethods =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    private static IEnumerable<string> PInvokeMethods(Assembly assembly) =>
        assembly.GetTypes()
            .SelectMany(t => t.GetMethods(AllMethods))
            .Where(m => (m.Attributes & MethodAttributes.PinvokeImpl) != 0)
            .Select(m => $"{m.DeclaringType?.FullName}.{m.Name}");

    [Fact]
    public void Platform_does_contain_pinvoke_so_the_detector_works()
    {
        // 對照組：確認偵測方式真的找得到，否則下面的「沒有」測試沒有意義
        Assert.NotEmpty(PInvokeMethods(typeof(Lumi.Platform.PlatformServiceCollectionExtensions).Assembly));
    }

    [Theory]
    [InlineData("Lumi.Core")]
    [InlineData("Lumi.Providers")]
    [InlineData("Lumi.Infrastructure")]
    [InlineData("Lumi.App")]
    public void Other_assemblies_contain_no_pinvoke(string name)
    {
        Assert.Empty(PInvokeMethods(Assembly.Load(name)));
    }

    [GeneratedRegex(@"\[\s*(?:DllImport|LibraryImport)\b")]
    private static partial Regex ImportAttribute();

    [Fact]
    public void Source_outside_Platform_has_no_import_attributes()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(RepoPaths.Root, file);
            var parts = relative.Split(Path.DirectorySeparatorChar);
            if (parts.Contains("obj") || parts.Contains("bin") || parts[1] == "Lumi.Platform")
                continue;
            if (ImportAttribute().IsMatch(File.ReadAllText(file)))
                offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0, "Platform 以外出現 DllImport／LibraryImport：\n" + string.Join("\n", offenders));
    }
}
