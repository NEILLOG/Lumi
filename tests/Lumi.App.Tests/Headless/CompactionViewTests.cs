using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Lumi.App.Controls;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Headless;

/// <summary>M22 22I 的畫面：摘要分隔線（收合與展開）、整理中提示、用量面板的「整理並繼續」。</summary>
public class CompactionViewTests
{
    private sealed class Rig
    {
        public FakeModelCatalog Catalog { get; } = new();
        public InMemoryConversationStore Store { get; }
        public WorkbenchViewModel Vm { get; }
        public WorkbenchWindow Window { get; }

        public Rig(IChatProvider provider, InMemoryConversationStore store)
        {
            Store = store;
            var service = new ConversationService(provider, store, Catalog);
            Vm = new WorkbenchViewModel(service, store, Catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
                new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
                NullLogger<WorkbenchViewModel>.Instance, tester: new FakeConnectionTester());
            Window = new WorkbenchWindow(Vm);
            Window.Show();
            Window.UpdateLayout();
        }
    }

    /// <summary>三輪的舊對話；有摘要時分隔線會出現在第 2 則之後（沒有摘要就還能再整理）。</summary>
    private static async Task<InMemoryConversationStore> SeededAsync(string? summary, int turns = 3)
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("舊對話");
        for (var i = 0; i < turns; i++)
        {
            await store.AppendMessageAsync(conversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new Lumi.Core.Chat.TextBlock($"問{i}")], DateTimeOffset.UtcNow));
            await store.AppendMessageAsync(conversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [new Lumi.Core.Chat.TextBlock($"答{i}")], DateTimeOffset.UtcNow));
        }
        if (summary is not null)
            await store.AddCompactionAsync(conversation.Id, new CompactionRecord(CompactionRecord.NewId(), CompactionKind.Summary, 2, summary, "m", DateTimeOffset.UtcNow));
        return store;
    }

    [AvaloniaFact]
    public async Task TheDivider_IsFoldedByDefault_AndExpandsToTheWholeSummary()
    {
        var rig = new Rig(new SequenceChatProvider(), await SeededAsync("第一段摘要\n第二段摘要"));
        await rig.Vm.InitializeAsync();
        rig.Window.UpdateLayout();

        var divider = rig.Window.GetVisualDescendants().OfType<Expander>()
            .Single(e => AutomationProperties.GetName(e) == Strings.Compact_Divider);
        Assert.False(divider.IsExpanded);
        Assert.DoesNotContain(rig.Window.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.Text == "第一段摘要\n第二段摘要");   // 收合：內容不在畫面上
        Assert.Contains(rig.Window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Strings.Compact_ShowSummary && t.IsEffectivelyVisible);

        divider.IsExpanded = true;
        rig.Window.UpdateLayout();

        var summary = rig.Window.GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => t.Text == "第一段摘要\n第二段摘要");
        Assert.True(summary.IsEffectivelyVisible);
        Assert.Contains(rig.Window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Strings.Compact_HideSummary && t.IsEffectivelyVisible);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task WhileCompacting_TheProgressLineShows_AndHidesWhenItStops()
    {
        var store = await SeededAsync(summary: null);
        var rig = new Rig(new SequenceChatProvider(new ChatEvent[] { }) { HangOnCall = 1 }, store);
        await rig.Vm.InitializeAsync();
        rig.Window.UpdateLayout();
        Assert.False(FindProgress(rig).IsEffectivelyVisible);

        var compacting = rig.Vm.CompactAndContinueCommand.ExecuteAsync(null);
        await SettingsHost.WaitUntil(() => rig.Vm.IsCompacting);
        rig.Window.UpdateLayout();

        Assert.True(FindProgress(rig).IsEffectivelyVisible);

        await rig.Vm.StopAndWaitAsync();
        await compacting;
        rig.Window.UpdateLayout();

        Assert.False(FindProgress(rig).IsEffectivelyVisible);
        rig.Window.Close();
    }

    private static TextBlock FindProgress(Rig rig) =>
        rig.Window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == Strings.Compact_InProgress);

    [AvaloniaFact]
    public async Task TheDangerReminder_PutsCompactAndContinueFirstAsThePrimaryButton()
    {
        // 空對話：舊的歷史本身就超過小視窗的門檻，會先自動整理（那是另一組測試的事）
        var store = await SeededAsync(summary: null, turns: 0);
        var rig = new Rig(new SequenceChatProvider(SequenceChatProvider.Answer("好")), store);
        await rig.Vm.InitializeAsync();
        rig.Catalog.Current = rig.Catalog.Current with { Models = [FakeModelCatalog.Model with { ContextWindow = 30 }] };
        rig.Catalog.Raise();
        rig.Vm.InputText = "問";
        await rig.Vm.SendCommand.ExecuteAsync(null);   // 用量 20 + 8 = 28 of 30：快滿
        Assert.Equal(UsageLevel.Danger, rig.Vm.Usage.Level);

        var toolbar = rig.Window.FindByName<ChatToolbar>("Toolbar");
        toolbar.UsageButton.Focus();
        rig.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        rig.Window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        rig.Window.UpdateLayout();
        var panel = (UsageFlyout)((Flyout)toolbar.UsageButton.Flyout!).Content!;

        var compact = panel.FindByName<Button>("CompactAndContinueInFlyout");
        var newConversation = panel.FindByName<Button>("NewConversationInFlyout");
        Assert.Equal(Strings.Usage_CompactAndContinue, compact.Content);
        Assert.Equal(Strings.Usage_CompactAndContinue, AutomationProperties.GetName(compact));
        Assert.Contains("primary", compact.Classes);
        Assert.Contains("secondary", newConversation.Classes);
        Assert.True(compact.IsEffectivelyEnabled);
        var row = (StackPanel)compact.Parent!;
        Assert.True(row.Children.IndexOf(compact) < row.Children.IndexOf(newConversation));   // 在「開新對話」左邊
        ((Flyout)toolbar.UsageButton.Flyout!).Hide();
        rig.Window.Close();
    }
}
