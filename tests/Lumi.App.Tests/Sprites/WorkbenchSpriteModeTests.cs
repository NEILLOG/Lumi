using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Lumi.App.Services;
using Lumi.App.Sprites;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Sprites.Abstractions;
using Lumi.Sprites.Orb;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Sprites;

/// <summary>工作台這一側：小精靈模式按鈕、「查看中」、在工作台送出就切回工作台模式、關閉視窗。</summary>
public class WorkbenchSpriteModeTests
{
    private sealed class FakeModes : IAppModeCoordinator
    {
        public ChatMode ActiveMode => ChatMode.Workbench;
        public bool IsSpriteTemporarilyHidden => false;
        public bool MayClose { get; set; } = true;
        public List<string> Calls { get; } = [];
        public void Start() => Calls.Add("start");
        public bool EnterSpriteMode() { Calls.Add("enterSprite"); return true; }
        public void OpenWorkbench(string? scrollToCallId = null) => Calls.Add("openWorkbench");
        public void WorkbenchWillSend() => Calls.Add("willSend");
        public bool WorkbenchMayClose() { Calls.Add("mayClose"); return MayClose; }
        public void ShowSprite() => Calls.Add("showSprite");
        public void HideSpriteTemporarily() => Calls.Add("hide");
        public void Exit() => Calls.Add("exit");
    }

    private sealed class Rig
    {
        public InMemoryConversationStore Store { get; } = new();
        public FakeModes Modes { get; } = new();
        public ChatSession Session { get; }
        public WorkbenchViewModel Vm { get; }
        public SequenceChatProvider Provider { get; }

        public Rig(SequenceChatProvider provider, bool withModes = true)
        {
            Provider = provider;
            var catalog = new FakeModelCatalog();
            var time = new FakeTimeProvider();
            var service = new ConversationService(provider, Store, catalog);
            Session = new ChatSession(service, Store);
            Vm = new WorkbenchViewModel(service, Store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
                new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), time, NullLogger<WorkbenchViewModel>.Instance,
                session: Session, modes: withModes ? Modes : null);
        }

        public async Task SendFromWorkbenchAsync(string text)
        {
            await Vm.InitializeAsync();
            Vm.InputText = text;
            await Vm.SendCommand.ExecuteAsync(null);
        }
    }

    private static SequenceChatProvider Answers(params string[] texts) => new(texts.Select(t => SequenceChatProvider.Answer(t)).ToArray());

    // ---- 小精靈模式按鈕 ----

    [Fact]
    public async Task TheSpriteModeButton_AsksTheCoordinator_AndIsDisabledWhileResponding()
    {
        var rig = new Rig(new SequenceChatProvider(SequenceChatProvider.Answer("好")) { HangOnCall = 1 });
        await rig.Vm.InitializeAsync();
        Assert.True(rig.Vm.EnterSpriteModeCommand.CanExecute(null));

        rig.Vm.EnterSpriteModeCommand.Execute(null);
        Assert.Equal(["enterSprite"], rig.Modes.Calls);

        rig.Vm.InputText = "x";
        var sending = rig.Vm.SendCommand.ExecuteAsync(null);
        await SettingsHost.WaitUntil(() => rig.Vm.IsResponding);
        Assert.False(rig.Vm.EnterSpriteModeCommand.CanExecute(null));   // 回應中不能切換（ui-spec 2.2）

        await rig.Vm.StopAndWaitAsync();
        await sending;
        Assert.True(rig.Vm.EnterSpriteModeCommand.CanExecute(null));
    }

    [Fact]
    public async Task WithoutACoordinator_TheSpriteModeButtonIsInert()
    {
        var rig = new Rig(Answers("好"), withModes: false);
        await rig.Vm.InitializeAsync();

        Assert.False(rig.Vm.EnterSpriteModeCommand.CanExecute(null));
        Assert.False(rig.Vm.HasModeCoordinator);
    }

    // ---- 查看中 ----

    [Fact]
    public async Task ViewOnly_IsRespondingInSpriteMode_AndEndsWithTheResponse()
    {
        var rig = new Rig(new SequenceChatProvider(SequenceChatProvider.Answer("好")) { HangOnCall = 1 });
        await rig.Vm.InitializeAsync();
        var changes = new List<string?>();
        rig.Vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        rig.Session.TrySetMode(ChatMode.Sprite);
        Assert.False(rig.Vm.IsViewOnly);   // 沒在回應：兩個視窗同時開著，工作台可以照常使用

        var sprite = rig.Session.SendAsync("嗨");   // 小精靈發起的
        await SettingsHost.WaitUntil(() => rig.Vm.IsResponding);

        Assert.True(rig.Vm.IsViewOnly);
        Assert.False(rig.Vm.CanSwitchModel);
        Assert.False(rig.Vm.EnterSpriteModeCommand.CanExecute(null));

        await rig.Session.StopAndWaitAsync();
        await sprite;
        Assert.False(rig.Vm.IsViewOnly);   // 回應結束自動解除
        Assert.Contains(nameof(WorkbenchViewModel.IsViewOnly), changes);
    }

    [Fact]
    public async Task ARunStartedInWorkbenchMode_IsNeverViewOnly()
    {
        var rig = new Rig(new SequenceChatProvider(SequenceChatProvider.Answer("好")) { HangOnCall = 1 });
        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = "x";

        var sending = rig.Vm.SendCommand.ExecuteAsync(null);
        await SettingsHost.WaitUntil(() => rig.Vm.IsResponding);

        Assert.False(rig.Vm.IsViewOnly);
        await rig.Vm.StopAndWaitAsync();
        await sending;
    }

    [Fact]
    public async Task ViewOnly_NeedsACoordinator()
    {
        var rig = new Rig(new SequenceChatProvider(SequenceChatProvider.Answer("好")) { HangOnCall = 1 }, withModes: false);
        await rig.Vm.InitializeAsync();
        rig.Session.TrySetMode(ChatMode.Sprite);
        var sprite = rig.Session.SendAsync("嗨");
        await SettingsHost.WaitUntil(() => rig.Vm.IsResponding);

        Assert.False(rig.Vm.IsViewOnly);   // 沒有協調者（沒有小精靈殼），工作台維持原樣
        await rig.Session.StopAndWaitAsync();
        await sprite;
    }

    // ---- 在工作台送出 ----

    [Fact]
    public async Task SendingRetryingAndRegenerating_AllTellTheCoordinatorFirst()
    {
        var rig = new Rig(Answers("一", "二", "三"));

        await rig.SendFromWorkbenchAsync("嗨");
        await rig.Vm.RegenerateCommand.ExecuteAsync(null);

        Assert.Equal(["willSend", "willSend"], rig.Modes.Calls);
    }

    [Fact]
    public async Task TheMessageThatIsSentAfterTheSwitch_UsesWorkbenchMode()
    {
        // 協調者在送出前把模式切回工作台：這一輪就是工作台模式
        var rig = new Rig(Answers("好"));
        await rig.Vm.InitializeAsync();
        rig.Session.TrySetMode(ChatMode.Sprite);
        RunStartedArgs? started = null;
        rig.Session.RunStarted += (_, a) => started = a;
        var real = new AppModeCoordinator(rig.Session, new FakeWindowHost(), new FakeModelCatalog(), new SpriteShellOptions(), new FakeTimeProvider(),
            NullLogger<AppModeCoordinator>.Instance);
        var vm = new WorkbenchViewModel(new ConversationService(rig.Provider, rig.Store, new FakeModelCatalog()), rig.Store, new FakeModelCatalog(),
            new FakeApiKeyPrompt(false), new FakeSettingsWindowService(), new ConnectionStatusCache(), new FakeClipboardService(),
            new SyncUiDispatcher(), new FakeTimeProvider(), NullLogger<WorkbenchViewModel>.Instance, session: rig.Session, modes: real);
        await vm.InitializeAsync();
        vm.InputText = "在工作台問";

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(ChatMode.Workbench, started?.Mode);
    }

    // ---- 啟動時載入 ----

    [Fact]
    public async Task Initialize_RunsOnlyOnce_SoOpeningTheWindowLaterDoesNotReloadTheConversation()
    {
        var rig = new Rig(Answers("好"));
        await rig.Vm.InitializeAsync();
        var conversationId = rig.Session.ConversationId;
        var changed = 0;
        rig.Session.ConversationChanged += (_, _) => changed++;

        await rig.Vm.InitializeAsync();   // 視窗第一次開啟時又呼叫一次

        Assert.Equal(0, changed);
        Assert.Equal(conversationId, rig.Session.ConversationId);
    }

    // ---- 視窗 ----

    [AvaloniaFact]
    public async Task TheViewOnlyBanner_AppearsOnlyWhileViewing_AndTheInputIsDisabled()
    {
        var rig = new Rig(new SequenceChatProvider(SequenceChatProvider.Answer("好")) { HangOnCall = 1 });
        var window = new WorkbenchWindow(rig.Vm, rig.Modes);
        window.Show();
        await rig.Vm.InitializeAsync();
        var banner = window.FindControl<Border>("ViewOnlyBanner")!;
        var input = window.FindByName<TextBox>("InputBox");
        Assert.False(banner.IsVisible);
        Assert.True(input.IsEnabled);

        rig.Session.TrySetMode(ChatMode.Sprite);
        var sprite = rig.Session.SendAsync("嗨");
        await SettingsHost.WaitUntil(() => banner.IsVisible);

        Assert.False(input.IsEnabled);
        Assert.Equal("小精靈回應中，結束後可在這裡繼續", ((Avalonia.Controls.TextBlock)banner.Child!).Text);

        await rig.Session.StopAndWaitAsync();
        await sprite;
        Assert.False(banner.IsVisible);
        Assert.True(input.IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public void ClosingTheWorkbench_InWorkbenchMode_QuitsAndCloses()
    {
        var rig = new Rig(Answers("好"));
        rig.Modes.MayClose = true;
        var window = new WorkbenchWindow(rig.Vm, rig.Modes);
        window.Show();

        window.Close();

        Assert.Contains("mayClose", rig.Modes.Calls);
        Assert.False(window.IsVisible);   // 真的關了
    }

    [AvaloniaFact]
    public void ClosingTheWorkbench_InSpriteMode_OnlyHidesIt_SoItCanBeOpenedAgain()
    {
        var rig = new Rig(Answers("好"));
        rig.Modes.MayClose = false;
        var window = new WorkbenchWindow(rig.Vm, rig.Modes);
        window.Show();

        window.Close();

        Assert.False(window.IsVisible);   // 隱藏了
        window.Show();                    // 沒有被銷毀，可以再顯示
        Assert.True(window.IsVisible);
        rig.Modes.MayClose = true;
        window.Close();
    }

    [AvaloniaFact]
    public void TheSpriteModeButton_IsEnabled_AndRunsTheCommand()
    {
        var rig = new Rig(Answers("好"));
        var window = new WorkbenchWindow(rig.Vm, rig.Modes);
        window.Show();
        var button = window.FindByName<Button>("SpriteModeButton");

        Assert.True(button.IsEnabled);
        button.Command!.Execute(null);

        Assert.Equal(["enterSprite"], rig.Modes.Calls);
        window.Close();
    }
}
