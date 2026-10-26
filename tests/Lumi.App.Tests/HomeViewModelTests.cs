using System.Globalization;
using System.Windows.Input;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Mcp;
using Lumi.Core.Memory;
using Lumi.Core.Models;
using Lumi.Core.Reminders;
using Lumi.Core.Secrets;
using Lumi.Core.Speech;

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

    // ---- M21 21D：首頁資料 ----

    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static McpServerSettings McpServer(string id, string name, bool enabled = true) =>
        new(id, id, name, McpTransportKind.Stdio, "node", [], null, [], null, [], enabled);

    private static ToolResultBlock Result(string name, bool isError = false, ToolApproval approval = ToolApproval.NotRequired,
        string? target = null, string? summary = null) =>
        new("tool-" + name, "", isError, name, target, summary, 10, approval);

    private static string Format(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);

    private static HomePendingItem? PendingById(HomeViewModel home, string id) => home.PendingItems.SingleOrDefault(i => i.Id == id);

    [Fact]
    public async Task Pending_DefaultState_ListsSpeechAndSetsItsSideAttention()
    {
        await UseWorkbenchModelAsync();   // 模型與金鑰都有，但連線還沒測試，語音端點也是空的

        var ids = _host.Home.PendingItems.Select(i => i.Id).ToList();

        Assert.Equal(["Untested", "Speech"], ids);
        Assert.True(_host.Home.HasPending);
        Assert.Equal(Format(Strings.Home_PendingTitleFormat, 2), _host.Home.PendingTitle);
        Assert.True(_host.Home.AttentionConnections);
        Assert.True(_host.Home.AttentionSpeech);
    }

    [Fact]
    public async Task Pending_NoModel_IsFirstAndGoesToModels()
    {
        foreach (var model in _host.Catalog.Current.Models)
            await _host.Catalog.DeleteModelAsync(model.Id);
        await _host.Home.RefreshAsync();
        SettingsNavigation? requested = null;
        _host.Home.NavigateRequested += nav => requested = nav;

        var item = _host.Home.PendingItems[0];
        item.ActionCommand.Execute(null);

        Assert.Equal("NoModel", item.Id);
        Assert.Equal(Strings.Home_PendingNoModelTitle, item.Title);
        Assert.Equal(Strings.Home_PendingGoSettings, item.ActionText);
        Assert.False(item.IsPrimary);
        Assert.Equal(SettingsSection.Models, requested!.Section);
        Assert.True(_host.Home.AttentionConnections);
    }

    [Fact]
    public async Task Pending_NoKey_GoesToTheConnectionsKeyField()
    {
        await UseWorkbenchModelAsync(apiKey: null);
        SettingsNavigation? requested = null;
        _host.Home.NavigateRequested += nav => requested = nav;

        var item = PendingById(_host.Home, "NoKey")!;
        item.ActionCommand.Execute(null);

        Assert.Equal(Format(Strings.Home_PendingNoKeyDescFormat, "測試連線"), item.Description);
        Assert.Equal(SettingsSection.Connections, requested!.Section);
        Assert.Equal("conn1", requested.ConnectionId);
        Assert.True(requested.FocusApiKey);
    }

    [Fact]
    public async Task Pending_Untested_TestsInPlaceAndClearsWhenOk()
    {
        await UseWorkbenchModelAsync();
        var item = PendingById(_host.Home, "Untested")!;
        Assert.True(item.IsPrimary);
        Assert.Equal(Strings.Home_PendingTest, item.ActionText);

        _host.Tester.Result = ConnectionTestResult.Ok(["claude-test-1"]);
        await _host.Home.TestConnectionCommand.ExecuteAsync(null);

        var call = Assert.Single(_host.Tester.Calls);
        Assert.Equal("conn1", call.Connection.Id);
        Assert.Null(call.Key);   // 用已儲存的金鑰（與連線頁的做法相同）
        Assert.Equal(ConnectionStatus.Ok, _host.Status.Get("conn1").Status);
        Assert.Null(PendingById(_host.Home, "Untested"));
        Assert.False(_host.Home.AttentionConnections);
    }

    [Fact]
    public async Task Pending_Failed_RetestKeepsTheFailureWhenStillFailing()
    {
        await UseWorkbenchModelAsync();
        _host.Status.Mark("conn1", ConnectionStatus.Failed);
        await _host.Home.RefreshAsync();

        var failed = PendingById(_host.Home, "Failed")!;
        Assert.Equal(Strings.Home_PendingRetest, failed.ActionText);
        Assert.Null(PendingById(_host.Home, "Untested"));

        _host.Tester.Result = FakeConnectionTester.Failure(ChatFailureKind.Network, "無法連線");
        await _host.Home.TestConnectionCommand.ExecuteAsync(null);

        Assert.Single(_host.Tester.Calls);
        Assert.NotNull(PendingById(_host.Home, "Failed"));
        Assert.True(_host.Home.AttentionConnections);
    }

    [Fact]
    public async Task Pending_AllGood_ShowsNoPendingItems()
    {
        await UseWorkbenchModelAsync();
        _host.Status.Mark("conn1", ConnectionStatus.Ok);
        await _host.Catalog.SetSpeechAsync(new SpeechSettings("https://speech.example.test/v1"), null);
        await _host.Home.RefreshAsync();

        Assert.Empty(_host.Home.PendingItems);
        Assert.False(_host.Home.HasPending);
        Assert.Equal(Strings.Home_AllGood, _host.Home.NoPendingText);
        Assert.False(_host.Home.AttentionConnections);
        Assert.False(_host.Home.AttentionSpeech);
    }

    [Fact]
    public async Task ModelCard_ShowsTheWorkbenchModelWithSharedNote()
    {
        await UseWorkbenchModelAsync();
        _host.Status.Mark("conn1", ConnectionStatus.Ok);
        await _host.Home.RefreshAsync();

        var card = _host.Home.ModelCard;
        Assert.Equal("測試模型", card.Name);
        Assert.Equal(Format(Strings.Home_ModelDetailFormat, "測試連線", "claude-test-1"), card.DetailText);
        Assert.Equal(Strings.Home_StatusOk, card.StatusText);
        Assert.False(card.NeedsAttention);
        Assert.True(card.ShowSharedNote);   // 小精靈與工作台共用 WorkbenchModelId
    }

    [Fact]
    public async Task ModelCard_WithoutModel_NeedsAttentionAndHidesTheSharedNote()
    {
        foreach (var model in _host.Catalog.Current.Models)
            await _host.Catalog.DeleteModelAsync(model.Id);
        await _host.Home.RefreshAsync();

        Assert.Equal(Strings.Home_NoModel, _host.Home.ModelCard.Name);
        Assert.True(_host.Home.ModelCard.NeedsAttention);
        Assert.False(_host.Home.ModelCard.ShowSharedNote);
    }

    [Fact]
    public async Task MemoryCard_ShowsCountsAgainstTheLimitsAsProgress()
    {
        _host.MemoryStore.Seed("喜歡喝茶", MemoryCategory.Preference);
        _host.MemoryStore.Seed("住在台北", MemoryCategory.Profile);
        await _host.Memory.ReloadAsync();
        await _host.Home.RefreshAsync();

        var card = _host.Home.MemoryCard;
        Assert.Equal(Strings.Home_MemoryOn, card.StatusText);
        Assert.Equal(Format(Strings.Home_MemoryItemsFormat, "2", "100"), card.ItemsText);
        Assert.Equal(0.02, card.ItemsProgress, 6);
        Assert.Equal(Format(Strings.Home_MemoryCharsFormat, "8", 2000.ToString("N0", CultureInfo.CurrentCulture)), card.CharsText);
        Assert.Equal(8.0 / 2000, card.CharsProgress, 6);
    }

    [Fact]
    public async Task MemoryCard_WhenTurnedOff_ShowsTheOffText()
    {
        _host.Memory.IsEnabled = false;
        await SettingsHost.WaitUntil(() => !_host.Memory.IsEnabled);
        await _host.Home.RefreshAsync();

        Assert.Equal(Strings.Home_MemoryOff, _host.Home.MemoryCard.StatusText);
    }

    [Fact]
    public async Task UsageCard_IsLoadingUntilLoadedThenSumsThisMonthAndToday()
    {
        Assert.True(_host.Home.UsageCard.IsLoading);

        // FakeTimeProvider 的本地時區是 UTC，所以本地 00:00 就是 UTC 00:00
        _host.Store.Usage.Add((new DateTimeOffset(2026, 9, 30, 23, 59, 0, TimeSpan.Zero), new TokenTotals(9_000, 0, 9_000)));   // 上個月，不算
        _host.Store.Usage.Add((new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new TokenTotals(1_000, 0, 40)));       // 本月第一刻
        _host.Store.Usage.Add((new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero), new TokenTotals(200, 0, 0)));        // 今天

        await _host.Home.LoadActivityAsync();

        var card = _host.Home.UsageCard;
        Assert.False(card.IsLoading);
        Assert.False(card.IsUnavailable);
        Assert.Equal("1.2K", card.MonthText);     // 本月輸入 1,200 + 輸出 40 = 1,240
        Assert.Equal(Format(Strings.Home_UsageTodayFormat, "200"), card.TodayText);
        Assert.Equal(Format(Strings.Home_UsageInputFormat, "1.2K"), card.InputText);
        Assert.Equal(Format(Strings.Home_UsageOutputFormat, "40"), card.OutputText);
        Assert.Contains(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), _host.Store.SinceQueries);
        Assert.Contains(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero), _host.Store.SinceQueries);
    }

    [Fact]
    public async Task UsageCard_WhenTheQueryFails_ShowsUnavailable()
    {
        _host.Store.FailUsage = true;

        await _host.Home.LoadActivityAsync();

        Assert.True(_host.Home.UsageCard.IsUnavailable);
        Assert.False(_host.Home.UsageCard.IsLoading);
    }

    [Fact]
    public async Task ToolsCard_SplitsBuiltInFromMcpServers()
    {
        await _host.Catalog.SaveMcpServerAsync(McpServer("srv-a", "filesystem"), null);
        _host.McpSource.Statuses.Add(new McpServerStatus("srv-a", McpServerState.Ready, null, 3));
        await _host.Home.RefreshAsync();

        var builtIn = _host.Tools.Groups.Where(g => !g.Id.StartsWith("mcp:", StringComparison.Ordinal)).SelectMany(g => g.Items).ToList();
        var card = _host.Home.ToolsCard;
        Assert.Equal(builtIn.Count(i => i.IsEnabled), card.BuiltInEnabled);
        Assert.Equal(builtIn.Count, card.BuiltInTotal);
        Assert.Equal(1, card.McpConnected);
        Assert.Equal(1, card.McpTotal);
        var row = Assert.Single(card.McpServers);
        Assert.Equal("filesystem", row.Name);
        Assert.True(row.IsConnected);
        Assert.Equal(Format(Strings.Home_McpToolCountFormat, 3), row.StatusText);
        Assert.False(row.CanReconnect);
        Assert.Null(card.MoreText);
    }

    [Fact]
    public async Task ToolsCard_ShowsAtMostFourServersAndCountsTheRest()
    {
        for (var i = 1; i <= 5; i++)
        {
            await _host.Catalog.SaveMcpServerAsync(McpServer($"srv{i}", $"s{i}"), null);
            _host.McpSource.Statuses.Add(new McpServerStatus($"srv{i}", McpServerState.Failed, "壞掉了", 0));
        }
        await _host.Home.RefreshAsync();

        var card = _host.Home.ToolsCard;
        Assert.Equal(5, card.McpTotal);
        Assert.Equal(0, card.McpConnected);
        Assert.Equal(4, card.McpServers.Count);
        Assert.All(card.McpServers, row => Assert.True(row.CanReconnect));
        Assert.Equal(Format(Strings.Home_McpMoreFormat, 1), card.MoreText);
    }

    [Fact]
    public async Task ToolsCard_ReconnectCallsRestartOnTheServer()
    {
        await _host.Catalog.SaveMcpServerAsync(McpServer("srv-a", "notion"), null);
        var mcp = new RecordingMcpSource();
        mcp.Statuses.Add(new McpServerStatus("srv-a", McpServerState.Failed, "連不上", 0));
        using var home = new HomeViewModel(_host.Catalog, _host.Status, new SyncUiDispatcher(), _host.Store, mcp, _host.Tester, _host.Clock);

        home.ToolsCard.McpServers[0].ReconnectCommand.Execute(null);

        Assert.Equal(["srv-a"], mcp.Restarted);
    }

    [Fact]
    public async Task RemindersCard_CountsRemindersAndTimersSeparately()
    {
        await CreateOneTimeReminderAsync("喝水");
        await _host.ReminderService.StartTimerAsync("煮麵", 300, default);
        await _host.Reminders.ReloadAsync();
        await _host.Home.RefreshAsync();

        var card = _host.Home.RemindersCard;
        Assert.Equal(1, card.PendingCount);
        Assert.Equal(1, card.TimerCount);
        Assert.True(card.HasNext);
        Assert.Contains("喝水", card.NextText);
    }

    [Fact]
    public void RemindersCard_WithNothingPending_ShowsTheHint()
    {
        Assert.Equal(0, _host.Home.RemindersCard.PendingCount);
        Assert.False(_host.Home.RemindersCard.HasNext);
        Assert.Equal(Strings.Home_RemindersNoneHint, _host.Home.RemindersCard.NextText);
    }

    [Fact]
    public async Task RecentActions_ShowStatusBadgeAndRelativeTime_NewestFirstAtMostFive()
    {
        _host.Store.Actions.Add(new RecentToolAction(Now.AddSeconds(-30), "c1", Result("寫入檔案", approval: ToolApproval.AllowedOnce, target: "plan/00.md", summary: "+12 −3 行")));
        _host.Store.Actions.Add(new RecentToolAction(Now.AddMinutes(-15), "c1", Result("執行", isError: true, target: "dotnet build", summary: "失敗（exit 1）")));
        _host.Store.Actions.Add(new RecentToolAction(Now.AddHours(-2), "c2", Result("刪除檔案", approval: ToolApproval.Denied, target: "tmp/test.log")));
        _host.Store.Actions.Add(new RecentToolAction(new DateTimeOffset(2026, 10, 18, 23, 0, 0, TimeSpan.Zero), "c2", Result("讀取網頁", approval: ToolApproval.AllowedForConversation)));
        _host.Store.Actions.Add(new RecentToolAction(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero), "c3", Result("舊的動作", approval: ToolApproval.AllowedAlways)));
        _host.Store.Actions.Add(new RecentToolAction(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), "c3", Result("最舊的動作")));

        await _host.Home.LoadActivityAsync();

        var rows = _host.Home.RecentActions.ToList();
        Assert.Equal(5, rows.Count);
        Assert.True(_host.Home.HasRecentActions);

        Assert.Equal(Strings.Home_TimeNow, rows[0].RelativeTime);
        Assert.Equal(HomeActionStatus.Succeeded, rows[0].Status);
        Assert.Equal(Strings.Home_ActionAllowed, rows[0].ApprovalBadge);
        Assert.Equal("plan/00.md", rows[0].Target);
        Assert.Equal(Format(Strings.Home_TimeMinutesFormat, 15), rows[1].RelativeTime);
        Assert.Equal(HomeActionStatus.Failed, rows[1].Status);
        Assert.Equal("失敗（exit 1）", rows[1].Summary);
        Assert.Null(rows[1].ApprovalBadge);
        Assert.Equal(Format(Strings.Home_TimeHoursFormat, 2), rows[2].RelativeTime);
        Assert.Equal(HomeActionStatus.Denied, rows[2].Status);
        Assert.Equal(Strings.Home_ActionDenied, rows[2].ApprovalBadge);
        Assert.Equal(Strings.Home_TimeYesterday, rows[3].RelativeTime);
        Assert.Equal("10/10", rows[4].RelativeTime);
    }

    [Fact]
    public async Task RecentActions_WithNoRecords_IsEmpty()
    {
        await _host.Home.LoadActivityAsync();

        Assert.Empty(_host.Home.RecentActions);
        Assert.False(_host.Home.HasRecentActions);
        Assert.Equal(Strings.Home_ActionsEmpty, _host.Home.NoRecentActionsText);
    }

    /// <summary>可以記錄 <see cref="RestartAsync"/> 呼叫的假 MCP 來源（<see cref="FakeMcpToolSource"/> 不記錄）。</summary>
    private sealed class RecordingMcpSource : IMcpToolSource
    {
        public List<McpServerStatus> Statuses { get; } = [];

        public List<string> Restarted { get; } = [];

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public IReadOnlyList<IMcpTool> GetTools() => [];

        public IReadOnlyList<McpServerStatus> GetStatuses() => Statuses.ToList();

        public Task WaitForStartupAsync(TimeSpan timeout, CancellationToken ct) => Task.CompletedTask;

        public Task<McpTestResult> TestAsync(McpServerSettings server, McpServerSecrets secrets, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task RestartAsync(string serverId, CancellationToken ct)
        {
            Restarted.Add(serverId);
            return Task.CompletedTask;
        }
    }
}
