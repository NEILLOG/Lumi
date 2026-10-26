using System.Globalization;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Models;
using Lumi.Core.Reminders;
using Lumi.Core.Secrets;

namespace Lumi.App.Tests;

/// <summary>M20 20G：設定首頁的狀態卡片（<see cref="HomeViewModel"/>）——6 張順序固定、各卡片的值與需要處理的判斷、即時更新、Dispose 後不再刷新。</summary>
public sealed class HomeViewModelTests : IDisposable
{
    private static readonly string[] SectionOrder = ["Connections", "Reminders", "Memory", "Tools", "General", "Network"];

    private readonly SettingsHost _host = new();

    public void Dispose() => _host.Dispose();

    private HomeCardViewModel Card(int index) => _host.Home.Cards[index];

    /// <summary>建一個有金鑰的連線與模型，設成工作台預設。</summary>
    private async Task UseWorkbenchModelAsync(string? apiKey = "sk-test")
    {
        var connection = new ConnectionSettings("conn1", "測試連線", ProviderFormat.Anthropic, "https://api.example.test");
        await _host.Catalog.SaveConnectionAsync(connection, apiKey is null ? null : new SecretValue(apiKey));
        await _host.Catalog.SaveModelAsync(new ModelSettings("model1", "conn1", "claude-test-1", "測試模型", 4096, 200000, true));
        await _host.Catalog.SetWorkbenchModelAsync("model1");
        await _host.Home.RefreshAsync();
    }

    private async Task CreateOneTimeReminderAsync(string title)
    {
        await _host.ReminderService.CreateReminderAsync(
            new NewReminder(title, "要做的事", new OneTimeTrigger(DateTimeOffset.UtcNow.AddHours(1)), "Asia/Taipei",
                ScheduledKind.Reminder, ScheduledCreator.User),
            default);
        await _host.Reminders.ReloadAsync();
    }

    [Fact]
    public void Cards_AreSixInFixedOrder_AndEachSectionParses()
    {
        var sections = _host.Home.Cards.Select(c => c.Section).ToList();

        Assert.Equal(SectionOrder, sections);
        foreach (var section in sections)
            Assert.True(Enum.TryParse<SettingsSection>(section, out _));
    }

    [Fact]
    public async Task WithoutAModel_ConnectionsCardAsksForOneAndNeedsAttention()
    {
        // 沒有設定檔時預設會有一個模型（JsonModelCatalog.CreateInitialSettings），這裡把它刪掉
        foreach (var model in _host.Catalog.Current.Models)
            await _host.Catalog.DeleteModelAsync(model.Id);
        await _host.Home.RefreshAsync();

        Assert.Equal(Strings.Home_NoModel, Card(0).Value);
        Assert.Null(Card(0).Detail);
        Assert.True(Card(0).NeedsAttention);
    }

    [Fact]
    public async Task WithModelKeyAndOkStatus_ShowsConnectionAndModel_AndNeedsNoAttention()
    {
        _host.Status.Mark("conn1", ConnectionStatus.Ok);

        await UseWorkbenchModelAsync();

        Assert.Equal("測試模型", Card(0).Value);
        Assert.Contains("測試連線", Card(0).Detail);
        Assert.Contains("claude-test-1", Card(0).Detail);
        Assert.Contains(Strings.Home_StatusOk, Card(0).Detail);
        Assert.False(Card(0).NeedsAttention);
    }

    [Fact]
    public async Task FailedStatus_MarksConnectionsCardAsNeedingAttention()
    {
        await UseWorkbenchModelAsync();
        Assert.False(Card(0).NeedsAttention);

        _host.Status.Mark("conn1", ConnectionStatus.Failed);
        await _host.Home.RefreshAsync();

        Assert.True(Card(0).NeedsAttention);
        Assert.Contains(Strings.Home_StatusFailed, Card(0).Detail);
    }

    [Fact]
    public async Task MissingApiKey_ShowsNoKeyAndNeedsAttention()
    {
        await UseWorkbenchModelAsync(apiKey: null);

        Assert.True(Card(0).NeedsAttention);
        Assert.Contains(Strings.Home_NoApiKey, Card(0).Detail);
    }

    [Fact]
    public async Task Reminders_CountAndNextTitleAppearAfterCreating()
    {
        Assert.Equal(Strings.Home_RemindersNone, Card(1).Value);
        Assert.Null(Card(1).Detail);

        await CreateOneTimeReminderAsync("喝水");
        await _host.Home.RefreshAsync();

        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Home_RemindersCountFormat, 1), Card(1).Value);
        Assert.Contains("喝水", Card(1).Detail);
        Assert.False(Card(1).NeedsAttention);
    }

    [Fact]
    public async Task Memory_TurningItOffChangesTheValue()
    {
        Assert.Equal(Strings.Home_MemoryOn, Card(2).Value);

        _host.Memory.IsEnabled = false;
        await SettingsHost.WaitUntil(() => !_host.Memory.IsEnabled);
        await _host.Home.RefreshAsync();

        Assert.Equal(Strings.Home_MemoryOff, Card(2).Value);
    }

    [Fact]
    public void Tools_CountMatchesAllToolItems()
    {
        var items = _host.Tools.Groups.SelectMany(g => g.Items).ToList();
        var enabled = items.Count(i => i.IsEnabled);

        Assert.True(items.Count > 0);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Home_ToolsCountFormat, enabled, items.Count), Card(3).Value);
    }

    [Fact]
    public void Appearance_ShowsThemeAndSprite()
    {
        Assert.Contains(Strings.Theme_System, Card(4).Value);
        Assert.Contains(_host.Sprite.SelectedSprite.Name, Card(4).Detail);
    }

    [Fact]
    public void Network_ShowsTheProxyModeWhichDefaultsToSystem()
    {
        Assert.Equal(Strings.Network_ModeSystem, Card(5).Value);
        Assert.Null(Card(5).Detail);
    }

    [Fact]
    public async Task AfterDispose_CardsNoLongerRefresh()
    {
        var before = Card(1).Value;

        _host.Home.Dispose();
        await CreateOneTimeReminderAsync("喝水");

        Assert.Equal(before, Card(1).Value);
    }
}
