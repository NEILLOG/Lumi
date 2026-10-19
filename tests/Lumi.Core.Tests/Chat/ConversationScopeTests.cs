using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests.Chat;

/// <summary>4A：對話迴圈依對話的上下文項目建立範圍；沒有範圍時不送需要範圍的工具。</summary>
public class ConversationScopeTests
{
    private static readonly FakeModelCatalog Catalog = new();

    private static async Task<List<ChatEvent>> Collect(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events)
            list.Add(e);
        return list;
    }

    private static (ConversationService Service, InMemoryConversationStore Store, InMemoryConversationContextStore Context, string Id) Create(
        IChatProvider provider, params ITool[] tools)
    {
        var store = new InMemoryConversationStore();
        var context = new InMemoryConversationContextStore();
        var conversation = store.CreateAsync("c").GetAwaiter().GetResult();
        var service = new ConversationService(provider, store, Catalog, new ToolRegistry(tools), new ScriptedApprovalService(),
            new ToolPermissionMemory(), new ToolLoopOptions(), TimeProvider.System, NullLogger<ConversationService>.Instance,
            context, new PrefixFileScopeFactory());
        return (service, store, context, conversation.Id);
    }

    private static ToolUseBlock Use(string id, string name, string text) => new(id, name, $$"""{"text":"{{text}}"}""");

    [Fact]
    public async Task NoScope_RequestOmitsScopedTools_ButKeepsTheOthers()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));
        var (service, _, _, id) = Create(provider, new FakeTool(), new ScopedTool());

        await Collect(service.SendAsync(id, "嗨"));

        Assert.Equal(["echo"], provider.Requests[0].Tools!.Select(t => t.Descriptor.Name));
    }

    [Fact]
    public async Task NoScope_AndOnlyScopedTools_RequestCarriesNoTools()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));
        var (service, _, _, id) = Create(provider, new ScopedTool());

        await Collect(service.SendAsync(id, "嗨"));

        Assert.Null(provider.Requests[0].Tools);
    }

    [Fact]
    public async Task WithAScope_RequestIncludesScopedTools()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));
        var (service, _, context, id) = Create(provider, new FakeTool(), new ScopedTool());
        await context.AddAsync(id, ContextKind.Folder, "/proj", "proj");

        await Collect(service.SendAsync(id, "嗨"));

        Assert.Equal(["echo", "scoped_read"], provider.Requests[0].Tools!.Select(t => t.Descriptor.Name));
    }

    [Fact]
    public async Task ScopedTool_ReadsInsideTheScope_AndTheModelSeesTheContent()
    {
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(Use("t1", "scoped_read", "/proj/a.txt")), SequenceChatProvider.Answer("讀到了"));
        var tool = new ScopedTool();
        var (service, _, context, id) = Create(provider, tool);
        await context.AddAsync(id, ContextKind.Folder, "/proj", "proj");

        var events = await Collect(service.SendAsync(id, "讀"));

        Assert.Equal(["/proj/a.txt"], tool.Resolved);
        var result = Assert.Single(events.OfType<ToolCallCompleted>()).Result;
        Assert.False(result.IsError);
        Assert.Equal("內容：/proj/a.txt", result.Content);
    }

    [Fact]
    public async Task ScopedTool_OutsideTheScope_ReturnsAnErrorTheModelReceives_AndTheTurnContinues()
    {
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(Use("t1", "scoped_read", "/etc/passwd")), SequenceChatProvider.Answer("好吧，讀不到"));
        var tool = new ScopedTool();
        var (service, _, context, id) = Create(provider, tool);
        await context.AddAsync(id, ContextKind.Folder, "/proj", "proj");

        var events = await Collect(service.SendAsync(id, "讀"));

        Assert.Empty(tool.Resolved);
        Assert.True(Assert.Single(events.OfType<ToolCallCompleted>()).Result.IsError);
        // 第二次請求帶回去的工具結果就是那段說明
        var toolMessage = provider.Requests[1].Messages.Last(m => m.Role == ChatRole.Tool);
        var block = Assert.IsType<ToolResultBlock>(Assert.Single(toolMessage.Content));
        Assert.Contains("不在可存取的範圍內", block.Content);
        Assert.IsType<Completed>(events[^1]);
    }

    [Fact]
    public async Task ScopeIsReevaluatedEveryIteration()
    {
        // 第一輪還沒有範圍；工具執行時使用者（假裝）加入了資料夾；第二輪請求就該帶上需要範圍的工具
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(Use("t1", "echo", "x")), SequenceChatProvider.Answer("好"));
        var store = new InMemoryConversationStore();
        var context = new InMemoryConversationContextStore();
        var conversation = await store.CreateAsync("c");
        var echo = new FakeTool
        {
            Behavior = async (_, _) =>
            {
                await context.AddAsync(conversation.Id, ContextKind.Folder, "/proj", "proj");
                return ToolResult.Ok("ok");
            },
        };
        var service = new ConversationService(provider, store, Catalog, new ToolRegistry([echo, new ScopedTool()]), new ScriptedApprovalService(),
            new ToolPermissionMemory(), new ToolLoopOptions(), TimeProvider.System, NullLogger<ConversationService>.Instance, context, new PrefixFileScopeFactory());

        await Collect(service.SendAsync(conversation.Id, "x"));

        Assert.Equal(["echo"], provider.Requests[0].Tools!.Select(t => t.Descriptor.Name));
        Assert.Equal(["echo", "scoped_read"], provider.Requests[1].Tools!.Select(t => t.Descriptor.Name));
    }

    [Fact]
    public async Task RemovingTheItem_TakesTheScopeAway_ForTheNextRun()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("一"), SequenceChatProvider.Answer("二"));
        var (service, _, context, id) = Create(provider, new ScopedTool());
        var item = await context.AddAsync(id, ContextKind.Folder, "/proj", "proj");

        await Collect(service.SendAsync(id, "第一次"));
        await context.RemoveAsync(id, item.Id);
        await Collect(service.SendAsync(id, "第二次"));

        Assert.NotNull(provider.Requests[0].Tools);
        Assert.Null(provider.Requests[1].Tools);
    }

    [Fact]
    public async Task ToolsDisabledOnTheModel_StillSendsNoToolsEvenWithAScope()
    {
        var catalog = new FakeModelCatalog
        {
            Current = new Lumi.Core.Models.AppSettings(
                [FakeModelCatalog.Connection], [FakeModelCatalog.Model with { ToolsEnabled = false }],
                FakeModelCatalog.Model.Id, FakeModelCatalog.Model.Id, Lumi.Core.Models.ThemePreference.System),
        };
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));
        var store = new InMemoryConversationStore();
        var context = new InMemoryConversationContextStore();
        var conversation = await store.CreateAsync("c");
        await context.AddAsync(conversation.Id, ContextKind.Folder, "/proj", "proj");
        var service = new ConversationService(provider, store, catalog, new ToolRegistry([new ScopedTool()]), new ScriptedApprovalService(),
            new ToolPermissionMemory(), new ToolLoopOptions(), TimeProvider.System, NullLogger<ConversationService>.Instance, context, new PrefixFileScopeFactory());

        await Collect(service.SendAsync(conversation.Id, "嗨"));

        Assert.Null(provider.Requests[0].Tools);
    }

    [Fact]
    public async Task ImagesAreNotPartOfTheScope()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));
        var (service, _, context, id) = Create(provider, new ScopedTool());
        await context.AddAsync(id, ContextKind.Image, "/shot.png", "shot.png");

        await Collect(service.SendAsync(id, "嗨"));

        Assert.Null(provider.Requests[0].Tools);   // 只有圖片：沒有可讀的範圍
    }
}
