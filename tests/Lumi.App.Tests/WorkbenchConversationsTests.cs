using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

/// <summary>2D：歷史對話、新對話、停止並等待、重新產生、複製、連線狀態、金鑰視窗期間切換對話。</summary>
public class WorkbenchConversationsTests
{
    private sealed record Rig(WorkbenchViewModel Vm, InMemoryConversationStore Store, FakeModelCatalog Catalog,
        ConnectionStatusCache Status, FakeClipboardService Clipboard, FakeTimeProvider Time);

    private static Rig CreateRig(IChatProvider provider, IApiKeyPrompt? prompt = null, InMemoryConversationStore? store = null,
        FakeModelCatalog? catalog = null)
    {
        store ??= new InMemoryConversationStore();
        catalog ??= new FakeModelCatalog();
        var status = new ConnectionStatusCache();
        var clipboard = new FakeClipboardService();
        var time = new FakeTimeProvider();
        var vm = new WorkbenchViewModel(new ConversationService(provider, store, catalog), store, catalog,
            prompt ?? new FakeApiKeyPrompt(false), new FakeSettingsWindowService(), status, clipboard, new SyncUiDispatcher(), time,
            NullLogger<WorkbenchViewModel>.Instance);
        return new Rig(vm, store, catalog, status, clipboard, time);
    }

    private static Completed Reply(string text, TokenUsage? usage = null) => new(
        new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [new ThinkingBlock("想", "sig"), new TextBlock(text)],
            DateTimeOffset.UtcNow, ModelId: "m", Usage: usage ?? new TokenUsage(1200, 300, 0, 0)),
        StopReason.EndTurn);

    private static async Task Wait(Func<bool> condition) => await SettingsHost.WaitUntil(condition);

    private static async Task<Conversation> SeedAsync(InMemoryConversationStore store, string title, params (ChatRole Role, string Text)[] messages)
    {
        var conversation = await store.CreateAsync(title);
        foreach (var (role, text) in messages)
            await store.AppendMessageAsync(conversation.Id,
                new ChatMessage(ChatMessage.NewId(), role, [new TextBlock(text)], DateTimeOffset.UtcNow, Usage: role == ChatRole.Assistant ? new TokenUsage(10, 5, 0, 0) : null));
        return conversation;
    }

    // ---- 新對話 ----

    [Fact]
    public async Task NewConversation_WhenCurrentIsEmpty_DoesNotCreateAnother()
    {
        var rig = CreateRig(new ScriptedChatProvider());
        await rig.Vm.InitializeAsync();
        var first = (await rig.Store.GetLatestAsync())!.Id;

        await rig.Vm.NewConversationCommand.ExecuteAsync(null);
        await rig.Vm.NewConversationCommand.ExecuteAsync(null);

        Assert.Equal(first, (await rig.Store.GetLatestAsync())!.Id);
    }

    [Fact]
    public async Task NewConversation_AfterMessages_CreatesBlankOne_AndHistoryListsTheOld()
    {
        var rig = CreateRig(new ScriptedChatProvider(Reply("答")));
        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = "問";
        await rig.Vm.SendCommand.ExecuteAsync(null);

        await rig.Vm.NewConversationCommand.ExecuteAsync(null);

        Assert.Empty(rig.Vm.Messages);
        Assert.Equal(Strings.Workbench_NewConversationTitle, rig.Vm.Title);
        var item = Assert.Single(rig.Vm.History.Items);   // 空白的新對話不列出
        Assert.Equal("問", item.Title);
        Assert.False(item.IsCurrent);
    }

    [Fact]
    public async Task NewConversation_WhileResponding_StopsFirst_ThenOpensBlankConversation()
    {
        var provider = new ScriptedChatProvider(new TextDelta("說到一半")) { HangAfter = 1 };
        var rig = CreateRig(provider);
        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = "問";
        var sending = rig.Vm.SendCommand.ExecuteAsync(null);
        await Wait(() => rig.Vm.Messages.Count == 2 && rig.Vm.Messages[1].HasText);
        var oldId = (await rig.Store.GetLatestAsync())!.Id;

        await rig.Vm.NewConversationCommand.ExecuteAsync(null);
        await sending;

        Assert.False(rig.Vm.IsResponding);
        Assert.Empty(rig.Vm.Messages);
        var saved = await rig.Store.GetMessagesAsync(oldId);
        Assert.Equal(MessageStatus.Stopped, saved[^1].Status);   // 已收到的文字存成「已停止」
    }

    // ---- 歷史對話 ----

    [Fact]
    public async Task History_ListsConversations_NewestFirst_AndMarksCurrent_WithTimeText()
    {
        var store = new InMemoryConversationStore();
        var older = await SeedAsync(store, "舊的", (ChatRole.User, "a"));
        await Task.Delay(5);
        var newer = await SeedAsync(store, "新的", (ChatRole.User, "b"));
        var rig = CreateRig(new ScriptedChatProvider(), store: store);

        await rig.Vm.InitializeAsync();
        await rig.Vm.History.RefreshAsync(newer.Id);

        Assert.Equal(["新的", "舊的"], rig.Vm.History.Items.Select(i => i.Title));
        Assert.True(rig.Vm.History.Items[0].IsCurrent);
        Assert.Equal(rig.Vm.History.Items[0], rig.Vm.History.SelectedItem);
        Assert.Equal("12:00", rig.Vm.History.FormatTime(rig.Time.GetUtcNow()));                   // 今天：HH:mm
        Assert.Equal("10/18", rig.Vm.History.FormatTime(rig.Time.GetUtcNow().AddDays(-1)));       // 其他：M/d
        _ = older;
    }

    [Fact]
    public async Task History_ClickingAnotherConversation_SwitchesToIt()
    {
        var store = new InMemoryConversationStore();
        var old = await SeedAsync(store, "舊對話", (ChatRole.User, "舊問題"), (ChatRole.Assistant, "舊答案"));
        await Task.Delay(5);
        await SeedAsync(store, "新對話", (ChatRole.User, "新問題"));
        var rig = CreateRig(new ScriptedChatProvider(), store: store);
        await rig.Vm.InitializeAsync();
        await rig.Vm.History.RefreshAsync(null);

        rig.Vm.History.SelectedItem = rig.Vm.History.Items.Single(i => i.Id == old.Id);
        await Wait(() => rig.Vm.Title == "舊對話");

        Assert.Equal(["舊問題", "舊答案"], rig.Vm.Messages.Select(m => m.Text));
        Assert.Equal(1, rig.Vm.Turns);
        Assert.True(rig.Vm.History.Items.Single(i => i.Id == old.Id).IsCurrent);
    }

    [Fact]
    public async Task SwitchingConversationWhileResponding_SavesStopped_AndOldResponseNeverLeaksIntoNewOne()
    {
        var store = new InMemoryConversationStore();
        var other = await SeedAsync(store, "別的對話", (ChatRole.User, "別的問題"), (ChatRole.Assistant, "別的答案"));
        await Task.Delay(5);
        var current = await SeedAsync(store, "目前", (ChatRole.User, "目前的問題"));
        var provider = new ScriptedChatProvider(new TextDelta("這是舊對話的回應")) { HangAfter = 1 };
        var rig = CreateRig(provider, store: store);
        await rig.Vm.InitializeAsync();
        Assert.Equal(current.Id, (await store.GetLatestAsync())!.Id);

        var retrying = rig.Vm.RegenerateOrRetryForTest();
        await Wait(() => rig.Vm.Messages.Any(m => m.HasText && m.IsAssistant));

        await rig.Vm.OpenConversationAsync(other);
        await retrying;

        Assert.Equal(["別的問題", "別的答案"], rig.Vm.Messages.Select(m => m.Text));   // 沒有舊回應混進來
        Assert.False(rig.Vm.IsResponding);
        var saved = await store.GetMessagesAsync(current.Id);
        Assert.Equal(MessageStatus.Stopped, saved[^1].Status);
        Assert.Equal("這是舊對話的回應", Assert.IsType<TextBlock>(saved[^1].Content[0]).Text);
    }

    [Fact]
    public async Task StopAndWait_WhenNothingIsRunning_ReturnsImmediately()
    {
        var rig = CreateRig(new ScriptedChatProvider());
        await rig.Vm.InitializeAsync();

        await rig.Vm.StopAndWaitAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    // ---- 金鑰視窗期間切換對話 ----

    [Fact]
    public async Task KeyPromptStaysOpen_UserSwitchesConversation_NoRetryHitsTheOtherConversation()
    {
        var store = new InMemoryConversationStore();
        var other = await SeedAsync(store, "別的對話", (ChatRole.User, "別的問題"), (ChatRole.Assistant, "別的答案"));
        await Task.Delay(5);
        await SeedAsync(store, "目前", (ChatRole.User, "目前的問題"));
        var provider = new ScriptedChatProvider(new Failed(new ChatFailure(ChatFailureKind.MissingApiKey, "沒有金鑰", false)));
        var promptGate = new TaskCompletionSource<bool>();
        var prompt = new GatedApiKeyPrompt(promptGate.Task);
        var rig = CreateRig(provider, prompt, store);
        await rig.Vm.InitializeAsync();

        var retrying = rig.Vm.RegenerateOrRetryForTest();
        await Wait(() => prompt.IsWaiting);

        // 設定視窗開著的期間，使用者切到別的對話；StopAndWait 不能被金鑰視窗卡住
        await rig.Vm.OpenConversationAsync(other).WaitAsync(TimeSpan.FromSeconds(2));
        promptGate.SetResult(true);   // 使用者存好金鑰、關掉視窗
        await retrying;

        Assert.Single(provider.Requests);                                              // 沒有重送
        Assert.Equal(["別的問題", "別的答案"], rig.Vm.Messages.Select(m => m.Text));
        Assert.False(rig.Vm.HasError);
    }

    // ---- 重新產生 ----

    [Fact]
    public async Task Regenerate_OnlyOnLastAssistantMessage_AndReplacesIt()
    {
        var store = new InMemoryConversationStore();
        await SeedAsync(store, "對話", (ChatRole.User, "問一"), (ChatRole.Assistant, "答一"), (ChatRole.User, "問二"), (ChatRole.Assistant, "答二"));
        var rig = CreateRig(new ScriptedChatProvider(Reply("新的答二")), store: store);
        await rig.Vm.InitializeAsync();

        Assert.Equal([false, false, false, true], rig.Vm.Messages.Select(m => m.IsLastAssistant));
        Assert.True(rig.Vm.RegenerateCommand.CanExecute(null));

        await rig.Vm.RegenerateCommand.ExecuteAsync(null);

        Assert.Equal(["問一", "答一", "問二", "新的答二"], rig.Vm.Messages.Select(m => m.Text));
        var saved = await store.GetMessagesAsync((await store.GetLatestAsync())!.Id);
        Assert.Equal(4, saved.Count);
        Assert.Equal(2, rig.Vm.Turns);
        Assert.True(rig.Vm.Messages[^1].IsLastAssistant);
    }

    [Fact]
    public async Task Regenerate_NotAvailable_WhenLastIsUserMessage_OrWhileResponding()
    {
        var store = new InMemoryConversationStore();
        await SeedAsync(store, "對話", (ChatRole.User, "只有問題"));
        var rig = CreateRig(new ScriptedChatProvider(), store: store);
        await rig.Vm.InitializeAsync();

        Assert.False(rig.Vm.RegenerateCommand.CanExecute(null));
    }

    [Fact]
    public async Task Regenerate_WhenItFails_ScreenMatchesDatabase_AndErrorRowAppears()
    {
        var store = new InMemoryConversationStore();
        await SeedAsync(store, "對話", (ChatRole.User, "問"), (ChatRole.Assistant, "舊答案"));
        var rig = CreateRig(new ScriptedChatProvider(new Failed(new ChatFailure(ChatFailureKind.Overloaded, "服務過載", true))), store: store);
        await rig.Vm.InitializeAsync();

        await rig.Vm.RegenerateCommand.ExecuteAsync(null);

        // 資料庫裡舊答案已經被移除，畫面跟著只剩問題；錯誤列的「重試」可用
        Assert.Equal(["問"], rig.Vm.Messages.Select(m => m.Text));
        Assert.Equal("服務過載", rig.Vm.ErrorMessage);
        Assert.True(rig.Vm.RetryCommand.CanExecute(null));
    }

    [Fact]
    public async Task Regenerate_WithoutModel_KeepsOldAnswerOnScreenAndInDatabase()
    {
        var store = new InMemoryConversationStore();
        await SeedAsync(store, "對話", (ChatRole.User, "問"), (ChatRole.Assistant, "舊答案"));
        var catalog = new FakeModelCatalog(withModel: false);
        var rig = CreateRig(new ScriptedChatProvider(), store: store, catalog: catalog);
        await rig.Vm.InitializeAsync();

        await rig.Vm.RegenerateCommand.ExecuteAsync(null);

        Assert.Equal(["問", "舊答案"], rig.Vm.Messages.Select(m => m.Text));
        Assert.NotNull(rig.Vm.ErrorMessage);
    }

    // ---- 複製、token、思考秒數 ----

    [Fact]
    public async Task Copy_PutsOnlyTheAnswerTextOnClipboard_AndFlagsCopiedBriefly()
    {
        var rig = CreateRig(new ScriptedChatProvider(Reply("答案正文")));
        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = "問";
        await rig.Vm.SendCommand.ExecuteAsync(null);
        var answer = rig.Vm.Messages[^1];

        await rig.Vm.CopyCommand.ExecuteAsync(answer);

        Assert.Equal(["答案正文"], rig.Clipboard.Texts);   // 不含思考過程
        Assert.True(answer.IsCopied);
    }

    [Fact]
    public async Task Copy_OfEmptyMessage_DoesNothing()
    {
        var rig = CreateRig(new ScriptedChatProvider());
        await rig.Vm.InitializeAsync();

        await rig.Vm.CopyCommand.ExecuteAsync(null);

        Assert.Empty(rig.Clipboard.Texts);
    }

    [Fact]
    public async Task UsageText_ShowsInputIncludingCache_AndOutput_AndHidesWhenMissing()
    {
        var withUsage = CreateRig(new ScriptedChatProvider(Reply("答", new TokenUsage(1000, 350, 100, 100))));
        await withUsage.Vm.InitializeAsync();
        withUsage.Vm.InputText = "問";
        await withUsage.Vm.SendCommand.ExecuteAsync(null);

        var message = withUsage.Vm.Messages[^1];
        Assert.True(message.HasUsage);
        Assert.Equal("↑ 輸入 1.2K · ↓ 輸出 350 tokens", message.UsageText);
        Assert.True(message.ShowActions);

        var noUsage = new Completed(new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock("答")], DateTimeOffset.UtcNow), StopReason.EndTurn);
        var without = CreateRig(new ScriptedChatProvider(noUsage));
        await without.Vm.InitializeAsync();
        without.Vm.InputText = "問";
        await without.Vm.SendCommand.ExecuteAsync(null);
        Assert.False(without.Vm.Messages[^1].HasUsage);
    }

    [Fact]
    public async Task ThinkingSeconds_MeasuredWhileStreaming_ButNotAfterReload()
    {
        var time = new FakeTimeProvider();
        var message = MessageViewModel.StreamingAssistant();

        message.AppendThinking("想", time.GetUtcNow());
        time.Advance(TimeSpan.FromSeconds(3.2));
        message.AppendText("答", time.GetUtcNow());

        Assert.Equal(4, message.ThinkingSeconds);                       // 進位成整秒
        Assert.Equal("思考過程 · 4 秒", message.ThinkingTitle);

        var reloaded = MessageViewModel.From(new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant,
            [new ThinkingBlock("想", "s"), new TextBlock("答")], DateTimeOffset.UtcNow));
        Assert.Null(reloaded.ThinkingSeconds);
        Assert.Equal(Strings.Workbench_Thinking, reloaded.ThinkingTitle);   // 重開後只有「思考過程」
        await Task.CompletedTask;
    }

    [Fact]
    public void ThinkingSeconds_IsAtLeastOne_AndCompleteEndsItToo()
    {
        var time = new FakeTimeProvider();
        var message = MessageViewModel.StreamingAssistant();
        message.AppendThinking("想", time.GetUtcNow());

        message.Complete(new ChatMessage("id", ChatRole.Assistant, [new TextBlock("答")], DateTimeOffset.UtcNow), time.GetUtcNow());

        Assert.Equal(1, message.ThinkingSeconds);
    }

    // ---- 連線狀態 ----

    [Fact]
    public async Task SuccessfulResponse_MarksConnectionOk()
    {
        var rig = CreateRig(new ScriptedChatProvider(Reply("答")));
        await rig.Vm.InitializeAsync();
        Assert.True(rig.Vm.IsConnectionUnknown);
        rig.Vm.InputText = "問";

        await rig.Vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(ConnectionStatus.Ok, rig.Status.Get(FakeModelCatalog.Connection.Id).Status);
        Assert.True(rig.Vm.IsConnectionOk);
        Assert.Equal(Strings.Workbench_StatusConnected, rig.Vm.ConnectionStatusText);
        Assert.True(rig.Vm.ModelSwitcher.IsOk);
    }

    [Theory]
    [InlineData(ChatFailureKind.Network, true)]
    [InlineData(ChatFailureKind.InvalidApiKey, true)]
    [InlineData(ChatFailureKind.RateLimited, false)]
    [InlineData(ChatFailureKind.Refused, false)]
    public async Task FailedResponse_MarksConnectionFailedOnlyForConnectionProblems(ChatFailureKind kind, bool marksFailed)
    {
        var rig = CreateRig(new ScriptedChatProvider(new Failed(new ChatFailure(kind, "x", false))));
        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = "問";

        await rig.Vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(marksFailed, rig.Vm.IsConnectionFailed);
        Assert.Equal(!marksFailed, rig.Vm.IsConnectionUnknown);
    }

    [Fact]
    public async Task TestConnectionResult_UpdatesStatusBarToo()
    {
        var rig = CreateRig(new ScriptedChatProvider());
        await rig.Vm.InitializeAsync();

        rig.Status.Set(FakeModelCatalog.Connection.Id, ConnectionTestResult.Ok(["a"]));

        Assert.True(rig.Vm.IsConnectionOk);
    }

    // ---- 換模型重試 ----

    [Fact]
    public async Task RetryWithOtherModel_SwitchesDefaultModel_ThenResends()
    {
        using var host = new SettingsHost();
        var second = new ModelSettings(ModelSettings.NewId(), "dev-anthropic", "claude-sonnet-5-5", "Sonnet", 4096, 200_000, false);
        await host.Catalog.SaveModelAsync(second);
        var store = new InMemoryConversationStore();
        await SeedAsync(store, "對話", (ChatRole.User, "問"));
        var failing = new SwitchableChatProvider(new ScriptedChatProvider(new Failed(new ChatFailure(ChatFailureKind.Overloaded, "過載", true))));
        var status = new ConnectionStatusCache();
        var vm = new WorkbenchViewModel(new ConversationService(failing, store, host.Catalog), store, host.Catalog, new FakeApiKeyPrompt(false),
            new FakeSettingsWindowService(), status, new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
            NullLogger<WorkbenchViewModel>.Instance);
        await vm.InitializeAsync();
        await vm.RetryForTest();
        Assert.True(vm.HasError);
        var ok = new ScriptedChatProvider(Reply("換模型後成功"));
        failing.Current = ok;

        var entry = vm.RetrySwitcher.Entries.Single(e => e.ModelSettingsId == second.Id);
        await vm.RetrySwitcher.SelectAsync(entry);

        Assert.Equal(second.Id, host.Catalog.Current.WorkbenchModelId);
        Assert.Equal("Sonnet", Assert.Single(ok.Requests).Model.Model.DisplayName);
        Assert.False(vm.HasError);
        Assert.Equal("換模型後成功", vm.Messages[^1].Text);
    }

    private sealed class GatedApiKeyPrompt(Task<bool> gate) : IApiKeyPrompt
    {
        public bool IsWaiting { get; private set; }

        public async Task<bool> PromptAsync(ApiKeyPromptReason reason)
        {
            IsWaiting = true;
            return await gate;
        }
    }
}

internal static class WorkbenchTestExtensions
{
    /// <summary>目前最後一則是使用者訊息時，用「重試」要一次回應（直接開始，不透過錯誤列）。</summary>
    public static Task RegenerateOrRetryForTest(this WorkbenchViewModel vm)
    {
        vm.ErrorMessage = "x";   // 讓 RetryCommand 可用（真實情況是上一次失敗留下的錯誤列）
        return vm.RetryCommand.ExecuteAsync(null);
    }

    public static Task RetryForTest(this WorkbenchViewModel vm) => vm.RegenerateOrRetryForTest();
}
