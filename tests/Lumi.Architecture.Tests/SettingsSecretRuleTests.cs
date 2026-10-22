using System.Reflection;
using System.Text.RegularExpressions;
using Lumi.Core.Models;
using Lumi.Core.Secrets;

namespace Lumi.Architecture.Tests;

/// <summary>設定檔的型別裡不能有任何金鑰（agent.md 第 10 節：金鑰只能存在 ISecretStore）。</summary>
public class SettingsSecretRuleTests
{
    private static IEnumerable<Type> ReachableTypes(Type root)
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>([root]);
        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (!seen.Add(type))
                continue;
            yield return type;

            var next = type.IsGenericType ? type.GetGenericArguments() : [];
            var props = type.Namespace?.StartsWith("Lumi", StringComparison.Ordinal) == true
                ? type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.PropertyType)
                : [];
            foreach (var t in next.Concat(props))
                queue.Enqueue(Nullable.GetUnderlyingType(t) ?? t);
        }
    }

    [Fact]
    public void AppSettings_does_not_reach_SecretValue()
    {
        var types = ReachableTypes(typeof(AppSettings)).ToList();

        Assert.Contains(typeof(ConnectionSettings), types);   // 對照組：確實走到了連線設定
        Assert.DoesNotContain(typeof(SecretValue), types);
    }

    /// <summary>log 呼叫（<c>_logger.LogXxx(...)</c>）的參數裡有 <c>.Reveal()</c> 就是把 Token 寫進 log。</summary>
    private static readonly Regex LogWithReveal = new(@"\bLog\w*\s*\([^;]*\.Reveal\(\)", RegexOptions.Singleline);

    [Fact]
    public void Speech_log_calls_never_reveal_the_token()
    {
        // 對照組：確認這個寫法真的抓得到 log 裡的 Reveal()，否則下面的「沒有」沒有意義
        Assert.Matches(LogWithReveal, "_logger.LogWarning(ex, token.Reveal());");

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "src", "Lumi.Speech"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(RepoPaths.Root, file);
            var parts = relative.Split(Path.DirectorySeparatorChar);
            if (parts.Contains("obj") || parts.Contains("bin"))
                continue;
            if (LogWithReveal.IsMatch(File.ReadAllText(file)))
                offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0, "Lumi.Speech 的 log 呼叫不可包含 Reveal()：\n" + string.Join("\n", offenders));
    }
}
