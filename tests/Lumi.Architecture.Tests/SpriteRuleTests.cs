using System.Reflection;
using System.Text.RegularExpressions;

namespace Lumi.Architecture.Tests;

/// <summary>
/// 殼與角色的界線（docs/sprite-dev-guide.md 第 1 節）：角色只認識 <c>Lumi.Sprites.Abstractions</c>，
/// 不知道對話、不碰視窗與平台、不自己開計時器。
/// </summary>
public partial class SpriteRuleTests
{
    private static readonly string[] SpriteAssemblies = ["Lumi.Sprites.Abstractions", "Lumi.Sprites.Orb"];

    private static string[] LumiReferences(string name) =>
        Assembly.Load(name).GetReferencedAssemblies().Select(a => a.Name!).Where(n => n.StartsWith("Lumi.", StringComparison.Ordinal)).ToArray();

    [Fact]
    public void Abstractions_ReferencesNoLumiProject()
    {
        Assert.Empty(LumiReferences("Lumi.Sprites.Abstractions"));
    }

    [Fact]
    public void Orb_ReferencesOnlyAbstractionsAmongLumiProjects()
    {
        Assert.Equal(["Lumi.Sprites.Abstractions"], LumiReferences("Lumi.Sprites.Orb"));
    }

    [Theory]
    [InlineData("Lumi.Core")]
    [InlineData("Lumi.Providers")]
    [InlineData("Lumi.Infrastructure")]
    [InlineData("Lumi.Platform")]
    [InlineData("Lumi.Tools")]
    public void TheCoreAndLibraries_DoNotReferenceSprites(string name)
    {
        Assert.DoesNotContain(LumiReferences(name), n => n.StartsWith("Lumi.Sprites", StringComparison.Ordinal));
    }

    [Fact]
    public void Sprites_DoNotReferenceTheApp()
    {
        foreach (var name in SpriteAssemblies)
            Assert.DoesNotContain("Lumi.App", LumiReferences(name));
    }

    [GeneratedRegex(@"\b(?:DispatcherTimer|DllImport|LibraryImport|Stopwatch|Window|Application|Dispatcher)\b")]
    private static partial Regex Forbidden();

    [Fact]
    public void SpriteSource_HasNoWindowsPlatformCallsOrTimers()
    {
        var offenders = new List<string>();
        var files = SpriteAssemblies.SelectMany(name =>
            Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "src", name), "*.cs", SearchOption.AllDirectories));
        foreach (var file in files)
        {
            var parts = file.Split(Path.DirectorySeparatorChar);
            if (parts.Contains("obj") || parts.Contains("bin"))
                continue;

            var lineNo = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNo++;
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                    continue;   // 註解可以提到它們
                if (Forbidden().IsMatch(line))
                    offenders.Add($"{Path.GetRelativePath(RepoPaths.Root, file)}:{lineNo}: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0, "角色原始碼出現視窗、平台或計時器：\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void TheDevGuide_RecordsTheCurrentAbstractionsVersion()
    {
        var guide = File.ReadAllText(Path.Combine(RepoPaths.Root, "docs", "sprite-dev-guide.md"));

        Assert.Matches($@"\|\s*{Regex.Escape(Lumi.Sprites.Abstractions.SpriteAbstractionsVersion.Current)}\s*\|", guide);
    }
}
