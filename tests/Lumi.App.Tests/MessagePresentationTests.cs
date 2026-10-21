using System.ComponentModel;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

/// <summary>UB-3：歡迎區的顯示條件（<c>ShowWelcome</c>）、訊息時間（<c>TimeText</c>）、助理氣泡（<c>ShowBubble</c>）。</summary>
public class MessagePresentationTests
{
    private static (WorkbenchViewModel Vm, InMemoryConversationStore Store) Create(SequenceChatProvider? provider = null, InMemoryConversationStore? store = null)
    {
        store ??= new InMemoryConversationStore();
        var catalog = new FakeModelCatalog();
        var service = new ConversationService(provider ?? new SequenceChatProvider(SequenceChatProvider.Answer("好")), store, catalog);
        var vm = new WorkbenchViewModel(service, store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
            new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
            NullLogger<WorkbenchViewModel>.Instance);
        return (vm, store);
    }

    private static string LocalHm(DateTimeOffset value) => value.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    // ---- ShowWelcome ----

    [Fact]
    public async Task ShowWelcome_IsTrueForAnEmptyConversation()
    {
        var (vm, _) = Create();

        await vm.InitializeAsync();

        Assert.Empty(vm.Messages);
        Assert.True(vm.ShowWelcome);
    }

    [Fact]
    public async Task ShowWelcome_IsFalseWhenTheLoadedConversationHasMessages()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("舊對話");
        await store.AppendMessageAsync(conversation.Id,
            new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("問")], DateTimeOffset.UtcNow));
        var (vm, _) = Create(store: store);

        await vm.InitializeAsync();

        Assert.False(vm.ShowWelcome);
    }

    [Fact]
    public async Task ShowWelcome_HidesAfterSending_AndComesBackForANewConversation()
    {
        var (vm, _) = Create();
        await vm.InitializeAsync();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.InputText = "嗨";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.False(vm.ShowWelcome);
        Assert.Contains(nameof(WorkbenchViewModel.ShowWelcome), changed);

        await vm.NewConversationCommand.ExecuteAsync(null);

        Assert.Empty(vm.Messages);
        Assert.True(vm.ShowWelcome);
    }

    [Fact]
    public void ShowWelcome_IsFalseWhileTheFirstConversationIsStillLoading()
    {
        var (vm, _) = Create();
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.IsLoadingMessages = true;

        Assert.False(vm.ShowWelcome);
        Assert.Contains(nameof(WorkbenchViewModel.ShowWelcome), changed);

        vm.IsLoadingMessages = false;

        Assert.True(vm.ShowWelcome);
    }

    // ---- TimeText ----

    [Fact]
    public void TimeText_IsLocalHourMinute_FromTheStoredCreatedAt()
    {
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 22, 9, 5, 0));
        var created = new DateTimeOffset(2026, 10, 22, 9, 5, 0, localOffset);

        var user = MessageViewModel.From(new ChatMessage("1", ChatRole.User, [new TextBlock("問")], created));
        var assistant = MessageViewModel.From(new ChatMessage("2", ChatRole.Assistant, [new TextBlock("答")], created));

        Assert.Equal("09:05", user.TimeText);   // 24 小時制、補零
        Assert.Equal("09:05", assistant.TimeText);
    }

    [Fact]
    public void TimeText_ConvertsUtcToLocalTime()
    {
        var utc = new DateTimeOffset(2026, 10, 22, 23, 59, 0, TimeSpan.Zero);

        var message = MessageViewModel.User("嗨", createdAt: utc);

        Assert.Equal(LocalHm(utc), message.TimeText);
        Assert.Matches(@"^\d{2}:\d{2}$", message.TimeText);
    }

    [Fact]
    public void TimeText_OfAStreamingMessage_IsItsCreationTime_AndStaysAfterCompletion()
    {
        var created = new DateTimeOffset(2026, 10, 22, 14, 28, 0, TimeSpan.Zero);
        var bubble = MessageViewModel.StreamingAssistant(created);
        var expected = LocalHm(created);
        Assert.Equal(expected, bubble.TimeText);

        bubble.Complete(new ChatMessage("1", ChatRole.Assistant, [new TextBlock("答")], created.AddMinutes(5)), created.AddMinutes(5));

        Assert.Equal(expected, bubble.TimeText);   // 以氣泡建立時間為準，不跟著完成時間變
    }

    [Fact]
    public async Task MessagesCreatedByTheWorkbench_UseTheWorkbenchClock()
    {
        var (vm, _) = Create();
        await vm.InitializeAsync();

        vm.InputText = "嗨";
        await vm.SendCommand.ExecuteAsync(null);

        var expected = LocalHm(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));   // FakeTimeProvider 的時間
        Assert.Equal(expected, vm.Messages[0].TimeText);
    }

    // ---- ShowBubble ----

    [Fact]
    public void ShowBubble_NeedsSomethingToShow()
    {
        var bubble = MessageViewModel.StreamingAssistant();
        Assert.True(bubble.ShowBubble);   // 等待中的「…」

        bubble.AppendText("字", DateTimeOffset.UtcNow);
        Assert.True(bubble.ShowBubble);

        var empty = MessageViewModel.From(new ChatMessage("1", ChatRole.Assistant, [], DateTimeOffset.UtcNow));
        Assert.False(empty.ShowBubble);

        var stopped = MessageViewModel.From(new ChatMessage("2", ChatRole.Assistant, [], DateTimeOffset.UtcNow, MessageStatus.Stopped));
        Assert.True(stopped.ShowBubble);
    }
}
