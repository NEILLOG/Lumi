using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Lumi.App.Markdown;
using Lumi.App.Services;
using Lumi.App.Sprites;
using Lumi.App.Tests.Sprites;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Sprites.Abstractions;
using Lumi.Sprites.Orb;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.TestSupport;

/// <summary>
/// UE-4：把「使用者看得到的畫面」集中成一份清單，供「每個按鈕都有層級」與「沒有 Fluent 預設色殘留」兩個掃描測試共用：
/// 工作台（有訊息、提示、錯誤列、歷史抽屜）、模型選單與 Flyout 外框、工具卡片與群組卡、程式碼區塊、設定視窗的各種畫面狀態、ConfirmDialog、小精靈視窗。
/// 每個畫面是一個 <see cref="Window"/>；測試結束呼叫 <see cref="Dispose"/> 關閉。
/// </summary>
internal sealed class AppSurfaces : IDisposable
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

    private readonly List<IDisposable> _owned = [];

    public List<(string Name, Window Window)> Windows { get; } = [];

    public WorkbenchViewModel? WorkbenchVm { get; private set; }

    /// <param name="includeSettings">是否包含設定視窗的所有畫面狀態（15 個，較慢）。</param>
    public static async Task<AppSurfaces> OpenAsync(bool includeSettings = true)
    {
        var surfaces = new AppSurfaces();
        await surfaces.AddWorkbenchAsync();
        surfaces.AddFlyouts();
        surfaces.AddToolViews();
        surfaces.AddConfirmDialogs();
        surfaces.AddStartupFailure();
        surfaces.AddSpriteWindow();
        if (includeSettings)
        {
            foreach (var page in SettingsRig.Pages)
            {
                var rig = await SettingsRig.CreateAsync(page);
                surfaces._owned.Add(rig);
                surfaces.Windows.Add(("settings:" + page, rig.Window));
            }
        }
        return surfaces;
    }

    private async Task AddWorkbenchAsync()
    {
        var store = new InMemoryConversationStore();
        var catalog = new FakeModelCatalog();
        var service = new ConversationService(new SequenceChatProvider(SequenceChatProvider.Answer("好")), store, catalog);
        var vm = new WorkbenchViewModel(service, store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
            new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
            NullLogger<WorkbenchViewModel>.Instance);
        var window = new WorkbenchWindow(vm) { Width = 1040, Height = 760 };
        window.Show();
        await vm.InitializeAsync();
        vm.InputText = "問題";
        await vm.SendCommand.ExecuteAsync(null);
        vm.Notice = "設定檔損毀，已改用預設值。";
        vm.ErrorMessage = "連不上伺服器。";
        vm.History.ToggleCommand.Execute(null);
        window.UpdateLayout();
        WorkbenchVm = vm;
        Windows.Add(("workbench", window));
    }

    /// <summary>模型選單（含搜尋與清單）放在 FlyoutPresenter 裡、工具列「…」的 MenuFlyoutPresenter 與 MenuItem（含停用項）。</summary>
    private void AddFlyouts()
    {
        var catalog = new FakeModelCatalog();
        var store = new InMemoryConversationStore();
        var service = new ConversationService(new SequenceChatProvider(SequenceChatProvider.Answer("好")), store, catalog);
        var vm = new WorkbenchViewModel(service, store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
            new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
            NullLogger<WorkbenchViewModel>.Instance);

        var picker = new FlyoutPresenter { Content = new ModelPicker { DataContext = vm.ModelSwitcher }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var menu = new MenuFlyoutPresenter
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Items =
            {
                new MenuItem { Header = "設定" },
                new MenuItem { Header = "管理連線與模型…" },
                new MenuItem { Header = "停用項目", IsEnabled = false },
            },
        };
        var window = new Window
        {
            Width = 700, Height = 700,
            Content = new StackPanel { Spacing = 16, Margin = new Thickness(16), Children = { picker, menu } },
        };
        window.Show();
        window.UpdateLayout();
        Windows.Add(("flyouts", window));
    }

    private void AddToolViews()
    {
        var responder = new RecordingResponder();
        ToolCardViewModel Card(string id) => new(id, "echo", "回聲", ToolPermission.Write, "檔案.txt", """{"text":"你好"}""", responder,
            new FakeClipboardService(), new FakeTimeProvider());

        var awaiting = Card("a1");
        awaiting.SetAwaiting(new ToolApprovalRequest("a1", "echo", "回聲", ToolPermission.Write, new ToolPreview("檔案.txt", "將寫入 3 行"), "{}"));
        var destructive = Card("d1");
        destructive.SetAwaiting(new ToolApprovalRequest("d1", "echo", "回聲", ToolPermission.Destructive, new ToolPreview("a.md", "將永久刪除"), "{}"));
        var done = Card("c1");
        done.Complete(new ToolResultBlock("c1", "完成了", false, "回聲", "檔案.txt", "182 行", 320, ToolApproval.NotRequired));
        done.ToggleCommand.Execute(null);   // 展開：顯示詳細、複製鈕
        var run = new ToolRunViewModel();
        for (var i = 0; i < 3; i++)
        {
            var c = Card($"g{i}");
            c.Complete(new ToolResultBlock($"g{i}", "ok", false, "回聲", null, "完成", 10, ToolApproval.NotRequired));
            run.Cards.Add(c);
        }

        var markdown = new MarkdownView
        {
            Services = new MarkdownServices(_ => Task.CompletedTask, _ => Task.CompletedTask),
            Markdown = "範例：\n\n```csharp\nConsole.WriteLine(1);\n```\n\n| 欄 | 值 |\n|---|---|\n| a | b |\n",
        };

        var panel = new StackPanel
        {
            Spacing = 16, Margin = new Thickness(16),
            Children =
            {
                new ToolCardView { DataContext = awaiting },
                new ToolCardView { DataContext = destructive },
                new ToolCardView { DataContext = done },
                new ToolRunView { DataContext = run },
                markdown,
            },
        };
        var window = new Window { Width = 700, Height = 1500, Content = new ScrollViewer { Content = panel } };
        window.Show();
        markdown.Flush();
        window.UpdateLayout();
        Windows.Add(("toolviews", window));
    }

    private void AddConfirmDialogs()
    {
        foreach (var (name, destructive) in new[] { ("confirm:normal", false), ("confirm:destructive", true) })
        {
            var dialog = new ConfirmDialog("要繼續嗎？", destructive ? "刪除" : "繼續", destructive);
            dialog.Show();
            dialog.UpdateLayout();
            Windows.Add((name, dialog));
        }
    }

    private void AddStartupFailure()
    {
        // M9 9C：啟動失敗說明視窗（不依賴 DI；這裡用固定的假報告）
        var report = new Lumi.App.Services.StartupFailureReport(
            Lumi.Infrastructure.Startup.StartupFailureKind.DatabaseLocked,
            Lumi.App.Services.StartupFailureReport.ReasonFor(Lumi.Infrastructure.Startup.StartupFailureKind.DatabaseLocked),
            "/tmp/lumi/logs", "Lumi 啟動失敗");
        var window = new StartupFailureWindow(report);
        window.Show();
        window.UpdateLayout();
        Windows.Add(("startup-failure", window));
    }

    private void AddSpriteWindow()
    {
        var window = new SpriteWindow(new SpriteCatalog([new OrbSpriteFactory()]), new NoNativeOverlay(), new FakeModelCatalog(),
            new SpriteShellOptions(), new FakeTimeProvider(), NullLogger<SpriteWindow>.Instance);
        window.Show();
        // 氣泡的「允許／拒絕」層級 class 是依是否破壞性綁定的：先送一個需要確認的事件，class 才會出現
        var bubble = window.FindControl<SpriteBubbleView>("Bubble")!;
        var bubbleVm = (SpriteBubbleViewModel)bubble.DataContext!;
        bubbleVm.OnRunStarted();
        bubbleVm.OnEvent(Ev.Approval("c1", "寫入檔案", "notes/a.md", ToolPermission.Write, detail: "notes/a.md"));
        window.UpdateLayout();
        Windows.Add(("sprite", window));
    }

    public void Dispose()
    {
        foreach (var (_, window) in Windows)
            window.Close();
        foreach (var owned in _owned)
            owned.Dispose();
    }
}
