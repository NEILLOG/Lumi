using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

/// <summary>3C：工具卡片、群組卡、卡片內的確認、停止、重開後重建。</summary>
public class WorkbenchToolCardsTests
{
    private sealed record Rig(WorkbenchViewModel Vm, InMemoryConversationStore Store, ToolApprovalService Approvals, FakeClipboardService Clipboard,
        IReadOnlyList<LambdaTool> Tools);

    private static Rig CreateRig(IChatProvider provider, params LambdaTool[] tools)
    {
        var store = new InMemoryConversationStore();
        var catalog = new FakeModelCatalog();
        var approvals = new ToolApprovalService();
        var clipboard = new FakeClipboardService();
        var time = new FakeTimeProvider();
        var service = new ConversationService(provider, store, catalog, new ToolRegistry(tools), approvals, new ToolPermissionMemory(),
            new ToolLoopOptions(), time, NullLogger<ConversationService>.Instance);
        var vm = new WorkbenchViewModel(service, store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
            new ConnectionStatusCache(), clipboard, new SyncUiDispatcher(), time, NullLogger<WorkbenchViewModel>.Instance, approvals);
        return new Rig(vm, store, approvals, clipboard, tools);
    }

    private static Task Wait(Func<bool> condition) => SettingsHost.WaitUntil(condition);

    private static async Task Send(Rig rig, string text = "請處理")
    {
        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = text;
        await rig.Vm.SendCommand.ExecuteAsync(null);
    }

    private static ToolRunViewModel Run(Rig rig) => rig.Vm.Messages.OfType<ToolRunViewModel>().Single();

    // ---- 唯讀工具 ----

    [Fact]
    public async Task ReadOnlyTool_ShowsOneSucceededCard_BetweenUserAndAnswer()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(null, SequenceChatProvider.Use("t1", "echo")), SequenceChatProvider.Answer("好了"));
        var rig = CreateRig(provider, new LambdaTool("echo", "回聲"));

        await Send(rig);

        Assert.Equal([true, false, false], rig.Vm.Messages.Select(m => m.IsUser));   // 使用者、工具段落、答案（沒有文字的工具步驟不留氣泡）
        var card = Assert.Single(Run(rig).Cards);
        Assert.Equal(ToolCardState.Succeeded, card.State);
        Assert.Equal("回聲 · 甲 · 完成 甲", card.Title);
        Assert.Equal("結果：甲", card.ResultText);
        Assert.False(card.IsForcedOpen);
        Assert.False(card.IsOpen);   // 預設收合
        Assert.Equal("好了", rig.Vm.Messages[^1].Text);
        Assert.Equal(1, rig.Vm.Turns);
        Assert.False(Run(rig).IsGroup);
    }

    [Fact]
    public async Task TextBeforeTheToolCall_KeepsItsOwnBubble_AndActionsOnlyOnTheLastOne()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls("我來查。", SequenceChatProvider.Use("t1", "echo")), SequenceChatProvider.Answer("查到了"));
        var rig = CreateRig(provider, new LambdaTool("echo", "回聲"));

        await Send(rig);

        Assert.Equal(["請處理", "我來查。", "", "查到了"], rig.Vm.Messages.Select(m => m.Text));
        Assert.IsType<ToolRunViewModel>(rig.Vm.Messages[2]);
        Assert.False(rig.Vm.Messages[1].ShowActions);   // 後面接著工具段落，不是這一輪的結尾
        Assert.True(rig.Vm.Messages[3].ShowActions);
        Assert.True(rig.Vm.Messages[3].IsLastAssistant);
    }

    [Fact]
    public async Task WhileWaitingForTheNextResponse_ATypingBubbleFollowsTheCards()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(null, SequenceChatProvider.Use("t1", "echo")), []) { HangOnCall = 2 };
        var rig = CreateRig(provider, new LambdaTool("echo", "回聲"));
        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = "x";

        var sending = rig.Vm.SendCommand.ExecuteAsync(null);
        await Wait(() => provider.Calls == 2 && rig.Vm.Messages.Count == 3);

        Assert.IsType<ToolRunViewModel>(rig.Vm.Messages[1]);
        Assert.True(rig.Vm.Messages[2].ShowTypingIndicator);
        await rig.Vm.StopAndWaitAsync();
        await sending;
    }

    // ---- 確認 ----

    [Fact]
    public async Task WriteTool_WaitsInTheCard_ThenRunsAfterAllowOnce()
    {
        var tool = new LambdaTool("save", "儲存", ToolPermission.Write);
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(null, SequenceChatProvider.Use("t1", "save")), SequenceChatProvider.Answer("存好了"));
        var rig = CreateRig(provider, tool);
        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = "存";

        var sending = rig.Vm.SendCommand.ExecuteAsync(null);
        await Wait(() => rig.Vm.Messages.OfType<ToolRunViewModel>().Any(r => r.Cards.Any(c => c.IsAwaiting)));
        var card = Run(rig).Cards[0];

        Assert.True(card.IsForcedOpen);
        Assert.False(card.CanToggle);
        Assert.True(card.ShowAllowForConversation);
        Assert.False(card.IsDestructive);
        Assert.Equal("會處理「甲」", card.PreviewDetail);
        Assert.Equal(0, tool.Runs);
        Assert.True(rig.Vm.IsResponding);   // 等待確認時仍是回應中

        card.AllowOnceCommand.Execute(null);
        await sending;

        Assert.Equal(1, tool.Runs);
        Assert.Equal(ToolCardState.Succeeded, card.State);
        Assert.Equal(ToolApproval.AllowedOnce, card.Approval);
        Assert.Equal(Strings.Tool_ApprovedOnce, card.ApprovalNote);
        Assert.Equal("存好了", rig.Vm.Messages[^1].Text);
    }

    [Fact]
    public async Task Deny_ShowsDenied_ToolNeverRuns_AndModelContinues()
    {
        var tool = new LambdaTool("save", "儲存", ToolPermission.Write);
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(null, SequenceChatProvider.Use("t1", "save")), SequenceChatProvider.Answer("好，不存了"));
        var rig = CreateRig(provider, tool);
        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = "存";

        var sending = rig.Vm.SendCommand.ExecuteAsync(null);
        await Wait(() => rig.Vm.Messages.OfType<ToolRunViewModel>().Any(r => r.Cards.Any(c => c.IsAwaiting)));
        var card = Run(rig).Cards[0];
        card.DenyCommand.Execute(null);
        await sending;

        Assert.Equal(0, tool.Runs);
        Assert.Equal(ToolCardState.Denied, card.State);
        Assert.Equal(Strings.Tool_Denied, card.ApprovalNote);
        Assert.Equal("好，不存了", rig.Vm.Messages[^1].Text);
    }

    [Fact]
    public async Task AllowForConversation_SecondCallDoesNotAsk()
    {
        var tool = new LambdaTool("save", "儲存", ToolPermission.Write);
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(null, SequenceChatProvider.Use("t1", "save", "甲")),
            SequenceChatProvider.ToolCalls(null, SequenceChatProvider.Use("t2", "save", "乙")),
            SequenceChatProvider.Answer("兩個都存了"));
        var rig = CreateRig(provider, tool);
        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = "存兩個";

        var sending = rig.Vm.SendCommand.ExecuteAsync(null);
        await Wait(() => rig.Vm.Messages.OfType<ToolRunViewModel>().Any(r => r.Cards.Any(c => c.IsAwaiting)));
        Run(rig).Cards[0].AllowForConversationCommand.Execute(null);
        await sending;

        var cards = Run(rig).Cards;
        Assert.Equal(2, cards.Count);
        Assert.All(cards, c => Assert.Equal(ToolApproval.AllowedForConversation, c.Approval));
        Assert.Equal(2, tool.Runs);
    }

    [Fact]
    public async Task DestructiveCard_HasNoRememberOption()
    {
        var tool = new LambdaTool("del", "刪除", ToolPermission.Destructive);
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(null, SequenceChatProvider.Use("t1", "del")), SequenceChatProvider.Answer("好"));
        var rig = CreateRig(provider, tool);
        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = "刪";

        var sending = rig.Vm.SendCommand.ExecuteAsync(null);
        await Wait(() => rig.Vm.Messages.OfType<ToolRunViewModel>().Any(r => r.Cards.Any(c => c.IsAwaiting)));
        var card = Run(rig).Cards[0];

        Assert.True(card.IsDestructive);
        Assert.False(card.ShowAllowForConversation);
        card.AllowForConversationCommand.Execute(null);   // 就算硬呼叫，也只算允許一次
        await sending;
        Assert.Equal(ToolApproval.AllowedOnce, card.Approval);
    }

    // ---- 停止 ----

    [Fact]
    public async Task Stop_WhileAwaitingApproval_ReloadsFromStore_WithStoppedCard()
    {
        var tool = new LambdaTool("save", "儲存", ToolPermission.Write);
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(null, SequenceChatProvider.Use("t1", "save")));
        var rig = CreateRig(provider, tool);
        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = "存";

        var sending = rig.Vm.SendCommand.ExecuteAsync(null);
        await Wait(() => rig.Vm.Messages.OfType<ToolRunViewModel>().Any(r => r.Cards.Any(c => c.IsAwaiting)));
        rig.Vm.StopCommand.Execute(null);
        await sending;

        Assert.False(rig.Vm.IsResponding);
        var card = Assert.Single(Run(rig).Cards);
        Assert.Equal(ToolCardState.Failed, card.State);   // 已停止：不是轉圈、也不是等待
        Assert.Equal("已停止", card.StatusText);
        Assert.False(card.IsForcedOpen);
        Assert.Equal(0, tool.Runs);
        Assert.Equal(ChatRole.Tool, (await rig.Store.GetMessagesAsync((await rig.Store.GetLatestAsync())!.Id))[^1].Role);
    }

    [Fact]
    public async Task Stop_WhileToolRuns_EndsWithStoppedCard_AndNoLeftovers()
    {
        var running = new TaskCompletionSource();
        var tool = new LambdaTool("slow", "慢工具")
        {
            Behavior = async (_, ct) => { running.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return ToolResult.Ok("never"); },
        };
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(null, SequenceChatProvider.Use("t1", "slow")));
        var rig = CreateRig(provider, tool);
        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = "跑";

        var sending = rig.Vm.SendCommand.ExecuteAsync(null);
        await running.Task;
        await rig.Vm.StopAndWaitAsync();
        await sending;

        Assert.Equal(ToolCardState.Failed, Assert.Single(Run(rig).Cards).State);
        Assert.DoesNotContain(rig.Vm.Messages, m => m.IsStreaming);
    }

    // ---- 群組 ----

    [Fact]
    public async Task ThreeConsecutiveCalls_MergeIntoOneGroup_WithSummaryTitle()
    {
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(null, SequenceChatProvider.Use("a", "echo", "一"), SequenceChatProvider.Use("b", "echo", "二"), SequenceChatProvider.Use("c", "look", "三")),
            SequenceChatProvider.Answer("好"));
        var rig = CreateRig(provider, new LambdaTool("echo", "回聲"), new LambdaTool("look", "查看"));

        await Send(rig);

        var run = Run(rig);
        Assert.True(run.IsGroup);
        Assert.Equal("3 個工具呼叫 · 回聲 2 次、查看 1 次，全部成功", run.GroupTitle);
        Assert.False(run.GroupIsOpen);   // 預設收合
        Assert.False(run.GroupHasFailure);
    }

    [Fact]
    public async Task Group_WithAFailure_NamesTheFailedItem()
    {
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(null, SequenceChatProvider.Use("a", "echo", "一"), SequenceChatProvider.Use("b", "bad", "二"), SequenceChatProvider.Use("c", "echo", "三")),
            SequenceChatProvider.Answer("好"));
        var bad = new LambdaTool("bad", "壞工具") { Behavior = (_, _) => Task.FromResult(ToolResult.Error("爆了", "爆了")) };
        var rig = CreateRig(provider, new LambdaTool("echo", "回聲"), bad);

        await Send(rig);

        var run = Run(rig);
        Assert.True(run.GroupHasFailure);
        Assert.Contains("1 個失敗（壞工具 二）", run.GroupTitle);
        Assert.DoesNotContain("全部成功", run.GroupTitle);
    }

    [Fact]
    public async Task Group_ForcesOpen_WhileACardAwaitsApproval()
    {
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(null, SequenceChatProvider.Use("a", "echo", "一"), SequenceChatProvider.Use("b", "save", "二"), SequenceChatProvider.Use("c", "echo", "三")),
            SequenceChatProvider.Answer("好"));
        var rig = CreateRig(provider, new LambdaTool("echo", "回聲"), new LambdaTool("save", "儲存", ToolPermission.Write));
        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = "x";

        var sending = rig.Vm.SendCommand.ExecuteAsync(null);
        await Wait(() => rig.Vm.Messages.OfType<ToolRunViewModel>().Any(r => r.Cards.Any(c => c.IsAwaiting)));
        var run = Run(rig);

        Assert.True(run.GroupIsOpen);
        Assert.False(run.CanToggleGroup);
        Assert.Contains("等待允許", run.GroupTitle);

        run.Cards.Single(c => c.IsAwaiting).AllowOnceCommand.Execute(null);
        await sending;
        Assert.False(run.GroupIsOpen);
        Assert.Contains("全部成功", run.GroupTitle);
    }

    // ---- 重開後重建 ----

    [Fact]
    public async Task ReopeningTheConversation_RebuildsCardsWithApprovalAndSummary()
    {
        var tool = new LambdaTool("save", "儲存", ToolPermission.Write);
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls("我來存。", SequenceChatProvider.Use("t1", "save")), SequenceChatProvider.Answer("存好了"));
        var rig = CreateRig(provider, tool);
        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = "存";
        var sending = rig.Vm.SendCommand.ExecuteAsync(null);
        await Wait(() => rig.Vm.Messages.OfType<ToolRunViewModel>().Any(r => r.Cards.Any(c => c.IsAwaiting)));
        Run(rig).Cards[0].AllowOnceCommand.Execute(null);
        await sending;
        var before = rig.Vm.Messages.Select(m => (m.GetType(), m.Text)).ToList();

        // 同一個資料庫、新的畫面
        var vm2 = new WorkbenchViewModel(new ConversationService(new SequenceChatProvider(), rig.Store, new FakeModelCatalog()), rig.Store, new FakeModelCatalog(),
            new FakeApiKeyPrompt(false), new FakeSettingsWindowService(), new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(),
            new FakeTimeProvider(), NullLogger<WorkbenchViewModel>.Instance);
        await vm2.InitializeAsync();

        Assert.Equal(before, vm2.Messages.Select(m => (m.GetType(), m.Text)).ToList());
        var card = Assert.Single(vm2.Messages.OfType<ToolRunViewModel>().Single().Cards);
        Assert.Equal(ToolCardState.Succeeded, card.State);
        Assert.Equal("儲存 · 甲 · 完成 甲", card.Title);
        Assert.Equal(Strings.Tool_ApprovedOnce, card.ApprovalNote);
    }

    [Fact]
    public async Task ReopeningWithAnUnansweredToolCall_ShowsNoResult_NotASpinner()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("中斷");
        await store.AppendMessageAsync(conversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("x")], DateTimeOffset.UtcNow));
        await store.AppendMessageAsync(conversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant,
            [new ToolUseBlock("t1", "echo", """{"text":"甲"}""")], DateTimeOffset.UtcNow));
        var catalog = new FakeModelCatalog();
        var vm = new WorkbenchViewModel(new ConversationService(new SequenceChatProvider(), store, catalog), store, catalog, new FakeApiKeyPrompt(false),
            new FakeSettingsWindowService(), new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
            NullLogger<WorkbenchViewModel>.Instance);

        await vm.InitializeAsync();

        var card = Assert.Single(Assert.IsType<ToolRunViewModel>(vm.Messages[1]).Cards);
        Assert.Equal(ToolCardState.Failed, card.State);
        Assert.Equal(Strings.Tool_NoResult, card.StatusText);
    }

    // ---- 重新產生 ----

    [Fact]
    public async Task Regenerate_AfterAToolTurn_RemovesTheCardsToo()
    {
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(null, SequenceChatProvider.Use("t1", "echo")), SequenceChatProvider.Answer("舊答案"),
            SequenceChatProvider.Answer("新答案"));
        var rig = CreateRig(provider, new LambdaTool("echo", "回聲"));
        await Send(rig);
        Assert.Equal(3, rig.Vm.Messages.Count);

        await rig.Vm.RegenerateCommand.ExecuteAsync(null);

        Assert.Equal(2, rig.Vm.Messages.Count);
        Assert.Empty(rig.Vm.Messages.OfType<ToolRunViewModel>());
        Assert.Equal("新答案", rig.Vm.Messages[^1].Text);
    }

    // ---- 卡片本身 ----

    private static ToolCardViewModel NewCard(FakeClipboardService? clipboard = null, IToolApprovalResponder? responder = null) =>
        new("c1", "echo", "回聲", ToolPermission.Write, "目標", """{"text":"你好"}""", responder ?? NullResponder.Instance,
            clipboard ?? new FakeClipboardService(), new FakeTimeProvider());

    private sealed class NullResponder : IToolApprovalResponder
    {
        public static readonly NullResponder Instance = new();
        public void Resolve(string callId, ApprovalDecision decision) { }
    }

    private static ToolResultBlock Result(string content, bool error = false, long ms = 320, string? summary = "摘要") =>
        new("c1", content, error, "回聲", "目標", summary, ms, ToolApproval.NotRequired);

    [Fact]
    public void Card_Running_IsForcedOpen_AndShowsRunningStatus()
    {
        var card = NewCard();

        Assert.True(card.IsRunning);
        Assert.True(card.IsOpen);
        Assert.False(card.CanToggle);
        Assert.Equal("回聲 · 目標 · " + Strings.Tool_Running, card.Title);
    }

    [Fact]
    public void Card_Finished_CanBeToggled_AndShowsPrettyArgumentsWithChinese()
    {
        var card = NewCard();
        card.Complete(Result("ok"));

        Assert.False(card.IsOpen);
        Assert.True(card.CanToggle);
        card.ToggleCommand.Execute(null);
        Assert.True(card.IsOpen);
        Assert.Contains("你好", card.ArgumentsText);   // 不跳成 \uXXXX
        Assert.Contains("\n", card.ArgumentsText);     // 有縮排
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(320, "320 毫秒")]
    [InlineData(1500, "1.5 秒")]
    public void Card_DurationText(long ms, string expected)
    {
        var card = NewCard();
        card.Complete(Result("ok", ms: ms));

        Assert.Equal(expected.Length > 0, card.HasDuration);
        if (expected.Length > 0)
            Assert.Equal(expected, card.DurationText);
    }

    [Fact]
    public void Card_ErrorWithoutSummary_FallsBackToFailedText()
    {
        var card = NewCard();
        card.Complete(Result("壞了", error: true, summary: null));

        Assert.Equal(ToolCardState.Failed, card.State);
        Assert.EndsWith(Strings.Tool_Failed, card.Title);
    }

    [Fact]
    public void Card_LongResult_ShowsFirstSixLines_UntilExpanded()
    {
        var card = NewCard();
        var content = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"第 {i} 行"));
        card.Complete(Result(content));

        Assert.True(card.HasMoreResult);
        Assert.Equal(string.Join('\n', Enumerable.Range(1, 6).Select(i => $"第 {i} 行")) + "\n…", card.ResultDisplay);
        card.ToggleFullOutputCommand.Execute(null);
        Assert.Equal(content, card.ResultDisplay);
        Assert.Equal(Strings.Tool_ShowLess, card.ShowFullLabel);
    }

    [Fact]
    public void Card_ShortResult_HasNoShowFullOption()
    {
        var card = NewCard();
        card.Complete(Result("一行\n兩行"));

        Assert.False(card.HasMoreResult);
        Assert.Equal("一行\n兩行", card.ResultDisplay);
    }

    [Fact]
    public async Task Card_CopyResult_CopiesTheFullTextNotThePreview()
    {
        var clipboard = new FakeClipboardService();
        var card = NewCard(clipboard);
        var content = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"第 {i} 行"));
        card.Complete(Result(content));

        await card.CopyResultCommand.ExecuteAsync(null);

        Assert.Equal([content], clipboard.Texts);
    }

    [Fact]
    public void Card_DecisionsAreIgnoredUnlessAwaiting()
    {
        var responder = new RecordingResponder();
        var card = NewCard(responder: responder);

        card.AllowOnceCommand.Execute(null);   // 還在執行中，沒有人在等

        Assert.Empty(responder.Calls);
    }

    private sealed class RecordingResponder : IToolApprovalResponder
    {
        public List<(string, ApprovalDecision)> Calls { get; } = [];
        public void Resolve(string callId, ApprovalDecision decision) => Calls.Add((callId, decision));
    }
}
