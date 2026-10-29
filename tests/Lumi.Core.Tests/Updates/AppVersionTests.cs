using Lumi.Core.Updates;

namespace Lumi.Core.Tests.Updates;

public sealed class AppVersionTests
{
    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("v1.2.3", true)]
    [InlineData("V1.2.3", true)]
    [InlineData("1.2", true)]
    [InlineData("1.2.3.4", true)]
    [InlineData("1.2.3-rc1", true)]
    [InlineData("1.2.3+abc123", true)]
    [InlineData("1.2.3-rc1+abc", true)]
    [InlineData("1", false)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("1.x.3", false)]
    [InlineData("latest", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TryParse_AcceptsOnlyDottedNumbers(string? text, bool expected) =>
        Assert.Equal(expected, AppVersion.TryParse(text, out _));

    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("v1.10.0", "1.9.9", true)]       // 數字比較，不是字串比較
    [InlineData("2.0", "1.9.9", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("v1.0.0", "1.0.0+local", false)]
    [InlineData("1.0.0", "1.0.1", false)]
    [InlineData("1.0.0-rc1", "1.0.0", false)]   // 後綴不參與比較
    [InlineData("not-a-version", "1.0.0", false)] // 看不懂就不提醒
    [InlineData("1.0.1", "dev", false)]
    public void IsNewer_ComparesTheNumbers(string candidate, string current, bool expected) =>
        Assert.Equal(expected, AppVersion.IsNewer(candidate, current));

    [Theory]
    [InlineData("1.2.3", "v1.2.3", true)]
    [InlineData("1.2.3", "1.2.3+abc", true)]
    [InlineData("1.2.3", "1.2.4", false)]
    [InlineData("", "1.2.3", false)]
    [InlineData("dev", "DEV", true)]
    public void AreSame_IgnoresPrefixAndSuffix(string a, string b, bool expected) =>
        Assert.Equal(expected, AppVersion.AreSame(a, b));

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3+abc", "1.2.3")]
    [InlineData("1.2.3-rc1", "1.2.3-rc1")]
    [InlineData("", "0.0.0")]
    [InlineData(null, "0.0.0")]
    public void Normalize_DropsThePrefixAndBuildMetadata(string? text, string expected) =>
        Assert.Equal(expected, AppVersion.Normalize(text));

    [Fact]
    public void Current_IsAParsableVersion() => Assert.True(AppVersion.TryParse(AppVersion.Current, out _), AppVersion.Current);
}
