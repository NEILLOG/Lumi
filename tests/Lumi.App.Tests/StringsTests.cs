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
}
