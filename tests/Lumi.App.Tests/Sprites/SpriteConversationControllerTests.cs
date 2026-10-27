using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Lumi.App.Services;
using Lumi.App.Sprites;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Sprites.Abstractions;
using Lumi.Sprites.Orb;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Sprites;

/// <summary>
/// 小精靈接上對話（真的 <see cref="ChatSession"/>、真的 <see cref="SpriteWindow"/>、假的供應商與原生層）：
/// 輸入列送出 → 狀態與氣泡跟著事件走；權限確認在氣泡內完成；錯誤、重試與缺金鑰的流程。
/// </summary>
public class SpriteConversationControllerTests
{
    private sealed class NoNativeOverlay : Lumi.Core.Platform.IOverlayPlatform
    {
        public string Name => "none";
        public bool IsSupported => false;
        public bool ClickThroughActive => false;
        public void Attach(nint nativeWindowHandle) { }
        public bool TryGetCursorInWindow(double renderScaling, double clientHeightDip, out Lumi.Core.Platform.OverlayPoint dip) { dip = default; return false; }
        public void SetClickThrough(bool on) { }
    }

    private sealed class Rig
    {
        public InMemoryConversationStore Store { get; } = new();
        public ToolApprovalService Approvals { get; } = new();
        public FakeWindowHost Host { get; } = new();
        public FakeApiKeyPrompt KeyPrompt { get; }
        public FakeSettingsWindowService SettingsWindow { get; } = new();
        public LambdaTool Tool { get; }
        public ChatSession Session { get; }
        public SpriteWindow Window { get; }
        public SpriteBubbleViewModel Bubble => Window.BubbleViewModel;
        public AppModeCoordinator Modes { get; }
        public SequenceChatProvider Provider { get; }
        public SpriteConversationService? Sprite { get; }
        public SpriteConversationSwitcher? Switcher { get; }

        public Rig(SequenceChatProvider provider, ToolPermission permission = ToolPermission.Write, bool keySaved = false, Action? onKeySaved = null,
            bool dedicatedConversation = false)
        {
            Provider = provider;
            Tool = new LambdaTool("save", "儲存", permission);
            var catalog = new FakeModelCatalog();
            var time = new FakeTimeProvider();
            var service = new ConversationService(provider, Store, catalog, new ToolRegistry([Tool]), Approvals, new ToolPermissionMemory(),
                new ToolLoopOptions(), time, NullLogger<ConversationService>.Instance);
            if (dedicatedConversation)
                Sprite = new SpriteConversationService(Store);   // M22 22J：小精靈用自己的專屬對話
            Session = new ChatSession(service, Store, sprite: Sprite);
            if (Sprite is not null)
                Switcher = new SpriteConversationSwitcher(Session, Sprite, Store, NullLogger<SpriteConversationSwitcher>.Instance);
            KeyPrompt = new FakeApiKeyPrompt(keySaved) { OnSaved = onKeySaved };
            var options = new SpriteShellOptions();
            var mapper = new SpriteStateMapper(options, time);
            var bubble = new SpriteBubbleViewModel(options, time, Approvals);
            Modes = new AppModeCoordinator(Session, Host, catalog, options, time, NullLogger<AppModeCoordinator>.Instance, Switcher);
            Window = new SpriteWindow(new SpriteCatalog([new OrbSpriteFactory()]), new NoNativeOverlay(), catalog, options, time,
                NullLogger<SpriteWindow>.Instance, null, mapper, bubble, Modes, Session, SettingsWindow);
            new SpriteConversationController(Session, mapper, bubble, Approvals, Window, Modes, KeyPrompt, SettingsWindow,
                NullLogger<SpriteConversationController>.Instance).Attach();
        }

        public async Task StartAsync()
        {
            await Session.InitializeAsync("新對話");
            Session.TrySetMode(ChatMode.Sprite);
            if (Switcher is not null)
                await Switcher.WhenIdleAsync();
        }

        public void Type(string text) => Window.FindControl<TextBox>("Input")!.Text = text;

        public void Submit() => Window.FindControl<Button>("SendButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        public Button BubbleButton(string name) => Window.FindControl<SpriteBubbleView>("Bubble")!.FindControl<Button>(name)!;

        public void Click(Button button) => button.Command!.Execute(null);
    }

    private static SequenceChatProvider Answer(string text) => new(SequenceChatProvider.Answer(text));

    private static SequenceChatProvider ToolThenAnswer(string answer = "存好了") =>
        new(SequenceChatProvider.ToolCalls(null, SequenceChatProvider.Use("t1", "save")), SequenceChatProvider.Answer(answer));

    private static SequenceChatProvider Failing(ChatFailure failure) => new(new ChatEvent[] { new Failed(failure) });

    // ---- 送出與狀態 ----

    [AvaloniaFact]
    public async Task SubmittingFromTheInputBar_SendsInSpriteMode_AndTheBubbleShowsTheAnswer()
    {
        var rig = new Rig(Answer("明天會下雨。"));
        await rig.StartAsync();
        RunStartedArgs? started = null;
        rig.Session.RunStarted += (_, a) => started = a;

        rig.Type("明天會下雨嗎");
        rig.Submit();
        await SettingsHost.WaitUntil(() => !rig.Session.IsResponding && rig.Bubble.Kind == SpriteBubbleKind.Done);

        Assert.Equal(ChatMode.Sprite, started?.Mode);
        Assert.Equal("明天會下雨。", rig.Bubble.Text);
        Assert.Equal(SpriteState.Done, rig.Window.AppliedState);
        Assert.Equal("", rig.Window.FindControl<TextBox>("Input")!.Text);
        Assert.Equal(2, (await rig.Store.GetMessagesAsync(rig.Session.ConversationId!)).Count);
    }

    [AvaloniaFact]
    public async Task TheStatesFollowTheEvents_ThinkingToolStreamingDone()
    {
        var provider = ToolThenAnswer("好了");
        var rig = new Rig(provider, ToolPermission.ReadOnly);
        await rig.StartAsync();
        var states = new List<SpriteState>();
        rig.Window.Mapper.Changed += states.Add;

        rig.Type("存");
        rig.Submit();
        await SettingsHost.WaitUntil(() => !rig.Session.IsResponding);

        Assert.Equal([SpriteState.Thinking, SpriteState.Tool, SpriteState.Thinking, SpriteState.Streaming, SpriteState.Done], states);
        Assert.Equal(1, rig.Tool.Runs);
    }

    [AvaloniaFact]
    public async Task WhileResponding_TheInputShowsStop_AndSubmittingIsIgnored()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好")) { HangOnCall = 1 };
        var rig = new Rig(provider);
        await rig.StartAsync();
        rig.Type("一");
        rig.Submit();
        await SettingsHost.WaitUntil(() => rig.Session.IsResponding);
        Assert.True(rig.Window.FindControl<Lumi.App.Controls.LumiIcon>("StopIcon")!.IsVisible);

        rig.Type("二");           // 草稿
        rig.Window.FindControl<Button>("SendButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));   // 這時是停止
        await SettingsHost.WaitUntil(() => !rig.Session.IsResponding);

        Assert.Equal(1, provider.Calls);   // 沒有送出第二則
        Assert.Equal("二", rig.Window.FindControl<TextBox>("Input")!.Text);   // 草稿還在
        Assert.False(rig.Bubble.IsVisible);   // 使用者停止：氣泡收起
        Assert.Equal(SpriteState.Idle, rig.Window.Mapper.State);
    }

    [AvaloniaFact]
    public async Task ARunStartedFromTheWorkbench_DoesNotTouchTheBubble()
    {
        var rig = new Rig(Answer("工作台的答案"));
        await rig.Session.InitializeAsync("新對話");   // 工作台模式

        await rig.Session.SendAsync("嗨");

        Assert.False(rig.Bubble.IsVisible);
        Assert.Equal(SpriteState.Idle, rig.Window.Mapper.State);
    }

    // ---- 權限確認 ----

    [AvaloniaFact]
    public async Task TheConfirmationIsAnsweredInTheBubble_AndTheToolThenRuns()
    {
        var rig = new Rig(ToolThenAnswer());
        await rig.StartAsync();
        rig.Type("存");
        rig.Submit();
        await SettingsHost.WaitUntil(() => rig.Bubble.IsApproval);

        Assert.Equal(SpriteState.Attention, rig.Window.AppliedState);   // 角色 Attention
        Assert.True(rig.Bubble.IsInteractive);
        Assert.Equal(0, rig.Tool.Runs);

        rig.Click(rig.BubbleButton("AllowButton"));
        await SettingsHost.WaitUntil(() => !rig.Session.IsResponding);

        Assert.Equal(1, rig.Tool.Runs);
        Assert.Equal("存好了", rig.Bubble.Text);
    }

    [AvaloniaFact]
    public async Task AnAnswerGivenInTheWorkbench_ReturnsTheBubbleAndTheSpriteToTool()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(null, SequenceChatProvider.Use("t1", "save")), SequenceChatProvider.Answer("好")) { HangOnCall = 2 };
        var rig = new Rig(provider);
        await rig.StartAsync();
        rig.Type("存");
        rig.Submit();
        await SettingsHost.WaitUntil(() => rig.Bubble.IsApproval);

        rig.Approvals.Resolve("t1", ApprovalDecision.AllowOnce);   // 工作台卡片上的按鈕
        await SettingsHost.WaitUntil(() => provider.Calls == 2);

        Assert.False(rig.Bubble.IsApproval);
        Assert.Equal(1, rig.Tool.Runs);
        await rig.Session.StopAndWaitAsync();
    }

    [AvaloniaFact]
    public async Task DenyingInTheBubble_NeverRunsTheTool_AndTheModelAnswersWithinTheBubble()
    {
        var rig = new Rig(ToolThenAnswer("好吧，沒有存。"));
        await rig.StartAsync();
        rig.Type("存");
        rig.Submit();
        await SettingsHost.WaitUntil(() => rig.Bubble.IsApproval);

        rig.Click(rig.BubbleButton("DenyButton"));
        await SettingsHost.WaitUntil(() => !rig.Session.IsResponding);

        Assert.Equal(0, rig.Tool.Runs);
        Assert.Equal("好吧，沒有存。", rig.Bubble.Text);
    }

    [AvaloniaFact]
    public async Task ViewInWorkbench_OpensTheWorkbenchAtThatToolCard()
    {
        var rig = new Rig(ToolThenAnswer());
        await rig.StartAsync();
        rig.Type("存");
        rig.Submit();
        await SettingsHost.WaitUntil(() => rig.Bubble.IsApproval);
        rig.Click(rig.BubbleButton("MoreLink"));   // 展開更多選項

        rig.Click(rig.BubbleButton("ViewLink"));

        Assert.Contains("showWorkbench", rig.Host.Log);
        Assert.Contains("scroll:t1", rig.Host.Log);
        rig.Approvals.Resolve("t1", ApprovalDecision.Deny);
        await SettingsHost.WaitUntil(() => !rig.Session.IsResponding);
    }

    [AvaloniaFact]
    public async Task ViewInWorkbench_ShowsTheSpriteConversation_NotTheWorkbenchOne()
    {
        var rig = new Rig(ToolThenAnswer(), dedicatedConversation: true);
        await rig.StartAsync();
        var spriteId = rig.Session.ConversationId;
        Assert.Equal(spriteId, await rig.Sprite!.GetIdAsync());
        rig.Type("存");
        rig.Submit();
        await SettingsHost.WaitUntil(() => rig.Bubble.IsApproval);
        rig.Click(rig.BubbleButton("MoreLink"));

        rig.Click(rig.BubbleButton("ViewLink"));   // 回應中：查看中，工作台看的是小精靈的對話
        Assert.Equal(spriteId, rig.Session.ConversationId);
        rig.Approvals.Resolve("t1", ApprovalDecision.Deny);
        await SettingsHost.WaitUntil(() => !rig.Session.IsResponding);
        await rig.Switcher!.WhenIdleAsync();

        Assert.Equal(ChatMode.Workbench, rig.Session.ActiveMode);   // 回應結束後切到工作台模式，但對話仍是小精靈的（要看完整內容）
        Assert.Equal(spriteId, rig.Session.ConversationId);
        Assert.Contains("scroll:t1", rig.Host.Log);
    }

    // ---- 錯誤與重試 ----

    [AvaloniaFact]
    public async Task AFailure_ShowsOneSentence_TheSpriteGoesToError_AndRetryResends()
    {
        var provider = new SequenceChatProvider(
            new ChatEvent[] { new Failed(new ChatFailure(ChatFailureKind.Network, "連不上伺服器", true)) },
            SequenceChatProvider.Answer("這次成功了"));
        var rig = new Rig(provider);
        await rig.StartAsync();
        rig.Type("嗨");
        rig.Submit();
        await SettingsHost.WaitUntil(() => rig.Bubble.IsError);

        Assert.Equal("連不上伺服器", rig.Bubble.ErrorText);
        Assert.Equal(SpriteState.Error, rig.Window.AppliedState);
        Assert.True(rig.Bubble.CanRetry);

        rig.Click(rig.BubbleButton("RetryButton"));
        await SettingsHost.WaitUntil(() => rig.Bubble.Kind == SpriteBubbleKind.Done);

        Assert.Equal("這次成功了", rig.Bubble.Text);
        Assert.Equal(SpriteState.Done, rig.Window.AppliedState);
    }

    [AvaloniaFact]
    public async Task AMissingKey_OffersSettings_AndSavingTheKeyRetriesAutomatically()
    {
        var provider = new SequenceChatProvider(
            new ChatEvent[] { new Failed(new ChatFailure(ChatFailureKind.MissingApiKey, "還沒有設定 API 金鑰", false)) },
            SequenceChatProvider.Answer("金鑰存好了"));
        var rig = new Rig(provider, keySaved: true);
        await rig.StartAsync();
        rig.Type("嗨");
        rig.Submit();
        await SettingsHost.WaitUntil(() => rig.Bubble.IsError);
        Assert.True(rig.Bubble.CanOpenSettings);

        rig.Click(rig.BubbleButton("SettingsButton"));
        await SettingsHost.WaitUntil(() => rig.Bubble.Kind == SpriteBubbleKind.Done);

        Assert.Equal([ApiKeyPromptReason.Missing], rig.KeyPrompt.Calls);
        Assert.Equal("金鑰存好了", rig.Bubble.Text);
    }

    [AvaloniaFact]
    public async Task AMissingKey_WhenTheUserCancelsThePrompt_StaysAnError()
    {
        var rig = new Rig(Failing(new ChatFailure(ChatFailureKind.InvalidApiKey, "金鑰無效", false)), keySaved: false);
        await rig.StartAsync();
        rig.Type("嗨");
        rig.Submit();
        await SettingsHost.WaitUntil(() => rig.Bubble.IsError);

        rig.Click(rig.BubbleButton("SettingsButton"));
        await SettingsHost.WaitUntil(() => rig.KeyPrompt.Calls.Count == 1);

        Assert.Equal([ApiKeyPromptReason.Invalid], rig.KeyPrompt.Calls);
        Assert.True(rig.Bubble.IsError);
    }

    [AvaloniaFact]
    public async Task ANonKeyProblem_OpensTheSettingsWindow_WithoutPrompting()
    {
        var rig = new Rig(Failing(new ChatFailure(ChatFailureKind.ModelNotConfigured, "沒有可用的模型", false)));
        await rig.StartAsync();
        rig.Type("嗨");
        rig.Submit();
        await SettingsHost.WaitUntil(() => rig.Bubble.IsError);

        rig.Click(rig.BubbleButton("SettingsButton"));
        await SettingsHost.WaitUntil(() => rig.SettingsWindow.Shown.Count == 1);

        Assert.Empty(rig.KeyPrompt.Calls);
    }

    [AvaloniaFact]
    public async Task ViewingAnErrorInTheWorkbench_OpensItWithoutScrolling()
    {
        var rig = new Rig(Failing(new ChatFailure(ChatFailureKind.Network, "連不上", true)));
        await rig.StartAsync();
        rig.Type("嗨");
        rig.Submit();
        await SettingsHost.WaitUntil(() => rig.Bubble.IsError);

        rig.Click(rig.BubbleButton("ErrorViewLink"));

        Assert.Contains("showWorkbench", rig.Host.Log);
        Assert.DoesNotContain(rig.Host.Log, l => l.StartsWith("scroll", StringComparison.Ordinal));
    }
}
