using System.Reflection;
using Lumi.App.Resources;

namespace Lumi.App.Tests;

public class StringsTests
{
    // Strings.cs 是手寫的包裝，resx 與它要同步；漏掉時 Get 會回 "!名稱!"
    [Fact]
    public void Every_string_property_has_a_resource()
    {
        var missing = typeof(Strings)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(string))
            .Where(p => ((string)p.GetValue(null)!).StartsWith('!'))
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(missing);
    }

    // Core 不能用 App 的資源檔，ChatSession 內建同一句話；兩邊要同字
    [Fact]
    public void AppFilesMissing_message_matches_the_core_constant() =>
        Assert.Equal(Lumi.Core.Chat.ChatSession.AppFilesMissingMessage, Strings.Chat_ErrorAppFilesMissing);
}
