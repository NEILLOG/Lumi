using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.ViewModels;
using Lumi.Core.Models;

namespace Lumi.App.Tests;

/// <summary>用量面板的計算與顯示字串（ui-usage-and-topbar.md 第 3、4 節）：等級門檻、百分比、數字格式、快取命中率、上限未知。</summary>
public sealed class ConversationUsageViewModelTests
{
    private static ConversationUsageViewModel Make(bool hasData, int used, int limit, int input = 0, int cacheRead = 0, int output = 0,
        int turns = 0, ConnectionStatus connection = ConnectionStatus.Ok, Func<Task>? retry = null)
    {
        var vm = new ConversationUsageViewModel(retry);
        vm.Update(new ConversationUsageSnapshot(hasData, used, limit, input, cacheRead, output, turns, "模型", "Deepseek", connection));
        return vm;
    }

    private static string Format(string format, params object[] args) => string.Format(System.Globalization.CultureInfo.CurrentCulture, format, args);

    [Theory]
    [InlineData(0, "0%", UsageLevel.Normal)]
    [InlineData(1, "<1%", UsageLevel.Normal)]          // 有用量但不到 1%
    [InlineData(590, "59%", UsageLevel.Normal)]
    [InlineData(600, "60%", UsageLevel.Warning)]
    [InlineData(840, "84%", UsageLevel.Warning)]
    [InlineData(850, "85%", UsageLevel.Danger)]
    [InlineData(1000, "100%", UsageLevel.Danger)]
    public void PercentAndLevel_AtTheDocumentedThresholds(int used, string percent, UsageLevel level)
    {
        var vm = Make(hasData: true, used: used, limit: 1000);

        Assert.Equal(percent, vm.PercentText);
        Assert.Equal(level, vm.Level);
        Assert.Equal(level == UsageLevel.Danger, vm.IsNearLimit);
        Assert.Equal(level == UsageLevel.Danger, vm.IsDangerLevel);
        Assert.Equal(level == UsageLevel.Warning, vm.IsWarningLevel);
        Assert.Equal(level == UsageLevel.Normal, vm.IsNormalLevel);
    }

    [Fact]
    public void ZeroPercent_WithNoData_IsEmptyLevel_AndShowsZero()
    {
        var vm = Make(hasData: false, used: 0, limit: 1000);

        Assert.Equal(UsageLevel.Empty, vm.Level);
        Assert.Equal("0%", vm.PercentText);
        Assert.Equal(0, vm.Ratio);
        Assert.Equal(Strings.Usage_RingTipEmpty, vm.RingTooltip);
        Assert.Equal(Strings.Usage_RingNameEmpty, vm.RingAutomationName);
        Assert.False(vm.HasStatus);
    }

    [Theory]
    [InlineData(100, 0)]   // Normal：空間充足
    [InlineData(700, 1)]   // Warning：已用超過一半
    [InlineData(900, 2)]   // Danger：快滿了
    public void StatusText_FollowsTheLevel(int used, int expectedIndex)
    {
        var expected = new[] { Strings.Usage_Ok, Strings.Usage_Half, Strings.Usage_Full }[expectedIndex];
        var vm = Make(hasData: true, used: used, limit: 1000);

        Assert.Equal(expected, vm.StatusText);
        Assert.True(vm.HasStatus);
    }

    [Fact]
    public void UsedOfAndRemaining_UseTheTokenFormat_AndRemainingIsNotBelowZero()
    {
        var vm = Make(hasData: true, used: 12_000, limit: 128_000);
        Assert.Equal("12K / 128K", vm.UsedOfText);
        Assert.Equal(Format(Strings.Usage_RemainingFormat, "116K"), vm.RemainingText);

        var over = Make(hasData: true, used: 1_200, limit: 1_000);
        Assert.Equal(Format(Strings.Usage_RemainingFormat, "0"), over.RemainingText);
    }

    [Theory]
    [InlineData(31, "31")]
    [InlineData(950, "950")]
    [InlineData(1000, "1K")]
    [InlineData(9_600, "9.6K")]
    [InlineData(118_000, "118K")]
    [InlineData(2_500_000, "2.5M")]
    public void Tokens_UseTheDocumentedShortFormat(int tokens, string text)
    {
        var vm = Make(hasData: true, used: tokens, limit: 0, input: tokens, output: tokens, turns: tokens);

        Assert.Equal(text, vm.UsedOfText);
        Assert.Equal(text, vm.InputText);
        Assert.Equal(text, vm.OutputText);
        Assert.Equal(text, vm.TurnsText);
    }

    [Fact]
    public void ContextWindowZero_HidesPercentAndRing_ButKeepsTheCumulativeNumbers()
    {
        var vm = Make(hasData: true, used: 12_000, limit: 0, input: 20_000, cacheRead: 5_000, output: 300, turns: 2);

        Assert.False(vm.HasContextLimit);
        Assert.Equal(UsageLevel.Empty, vm.Level);
        Assert.Equal(0, vm.Ratio);
        Assert.Equal("", vm.PercentText);
        Assert.Equal("", vm.RemainingText);
        Assert.Equal("", vm.StatusText);
        Assert.False(vm.HasStatus);
        Assert.Equal("12K", vm.UsedOfText);
        Assert.Equal("20K", vm.InputText);
        Assert.Equal("300", vm.OutputText);
        Assert.Equal("2", vm.TurnsText);
        Assert.Equal(Strings.Usage_Title, vm.RingTooltip);
        Assert.Equal(Strings.Usage_Title, vm.RingAutomationName);
        Assert.False(vm.IsNearLimit);
    }

    [Fact]
    public void RingTooltipAndName_CarryThePercent_WhenThereIsData()
    {
        var vm = Make(hasData: true, used: 90, limit: 1000);

        Assert.Equal(Format(Strings.Usage_RingTipFormat, "9%"), vm.RingTooltip);
        Assert.Equal(Format(Strings.Usage_RingNameFormat, "9%"), vm.RingAutomationName);
    }

    [Fact]
    public void RingTooltip_ShowsLessThanOnePercent_ForTinyUsage()
    {
        var vm = Make(hasData: true, used: 1, limit: 1000);

        Assert.Equal(Format(Strings.Usage_RingTipFormat, Strings.Usage_LessThanOnePercent), vm.RingTooltip);
    }

    // 三家供應商的 TokenUsage 已統一成「InputTokens 不含快取」，所以總輸入 = Input + creation + read
    [Theory]
    // Anthropic：input_tokens 100 + creation 50 + read 200 = 350；命中 200 ÷ 350 ≈ 57%
    [InlineData(350, 200, "57")]
    // OpenAI 相容：prompt 1000、其中快取 800 → 轉換後 InputTokens 200 + read 800；總輸入 1000，命中 80%
    [InlineData(1000, 800, "80")]
    // DeepSeek：prompt 500、快取命中 300 → 總輸入 500，命中 60%
    [InlineData(500, 300, "60")]
    public void CacheRatio_IsReadOverTotalInputIncludingCache_ForEachProvider(int totalInput, int cacheRead, string percent)
    {
        var vm = Make(hasData: true, used: 10, limit: 1000, input: totalInput, cacheRead: cacheRead);

        Assert.Equal(Format(Strings.Usage_CacheFormat, percent), vm.CacheRatioText);
        Assert.True(vm.ShowCacheRing);
    }

    [Fact]
    public void ZeroCumulativeCacheRead_ShowsTheInputArrow_NotTheRing()
    {
        var vm = Make(hasData: true, used: 10, limit: 1000, input: 500, cacheRead: 0, output: 10);

        Assert.False(vm.ShowCacheRing);
        Assert.Equal(0, vm.CacheRatio);
    }

    [Fact]
    public void ConnectionText_FollowsTheStatus_AndRetryOnlyWhenFailed()
    {
        var ok = Make(hasData: true, used: 1, limit: 1000, connection: ConnectionStatus.Ok);
        Assert.Equal(Format(Strings.Usage_ConnectedFormat, "Deepseek"), ok.ConnectionText);
        Assert.False(ok.ShowRetry);

        var unknown = Make(hasData: true, used: 1, limit: 1000, connection: ConnectionStatus.Unknown);
        Assert.Equal(Strings.Usage_Unknown, unknown.ConnectionText);
        Assert.True(unknown.IsConnectionUnknown);
        Assert.False(unknown.ShowRetry);

        var failed = Make(hasData: true, used: 1, limit: 1000, connection: ConnectionStatus.Failed);
        Assert.Equal(Strings.Usage_Failed, failed.ConnectionText);
        Assert.True(failed.ShowRetry);
    }

    [Fact]
    public async Task RetryCommand_RunsTheInjectedRetry()
    {
        var calls = 0;
        var vm = Make(hasData: true, used: 1, limit: 1000, connection: ConnectionStatus.Failed, retry: () => { calls++; return Task.CompletedTask; });

        await vm.RetryCommand.ExecuteAsync(null);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void UpdateRaisesChangeNotifications_ForTheComputedValues()
    {
        var vm = Make(hasData: false, used: 0, limit: 1000);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Update(new ConversationUsageSnapshot(true, 900, 1000, 0, 0, 0, 1, "m", "c", ConnectionStatus.Ok));

        Assert.Contains(nameof(ConversationUsageViewModel.Level), changed);
        Assert.Contains(nameof(ConversationUsageViewModel.PercentText), changed);
        Assert.Contains(nameof(ConversationUsageViewModel.IsNearLimit), changed);
        Assert.Contains(nameof(ConversationUsageViewModel.RingAutomationName), changed);
    }
}
