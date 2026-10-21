using Lumi.Core.Models;

namespace Lumi.App.Tests.Theming;

public sealed class ThemeIdTests
{
    [Fact]
    public void Palettes_AreTheFiveFromTheGuide_WithSageAsDefault()
    {
        Assert.Equal(["sage", "teal", "mist-blue", "indigo", "terracotta"], ThemePalettes.All);
        Assert.Equal("sage", ThemePalettes.Default);
    }

    [Theory]
    [InlineData(null, "sage")]
    [InlineData("", "sage")]
    [InlineData("neon", "sage")]
    [InlineData("Teal", "sage")]        // 大小寫不同視為不認得
    [InlineData("terracotta", "terracotta")]
    public void Normalize_FallsBackToSage_ForUnknownOrMissingIds(string? id, string expected) =>
        Assert.Equal(expected, ThemePalettes.Normalize(id));

    [Fact]
    public void AppSettings_DefaultsToSage()
    {
        Assert.Equal("sage", AppSettings.Empty.ThemeId);
    }
}
