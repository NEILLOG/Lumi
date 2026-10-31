using System.Globalization;
using Lumi.Core.Chat;
using Lumi.Core.Memory;
using Lumi.Core.Models;
using Lumi.Core.Prompts;

namespace Lumi.Core.Tests.Prompts;

/// <summary>第 6 層：排程自動執行的說明（M24 4.5）。Run 為 null 時整個組成必須與以前逐字相同。</summary>
public sealed class ScheduledRunPromptLayerTests
{
    private static readonly DateTimeOffset Planned = DateTimeOffset.Parse("2026-10-23T09:00+08:00", CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset Started = DateTimeOffset.Parse("2026-10-23T09:02+08:00", CultureInfo.InvariantCulture);

    /// <summary>以前的五層（不含排程層）。</summary>
    private static PromptComposer WithoutScheduledLayer() => new(
        [new BasePromptLayer(), new PersonaPromptLayer(), new MemoryPromptLayer(), new ModeFormatPromptLayer(), new SpriteVoicePromptLayer(new NoSpriteVoiceProvider())]);

    private static PromptComposer WithScheduledLayer() => new(
        [new BasePromptLayer(), new PersonaPromptLayer(), new MemoryPromptLayer(), new ModeFormatPromptLayer(),
            new SpriteVoicePromptLayer(new NoSpriteVoiceProvider()), new ScheduledRunPromptLayer()]);

    [Theory]
    [InlineData(ChatMode.Workbench)]
    [InlineData(ChatMode.Sprite)]
    public void RunIsNull_OutputIsByteIdenticalToBefore(ChatMode mode)
    {
        var memory = new MemoryPromptContext([]);
        var context = new PromptContext(mode, PersonaSettings.Default, memory, null);

        var before = WithoutScheduledLayer().Compose(context);
        var after = WithScheduledLayer().Compose(context);

        Assert.Equal(before.System, after.System);
        Assert.Equal(before.TurnSuffix, after.TurnSuffix);
        Assert.Null(new ScheduledRunPromptLayer().Render(context));
    }

    [Fact]
    public void RunIsNull_NoSuffixAtAllWhenNoModeLayerRenders()
    {
        var context = new PromptContext(ChatMode.Workbench, PersonaSettings.Default);
        var composer = new PromptComposer([new ScheduledRunPromptLayer()]);

        Assert.Equal(ComposedPrompt.Empty, composer.Compose(context));
    }

    [Fact]
    public void RunPresent_RendersScheduleNameAndLocalTimes()
    {
        var info = new ScheduledRunInfo("晨間整理", Planned, Started);
        var text = new ScheduledRunPromptLayer().Render(new PromptContext(ChatMode.Workbench, PersonaSettings.Default, null, info));

        Assert.NotNull(text);
        Assert.StartsWith("<lumi_scheduled_run>\n", text);
        Assert.EndsWith("\n</lumi_scheduled_run>", text);
        Assert.Contains("「晨間整理」", text);
        Assert.Contains(Planned.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), text);
        Assert.Contains(Started.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), text);
        Assert.DoesNotContain("{schedule_name}", text);
        Assert.DoesNotContain("{planned_at}", text);
        Assert.DoesNotContain("{started_at}", text);
    }

    [Fact]
    public void RunPresent_IsAppendedAfterTheModeLayer()
    {
        var info = new ScheduledRunInfo("晨間整理", Planned, Started);
        var suffix = WithScheduledLayer().Compose(new PromptContext(ChatMode.Workbench, PersonaSettings.Default, null, info)).TurnSuffix;

        Assert.NotNull(suffix);
        var mode = suffix.IndexOf("目前模式", StringComparison.Ordinal);
        var scheduled = suffix.IndexOf("<lumi_scheduled_run>", StringComparison.Ordinal);
        Assert.True(mode >= 0 && scheduled > mode);
    }

    [Fact]
    public void RunPresent_ScheduleLayerIsTheTurnSuffixNotSystem()
    {
        var info = new ScheduledRunInfo("晨間整理", Planned, Started);
        var composed = WithScheduledLayer().Compose(new PromptContext(ChatMode.Workbench, PersonaSettings.Default, null, info));

        Assert.DoesNotContain("<lumi_scheduled_run>", composed.System ?? string.Empty);
        Assert.Contains("<lumi_scheduled_run>", composed.TurnSuffix ?? string.Empty);
    }

    [Fact]
    public void ScheduledRunText_KeepsAllThreePlaceholders()
    {
        var text = PromptText.Get("scheduled-run");

        Assert.Contains("{schedule_name}", text);
        Assert.Contains("{planned_at}", text);
        Assert.Contains("{started_at}", text);
    }
}
