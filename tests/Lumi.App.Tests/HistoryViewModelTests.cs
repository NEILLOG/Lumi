using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;

namespace Lumi.App.Tests;

/// <summary>
/// 9B：歷史抽屜的 ViewModel（不開視窗）：日期分組邊界、搜尋節流與取消、清除搜尋、分頁、改名驗證、刪除與確認、回應中停用。
/// </summary>
public class HistoryViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private sealed record Rig(HistoryViewModel Vm, StaticListStore Store, FakeHistoryHost Host, ManualTimeProvider Time, FakeConfirmService Confirm);

    private static Rig CreateRig(StaticListStore? store = null, HistoryOptions? options = null, bool responding = false,
        string? current = null, ManualTimeProvider? time = null, bool withDeletion = true)
    {
        store ??= new StaticListStore();
        time ??= new ManualTimeProvider(Now);
        var host = new FakeHistoryHost { IsResponding = responding, CurrentConversationId = current };
        var confirm = new FakeConfirmService();
        var vm = new HistoryViewModel(store, host, time, options,
            withDeletion ? HistoryTestSupport.Deletion(store) : null, confirm);
        return new Rig(vm, store, host, time, confirm);
    }

    private static string[] GroupTitles(HistoryViewModel vm) => vm.Groups.Select(g => g.Title ?? "").ToArray();

    // ---- 日期分組 ----

    [Fact]
    public void BucketOf_AtAndAroundMidnight_FollowsTheLocalDate()
    {
        var zone = HistoryTestSupport.Zone(8);
        var now = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.FromHours(8));   // 剛過午夜：今天

        Assert.Equal(HistoryBucket.Today, HistoryGrouping.BucketOf(new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.FromHours(8)), now, zone));
        Assert.Equal(HistoryBucket.Yesterday, HistoryGrouping.BucketOf(new DateTimeOffset(2026, 10, 20, 23, 59, 59, TimeSpan.FromHours(8)), now, zone));
    }

    [Fact]
    public void BucketOf_UsesTheLocalZone_NotUtc()
    {
        // 本地（UTC+8）now 是 10/21 中午、更新於 10/21 凌晨 00:10：本地同一天＝今天；
        // 若誤用 UTC，now 是 10/21 04:00、更新是 10/20 16:10，會變成昨天
        var zone = HistoryTestSupport.Zone(8);
        var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.FromHours(8));
        var updated = new DateTimeOffset(2026, 10, 21, 0, 10, 0, TimeSpan.FromHours(8));

        Assert.Equal(HistoryBucket.Today, HistoryGrouping.BucketOf(updated, now, zone));
        Assert.Equal(HistoryBucket.Yesterday, HistoryGrouping.BucketOf(updated, now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void BucketOf_DayEdges_AreSevenAndThirtyDays()
    {
        var zone = TimeZoneInfo.Utc;
        var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        HistoryBucket At(int daysAgo) => HistoryGrouping.BucketOf(now.AddDays(-daysAgo), now, zone);

        Assert.Equal(HistoryBucket.Today, At(0));
        Assert.Equal(HistoryBucket.Yesterday, At(1));
        Assert.Equal(HistoryBucket.Last7Days, At(2));
        Assert.Equal(HistoryBucket.Last7Days, At(7));
        Assert.Equal(HistoryBucket.Last30Days, At(8));
        Assert.Equal(HistoryBucket.Last30Days, At(30));
        Assert.Equal(HistoryBucket.Older, At(31));
        Assert.Equal(HistoryBucket.Today, HistoryGrouping.BucketOf(now.AddHours(2), now, zone));   // 時鐘誤差：未來歸今天
    }

    [Fact]
    public async Task Groups_SplitByDate_InOrder_AndSkipEmptyGroups()
    {
        var store = new StaticListStore();
        store.Add("today", "今天的", Now.AddHours(-1));
        store.Add("yesterday", "昨天的", Now.AddDays(-1).AddHours(-1));
        store.Add("week", "一週內", Now.AddDays(-3));
        store.Add("month", "一個月內", Now.AddDays(-20));
        store.Add("old", "很久以前", Now.AddDays(-100));
        var rig = CreateRig(store);

        await rig.Vm.RefreshAsync(null);

        Assert.Equal(new[] { Strings.History_GroupToday, Strings.History_GroupYesterday, Strings.History_GroupLast7,
            Strings.History_GroupLast30, Strings.History_GroupOlder }, GroupTitles(rig.Vm));
        Assert.All(rig.Vm.Groups, g => Assert.Single(g.Items));
        // 列也跟著攤平：標題列在前、對話列在後
        Assert.IsType<HistoryGroupHeader>(rig.Vm.Rows[0]);
        Assert.Equal(10, rig.Vm.Rows.Count);
    }

    [Fact]
    public async Task Groups_WithOnlyOneBucket_ShowOnlyThatGroup()
    {
        var store = new StaticListStore();
        store.Add("a", "只有今天", Now.AddMinutes(-5));
        store.Add("b", "也是今天", Now.AddMinutes(-9));
        var rig = CreateRig(store);

        await rig.Vm.RefreshAsync(null);

        Assert.Equal(new[] { Strings.History_GroupToday }, GroupTitles(rig.Vm));
        Assert.Equal(2, rig.Vm.Groups[0].Items.Count);
    }

    // ---- 搜尋：節流、取消、不分組、清除 ----

    [Fact]
    public async Task Search_IsThrottled_UntilTheDebounceElapses()
    {
        var store = new StaticListStore();
        store.Add("r", "報告", Now);
        var rig = CreateRig(store);

        rig.Vm.SearchText = "報";
        Assert.True(rig.Vm.IsSearching);
        rig.Time.Advance(TimeSpan.FromMilliseconds(249));
        await Task.Delay(30);
        Assert.Empty(store.SearchedQueries);

        rig.Time.Advance(TimeSpan.FromMilliseconds(1));
        await SettingsHost.WaitUntil(() => !rig.Vm.IsSearching);
        Assert.Equal(new[] { "報" }, store.SearchedQueries);
    }

    [Fact]
    public async Task Search_TypingAgain_CancelsThePreviousQuery_AndAnOldResultNeverWins()
    {
        var store = new StaticListStore();
        var pending = new Dictionary<string, (TaskCompletionSource<IReadOnlyList<ConversationSearchHit>> Done, CancellationToken Token)>(StringComparer.Ordinal);
        store.SearchHook = (query, _, ct) =>
        {
            // 模擬不理會取消的儲存：舊查詢之後照樣完成，VM 必須自己丟掉
            var done = new TaskCompletionSource<IReadOnlyList<ConversationSearchHit>>();
            pending[query] = (done, ct);
            return done.Task;
        };
        var rig = CreateRig(store);
        var old = new Conversation("old", "舊結果", Now, Now);
        var newer = new Conversation("new", "新結果", Now, Now);

        rig.Vm.SearchText = "報";
        rig.Time.Advance(TimeSpan.FromMilliseconds(250));
        await SettingsHost.WaitUntil(() => pending.ContainsKey("報"));

        rig.Vm.SearchText = "報告";
        rig.Time.Advance(TimeSpan.FromMilliseconds(250));
        await SettingsHost.WaitUntil(() => pending.ContainsKey("報告"));

        Assert.True(pending["報"].Token.IsCancellationRequested);
        pending["報告"].Done.SetResult([new ConversationSearchHit(newer, "")]);
        await SettingsHost.WaitUntil(() => !rig.Vm.IsSearching);
        pending["報"].Done.SetResult([new ConversationSearchHit(old, "")]);   // 舊結果晚到
        await Task.Delay(30);

        Assert.Equal(new[] { "new" }, rig.Vm.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task Search_ResultsAreNotGrouped_ButShowACount()
    {
        var store = new StaticListStore();
        store.Add("1", "報告一", Now.AddDays(-40));
        store.Add("2", "報告二", Now.AddHours(-1));
        store.Add("3", "週報", Now.AddDays(-3));
        store.Add("4", "無關", Now);
        var rig = CreateRig(store);

        rig.Vm.SearchText = "報";
        rig.Time.Advance(TimeSpan.FromMilliseconds(250));
        await SettingsHost.WaitUntil(() => !rig.Vm.IsSearching);

        var group = Assert.Single(rig.Vm.Groups);
        Assert.Equal(string.Format(Strings.History_ResultsFormat, 3), group.Title);
        Assert.Equal(3, group.Items.Count);
        Assert.False(rig.Vm.IsEmpty);
        Assert.False(rig.Vm.HasMore);
    }

    [Fact]
    public async Task Search_AtTheLimit_SaysOnlyTheFirstFewAreShown()
    {
        var store = new StaticListStore();
        for (var i = 0; i < 4; i++)
            store.Add($"c{i}", $"報告{i}", Now.AddMinutes(-i));
        var rig = CreateRig(store, new HistoryOptions { SearchLimit = 3 });

        rig.Vm.SearchText = "報";
        rig.Time.Advance(TimeSpan.FromMilliseconds(250));
        await SettingsHost.WaitUntil(() => !rig.Vm.IsSearching);

        Assert.Equal(string.Format(Strings.History_ResultsCappedFormat, 3), rig.Vm.Groups[0].Title);
        Assert.Equal(3, rig.Vm.Items.Count);
    }

    [Fact]
    public async Task Search_WithNoMatch_ShowsTheNoResultsText()
    {
        var store = new StaticListStore();
        store.Add("1", "報告", Now);
        var rig = CreateRig(store);

        rig.Vm.SearchText = "  不存在  ";
        rig.Time.Advance(TimeSpan.FromMilliseconds(250));
        await SettingsHost.WaitUntil(() => !rig.Vm.IsSearching);

        Assert.Empty(rig.Vm.Groups);
        Assert.True(rig.Vm.ShowNoResults);
        Assert.False(rig.Vm.IsEmpty);
        Assert.Equal(string.Format(Strings.History_NoResultsFormat, "不存在"), rig.Vm.NoResultsText);
    }

    [Fact]
    public async Task Search_WhitespaceOnly_IsBrowseMode_AndNeverQueries()
    {
        var store = new StaticListStore();
        store.Add("1", "報告", Now);
        var rig = CreateRig(store);

        rig.Vm.SearchText = "   ";
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(30);

        Assert.False(rig.Vm.IsSearchMode);
        Assert.Empty(store.SearchedQueries);
    }

    [Fact]
    public async Task ClearingTheSearch_ReturnsToTheGroupedList()
    {
        var store = new StaticListStore();
        store.Add("1", "報告", Now);
        store.Add("2", "舊的", Now.AddDays(-60));
        var rig = CreateRig(store);
        await rig.Vm.RefreshAsync(null);

        rig.Vm.SearchText = "報";
        rig.Time.Advance(TimeSpan.FromMilliseconds(250));
        await SettingsHost.WaitUntil(() => !rig.Vm.IsSearching && rig.Vm.Items.Count == 1);
        Assert.False(rig.Vm.Groups[0].Title is null);

        rig.Vm.SearchText = "";
        await SettingsHost.WaitUntil(() => rig.Vm.Items.Count == 2);

        Assert.False(rig.Vm.IsSearchMode);
        Assert.Equal(new[] { Strings.History_GroupToday, Strings.History_GroupOlder }, GroupTitles(rig.Vm));
        Assert.False(rig.Vm.ShowNoResults);
    }

    // ---- 分頁 ----

    [Fact]
    public async Task LoadMore_AppendsPages_WithoutDuplicates_AndStopsAtTheEnd()
    {
        var store = new StaticListStore();
        for (var i = 0; i < 120; i++)
            store.Add($"c{i:000}", $"對話 {i}", Now.AddMinutes(-i));
        var rig = CreateRig(store);

        await rig.Vm.RefreshAsync(null);
        Assert.Equal(50, rig.Vm.Items.Count);
        Assert.True(rig.Vm.HasMore);

        await rig.Vm.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(100, rig.Vm.Items.Count);
        Assert.True(rig.Vm.HasMore);

        await rig.Vm.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(120, rig.Vm.Items.Count);
        Assert.False(rig.Vm.HasMore);
        Assert.False(rig.Vm.LoadMoreCommand.CanExecute(null));
        Assert.Equal(120, rig.Vm.Items.Select(i => i.Id).Distinct().Count());
    }

    [Fact]
    public async Task LoadMore_CalledTwiceAtOnce_DoesNotDuplicate()
    {
        var store = new StaticListStore();
        for (var i = 0; i < 60; i++)
            store.Add($"c{i:000}", $"對話 {i}", Now.AddMinutes(-i));
        var rig = CreateRig(store);
        await rig.Vm.RefreshAsync(null);

        var first = rig.Vm.LoadMoreCommand.ExecuteAsync(null);
        var second = rig.Vm.LoadMoreCommand.ExecuteAsync(null);
        await Task.WhenAll(first, second);

        Assert.Equal(60, rig.Vm.Items.Count);
        Assert.Equal(60, rig.Vm.Items.Select(i => i.Id).Distinct().Count());
    }

    [Fact]
    public async Task LoadMore_IsNotAvailableWhileSearching()
    {
        var store = new StaticListStore();
        for (var i = 0; i < 60; i++)
            store.Add($"c{i:000}", $"報告 {i}", Now.AddMinutes(-i));
        var rig = CreateRig(store);
        await rig.Vm.RefreshAsync(null);
        Assert.True(rig.Vm.HasMore);

        rig.Vm.SearchText = "報";
        Assert.False(rig.Vm.LoadMoreCommand.CanExecute(null));
        Assert.False(rig.Vm.HasMore);
    }

    // ---- 改名 ----

    [Fact]
    public async Task Rename_TrimsTheTitle_PersistsIt_AndTellsTheWorkbench()
    {
        var store = new StaticListStore();
        store.Add("c1", "舊標題", Now);
        var rig = CreateRig(store);
        await rig.Vm.RefreshAsync(null);
        var item = rig.Vm.Items[0];

        rig.Vm.RenameCommand.Execute(item);
        Assert.True(item.IsEditing);
        Assert.Equal("舊標題", item.EditText);
        item.EditText = "  新標題  ";
        await rig.Vm.CommitRenameCommand.ExecuteAsync(item);

        Assert.False(item.IsEditing);
        Assert.Equal("新標題", item.Title);
        Assert.Equal("新標題", store.Get("c1").Title);
        Assert.Equal(new[] { ("c1", "新標題") }, rig.Host.Renamed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("舊標題")]
    public async Task Rename_BlankOrUnchanged_IsTreatedAsCancel_AndWritesNothing(string input)
    {
        var store = new StaticListStore();
        store.Add("c1", "舊標題", Now);
        var rig = CreateRig(store);
        await rig.Vm.RefreshAsync(null);
        var item = rig.Vm.Items[0];

        rig.Vm.RenameCommand.Execute(item);
        item.EditText = input;
        await rig.Vm.CommitRenameCommand.ExecuteAsync(item);

        Assert.False(item.IsEditing);
        Assert.Equal("舊標題", store.Get("c1").Title);
        Assert.Empty(rig.Host.Renamed);
    }

    [Fact]
    public async Task Rename_Escape_CancelsWithoutWriting()
    {
        var store = new StaticListStore();
        store.Add("c1", "舊標題", Now);
        var rig = CreateRig(store);
        await rig.Vm.RefreshAsync(null);
        var item = rig.Vm.Items[0];

        rig.Vm.RenameCommand.Execute(item);
        item.EditText = "改了一半";
        rig.Vm.CancelRenameCommand.Execute(item);

        Assert.False(item.IsEditing);
        Assert.Equal("舊標題", item.Title);
        Assert.Equal("舊標題", store.Get("c1").Title);
    }

    [Fact]
    public async Task Rename_LongTitle_IsClippedTo60Characters()
    {
        var store = new StaticListStore();
        store.Add("c1", "舊", Now);
        var rig = CreateRig(store);
        await rig.Vm.RefreshAsync(null);
        var item = rig.Vm.Items[0];

        rig.Vm.RenameCommand.Execute(item);
        item.EditText = new string('長', 75);
        await rig.Vm.CommitRenameCommand.ExecuteAsync(item);

        Assert.Equal(new string('長', 60), item.Title);
        Assert.Equal(new string('長', 60), store.Get("c1").Title);
    }

    [Theory]
    [InlineData("  標題  ", "標題")]
    [InlineData("", null)]
    [InlineData(" \t\r\n ", null)]
    [InlineData(null, null)]
    public void NormalizeTitle_TrimsAndRejectsBlank(string? input, string? expected) =>
        Assert.Equal(expected, HistoryViewModel.NormalizeTitle(input, 60));

    [Fact]
    public void NormalizeTitle_ClipsByCharacter_NotByUtf16Unit()
    {
        // 🇹🇼 是兩個字元，不能切開；每個 emoji 算一個字
        var input = string.Concat(Enumerable.Repeat("😀", 61));
        var clipped = HistoryViewModel.NormalizeTitle(input, 60);

        Assert.Equal(string.Concat(Enumerable.Repeat("😀", 60)), clipped);
    }

    [Fact]
    public async Task Rename_WhileResponding_IsAllowed()
    {
        var store = new StaticListStore();
        store.Add("c1", "舊", Now);
        var rig = CreateRig(store, responding: true, current: "c1");
        await rig.Vm.RefreshAsync("c1");
        var item = rig.Vm.Items[0];

        rig.Vm.RenameCommand.Execute(item);
        item.EditText = "回應中改的";
        await rig.Vm.CommitRenameCommand.ExecuteAsync(item);

        Assert.Equal("回應中改的", store.Get("c1").Title);
    }

    [Fact]
    public async Task Rename_OnlyOneRowIsEditedAtATime()
    {
        var store = new StaticListStore();
        store.Add("a", "甲", Now);
        store.Add("b", "乙", Now.AddMinutes(-1));
        var rig = CreateRig(store);
        await rig.Vm.RefreshAsync(null);

        rig.Vm.RenameCommand.Execute(rig.Vm.Items[0]);
        rig.Vm.RenameCommand.Execute(rig.Vm.Items[1]);

        Assert.False(rig.Vm.Items[0].IsEditing);
        Assert.True(rig.Vm.Items[1].IsEditing);
    }

    // ---- 刪除 ----

    [Fact]
    public async Task Delete_AsksFirst_WithTheExplanation_ThenDeletesFromTheStore()
    {
        var store = new StaticListStore();
        store.Add("a", "要刪的", Now);
        store.Add("b", "留著", Now.AddMinutes(-1));
        var rig = CreateRig(store, current: "b");
        await rig.Vm.RefreshAsync("b");

        await rig.Vm.DeleteCommand.ExecuteAsync(rig.Vm.Items[0]);

        var asked = Assert.Single(rig.Confirm.Asked);
        Assert.Contains("要刪的", asked.Message);
        Assert.Contains("無法復原", asked.Message);
        Assert.Contains("檔案清單與圖片附件", asked.Message);
        Assert.Equal(Strings.Confirm_Delete, asked.ConfirmText);
        Assert.Equal(new[] { "b" }, store.All().Select(c => c.Id).ToArray());
        Assert.Equal(new[] { "b" }, rig.Vm.Items.Select(i => i.Id).ToArray());
        Assert.Equal(new[] { ("a", (string?)null) }, rig.Host.Deleted);
        Assert.Equal("b", rig.Host.CurrentConversationId);   // 刪的不是目前的對話，不切換
    }

    [Fact]
    public async Task Delete_Declined_KeepsEverything()
    {
        var store = new StaticListStore();
        store.Add("a", "留著", Now);
        var rig = CreateRig(store);
        await rig.Vm.RefreshAsync(null);
        rig.Confirm.Answer = false;

        await rig.Vm.DeleteCommand.ExecuteAsync(rig.Vm.Items[0]);

        Assert.Single(store.All());
        Assert.Single(rig.Vm.Items);
        Assert.Empty(rig.Host.Deleted);
    }

    [Fact]
    public async Task Delete_TheCurrentConversation_PassesTheNextOneInTheList()
    {
        var store = new StaticListStore();
        store.Add("a", "目前的", Now);
        store.Add("b", "下一則", Now.AddMinutes(-1));
        store.Add("c", "再下一則", Now.AddMinutes(-2));
        var rig = CreateRig(store, current: "a");
        await rig.Vm.RefreshAsync("a");

        await rig.Vm.DeleteCommand.ExecuteAsync(rig.Vm.Items[0]);

        Assert.Equal(new[] { ("a", (string?)"b") }, rig.Host.Deleted);
        Assert.DoesNotContain(rig.Vm.Items, i => i.Id == "a");
    }

    [Fact]
    public async Task Delete_TheLastOne_PassesNoNext_SoTheWorkbenchOpensANewOne()
    {
        var store = new StaticListStore();
        store.Add("a", "唯一的", Now);
        var rig = CreateRig(store, current: "a");
        await rig.Vm.RefreshAsync("a");

        await rig.Vm.DeleteCommand.ExecuteAsync(rig.Vm.Items[0]);

        Assert.Equal(new[] { ("a", (string?)null) }, rig.Host.Deleted);
        Assert.Empty(rig.Vm.Items);
        Assert.True(rig.Vm.IsEmpty);
    }

    [Fact]
    public async Task Delete_IsBlocked_WhileResponding_WithAReason()
    {
        var store = new StaticListStore();
        store.Add("a", "回應中", Now);
        store.Add("b", "另一則", Now.AddMinutes(-1));
        var host = new FakeHistoryHost { IsResponding = true, CurrentConversationId = "a" };
        var vm = new HistoryViewModel(store, host, new ManualTimeProvider(Now), null, HistoryTestSupport.Deletion(store), new FakeConfirmService());
        await vm.RefreshAsync("a");

        Assert.False(vm.DeleteCommand.CanExecute(vm.Items[0]));
        Assert.False(vm.DeleteCommand.CanExecute(vm.Items[1]));   // 回應中就整體停用（清附件的時機）
        Assert.True(vm.Items[0].DeleteBlocked);
        Assert.Equal(Strings.History_DeleteBlocked, vm.Items[0].DeleteTip);

        host.IsResponding = false;
        vm.HostStateChanged();

        Assert.True(vm.DeleteCommand.CanExecute(vm.Items[0]));
        Assert.False(vm.Items[0].DeleteBlocked);
        Assert.Equal("", vm.Items[0].DeleteTip);
    }

    [Fact]
    public async Task Delete_WithoutTheService_IsNotAvailable()
    {
        var store = new StaticListStore();
        store.Add("a", "沒有刪除服務", Now);
        var rig = CreateRig(store, withDeletion: false);
        await rig.Vm.RefreshAsync(null);

        Assert.False(rig.Vm.DeleteCommand.CanExecute(rig.Vm.Items[0]));
    }

    // ---- 選取與目前對話 ----

    [Fact]
    public async Task SelectingAnItem_OpensIt_AndRefreshDoesNotOpenAnything()
    {
        var store = new StaticListStore();
        store.Add("a", "甲", Now);
        store.Add("b", "乙", Now.AddMinutes(-1));
        var rig = CreateRig(store, current: "a");
        await rig.Vm.RefreshAsync("a");
        Assert.Same(rig.Vm.Items[0], rig.Vm.SelectedItem);   // 目前的對話預先選取，不會開啟

        rig.Vm.SelectedItem = rig.Vm.Items[1];
        await Task.Delay(20);

        Assert.Equal(new[] { "b" }, rig.Host.Opened);
    }

    [Fact]
    public async Task Refresh_MarksOnlyTheCurrentConversation()
    {
        var store = new StaticListStore();
        store.Add("a", "甲", Now);
        store.Add("b", "乙", Now.AddMinutes(-1));
        var rig = CreateRig(store);
        await rig.Vm.RefreshAsync("b");

        Assert.False(rig.Vm.Items[0].IsCurrent);
        Assert.True(rig.Vm.Items[1].IsCurrent);
        Assert.Empty(rig.Host.Opened);   // 重新整理不會開啟任何對話
    }

    [Fact]
    public void Highlight_MarksEveryCaseInsensitiveHit()
    {
        var parts = ConversationListItem.Highlight("Report 週報 report", "REPORT");

        Assert.Equal(new[] { ("Report", true), (" 週報 ", false), ("report", true) },
            parts.Select(p => (p.Text, p.Hit)).ToArray());
    }

    [Fact]
    public void Highlight_WithoutAQuery_IsOnePlainPart()
    {
        var parts = ConversationListItem.Highlight("沒有查詢", "");
        Assert.Equal(new[] { ("沒有查詢", false) }, parts.Select(p => (p.Text, p.Hit)).ToArray());
    }
}
