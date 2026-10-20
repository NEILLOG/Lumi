using Lumi.Core.Chat;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests.Chat;

public class ConversationToolLoopTests
{
    private static readonly FakeModelCatalog Catalog = new();

    private static async Task<List<ChatEvent>> Collect(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events)
            list.Add(e);
        return list;
    }

    private static ToolUseBlock Use(string id, string name = "echo", string json = """{"text":"嗨"}""") => new(id, name, json);

    private sealed class Rig
    {
        public InMemoryConversationStore Store { get; } = new();
        public ToolPermissionMemory Memory { get; } = new();
        public Conversation Conversation { get; private set; } = null!;
        public ConversationService Service { get; private set; } = null!;

        public static async Task<Rig> CreateAsync(
            IChatProvider provider, IEnumerable<ITool> tools, IToolApprovalService? approvals = null,
            ToolLoopOptions? options = null, FakeModelCatalog? catalog = null)
        {
            var rig = new Rig();
            rig.Conversation = await rig.Store.CreateAsync("新對話");
            rig.Service = new ConversationService(provider, rig.Store, catalog ?? Catalog, new ToolRegistry(tools),
                approvals ?? new ScriptedApprovalService(), rig.Memory, options ?? new ToolLoopOptions(),
                TimeProvider.System, NullLogger<ConversationService>.Instance);
            return rig;
        }

        public Task<IReadOnlyList<ChatMessage>> Messages() => Store.GetMessagesAsync(Conversation.Id);
    }

    [Fact]
    public async Task SingleToolCall_RunsToolAndSendsResultBack()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1")), SequenceChatProvider.Answer("完成"));
        var tool = new FakeTool();
        var rig = await Rig.CreateAsync(provider, [tool]);

        var events = await Collect(rig.Service.SendAsync(rig.Conversation.Id, "請回聲"));

        Assert.Equal(2, events.OfType<Completed>().Count());
        var started = Assert.Single(events.OfType<ToolCallStarted>());
        Assert.Equal("t1", started.CallId);
        Assert.Equal("回聲", started.DisplayName);
        Assert.Equal("嗨", started.Target);
        var done = Assert.Single(events.OfType<ToolCallCompleted>());
        Assert.False(done.Result.IsError);
        Assert.Equal("嗨", done.Result.Content);

        var messages = await rig.Messages();
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant], messages.Select(m => m.Role));
        Assert.Equal("t1", Assert.IsType<ToolResultBlock>(Assert.Single(messages[2].Content)).ToolUseId);

        // 第二次請求帶著完整的 tool_use / tool_result
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool], provider.Requests[1].Messages.Select(m => m.Role));
        Assert.NotNull(provider.Requests[0].Tools);
        Assert.Equal("echo", Assert.Single(provider.Requests[0].Tools!).Descriptor.Name);
    }

    [Fact]
    public async Task ToolsDisabled_RequestCarriesNoTools()
    {
        var catalog = new FakeModelCatalog
        {
            Current = new Lumi.Core.Models.AppSettings(
                [FakeModelCatalog.Connection], [FakeModelCatalog.Model with { ToolsEnabled = false }],
                FakeModelCatalog.Model.Id, FakeModelCatalog.Model.Id, Lumi.Core.Models.ThemePreference.System),
        };
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));
        var rig = await Rig.CreateAsync(provider, [new FakeTool()], catalog: catalog);

        await Collect(rig.Service.SendAsync(rig.Conversation.Id, "嗨"));

        Assert.Null(Assert.Single(provider.Requests).Tools);
    }

    [Fact]
    public async Task ToolDefinitions_AreTheSameInBothModes()
    {
        // 工具清單不隨模式改變，切換模式才不會讓 prompt 快取失效（M5 的 Q1）
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"), SequenceChatProvider.Answer("好"));
        var rig = await Rig.CreateAsync(provider, [new FakeTool("only_sprite", modes: ToolModes.Sprite), new FakeTool("only_bench", modes: ToolModes.Workbench)]);

        await Collect(rig.Service.SendAsync(rig.Conversation.Id, "嗨", mode: ChatMode.Workbench));
        await Collect(rig.Service.SendAsync(rig.Conversation.Id, "嗨", mode: ChatMode.Sprite));

        Assert.Equal(["only_bench", "only_sprite"], provider.Requests[0].Tools!.Select(t => t.Descriptor.Name));
        Assert.Equal(["only_bench", "only_sprite"], provider.Requests[1].Tools!.Select(t => t.Descriptor.Name));
    }

    [Fact]
    public async Task ToolCalledInWrongMode_ReturnsError_WithoutRunningOrAsking()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1", "only_sprite")), SequenceChatProvider.Answer("好"));
        var tool = new FakeTool("only_sprite", ToolPermission.Write, ToolModes.Sprite);
        var approvals = new ScriptedApprovalService();
        var rig = await Rig.CreateAsync(provider, [tool], approvals);

        var events = await Collect(rig.Service.SendAsync(rig.Conversation.Id, "嗨", mode: ChatMode.Workbench));

        Assert.Empty(events.OfType<ApprovalRequired>());
        Assert.Empty(tool.Calls);
        var done = Assert.Single(events.OfType<ToolCallCompleted>());
        Assert.True(done.Result.IsError);
        Assert.Contains("目前模式", done.Result.Content);
        Assert.Equal(2, provider.Requests.Count);   // 錯誤結果送回給模型，讓它改用其他方式回答
    }

    [Fact]
    public async Task MultipleToolCalls_RunSequentially_ResultsInOneToolMessage()
    {
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(Use("a", json: """{"text":"一"}"""), Use("b", json: """{"text":"二"}""")),
            SequenceChatProvider.Answer("好"));
        var order = new List<string>();
        var tool = new FakeTool
        {
            Behavior = async (args, _) =>
            {
                order.Add("start " + args.Text);
                await Task.Delay(20);
                order.Add("end " + args.Text);
                return ToolResult.Ok(args.Text);
            },
        };
        var rig = await Rig.CreateAsync(provider, [tool]);

        await Collect(rig.Service.SendAsync(rig.Conversation.Id, "x"));

        Assert.Equal(["start 一", "end 一", "start 二", "end 二"], order);
        var results = (await rig.Messages())[2].Content.OfType<ToolResultBlock>().ToList();
        Assert.Equal(["a", "b"], results.Select(r => r.ToolUseId));
    }

    [Fact]
    public async Task UnknownTool_ReturnsErrorResult_AndLoopContinues()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1", "nope")), SequenceChatProvider.Answer("好"));
        var rig = await Rig.CreateAsync(provider, [new FakeTool()]);

        var events = await Collect(rig.Service.SendAsync(rig.Conversation.Id, "x"));

        var result = Assert.Single(events.OfType<ToolCallCompleted>()).Result;
        Assert.True(result.IsError);
        Assert.Contains("nope", result.Content);
        Assert.IsType<Completed>(events[^1]);
    }

    [Fact]
    public async Task BadArguments_ReturnErrorResult_ToolNotRun()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1", json: "{壞掉")), SequenceChatProvider.Answer("好"));
        var tool = new FakeTool();
        var rig = await Rig.CreateAsync(provider, [tool]);

        var events = await Collect(rig.Service.SendAsync(rig.Conversation.Id, "x"));

        Assert.True(Assert.Single(events.OfType<ToolCallCompleted>()).Result.IsError);
        Assert.Empty(tool.Calls);
    }

    [Fact]
    public async Task ToolException_BecomesGenericError_WithoutLeakingDetails()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1")), SequenceChatProvider.Answer("好"));
        var tool = new FakeTool { Behavior = (_, _) => throw new InvalidOperationException("秘密路徑 /Users/x/secret") };
        var rig = await Rig.CreateAsync(provider, [tool]);

        var events = await Collect(rig.Service.SendAsync(rig.Conversation.Id, "x"));

        var result = Assert.Single(events.OfType<ToolCallCompleted>()).Result;
        Assert.True(result.IsError);
        Assert.DoesNotContain("秘密", result.Content);
    }

    [Fact]
    public async Task ToolTimeout_ReturnsTimeoutError()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1")), SequenceChatProvider.Answer("好"));
        var tool = new FakeTool(timeout: TimeSpan.FromMilliseconds(50))
        {
            Behavior = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return ToolResult.Ok("never"); },
        };
        var rig = await Rig.CreateAsync(provider, [tool]);

        var events = await Collect(rig.Service.SendAsync(rig.Conversation.Id, "x"));

        var result = Assert.Single(events.OfType<ToolCallCompleted>()).Result;
        Assert.True(result.IsError);
        Assert.Contains("逾時", result.Content);
    }

    [Fact]
    public async Task WriteTool_AsksApproval_AllowOnce_AsksAgainNextTime()
    {
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(Use("a")), SequenceChatProvider.ToolCalls(Use("b")), SequenceChatProvider.Answer("好"));
        var approvals = new ScriptedApprovalService(ApprovalDecision.AllowOnce, ApprovalDecision.AllowOnce);
        var rig = await Rig.CreateAsync(provider, [new FakeTool(permission: ToolPermission.Write)], approvals);

        var events = await Collect(rig.Service.SendAsync(rig.Conversation.Id, "x"));

        Assert.Equal(2, approvals.Requests.Count);
        Assert.Equal(2, events.OfType<ApprovalRequired>().Count());
        Assert.All(events.OfType<ToolCallCompleted>(), e => Assert.Equal(ToolApproval.AllowedOnce, e.Result.Approval));
    }

    [Fact]
    public async Task WriteTool_AllowForConversation_DoesNotAskAgain_AndIsPerConversation()
    {
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(Use("a")), SequenceChatProvider.ToolCalls(Use("b")), SequenceChatProvider.Answer("好"));
        var approvals = new ScriptedApprovalService(ApprovalDecision.AllowForConversation);
        var rig = await Rig.CreateAsync(provider, [new FakeTool(permission: ToolPermission.Write)], approvals);

        var events = await Collect(rig.Service.SendAsync(rig.Conversation.Id, "x"));

        Assert.Single(approvals.Requests);
        var results = events.OfType<ToolCallCompleted>().Select(e => e.Result.Approval).ToList();
        Assert.Equal([ToolApproval.AllowedForConversation, ToolApproval.AllowedForConversation], results);
        Assert.True(rig.Memory.IsAllowed(rig.Conversation.Id, ToolPermission.Write));
        Assert.False(rig.Memory.IsAllowed("別的對話", ToolPermission.Write));
    }

    [Fact]
    public async Task DestructiveTool_AlwaysAsks_EvenAfterAllowForConversation()
    {
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(Use("a")), SequenceChatProvider.ToolCalls(Use("b")), SequenceChatProvider.Answer("好"));
        var approvals = new ScriptedApprovalService(ApprovalDecision.AllowForConversation, ApprovalDecision.AllowOnce);
        var rig = await Rig.CreateAsync(provider, [new FakeTool(permission: ToolPermission.Destructive)], approvals);

        var events = await Collect(rig.Service.SendAsync(rig.Conversation.Id, "x"));

        Assert.Equal(2, approvals.Requests.Count);
        Assert.All(events.OfType<ToolCallCompleted>(), e => Assert.Equal(ToolApproval.AllowedOnce, e.Result.Approval));
    }

    [Fact]
    public async Task Denied_ToolNotRun_ErrorResultTellsModel_AndLoopContinues()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1")), SequenceChatProvider.Answer("好吧"));
        var tool = new FakeTool(permission: ToolPermission.Write);
        var rig = await Rig.CreateAsync(provider, [tool], new ScriptedApprovalService(ApprovalDecision.Deny));

        var events = await Collect(rig.Service.SendAsync(rig.Conversation.Id, "x"));

        var result = Assert.Single(events.OfType<ToolCallCompleted>()).Result;
        Assert.True(result.IsError);
        Assert.Equal(ToolApproval.Denied, result.Approval);
        Assert.Empty(tool.Calls);
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public async Task IterationLimit_FailsWithToolLoopLimit_AndKeepsHistoryRepairable()
    {
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(Use("a")), SequenceChatProvider.ToolCalls(Use("b")), SequenceChatProvider.ToolCalls(Use("c")));
        var rig = await Rig.CreateAsync(provider, [new FakeTool()], options: new ToolLoopOptions { MaxIterations = 3 });

        var events = await Collect(rig.Service.SendAsync(rig.Conversation.Id, "x"));

        var failed = Assert.IsType<Failed>(events[^1]);
        Assert.Equal(ChatFailureKind.ToolLoopLimit, failed.Failure.Kind);
        Assert.Equal(3, provider.Requests.Count);
        Assert.Equal(ChatRole.Tool, (await rig.Messages())[^1].Role);   // 每個 tool_use 都有結果，可以直接重試
    }

    [Fact]
    public async Task Cancel_WhileAwaitingApproval_BackfillsStoppedResult_AndRethrows()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("a"), Use("b")));
        var approvals = new ScriptedApprovalService { Gate = new TaskCompletionSource<ApprovalDecision>() };
        var tool = new FakeTool(permission: ToolPermission.Write);
        var rig = await Rig.CreateAsync(provider, [tool], approvals);
        using var cts = new CancellationTokenSource();

        var run = Collect(rig.Service.SendAsync(rig.Conversation.Id, "x", cts.Token));
        while (approvals.Requests.Count == 0)
            await Task.Delay(5);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Empty(tool.Calls);
        var last = (await rig.Messages())[^1];
        Assert.Equal(ChatRole.Tool, last.Role);
        var results = last.Content.OfType<ToolResultBlock>().ToList();
        Assert.Equal(["a", "b"], results.Select(r => r.ToolUseId));   // 後面還沒開始的呼叫也補了結果
        Assert.All(results, r => Assert.True(r.IsError));
    }

    [Fact]
    public async Task Cancel_WhileToolRunning_BackfillsStoppedResult()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("a")));
        var running = new TaskCompletionSource();
        var tool = new FakeTool
        {
            Behavior = async (_, ct) => { running.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return ToolResult.Ok("never"); },
        };
        var rig = await Rig.CreateAsync(provider, [tool]);
        using var cts = new CancellationTokenSource();

        var run = Collect(rig.Service.SendAsync(rig.Conversation.Id, "x", cts.Token));
        await running.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        var result = Assert.Single((await rig.Messages())[^1].Content.OfType<ToolResultBlock>());
        Assert.True(result.IsError);
        Assert.Contains("停止", result.Content);
    }

    [Fact]
    public async Task Retry_FromToolTerminatedHistory_ContinuesLoop()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("a")), SequenceChatProvider.Answer("續上"));
        var rig = await Rig.CreateAsync(provider, [new FakeTool()], options: new ToolLoopOptions { MaxIterations = 1 });
        var first = await Collect(rig.Service.SendAsync(rig.Conversation.Id, "x"));
        Assert.Equal(ChatFailureKind.ToolLoopLimit, Assert.IsType<Failed>(first[^1]).Failure.Kind);

        var rig2Service = new ConversationService(provider, rig.Store, Catalog, new ToolRegistry([new FakeTool()]),
            new ScriptedApprovalService(), rig.Memory, new ToolLoopOptions(), TimeProvider.System, NullLogger<ConversationService>.Instance);
        var events = await Collect(rig2Service.RetryAsync(rig.Conversation.Id));

        Assert.IsType<Completed>(events[^1]);
        Assert.Equal(ChatRole.Assistant, (await rig.Messages())[^1].Role);
    }

    [Fact]
    public async Task Regenerate_RemovesWholeLastTurn_BackToLastUserMessage()
    {
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(Use("a")), SequenceChatProvider.Answer("舊答案"), SequenceChatProvider.Answer("新答案"));
        var rig = await Rig.CreateAsync(provider, [new FakeTool()]);
        await Collect(rig.Service.SendAsync(rig.Conversation.Id, "x"));
        Assert.Equal(4, (await rig.Messages()).Count);

        await Collect(rig.Service.RegenerateAsync(rig.Conversation.Id));

        var messages = await rig.Messages();
        Assert.Equal([ChatRole.User, ChatRole.Assistant], messages.Select(m => m.Role));
        Assert.Equal("新答案", Assert.IsType<TextBlock>(messages[1].Content[0]).Text);
    }

    // ---- BuildRequestMessages ----

    private static ChatMessage Assistant(params ContentBlock[] blocks) =>
        new(ChatMessage.NewId(), ChatRole.Assistant, blocks, DateTimeOffset.UtcNow);

    private static ChatMessage UserMsg(string text) =>
        new(ChatMessage.NewId(), ChatRole.User, [new TextBlock(text)], DateTimeOffset.UtcNow);

    private static ChatMessage ToolMsg(params string[] ids) =>
        new(ChatMessage.NewId(), ChatRole.Tool,
            ids.Select(id => (ContentBlock)new ToolResultBlock(id, "結果", false, "回聲", null, null, 1, ToolApproval.NotRequired)).ToList(),
            DateTimeOffset.UtcNow);

    [Fact]
    public void BuildRequest_KeepsPairedToolHistory()
    {
        var history = new[] { UserMsg("x"), Assistant(new ToolUseBlock("a", "echo", "{}")), ToolMsg("a"), Assistant(new TextBlock("好")) };

        var result = ConversationService.BuildRequestMessages(history);

        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant], result.Select(m => m.Role));
    }

    [Fact]
    public void BuildRequest_SynthesizesMissingResult()
    {
        var history = new[] { UserMsg("x"), Assistant(new ToolUseBlock("a", "echo", "{}"), new ToolUseBlock("b", "echo", "{}")), ToolMsg("a") };

        var result = ConversationService.BuildRequestMessages(history);

        var results = result[^1].Content.OfType<ToolResultBlock>().ToList();
        Assert.Equal(["a", "b"], results.Select(r => r.ToolUseId));
        Assert.True(results[1].IsError);
    }

    [Fact]
    public void BuildRequest_SynthesizesResultMessage_WhenCrashedBeforeAnyResultSaved()
    {
        var history = new[] { UserMsg("x"), Assistant(new ToolUseBlock("a", "echo", "{}")) };

        var result = ConversationService.BuildRequestMessages(history);

        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool], result.Select(m => m.Role));
    }

    [Fact]
    public void BuildRequest_DropsOrphanResults()
    {
        var history = new[] { UserMsg("x"), ToolMsg("ghost"), Assistant(new TextBlock("好")) };

        var result = ConversationService.BuildRequestMessages(history);

        Assert.Equal([ChatRole.User, ChatRole.Assistant], result.Select(m => m.Role));
    }

    [Fact]
    public void BuildRequest_DropsResultWithoutMatchingUse()
    {
        var history = new[] { UserMsg("x"), Assistant(new ToolUseBlock("a", "echo", "{}")), ToolMsg("a", "ghost") };

        var result = ConversationService.BuildRequestMessages(history);

        Assert.Equal(["a"], result[^1].Content.OfType<ToolResultBlock>().Select(r => r.ToolUseId));
    }
}
