using System.Reflection;
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
}
