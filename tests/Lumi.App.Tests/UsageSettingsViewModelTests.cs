using System.Globalization;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Usage;

namespace Lumi.App.Tests;

/// <summary>測試用的用量紀錄與提示紀錄（設定 › 用量與費用、CSV 共用）。</summary>
internal static class UsageSamples
{
    /// <summary>有單價的請求預設用的單價快照（US$ 每百萬 tokens：輸入 3、輸出 15、快取讀 0.3、快取寫 3.75）。</summary>
    public static readonly PriceSnapshot Price = new(3, 15, 0.3m, 3.75m, IsFree: false);

    public static UsageRecord Record(DateTimeOffset at, decimal? cost, string? conversationId = null, bool estimated = false,
        PriceSnapshot? price = null, UsagePurpose purpose = UsagePurpose.Chat, ChatMode mode = ChatMode.Workbench, TokenUsage? tokens = null) =>
        new(UsageRecord.NewId(), at, conversationId, purpose, mode, "test-model-settings", "test-model", "測試連線",
            tokens ?? new TokenUsage(1_000, 200, 0, 0), estimated, price ?? (cost is null ? null : Price), cost, 0);

    public static UsageHintEntry Hint(string rule, string? conversationId, DateTimeOffset at, UsageHintOutcome outcome = UsageHintOutcome.Shown) =>
        new(Guid.NewGuid().ToString(), rule, conversationId, at, outcome);

    /// <summary>測試時間：2026-10-29 12:00（UTC）。</summary>
    public static ManualTimeProvider October29() => new(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);

    public static DateTimeOffset Day(int day, int hour = 12) => new(2026, 10, day, hour, 0, 0, TimeSpan.Zero);
}

/// <summary>包住 <see cref="InMemoryUsageStore"/>，只把 <see cref="ListTopConversationsAsync"/> 換成指定的清單（那個測試替身固定回空清單）。</summary>
internal sealed class TopConversationsStore(IUsageStore inner, IReadOnlyList<ConversationCostItem> top) : IUsageStore
{
    public Task AddAsync(UsageRecord record, CancellationToken ct = default) => inner.AddAsync(record, ct);

    public Task<IReadOnlyList<UsageRecord>> ListAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        inner.ListAsync(from, to, ct);

    public Task<ConversationCost> GetConversationCostAsync(string conversationId, CancellationToken ct = default) =>
        inner.GetConversationCostAsync(conversationId, ct);

    public Task<IReadOnlyList<ConversationCostItem>> ListTopConversationsAsync(DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken ct = default) =>
        Task.FromResult(top);

    public Task<decimal> SumCostAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        inner.SumCostAsync(from, to, ct);
}

/// <summary>記錄 <see cref="SaveFileAsync"/> 的呼叫；回傳指定的路徑、取消（null）或丟例外。</summary>
internal sealed class FakeSavePicker : IFilePickerService
{
    public string? SavePath { get; set; }

    public Exception? Throws { get; set; }

    public List<(string Name, string Extension)> Saves { get; } = [];

    public Task<IReadOnlyList<string>> PickFilesAsync() => Task.FromResult<IReadOnlyList<string>>([]);

    public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);

    public Task<string?> SaveFileAsync(string suggestedName, string extension)
    {
        Saves.Add((suggestedName, extension));
        if (Throws is not null)
            throw Throws;
        return Task.FromResult(SavePath);
    }
}

public class UsageSettingsViewModelTests
{
    private sealed class Rig
    {
        public Rig(ManualTimeProvider? time = null, FakeModelCatalog? catalog = null, InMemoryUsageStore? store = null,
            IReadOnlyList<ConversationCostItem>? top = null, Func<string, Task>? openConversation = null, Action? openPricing = null)
        {
            Time = time ?? UsageSamples.October29();
            Catalog = catalog ?? new FakeModelCatalog();
            Records = store ?? new InMemoryUsageStore();
            Hints = new InMemoryUsageHintLog();
            Conversations = new InMemoryConversationStore();
            Picker = new FakeSavePicker();
            Vm = new UsageSettingsViewModel(top is null ? Records : new TopConversationsStore(Records, top), Hints, Catalog, Conversations,
                Picker, Time, openConversation, openPricing);
        }

        public ManualTimeProvider Time { get; }

        public FakeModelCatalog Catalog { get; }

        public InMemoryUsageStore Records { get; }

        public InMemoryUsageHintLog Hints { get; }

        public InMemoryConversationStore Conversations { get; }

        public FakeSavePicker Picker { get; }

        public UsageSettingsViewModel Vm { get; }

        public void SetBudgetUsd(decimal? usd) =>
            Catalog.Current = Catalog.Current with { Usage = Catalog.Current.Usage with { MonthlyBudgetUsd = usd } };

        public void SetMuted(params string[] rules) =>
            Catalog.Current = Catalog.Current with { Usage = Catalog.Current.Usage with { MutedHints = rules } };
    }

    private static string Format(string template, params string[] args) => string.Format(CultureInfo.CurrentCulture, template, args);

    // ===== 本月卡片 =====

    [Fact]
    public async Task EmptyMonth_ShowsZeroAndHidesEverythingElse()
    {
        var rig = new Rig();
        await rig.Vm.LoadAsync();

        var vm = rig.Vm;
        Assert.True(vm.IsEmpty);
        Assert.Equal("NT$0", vm.TotalText);
        Assert.Null(vm.RangeText);
        Assert.False(vm.ShowBudgetSummary);
        Assert.False(vm.ShowSetBudgetLink);
        Assert.False(vm.ShowBudgetEditor);
        Assert.False(vm.ShowProjection);
        Assert.Null(vm.UnpricedText);
        Assert.False(vm.ShowHints);
        Assert.False(vm.HasTopConversations);
        Assert.Equal(31, vm.Daily.Count);   // 10 月共 31 天
        Assert.True(vm.Daily[28].IsToday);  // 10/29
    }

    [Fact]
    public async Task BudgetAt61Percent_ShowsAmountPercentAndNormalLevel()
    {
        var rig = new Rig();
        rig.SetBudgetUsd(25m);   // NT$800
        rig.Records.Records.Add(UsageSamples.Record(UsageSamples.Day(10), 15.25m));   // US$15.25 ＝ NT$488

        await rig.Vm.LoadAsync();

        var vm = rig.Vm;
        Assert.False(vm.IsEmpty);
        Assert.Equal("NT$488", vm.TotalText);
        Assert.Equal("10/10–10/29 · 約 US$15.25", vm.RangeText);   // 起日取本月第一筆紀錄
        Assert.True(vm.HasBudget);
        Assert.Equal("每月預算 NT$800 · 已用 61%", vm.BudgetText);
        Assert.Equal(0.61, vm.BudgetRatio, 9);
        Assert.Equal(UsageLevel.Normal, vm.BudgetLevel);
        Assert.True(vm.ShowBudgetSummary);
        Assert.False(vm.ShowSetBudgetLink);
    }

    [Theory]
    [InlineData(20.0, UsageLevel.Warning, "已用 80%")]   // 80% 起警示
    [InlineData(24.99, UsageLevel.Warning, "已用 99%")]    // 99.96% 仍是警示，百分比向下取整
    [InlineData(25.0, UsageLevel.Danger, "已用 100%")]
    [InlineData(37.5, UsageLevel.Danger, "已用 150%")]     // 超過 100% 時進度條夾在 1
    public async Task BudgetLevels_FollowThePercentage(decimal spent, UsageLevel level, string percentText)
    {
        var rig = new Rig();
        rig.SetBudgetUsd(25m);
        rig.Records.Records.Add(UsageSamples.Record(UsageSamples.Day(10), spent));

        await rig.Vm.LoadAsync();

        Assert.Equal(level, rig.Vm.BudgetLevel);
        Assert.EndsWith(percentText, rig.Vm.BudgetText);
        Assert.Equal(Math.Min(1.0, (double)(spent / 25m)), rig.Vm.BudgetRatio, 9);
        Assert.Equal(level == UsageLevel.Warning, rig.Vm.IsBudgetWarning);
        Assert.Equal(level == UsageLevel.Danger, rig.Vm.IsBudgetDanger);
    }

    [Fact]
    public async Task Projection_ShowsOnlyWithSevenDaysOfRecords_AndSaysOverBudget()
    {
        var rig = new Rig();
        rig.SetBudgetUsd(25m);   // NT$800
        // 10/23–10/29 共 7 天，每天 US$1：累計 7，已過 7 天，月底預測 7 ÷ 7 × 31 = US$31（NT$992）
        for (var day = 23; day <= 29; day++)
            rig.Records.Records.Add(UsageSamples.Record(UsageSamples.Day(day), 1m));

        await rig.Vm.LoadAsync();

        Assert.True(rig.Vm.ShowProjection);
        Assert.Equal(Format(Strings.UsagePage_ProjectionOverFormat, "NT$992"), rig.Vm.ProjectionText);
    }

    [Fact]
    public async Task Projection_WithoutBudget_UsesTheShortSentence()
    {
        var rig = new Rig();
        for (var day = 23; day <= 29; day++)
            rig.Records.Records.Add(UsageSamples.Record(UsageSamples.Day(day), 1m));

        await rig.Vm.LoadAsync();

        Assert.True(rig.Vm.ShowProjection);
        Assert.Equal(Format(Strings.UsagePage_ProjectionFormat, "NT$992"), rig.Vm.ProjectionText);
    }

    [Fact]
    public async Task Projection_IsHiddenWhenFewerThanSevenDaysHaveRecords()
    {
        var rig = new Rig();
        for (var day = 24; day <= 29; day++)   // 6 天
            rig.Records.Records.Add(UsageSamples.Record(UsageSamples.Day(day), 1m));

        await rig.Vm.LoadAsync();

        Assert.False(rig.Vm.ShowProjection);
        Assert.Null(rig.Vm.ProjectionText);
    }

    [Fact]
    public async Task UnpricedRequests_AreCountedAndNotAddedToTheTotal()
    {
        var rig = new Rig();
        rig.Records.Records.Add(UsageSamples.Record(UsageSamples.Day(10), 1m));
        rig.Records.Records.Add(UsageSamples.Record(UsageSamples.Day(11), null, price: null));
        rig.Records.Records.Add(UsageSamples.Record(UsageSamples.Day(12), null, price: null));

        await rig.Vm.LoadAsync();

        Assert.Equal("NT$32", rig.Vm.TotalText);
        Assert.Equal(Format(Strings.UsagePage_UnpricedFormat, "2"), rig.Vm.UnpricedText);
    }

    [Fact]
    public async Task EstimatedRecords_PrefixTheTotalWithApproximately()
    {
        var rig = new Rig();
        rig.Records.Records.Add(UsageSamples.Record(UsageSamples.Day(10), 1m, estimated: true));

        await rig.Vm.LoadAsync();

        Assert.Equal("≈NT$32", rig.Vm.TotalText);
    }

    // ===== 預算編輯 =====

    [Fact]
    public async Task EditBudget_PrefillsTheTwdAmount_AndSavingConvertsToUsd()
    {
        var rig = new Rig();
        rig.SetBudgetUsd(25m);
        rig.Records.Records.Add(UsageSamples.Record(UsageSamples.Day(10), 1m));
        await rig.Vm.LoadAsync();

        rig.Vm.EditBudgetCommand.Execute(null);
        Assert.True(rig.Vm.IsEditingBudget);
        Assert.Equal("800", rig.Vm.BudgetInput);
        Assert.False(rig.Vm.ShowBudgetSummary);

        rig.Vm.BudgetInput = "1,000";
        await rig.Vm.SaveBudgetCommand.ExecuteAsync(null);

        Assert.False(rig.Vm.IsEditingBudget);
        Assert.Null(rig.Vm.BudgetError);
        Assert.Equal(31.25m, rig.Catalog.UsageSaves.Last().MonthlyBudgetUsd);   // NT$1,000 ÷ 32
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("12.5")]
    [InlineData("abc")]
    [InlineData("")]
    public async Task SaveBudget_RejectsNonPositiveIntegers(string input)
    {
        var rig = new Rig();
        await rig.Vm.LoadAsync();
        rig.Vm.EditBudgetCommand.Execute(null);

        rig.Vm.BudgetInput = input;
        await rig.Vm.SaveBudgetCommand.ExecuteAsync(null);

        Assert.Equal(Strings.UsagePage_BudgetInvalid, rig.Vm.BudgetError);
        Assert.True(rig.Vm.IsEditingBudget);
        Assert.Empty(rig.Catalog.UsageSaves);
    }

    [Fact]
    public async Task CancelBudget_ClosesTheEditorAndClearsTheError()
    {
        var rig = new Rig();
        await rig.Vm.LoadAsync();
        rig.Vm.EditBudgetCommand.Execute(null);
        rig.Vm.BudgetInput = "0";
        await rig.Vm.SaveBudgetCommand.ExecuteAsync(null);

        rig.Vm.CancelBudgetCommand.Execute(null);

        Assert.False(rig.Vm.IsEditingBudget);
        Assert.Null(rig.Vm.BudgetError);
        Assert.Empty(rig.Catalog.UsageSaves);
    }

    [Fact]
    public async Task ClearBudget_RemovesTheBudgetAndOffersTheLink()
    {
        var rig = new Rig();
        rig.SetBudgetUsd(25m);
        rig.Records.Records.Add(UsageSamples.Record(UsageSamples.Day(10), 1m));
        await rig.Vm.LoadAsync();
        rig.Vm.EditBudgetCommand.Execute(null);

        await rig.Vm.ClearBudgetCommand.ExecuteAsync(null);

        Assert.Null(rig.Catalog.UsageSaves.Last().MonthlyBudgetUsd);
        Assert.False(rig.Vm.HasBudget);
        Assert.Null(rig.Vm.BudgetText);
        Assert.False(rig.Vm.IsEditingBudget);
        Assert.True(rig.Vm.ShowSetBudgetLink);
    }

    // ===== 花最多的對話 =====

    [Fact]
    public async Task TopConversations_ShowTitleMetaAndCost()
    {
        var top = new[]
        {
            new ConversationCostItem("c1", "重構設定頁", UsageSamples.Day(29, 9), TurnCount: 12, CostUsd: 2.5m, HasEstimated: true),
        };
        var rig = new Rig(top: top);
        rig.Records.Records.Add(UsageSamples.Record(UsageSamples.Day(29), 2.5m, "c1"));

        await rig.Vm.LoadAsync();

        Assert.True(rig.Vm.HasTopConversations);
        var item = Assert.Single(rig.Vm.TopConversations);
        Assert.Equal("c1", item.ConversationId);
        Assert.Equal("重構設定頁", item.Title);
        Assert.Equal(Format(Strings.UsagePage_TopMetaFormat, "10/29", "12"), item.MetaText);
        Assert.Equal("≈NT$80", item.CostText);
    }

    [Fact]
    public async Task OpenConversation_CallsTheDelegate_AndIsANoOpWithoutOne()
    {
        var opened = new List<string>();
        var rig = new Rig(openConversation: id => { opened.Add(id); return Task.CompletedTask; });
        await rig.Vm.OpenConversationCommand.ExecuteAsync("c1");
        Assert.Equal(new[] { "c1" }, opened);

        var plain = new Rig();
        await plain.Vm.OpenConversationCommand.ExecuteAsync("c1");   // 沒有接上整合：不丟例外
    }

    [Fact]
    public void OpenPricing_CallsTheDelegate_AndIsANoOpWithoutOne()
    {
        var calls = 0;
        var rig = new Rig(openPricing: () => calls++);
        rig.Vm.OpenPricingCommand.Execute(null);
        Assert.Equal(1, calls);

        new Rig().Vm.OpenPricingCommand.Execute(null);
    }

    // ===== 這個月出現過的情況 =====

    [Fact]
    public async Task Hints_ListOnlyRulesWithRecords_InPriorityOrder_AndOnlyTitledConversations()
    {
        var rig = new Rig();
        var a = await rig.Conversations.CreateAsync("對話 A");
        var b = await rig.Conversations.CreateAsync("對話 B");
        rig.Hints.Entries.Add(UsageSamples.Hint(UsageHintIds.LongConversation, a.Id, UsageSamples.Day(20)));
        rig.Hints.Entries.Add(UsageSamples.Hint(UsageHintIds.LongConversation, b.Id, UsageSamples.Day(25)));
        rig.Hints.Entries.Add(UsageSamples.Hint(UsageHintIds.ModelSwitch, a.Id, UsageSamples.Day(22), UsageHintOutcome.Suppressed));
        rig.Hints.Entries.Add(UsageSamples.Hint(UsageHintIds.BigFile, "deleted-conversation", UsageSamples.Day(21)));

        await rig.Vm.LoadAsync();

        Assert.True(rig.Vm.ShowHints);
        Assert.Equal(new[] { UsageHintIds.ModelSwitch, UsageHintIds.LongConversation, UsageHintIds.BigFile }, rig.Vm.Hints.Select(h => h.Rule));

        var longConversation = rig.Vm.Hints[1];
        Assert.Equal(Strings.Hint_LongConversationName, longConversation.Name);
        Assert.Equal(Strings.Hint_LongConversationSummary, longConversation.Summary);
        Assert.Equal(Format(Strings.UsagePage_HintCountFormat, "2"), longConversation.CountText);
        // 最近的在前；標題與日期
        Assert.Equal(new[] { "對話 B", "對話 A" }, longConversation.Conversations.Select(c => c.Title));
        Assert.Equal(new[] { "10/25", "10/20" }, longConversation.Conversations.Select(c => c.DateText));

        // 查不到標題（已刪除）的對話不列，但仍算在「N 則對話」裡
        var bigFile = rig.Vm.Hints[2];
        Assert.Empty(bigFile.Conversations);
        Assert.False(bigFile.HasConversations);
        Assert.Equal(Format(Strings.UsagePage_HintCountFormat, "1"), bigFile.CountText);
    }

    [Fact]
    public async Task Hints_MutedRule_ShowsTheMarkAndUnmuteRestoresIt()
    {
        var rig = new Rig();
        var a = await rig.Conversations.CreateAsync("對話 A");
        rig.Hints.Entries.Add(UsageSamples.Hint(UsageHintIds.LongConversation, a.Id, UsageSamples.Day(20)));
        rig.SetMuted(UsageHintIds.LongConversation, UsageHintIds.Images);
        await rig.Vm.LoadAsync();

        var item = Assert.Single(rig.Vm.Hints);
        Assert.True(item.IsMuted);

        await rig.Vm.UnmuteHintCommand.ExecuteAsync(UsageHintIds.LongConversation);

        var saved = rig.Catalog.UsageSaves.Last();
        Assert.DoesNotContain(UsageHintIds.LongConversation, saved.Muted);
        Assert.Contains(UsageHintIds.Images, saved.Muted);   // 其他靜音的規則保留
        Assert.False(item.IsMuted);
    }

    [Fact]
    public async Task ToggleHint_ExpandsOnlyWhenThereAreConversations()
    {
        var rig = new Rig();
        var a = await rig.Conversations.CreateAsync("對話 A");
        rig.Hints.Entries.Add(UsageSamples.Hint(UsageHintIds.LongConversation, a.Id, UsageSamples.Day(20)));
        rig.Hints.Entries.Add(UsageSamples.Hint(UsageHintIds.BigFile, "deleted", UsageSamples.Day(21)));
        await rig.Vm.LoadAsync();

        rig.Vm.ToggleHintCommand.Execute(UsageHintIds.LongConversation);
        rig.Vm.ToggleHintCommand.Execute(UsageHintIds.BigFile);

        Assert.True(rig.Vm.Hints.Single(h => h.Rule == UsageHintIds.LongConversation).IsExpanded);
        Assert.False(rig.Vm.Hints.Single(h => h.Rule == UsageHintIds.BigFile).IsExpanded);
    }

    // ===== 匯出 =====

    [Fact]
    public async Task Export_SuggestsTheMonthFileName_AndWritesTheCsv()
    {
        var rig = new Rig();
        var conversation = await rig.Conversations.CreateAsync("標題");
        rig.Records.Records.Add(UsageSamples.Record(UsageSamples.Day(10), 1m, conversation.Id));
        var path = Path.Combine(Path.GetTempPath(), "lumi-usage-" + Guid.NewGuid().ToString("N") + ".csv");
        rig.Picker.SavePath = path;
        try
        {
            await rig.Vm.ExportCommand.ExecuteAsync(null);

            Assert.Equal(new[] { ("Lumi-用量-2026-10.csv", "csv") }, rig.Picker.Saves);
            Assert.True(File.Exists(path));
            var text = await File.ReadAllTextAsync(path);
            Assert.Contains("標題", text);
            Assert.Equal(Format(Strings.UsagePage_ExportDoneFormat, path), rig.Vm.ExportMessage);
            Assert.False(rig.Vm.ExportMessageIsError);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Export_CancelledPicker_WritesNothing()
    {
        var rig = new Rig();
        rig.Records.Records.Add(UsageSamples.Record(UsageSamples.Day(10), 1m));
        rig.Picker.SavePath = null;

        await rig.Vm.ExportCommand.ExecuteAsync(null);

        Assert.Single(rig.Picker.Saves);
        Assert.Null(rig.Vm.ExportMessage);
    }

    [Fact]
    public async Task Export_WhenWritingFails_ShowsAnErrorMessage()
    {
        var rig = new Rig();
        rig.Records.Records.Add(UsageSamples.Record(UsageSamples.Day(10), 1m));
        rig.Picker.SavePath = Path.Combine(Path.GetTempPath(), "no-such-folder-" + Guid.NewGuid().ToString("N"), "out.csv");

        await rig.Vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal(Strings.UsagePage_ExportFailed, rig.Vm.ExportMessage);
        Assert.True(rig.Vm.ExportMessageIsError);
    }
}
