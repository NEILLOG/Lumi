using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests.Chat;

/// <summary>
/// 4A：對話迴圈依對話的上下文項目建立範圍。工具定義一律照送、不因有沒有範圍而增減
/// （2026-10-25：工具清單變了，Anthropic 的 thinking 簽章就失效、請求 400）；沒有範圍時執行會被擋下並說明。
/// </summary>
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
    public async Task NoScope_RequestStillCarriesTheScopedToolDefinitions()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));
        var (service, _, _, id) = Create(provider, new FakeTool(), new ScopedTool());

        await Collect(service.SendAsync(id, "嗨"));

        Assert.Equal(["echo", "scoped_read"], provider.Requests[0].Tools!.Select(t => t.Name));
    }

    [Fact]
    public async Task WriteToolsThatNeedAScope_AreSentWithAndWithoutAScope_SoTheListNeverChanges()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"), SequenceChatProvider.Answer("好"));
        var (service, _, _, id) = Create(provider, new FakeTool(), new ScopedWriteTool());
        await Collect(service.SendAsync(id, "嗨"));

        var (withScope, _, scopeContext, scopedId) = Create(provider, new FakeTool(), new ScopedWriteTool());
        await scopeContext.AddAsync(scopedId, ContextKind.Folder, "/proj", "proj");
        await Collect(withScope.SendAsync(scopedId, "嗨"));

        Assert.Equal(["echo", "scoped_write"], provider.Requests[0].Tools!.Select(t => t.Name));
        Assert.Equal(["echo", "scoped_write"], provider.Requests[1].Tools!.Select(t => t.Name));
    }

    private sealed class ScopedWriteTool : ToolBase<EchoArgs>
    {
        public override ToolDescriptor Descriptor { get; } = new(
            "scoped_write", "寫範圍內的檔案", "測試用：寫入類、需要範圍。", ToolPermission.Write, ToolModes.Workbench, TimeSpan.FromSeconds(5), RequiresScope: true);

        protected override Task<ToolResult> InvokeAsync(EchoArgs args, ToolContext context, CancellationToken ct) =>
            Task.FromResult(ToolResult.Ok("寫入完成"));
    }

    [Fact]
    public async Task NoScope_CallingAScopedTool_IsRefusedWithAnExplanation_AndTheTurnContinues()
    {
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(Use("t1", "scoped_read", "/proj/a.txt")), SequenceChatProvider.Answer("沒有可讀的檔案"));
        var tool = new ScopedTool();
        var (service, _, _, id) = Create(provider, tool);

        var events = await Collect(service.SendAsync(id, "讀"));

        Assert.Empty(tool.Resolved);
        var result = Assert.Single(events.OfType<ToolCallCompleted>()).Result;
        Assert.True(result.IsError);
        Assert.Contains("目前沒有加入任何檔案或資料夾", result.Content);
        Assert.IsType<Completed>(events[^1]);
    }

    [Fact]
    public async Task WithAScope_RequestIncludesScopedTools()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));
        var (service, _, context, id) = Create(provider, new FakeTool(), new ScopedTool());
        await context.AddAsync(id, ContextKind.Folder, "/proj", "proj");

        await Collect(service.SendAsync(id, "嗨"));

        Assert.Equal(["echo", "scoped_read"], provider.Requests[0].Tools!.Select(t => t.Name));
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
    public async Task ScopeIsReevaluatedEveryIteration_ButTheToolListStaysTheSame()
    {
        // 第一輪還沒有範圍；工具執行時使用者（假裝）加入了資料夾；第二輪的範圍變了，工具清單不變
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

        Assert.Equal(["echo", "scoped_read"], provider.Requests[0].Tools!.Select(t => t.Name));
        Assert.Equal(["echo", "scoped_read"], provider.Requests[1].Tools!.Select(t => t.Name));
    }

    [Fact]
    public async Task RemovingTheItem_TakesTheScopeAway_ButNotTheToolDefinitions()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("一"), SequenceChatProvider.Answer("二"));
        var (service, _, context, id) = Create(provider, new ScopedTool());
        var item = await context.AddAsync(id, ContextKind.Folder, "/proj", "proj");

        await Collect(service.SendAsync(id, "第一次"));
        await context.RemoveAsync(id, item.Id);
        await Collect(service.SendAsync(id, "第二次"));

        Assert.Equal(["scoped_read"], provider.Requests[0].Tools!.Select(t => t.Name));
        Assert.Equal(["scoped_read"], provider.Requests[1].Tools!.Select(t => t.Name));
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
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(Use("t1", "scoped_read", "/shot.png")), SequenceChatProvider.Answer("好"));
        var tool = new ScopedTool();
        var (service, _, context, id) = Create(provider, tool);
        await context.AddAsync(id, ContextKind.Image, "/shot.png", "shot.png");

        var events = await Collect(service.SendAsync(id, "嗨"));

        Assert.Empty(tool.Resolved);   // 只有圖片：沒有可讀的範圍
        Assert.True(Assert.Single(events.OfType<ToolCallCompleted>()).Result.IsError);
    }
}
