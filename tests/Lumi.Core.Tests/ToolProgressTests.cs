using Lumi.Core.Chat;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests;

/// <summary>對話迴圈轉發工具進度（M19 D12）。</summary>
public class ToolProgressTests
{
    private static async Task<List<ChatEvent>> Collect(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events)
            list.Add(e);
        return list;
    }

    private static ToolUseBlock Use(string id) => new(id, "echo", """{"text":"嗨"}""");

    /// <summary>行為可以用 <see cref="ToolContext"/> 回報進度的測試工具。</summary>
    private sealed class ProgressTool(Func<ToolContext, CancellationToken, Task<ToolResult>> behavior) : ToolBase<EchoArgs>
    {
        private static readonly ToolDescriptor Desc = new("echo", "回聲", "測試用。", ToolPermission.ReadOnly, ToolModes.Both, TimeSpan.FromSeconds(5));

        public override ToolDescriptor Descriptor => Desc;

        protected override Task<ToolResult> InvokeAsync(EchoArgs args, ToolContext context, CancellationToken ct) =>
            behavior(context, ct);
    }

    private static (ConversationService Service, InMemoryConversationStore Store, SequenceChatProvider Provider) Build(
        SequenceChatProvider provider, ITool tool)
    {
        var store = new InMemoryConversationStore();
        var service = new ConversationService(provider, store, new FakeModelCatalog(), new ToolRegistry([tool]),
            new ScriptedApprovalService(), new ToolPermissionMemory(), new ToolLoopOptions(), TimeProvider.System,
            NullLogger<ConversationService>.Instance);
        return (service, store, provider);
    }

    private static List<string> ProgressTexts(List<ChatEvent> events) =>
        events.OfType<ToolCallProgress>().Select(p => p.Text).ToList();

    [Fact]
    public async Task Progress_is_forwarded_between_started_and_completed_in_order()
    {
        var tool = new ProgressTool(async (context, ct) =>
        {
            context.Progress?.Report("1");
            await Task.Delay(20, ct);
            context.Progress?.Report("2");
            await Task.Delay(100, ct);   // 讓最後一則進度在工具結束前被讀到
            return ToolResult.Ok("完成");
        });
        var (service, store, provider) = Build(
            new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1")), SequenceChatProvider.Answer("好")), tool);
        var conversation = await store.CreateAsync("測試");

        var events = await Collect(service.SendAsync(conversation.Id, "嗨"));

        var order = events
            .Where(e => e is ToolCallStarted or ToolCallProgress or ToolCallCompleted)
            .Select(e => e switch
            {
                ToolCallStarted => "started",
                ToolCallProgress p => "progress:" + p.Text,
                _ => "completed",
            })
            .ToList();
        Assert.Equal(["started", "progress:1", "progress:2", "completed"], order);
        Assert.All(events.OfType<ToolCallProgress>(), p => Assert.Equal("t1", p.CallId));
    }

    [Fact]
    public async Task Blank_progress_is_skipped()
    {
        var tool = new ProgressTool(async (context, ct) =>
        {
            context.Progress?.Report("");
            context.Progress?.Report("   ");
            context.Progress?.Report("x");
            await Task.Delay(100, ct);
            return ToolResult.Ok("完成");
        });
        var (service, store, _) = Build(
            new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1")), SequenceChatProvider.Answer("好")), tool);
        var conversation = await store.CreateAsync("測試");

        var events = await Collect(service.SendAsync(conversation.Id, "嗨"));

        Assert.Equal(["x"], ProgressTexts(events));
    }

    [Fact]
    public async Task Long_progress_is_truncated_to_100_characters()
    {
        var tool = new ProgressTool(async (context, ct) =>
        {
            context.Progress?.Report(new string('字', 150));
            await Task.Delay(100, ct);
            return ToolResult.Ok("完成");
        });
        var (service, store, _) = Build(
            new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1")), SequenceChatProvider.Answer("好")), tool);
        var conversation = await store.CreateAsync("測試");

        var events = await Collect(service.SendAsync(conversation.Id, "嗨"));

        var text = Assert.Single(ProgressTexts(events));
        Assert.Equal(100, text.Length);
    }

    [Fact]
    public async Task Truncation_does_not_split_a_surrogate_pair()
    {
        // 第 100 個字元位置會落在 emoji 的前半：要退回 99 字，不留半個代理對
        var tool = new ProgressTool(async (context, ct) =>
        {
            context.Progress?.Report(new string('a', 99) + "😀");
            await Task.Delay(100, ct);
            return ToolResult.Ok("完成");
        });
        var (service, store, _) = Build(
            new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1")), SequenceChatProvider.Answer("好")), tool);
        var conversation = await store.CreateAsync("測試");

        var events = await Collect(service.SendAsync(conversation.Id, "嗨"));

        Assert.Equal(new string('a', 99), Assert.Single(ProgressTexts(events)));
    }

    [Fact]
    public async Task Tool_that_never_reports_produces_no_progress_events()
    {
        var (service, store, _) = Build(
            new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1")), SequenceChatProvider.Answer("好")), new FakeTool());
        var conversation = await store.CreateAsync("測試");

        var events = await Collect(service.SendAsync(conversation.Id, "嗨"));

        Assert.Empty(events.OfType<ToolCallProgress>());
        Assert.Single(events.OfType<ToolCallCompleted>());
    }

    [Fact]
    public async Task Tool_exception_still_returns_generic_failure_after_progress()
    {
        var tool = new ProgressTool((context, _) =>
        {
            context.Progress?.Report("做到一半");
            throw new InvalidOperationException("內部細節不該給模型看");
        });
        var (service, store, _) = Build(
            new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1")), SequenceChatProvider.Answer("好")), tool);
        var conversation = await store.CreateAsync("測試");

        var events = await Collect(service.SendAsync(conversation.Id, "嗨"));

        var done = Assert.Single(events.OfType<ToolCallCompleted>());
        Assert.True(done.Result.IsError);
        Assert.Equal("工具執行失敗。", done.Result.Content);
    }

    [Fact]
    public async Task Cancel_while_tool_reports_progress_stops_as_before()
    {
        var tool = new ProgressTool(async (context, ct) =>
        {
            context.Progress?.Report("進行中");
            await Task.Delay(Timeout.Infinite, ct);
            return ToolResult.Ok("never");
        });
        var (service, store, _) = Build(
            new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1"))), tool);
        var conversation = await store.CreateAsync("測試");
        using var cts = new CancellationTokenSource();

        var events = new List<ChatEvent>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var e in service.SendAsync(conversation.Id, "嗨", cts.Token))
            {
                events.Add(e);
                if (e is ToolCallProgress)
                    cts.Cancel();   // 進度已經送出、工具還在執行中時按停止
            }
        });

        Assert.Equal(["進行中"], ProgressTexts(events));
        var result = Assert.Single((await store.GetMessagesAsync(conversation.Id))[^1].Content.OfType<ToolResultBlock>());
        Assert.True(result.IsError);
        Assert.Contains("停止", result.Content);
    }

    [Fact]
    public async Task Progress_is_not_stored_or_sent_back_to_the_model()
    {
        var tool = new ProgressTool(async (context, ct) =>
        {
            context.Progress?.Report("不該存進去");
            await Task.Delay(100, ct);
            return ToolResult.Ok("結果");
        });
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1")), SequenceChatProvider.Answer("好"));
        var (service, store, _) = Build(provider, tool);
        var conversation = await store.CreateAsync("測試");

        var events = await Collect(service.SendAsync(conversation.Id, "嗨"));

        Assert.Single(ProgressTexts(events));   // 畫面有收到
        var stored = await store.GetMessagesAsync(conversation.Id);
        var storedText = string.Concat(stored.SelectMany(m => m.Content).Select(b => b switch
        {
            TextBlock t => t.Text,
            ToolResultBlock r => r.Content,
            _ => "",
        }));
        Assert.DoesNotContain("不該存進去", storedText);
        Assert.Equal("結果", stored.SelectMany(m => m.Content).OfType<ToolResultBlock>().Single().Content);

        var sent = string.Concat(provider.Requests[^1].Messages.SelectMany(m => m.Content).Select(b => b switch
        {
            TextBlock t => t.Text,
            ToolResultBlock r => r.Content,
            _ => "",
        }));
        Assert.DoesNotContain("不該存進去", sent);
    }
}
