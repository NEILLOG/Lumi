using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

/// <summary>4C：上下文列接進工作台（送出、鎖定、重開、預估）。</summary>
public class WorkbenchContextTests
{
    private sealed class Rig
    {
        public InMemoryConversationStore Store { get; } = new();
        public InMemoryConversationContextStore Context { get; } = new();
        public FakeContextInspector Inspector { get; } = new();
        public FakeContextBuilder Builder { get; } = new();
        public FakeModelCatalog Catalog { get; } = new();
        public ContextBarViewModel Bar { get; }
        public WorkbenchViewModel Vm { get; }
        public SequenceChatProvider Provider { get; }

        public Rig(SequenceChatProvider? provider = null, InMemoryConversationStore? store = null, InMemoryConversationContextStore? context = null)
        {
            Store = store ?? Store;
            Context = context ?? Context;
            Provider = provider ?? new SequenceChatProvider(SequenceChatProvider.Answer("好"), SequenceChatProvider.Answer("好"), SequenceChatProvider.Answer("好"));
            var time = new FakeTimeProvider();
            var service = new ConversationService(Provider, Store, Catalog, new ToolRegistry([]), new ToolApprovalService(), new ToolPermissionMemory(),
                new ToolLoopOptions(), time, NullLogger<ConversationService>.Instance, Context, new EmptyFileScopeFactory(), Builder);
            Bar = new ContextBarViewModel(Context, Store, Inspector, Builder, new HeuristicTokenEstimator(), new FakeFilePicker(),
                new FakeClipboardImage(), new FakeAttachmentStore(), time);
            Vm = new WorkbenchViewModel(service, Store, Catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
                new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), time, NullLogger<WorkbenchViewModel>.Instance,
                contextBar: Bar);
        }
    }

    private static Task Wait(Func<bool> condition) => SettingsHost.WaitUntil(condition);

    [Fact]
    public async Task Initialize_LoadsTheLatestConversationsChips_AndTheModelsContextWindow()
    {
        var store = new InMemoryConversationStore();
        var context = new InMemoryConversationContextStore();
        var conversation = await store.CreateAsync("c");
        await context.AddAsync(conversation.Id, ContextKind.File, "/a.txt", "a.txt");
        var rig = new Rig(store: store, context: context);
        rig.Inspector.Know("/a.txt", ContextKind.File);

        await rig.Vm.InitializeAsync();

        Assert.Equal(["a.txt"], rig.Vm.Context.Items.Select(c => c.DisplayName));
        Assert.Equal(FakeModelCatalog.Model.ContextWindow, rig.Vm.Context.ContextWindow);
    }

    [Fact]
    public async Task Send_ShowsThePendingChipsOnTheBubble_AndTurnsThemIntoSentChips()
    {
        var rig = new Rig();
        rig.Inspector.Know("/proj", ContextKind.Folder, new ContextItemStatus(ContextItemProblem.None, null, 7)).Know("/a.txt", ContextKind.File);
        await rig.Vm.InitializeAsync();
        await rig.Bar.AddPathsAsync(["/proj", "/a.txt"]);

        rig.Vm.InputText = "看一下";
        await rig.Vm.SendCommand.ExecuteAsync(null);

        var bubble = rig.Vm.Messages[0];
        Assert.Equal(["proj", "a.txt"], bubble.Attachments.Select(a => a.DisplayName));
        Assert.Equal([ContextKind.Folder, ContextKind.File], bubble.Attachments.Select(a => a.Kind));
        Assert.True(bubble.HasAttachments);
        Assert.All(rig.Vm.Context.Items, c => Assert.True(c.IsSent));   // 送出之後變成「已帶入」
        Assert.Empty(rig.Vm.Context.PendingChips());
    }

    [Fact]
    public async Task SecondSend_HasNoChipsOnItsBubble()
    {
        var rig = new Rig();
        rig.Inspector.Know("/a.txt", ContextKind.File);
        await rig.Vm.InitializeAsync();
        await rig.Bar.AddPathsAsync(["/a.txt"]);
        rig.Vm.InputText = "一";
        await rig.Vm.SendCommand.ExecuteAsync(null);

        rig.Vm.InputText = "二";
        await rig.Vm.SendCommand.ExecuteAsync(null);

        var users = rig.Vm.Messages.Where(m => m.IsUser).ToList();
        Assert.True(users[0].HasAttachments);
        Assert.False(users[1].HasAttachments);
    }

    [Fact]
    public async Task ReopeningTheConversation_RebuildsTheChipsOnTheBubble_FromTheStoredBlocks()
    {
        var rig = new Rig();
        rig.Inspector.Know("/proj", ContextKind.Folder).Know("/a.txt", ContextKind.File);
        await rig.Vm.InitializeAsync();
        await rig.Bar.AddPathsAsync(["/proj", "/a.txt"]);
        rig.Vm.InputText = "看";
        await rig.Vm.SendCommand.ExecuteAsync(null);

        var reopened = new Rig(store: rig.Store, context: rig.Context);
        await reopened.Vm.InitializeAsync();

        var bubble = reopened.Vm.Messages[0];
        Assert.Equal(["proj", "a.txt"], bubble.Attachments.Select(a => a.DisplayName));
        Assert.Equal("7 個項目", bubble.Attachments[0].Detail);   // 資料夾的項目數來自存下來的區塊
        Assert.Null(bubble.Attachments[1].Detail);
        Assert.All(bubble.Attachments, a => Assert.False(a.HasProblem));
    }

    [Fact]
    public async Task NotEmbeddedBlocks_ShowAProblemChipOnTheBubble()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("c");
        await store.AppendMessageAsync(conversation.Id, new ChatMessage("u", ChatRole.User,
            [new ContextBlock(ContextKind.File, "big.log", "/big.log", null, false, "太大"), new ImageBlock("f.png", "image/png", "截圖.png"), new TextBlock("看")],
            DateTimeOffset.UtcNow));
        var rig = new Rig(store: store);

        await rig.Vm.InitializeAsync();

        var chips = rig.Vm.Messages[0].Attachments;
        Assert.Equal(["big.log", "截圖.png"], chips.Select(c => c.DisplayName));
        Assert.True(chips[0].HasProblem);
        Assert.Equal("沒有放入內容", chips[0].Detail);
        Assert.Contains("太大", chips[0].Tooltip);
        Assert.True(chips[1].IsImage);
        Assert.False(chips[1].HasProblem);
    }

    [Fact]
    public async Task WhileResponding_TheBarIsLocked_AndUnlocksAfterwards()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("說到一半")) { HangOnCall = 1 };
        var rig = new Rig(provider);
        rig.Inspector.Know("/a.txt", ContextKind.File);
        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = "問";

        var sending = rig.Vm.SendCommand.ExecuteAsync(null);
        await Wait(() => rig.Vm.IsResponding);

        Assert.True(rig.Bar.IsLocked);
        Assert.False(rig.Bar.AddFileCommand.CanExecute(null));
        Assert.Equal(0, await rig.Bar.AddPathsAsync(["/a.txt"]));

        await rig.Vm.StopAndWaitAsync();
        await sending;

        Assert.False(rig.Bar.IsLocked);
        Assert.True(rig.Bar.AddFileCommand.CanExecute(null));
    }

    [Fact]
    public async Task TypingInTheInputBox_UpdatesTheEstimate()
    {
        var rig = new Rig();
        await rig.Vm.InitializeAsync();
        var before = rig.Bar.EstimatedTokens;

        rig.Vm.InputText = new string('x', 400);

        Assert.Equal(before + 100, rig.Bar.EstimatedTokens);
    }

    [Fact]
    public async Task NewConversation_StartsWithAnEmptyBar()
    {
        var rig = new Rig();
        rig.Inspector.Know("/a.txt", ContextKind.File);
        await rig.Vm.InitializeAsync();
        await rig.Bar.AddPathsAsync(["/a.txt"]);
        rig.Vm.InputText = "問";
        await rig.Vm.SendCommand.ExecuteAsync(null);

        await rig.Vm.NewConversationCommand.ExecuteAsync(null);

        Assert.False(rig.Bar.HasItems);
        Assert.True(rig.Bar.CanChange);
    }

    [Fact]
    public async Task SwitchingBack_ShowsTheOldConversationsChipsAgain()
    {
        var rig = new Rig();
        rig.Inspector.Know("/a.txt", ContextKind.File);
        await rig.Vm.InitializeAsync();
        await rig.Bar.AddPathsAsync(["/a.txt"]);
        rig.Vm.InputText = "問";
        await rig.Vm.SendCommand.ExecuteAsync(null);
        var first = (await rig.Store.GetLatestAsync())!;
        await rig.Vm.NewConversationCommand.ExecuteAsync(null);

        await rig.Vm.OpenConversationAsync(first);

        Assert.Equal(["a.txt"], rig.Bar.Items.Select(c => c.DisplayName));
        Assert.True(rig.Bar.Items[0].IsSent);
    }

    [Fact]
    public async Task ARemovedChip_IsNotSentWithTheNextMessage()
    {
        var rig = new Rig();
        rig.Inspector.Know("/a.txt", ContextKind.File).Know("/b.txt", ContextKind.File);
        await rig.Vm.InitializeAsync();
        await rig.Bar.AddPathsAsync(["/a.txt", "/b.txt"]);
        await rig.Bar.RemoveCommand.ExecuteAsync(rig.Bar.Items[0]);

        rig.Vm.InputText = "問";
        await rig.Vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(["b.txt"], rig.Vm.Messages[0].Attachments.Select(a => a.DisplayName));
    }

    [Fact]
    public async Task Regenerate_DoesNotChangeTheChips()
    {
        var rig = new Rig();
        rig.Inspector.Know("/a.txt", ContextKind.File);
        await rig.Vm.InitializeAsync();
        await rig.Bar.AddPathsAsync(["/a.txt"]);
        rig.Vm.InputText = "問";
        await rig.Vm.SendCommand.ExecuteAsync(null);
        await rig.Bar.AddPathsAsync(["/a.txt"]);   // 再加入：變成待送出
        Assert.False(rig.Bar.Items[0].IsSent);

        await rig.Vm.RegenerateCommand.ExecuteAsync(null);

        Assert.False(rig.Bar.Items[0].IsSent);   // 重新產生不是新的送出，不會消耗待送出的項目
    }
}
