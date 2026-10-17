using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

public class WorkbenchViewModelTests
{
    private static readonly ChatDefaults Defaults = new("claude-opus-5-5", 1024, 1_000_000);

    private static (WorkbenchViewModel Vm, InMemoryConversationStore Store) Create(
        IChatProvider provider, IApiKeyPrompt? prompt = null, InMemoryConversationStore? store = null)
    {
        store ??= new InMemoryConversationStore();
        var service = new ConversationService(provider, store, Defaults);
        var vm = new WorkbenchViewModel(service, store, Defaults, prompt ?? new FakeApiKeyPrompt(false),
            NullLogger<WorkbenchViewModel>.Instance);
        return (vm, store);
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
        Assert.Equal("1.5K / 1M", vm.ContextText);
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
        Assert.False(vm.NewConversationCommand.CanExecute(null));
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
