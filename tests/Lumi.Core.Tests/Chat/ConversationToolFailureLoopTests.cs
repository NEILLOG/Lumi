using Lumi.Core.Chat;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests.Chat;

/// <summary>M23 23B（L2）：同一個工具、相同參數連續失敗 3 次就提早停下，結果已存好、可以直接重試。</summary>
public class ConversationToolFailureLoopTests
{
    private static async Task<List<ChatEvent>> Collect(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events)
            list.Add(e);
        return list;
    }

    private static ToolUseBlock Use(string id, string json = """{"text":"嗨"}""") => new(id, "echo", json);

    private static ChatEvent[] Round(params ToolUseBlock[] uses) => SequenceChatProvider.ToolCalls(uses);

    private static (ConversationService Service, InMemoryConversationStore Store, Conversation Conversation) Rig(IChatProvider provider, ITool tool)
    {
        var store = new InMemoryConversationStore();
        var conversation = store.CreateAsync("新對話").GetAwaiter().GetResult();
        var service = new ConversationService(provider, store, new FakeModelCatalog(), new ToolRegistry([tool]),
            new ScriptedApprovalService(), new ToolPermissionMemory(), new ToolLoopOptions(),
            TimeProvider.System, NullLogger<ConversationService>.Instance);
        return (service, store, conversation);
    }

    private static FakeTool Failing(string message = "檔案不存在") =>
        new() { Behavior = (_, _) => Task.FromResult(ToolResult.Error(message)) };

    [Fact]
    public async Task SameToolSameArguments_ThreeFailuresInARow_StopsWithToolLoopLimit()
    {
        var provider = new SequenceChatProvider(
            Round(Use("a")), Round(Use("b")), Round(Use("c")), SequenceChatProvider.Answer("不會到這裡"));
        var (service, store, conversation) = Rig(provider, Failing());

        var events = await Collect(service.SendAsync(conversation.Id, "讀檔"));

        var failed = Assert.IsType<Failed>(events[^1]);
        Assert.Equal(ChatFailureKind.ToolLoopLimit, failed.Failure.Kind);
        Assert.True(failed.Failure.Retryable);
        Assert.Contains("「回聲」連續 3 次失敗", failed.Failure.Message);
        Assert.Contains("檔案不存在", failed.Failure.Message);
        Assert.Equal(3, provider.Requests.Count);
        Assert.Equal(ChatRole.Tool, (await store.GetMessagesAsync(conversation.Id))[^1].Role);   // 結果已存好，可以重試
    }

    [Fact]
    public async Task SameArgumentsWithDifferentWhitespace_CountAsTheSame()
    {
        var provider = new SequenceChatProvider(
            Round(Use("a", """{"text":"嗨"}""")), Round(Use("b", """{ "text" : "嗨" }""")), Round(Use("c", """{"text":"嗨"}""")),
            SequenceChatProvider.Answer("不會到這裡"));
        var (service, _, conversation) = Rig(provider, Failing());

        var events = await Collect(service.SendAsync(conversation.Id, "讀檔"));

        Assert.Equal(ChatFailureKind.ToolLoopLimit, Assert.IsType<Failed>(events[^1]).Failure.Kind);
        Assert.Equal(3, provider.Requests.Count);
    }

    [Fact]
    public async Task DifferentArguments_DoNotStop()
    {
        var provider = new SequenceChatProvider(
            Round(Use("a", """{"text":"一"}""")), Round(Use("b", """{"text":"二"}""")), Round(Use("c", """{"text":"三"}""")),
            SequenceChatProvider.Answer("好"));
        var (service, _, conversation) = Rig(provider, Failing());

        var events = await Collect(service.SendAsync(conversation.Id, "讀檔"));

        Assert.IsType<Completed>(events[^1]);
        Assert.Equal(4, provider.Requests.Count);
    }

    [Fact]
    public async Task SuccessInBetween_ResetsTheCount()
    {
        var outcomes = new Queue<ToolResult>(new[]
        {
            ToolResult.Error("失敗"), ToolResult.Error("失敗"), ToolResult.Ok("成功了"), ToolResult.Error("失敗"), ToolResult.Error("失敗"),
        });
        var tool = new FakeTool { Behavior = (_, _) => Task.FromResult(outcomes.Dequeue()) };
        var provider = new SequenceChatProvider(
            Round(Use("1")), Round(Use("2")), Round(Use("3")), Round(Use("4")), Round(Use("5")), SequenceChatProvider.Answer("好"));
        var (service, _, conversation) = Rig(provider, tool);

        var events = await Collect(service.SendAsync(conversation.Id, "讀檔"));

        Assert.IsType<Completed>(events[^1]);
        Assert.Equal(6, provider.Requests.Count);
    }

    [Fact]
    public async Task RoundsWithTwoCalls_DoNotCount()
    {
        // 一輪有兩個工具呼叫時不算（L2 只看「一輪只有一個呼叫」的失敗）
        var provider = new SequenceChatProvider(
            Round(Use("a"), Use("b")), Round(Use("c"), Use("d")), Round(Use("e"), Use("f")), SequenceChatProvider.Answer("好"));
        var (service, _, conversation) = Rig(provider, Failing());

        var events = await Collect(service.SendAsync(conversation.Id, "讀檔"));

        Assert.IsType<Completed>(events[^1]);
        Assert.Equal(4, provider.Requests.Count);
    }

    [Fact]
    public async Task ThreeFailures_ThenRetry_ContinuesFromTheStoredHistory()
    {
        // 停下後使用者按「重試」：沒有新的失敗計數，而且最後的錯誤結果還在歷史裡，模型可以接著處理
        var provider = new SequenceChatProvider(
            Round(Use("a")), Round(Use("b")), Round(Use("c")), SequenceChatProvider.Answer("換個方式做了"));
        var (service, store, conversation) = Rig(provider, Failing());
        await Collect(service.SendAsync(conversation.Id, "讀檔"));

        var events = await Collect(service.RetryAsync(conversation.Id));

        Assert.IsType<Completed>(events[^1]);
        Assert.Equal(ChatRole.Assistant, (await store.GetMessagesAsync(conversation.Id))[^1].Role);
    }

    [Fact]
    public async Task UserDenials_AreNotCountedAsToolFailures()
    {
        // Write 權限的工具每次都要確認，ScriptedApprovalService 沒有預設決定就一律拒絕：拒絕 3 次是使用者的選擇，不是工具故障
        var provider = new SequenceChatProvider(
            Round(Use("a")), Round(Use("b")), Round(Use("c")), SequenceChatProvider.Answer("好，我不做了"));
        var (service, _, conversation) = Rig(provider, new FakeTool(permission: ToolPermission.Write));

        var events = await Collect(service.SendAsync(conversation.Id, "寫檔"));

        Assert.IsType<Completed>(events[^1]);
        Assert.Equal(4, provider.Requests.Count);
    }
}
