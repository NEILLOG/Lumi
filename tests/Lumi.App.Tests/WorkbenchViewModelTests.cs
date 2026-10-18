using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

public class WorkbenchViewModelTests
{

    private sealed record Rig(WorkbenchViewModel Vm, InMemoryConversationStore Store, FakeModelCatalog Catalog,
        ConnectionStatusCache Status, FakeClipboardService Clipboard, FakeTimeProvider Time);

    private static Rig CreateRig(
        IChatProvider provider, IApiKeyPrompt? prompt = null, InMemoryConversationStore? store = null, FakeModelCatalog? catalog = null,
        ISettingsWindowService? settingsWindow = null)
    {
        store ??= new InMemoryConversationStore();
        catalog ??= new FakeModelCatalog();
        var status = new ConnectionStatusCache();
        var clipboard = new FakeClipboardService();
        var time = new FakeTimeProvider();
        var service = new ConversationService(provider, store, catalog);
        var vm = new WorkbenchViewModel(service, store, catalog, prompt ?? new FakeApiKeyPrompt(false),
            settingsWindow ?? new FakeSettingsWindowService(), status, clipboard, new SyncUiDispatcher(), time,
            NullLogger<WorkbenchViewModel>.Instance);
        return new Rig(vm, store, catalog, status, clipboard, time);
    }

    private static (WorkbenchViewModel Vm, InMemoryConversationStore Store) Create(
        IChatProvider provider, IApiKeyPrompt? prompt = null, InMemoryConversationStore? store = null, FakeModelCatalog? catalog = null,
        ISettingsWindowService? settingsWindow = null)
    {
        var rig = CreateRig(provider, prompt, store, catalog, settingsWindow);
        return (rig.Vm, rig.Store);
    }

    private static Completed Reply(string text, TokenUsage? usage = null) => new(
        new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [new ThinkingBlock("想", "sig"), new TextBlock(text)],
            DateTimeOffset.UtcNow, ModelId: "claude-opus-5-5", Usage: usage ?? new TokenUsage(1200, 300, 0, 0)),
        StopReason.EndTurn);

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task Initialize_WithEmptyStore_CreatesConversation()
    {
        var (vm, store) = Create(new ScriptedChatProvider());

        await vm.InitializeAsync();

        Assert.NotNull(await store.GetLatestAsync());
        Assert.Empty(vm.Messages);
        Assert.Equal(Strings.Workbench_NewConversationTitle, vm.Title);
    }

    [Fact]
    public async Task Initialize_LoadsLatestConversation_WithStoppedFlagAndThinking()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("舊對話");
        await store.AppendMessageAsync(conversation.Id,
            new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("問")], DateTimeOffset.UtcNow));
        await store.AppendMessageAsync(conversation.Id,
            new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [new ThinkingBlock("思考", "s"), new TextBlock("答到一半")],
                DateTimeOffset.UtcNow, MessageStatus.Stopped, Usage: new TokenUsage(10, 5, 0, 0)));
        var (vm, _) = Create(new ScriptedChatProvider(), store: store);

        await vm.InitializeAsync();

        Assert.Equal("舊對話", vm.Title);
        Assert.Equal(2, vm.Messages.Count);
        Assert.True(vm.Messages[0].IsUser);
        Assert.True(vm.Messages[1].IsStopped);
        Assert.Equal("思考", vm.Messages[1].Thinking);
        Assert.Equal(1, vm.Turns);
        Assert.Equal(15, vm.ContextUsed);
    }

    [Fact]
    public async Task Send_ClearsInput_StreamsText_AndCompletes()
    {
        var provider = new ScriptedChatProvider(new TextDelta("你"), new TextDelta("好"), Reply("你好"));
        var (vm, store) = Create(provider);
        await vm.InitializeAsync();
        vm.InputText = "  嗨  ";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal("", vm.InputText);
        Assert.Equal(2, vm.Messages.Count);
        Assert.Equal("嗨", vm.Messages[0].Text);
        Assert.Equal("你好", vm.Messages[1].Text);
        Assert.Equal("想", vm.Messages[1].Thinking);
        Assert.False(vm.Messages[1].IsStreaming);
        Assert.False(vm.IsResponding);
        Assert.Equal(1, vm.Turns);
        Assert.Equal(1500, vm.ContextUsed);
        Assert.Equal("1.5K / 200K", vm.ContextText);
        Assert.Equal("嗨", vm.Title);
        Assert.Equal("嗨", (await store.GetLatestAsync())!.Title);
        Assert.Equal(2, (await store.GetMessagesAsync((await store.GetLatestAsync())!.Id)).Count);
    }

    [Fact]
    public async Task Send_IsDisabledForBlankInput()
    {
        var (vm, _) = Create(new ScriptedChatProvider());
        await vm.InitializeAsync();

        vm.InputText = "   ";

        Assert.False(vm.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task WhileResponding_SendDisabled_StopEnabled_StopMarksStopped()
    {
        var provider = new ScriptedChatProvider(new TextDelta("說到一半")) { HangAfter = 1 };
        var (vm, _) = Create(provider);
        await vm.InitializeAsync();
        vm.InputText = "問";

        var sending = vm.SendCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Messages.Count == 2 && vm.Messages[1].HasText);

        Assert.True(vm.IsResponding);
        vm.InputText = "回應中還能打草稿";
        Assert.False(vm.SendCommand.CanExecute(null));
        Assert.True(vm.NewConversationCommand.CanExecute(null));   // 回應中可以按：會先停止再開新對話
        Assert.True(vm.ShowSwitchBlockedHint);
        Assert.False(vm.CanSwitchModel);
        Assert.True(vm.StopCommand.CanExecute(null));

        vm.StopCommand.Execute(null);
        await sending;

        Assert.False(vm.IsResponding);
        Assert.True(vm.Messages[1].IsStopped);
        Assert.Equal("回應中還能打草稿", vm.InputText);
        Assert.True(vm.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task Stop_BeforeAnyText_RemovesEmptyAssistantMessage()
    {
        var provider = new ScriptedChatProvider() { HangAfter = 0 };
        var (vm, _) = Create(provider);
        await vm.InitializeAsync();
        vm.InputText = "問";

        var sending = vm.SendCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.IsResponding && vm.Messages.Count == 2);
        vm.StopCommand.Execute(null);
        await sending;

        Assert.Single(vm.Messages);
    }

    [Fact]
    public async Task Failure_ShowsErrorRow_AndRetrySucceeds()
    {
        var failing = new ScriptedChatProvider(new Failed(new ChatFailure(ChatFailureKind.Overloaded, "服務暫時過載", true)));
        var switchable = new SwitchableChatProvider(failing);
        var (vm, _) = Create(switchable);
        await vm.InitializeAsync();
        vm.InputText = "問";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal("服務暫時過載", vm.ErrorMessage);
        Assert.True(vm.HasError);
        Assert.Single(vm.Messages);   // 失敗的回應不顯示
        Assert.True(vm.RetryCommand.CanExecute(null));

        switchable.Current = new ScriptedChatProvider(Reply("這次成功"));
        await vm.RetryCommand.ExecuteAsync(null);

        Assert.False(vm.HasError);
        Assert.Equal("這次成功", vm.Messages[^1].Text);
        Assert.Equal(2, vm.Messages.Count);
    }

    [Fact]
    public async Task MissingKey_PromptSaved_RetriesAutomatically()
    {
        var switchable = new SwitchableChatProvider(
            new ScriptedChatProvider(new Failed(new ChatFailure(ChatFailureKind.MissingApiKey, "沒有金鑰", false))));
        var prompt = new FakeApiKeyPrompt(true) { OnSaved = () => switchable.Current = new ScriptedChatProvider(Reply("存好了")) };
        var (vm, _) = Create(switchable, prompt);
        await vm.InitializeAsync();
        vm.InputText = "問";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal([ApiKeyPromptReason.Missing], prompt.Calls);
        Assert.False(vm.HasError);
        Assert.Equal("存好了", vm.Messages[^1].Text);
    }

    [Theory]
    [InlineData(ChatFailureKind.InvalidApiKey, ApiKeyPromptReason.Invalid)]
    [InlineData(ChatFailureKind.SecretUnreadable, ApiKeyPromptReason.Unreadable)]
    public async Task KeyProblems_PromptCancelled_LeavesErrorRow(ChatFailureKind kind, ApiKeyPromptReason expectedReason)
    {
        var provider = new ScriptedChatProvider(new Failed(new ChatFailure(kind, "金鑰有問題", false)));
        var prompt = new FakeApiKeyPrompt(false);
        var (vm, _) = Create(provider, prompt);
        await vm.InitializeAsync();
        vm.InputText = "問";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal([expectedReason], prompt.Calls);
        Assert.Equal("金鑰有問題", vm.ErrorMessage);
    }

    [Fact]
    public async Task OtherFailures_DoNotPrompt()
    {
        var prompt = new FakeApiKeyPrompt(true);
        var (vm, _) = Create(new ScriptedChatProvider(new Failed(new ChatFailure(ChatFailureKind.RateLimited, "太多請求", true))), prompt);
        await vm.InitializeAsync();
        vm.InputText = "問";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Empty(prompt.Calls);
    }

    [Fact]
    public async Task NewConversation_ClearsMessagesAndStatus()
    {
        var (vm, store) = Create(new ScriptedChatProvider(Reply("答")));
        await vm.InitializeAsync();
        vm.InputText = "問";
        await vm.SendCommand.ExecuteAsync(null);

        await vm.NewConversationCommand.ExecuteAsync(null);

        Assert.Empty(vm.Messages);
        Assert.Equal(0, vm.Turns);
        Assert.Equal(0, vm.ContextUsed);
        Assert.Empty(await store.GetMessagesAsync((await store.GetLatestAsync())!.Id));
    }

    [Fact]
    public void ModelName_AndContextWindow_ComeFromWorkbenchModel_AndFollowChanges()
    {
        var catalog = new FakeModelCatalog();
        var (vm, _) = Create(new ScriptedChatProvider(), catalog: catalog);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal("測試模型", vm.ModelName);
        Assert.Equal(200_000, vm.ContextWindow);

        catalog.Current = catalog.Current with
        {
            Models = [FakeModelCatalog.Model with { DisplayName = "換過的模型", ContextWindow = 1_000_000 }],
        };
        catalog.Raise();

        Assert.Equal("換過的模型", vm.ModelName);
        Assert.Equal(1_000_000, vm.ContextWindow);
        Assert.Contains(nameof(WorkbenchViewModel.ModelName), changed);
    }

    [Fact]
    public void NoModel_ShowsPlaceholderName()
    {
        var (vm, _) = Create(new ScriptedChatProvider(), catalog: new FakeModelCatalog(withModel: false));

        Assert.Equal(Strings.Workbench_NoModel, vm.ModelName);
        Assert.Equal("0", vm.ContextText);
    }

    [Fact]
    public void OpenSettings_ShowsSettingsWindow_WithoutWaitingForItToClose()
    {
        var settings = new FakeSettingsWindowService { WhileOpen = () => new TaskCompletionSource().Task };
        var (vm, _) = Create(new ScriptedChatProvider(), settingsWindow: settings);

        vm.OpenSettingsCommand.Execute(null);

        Assert.Equal(SettingsSection.Connections, Assert.Single(settings.Shown).Section);
        Assert.True(vm.OpenSettingsCommand.CanExecute(null));
    }

    [Fact]
    public void LoadProblem_IsShownOnce_AndCanBeDismissed()
    {
        var catalog = new FakeModelCatalog { LoadProblem = "設定檔損毀" };
        var (vm, _) = Create(new ScriptedChatProvider(), catalog: catalog);

        Assert.Equal("設定檔損毀", vm.Notice);
        vm.DismissNoticeCommand.Execute(null);

        Assert.False(vm.HasNotice);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(950, "950")]
    [InlineData(1_500, "1.5K")]
    [InlineData(14_200, "14K")]
    [InlineData(200_000, "200K")]
    [InlineData(1_000_000, "1M")]
    public void FormatTokens(int tokens, string expected) =>
        Assert.Equal(expected, WorkbenchViewModel.FormatTokens(tokens));
}
