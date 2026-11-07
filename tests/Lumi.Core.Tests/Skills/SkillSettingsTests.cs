using Lumi.Core.Skills;

namespace Lumi.Core.Tests.Skills;

/// <summary>M29 D1：停用清單的讀寫、改名與正規化。</summary>
public sealed class SkillSettingsTests
{
    [Fact]
    public void Default_HasNothingDisabled()
    {
        Assert.Empty(SkillSettings.Default.Disabled);
        Assert.False(SkillSettings.Default.IsDisabled("週報"));
    }

    [Fact]
    public void IsDisabled_IgnoresCase()
    {
        var settings = new SkillSettings { Disabled = ["Weekly"] };

        Assert.True(settings.IsDisabled("weekly"));
        Assert.False(settings.IsDisabled("weekly2"));
    }

    [Fact]
    public void WithEnabled_False_AddsAndTrue_Removes()
    {
        var disabled = SkillSettings.Default.WithEnabled("週報", false);
        Assert.True(disabled.IsDisabled("週報"));

        var enabled = disabled.WithEnabled("週報", true);
        Assert.False(enabled.IsDisabled("週報"));
        Assert.Empty(enabled.Disabled);
    }

    [Fact]
    public void WithEnabled_IsCaseInsensitive_NoDuplicates()
    {
        var settings = SkillSettings.Default.WithEnabled("abc", false).WithEnabled("ABC", false);

        Assert.Equal(["ABC"], settings.Disabled);   // 同名不分大小寫只留一筆，保留最後寫入的寫法
        Assert.Empty(settings.WithEnabled("Abc", true).Disabled);
    }

    [Fact]
    public void WithRenamed_MovesTheDisabledState()
    {
        var settings = SkillSettings.Default.WithEnabled("週報", false).WithRenamed("週報", "週報2");

        Assert.False(settings.IsDisabled("週報"));
        Assert.True(settings.IsDisabled("週報2"));
    }

    [Fact]
    public void WithRenamed_WhenEnabled_DoesNothing()
    {
        var settings = SkillSettings.Default.WithEnabled("其他", false);

        Assert.Equal(settings, settings.WithRenamed("週報", "週報2"));
    }

    [Fact]
    public void Normalize_TrimsDeduplicatesAndSorts()
    {
        var settings = new SkillSettings { Disabled = [" b ", "A", "a", "", "  ", "c"] }.Normalize();

        Assert.Equal(["A", "b", "c"], settings.Disabled);
    }

    [Fact]
    public void Normalize_NullListIsEmpty()
    {
        var settings = new SkillSettings { Disabled = null! }.Normalize();

        Assert.Empty(settings.Disabled);
    }
}
