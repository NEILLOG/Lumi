using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lumi.App.Controls;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.ContextNotes;
using Microsoft.Extensions.Logging.Abstractions;
using AvaloniaTextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Headless;

/// <summary>M35 35J：啟動研究本的對話框（目標、從哪裡開始、費用說明），以及工具列的「研究本」按鈕在 Compact 寬度只剩圖示。</summary>
public class ContextNoteStartDialogTests
{
    private static ContextNoteStartDialog Dialog(string goal = "", int messages = 3, string? model = "測試 · test-model")
    {
        var dialog = new ContextNoteStartDialog(goal, messages, model);
        dialog.Show();
        dialog.UpdateLayout();
        return dialog;
    }

    [AvaloniaFact]
    public void EmptyGoal_DisablesStart_AndTypingEnablesIt()
    {
        var dialog = Dialog();
        var start = dialog.FindControl<Button>("StartButton")!;
        var goal = dialog.FindControl<TextBox>("GoalBox")!;

        Assert.False(start.IsEnabled);
        goal.Text = "比較兩個方案";
        Assert.True(start.IsEnabled);
        goal.Text = "   ";
        Assert.False(start.IsEnabled);
        dialog.Close();
    }

    [AvaloniaFact]
    public void SuggestedGoal_IsPrefilled_AndEnablesStart()
    {
        var dialog = Dialog(goal: "  要不要換資料庫  ");

        Assert.Equal("要不要換資料庫", dialog.FindControl<TextBox>("GoalBox")!.Text);
        Assert.True(dialog.FindControl<Button>("StartButton")!.IsEnabled);
        dialog.Close();
    }

    [AvaloniaFact]
    public void WithoutMessages_FirstCardIsDisabled_AndBlankIsSelected()
    {
        var dialog = Dialog(messages: 0);
        var fromConversation = dialog.FindControl<RadioButton>("FromConversationCard")!;
        var blank = dialog.FindControl<RadioButton>("BlankCard")!;

        Assert.False(fromConversation.IsEnabled);
        Assert.True(blank.IsChecked);
        Assert.False(fromConversation.IsChecked);
        Assert.Equal(Strings.ContextNote_Start_FromConversationEmpty, dialog.FindControl<AvaloniaTextBlock>("FromConversationDesc")!.Text);
        dialog.Close();
    }

    [AvaloniaFact]
    public void WithMessages_FirstCardIsChecked_AndSaysHowManyMessages()
    {
        var dialog = Dialog(messages: 12);
        var fromConversation = dialog.FindControl<RadioButton>("FromConversationCard")!;

        Assert.True(fromConversation.IsEnabled);
        Assert.True(fromConversation.IsChecked);
        Assert.Equal(string.Format(Strings.ContextNote_Start_FromConversationDescFormat, 12),
            dialog.FindControl<AvaloniaTextBlock>("FromConversationDesc")!.Text);
        dialog.Close();
    }

    [AvaloniaFact]
    public void CostNote_NamesTheModel_OrSaysThereIsNoModel()
    {
        var withModel = Dialog(model: "測試 · test-model");
        Assert.Equal(string.Format(Strings.ContextNote_Start_CostNoteFormat, "測試 · test-model"),
            withModel.FindControl<AvaloniaTextBlock>("CostNote")!.Text);
        withModel.Close();

        var noModel = Dialog(model: null);
        Assert.Equal(Strings.ContextNote_Start_NoModel, noModel.FindControl<AvaloniaTextBlock>("CostNote")!.Text);
        noModel.Close();
    }

    // ---- 工具列的按鈕 ----

    private sealed record ToolbarRig(WorkbenchViewModel Vm, WorkbenchWindow Window, FakeContextNoteService Notes, string ConversationId, FakeChatSession Session);

    /// <summary>視窗打開時會自己初始化工作台（<c>WorkbenchWindow</c>），所以研究本要在打開之前就存在。</summary>
    private static ToolbarRig Toolbar(double width, bool withNote = false)
    {
        var store = new InMemoryConversationStore();
        var conversation = store.CreateAsync("測試").GetAwaiter().GetResult();
        var session = new FakeChatSession { ConversationId = conversation.Id };
        var notes = new FakeContextNoteService();
        if (withNote)
            notes.StartAsync(conversation.Id, "目標", ContextNoteStartMode.Blank).GetAwaiter().GetResult();
        var vm = new WorkbenchViewModel(null!, store, new FakeModelCatalog(), new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
            new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
            NullLogger<WorkbenchViewModel>.Instance, session: session, attachments: new FakeAttachmentStore(),
            contextNotes: notes, contextNoteStore: new InMemoryContextNoteStore(), contextNoteWindow: new FakeContextNoteWindowService(),
            contextNoteStart: new FakeContextNoteStartPrompt());
        var window = new WorkbenchWindow(vm) { Width = width, Height = 650 };
        window.Show();
        window.UpdateLayout();
        return new ToolbarRig(vm, window, notes, conversation.Id, session);
    }

    private static IEnumerable<string> VisibleTexts(Visual root) =>
        root.GetVisualDescendants().OfType<AvaloniaTextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "");

    [AvaloniaTheory]
    [InlineData(1200, true)]
    [InlineData(900, false)]
    [InlineData(620, false)]
    public async Task ContextNoteButton_IsIconOnly_FromCompact_AndHasTipAndName(double width, bool showsText)
    {
        var rig = Toolbar(width);
        await rig.Vm.InitializeAsync();
        rig.Window.UpdateLayout();
        var button = rig.Window.FindByName<ChatToolbar>("Toolbar")!.ContextNoteButton;

        Assert.True(button.IsVisible);
        Assert.Equal(showsText, VisibleTexts(button).Contains(Strings.ContextNote_Button));
        Assert.Contains(button.GetVisualDescendants().OfType<LumiIcon>(), i => i.IsEffectivelyVisible);
        Assert.Equal(Strings.ContextNote_ButtonTipStart, ToolTip.GetTip(button));
        Assert.Equal(Strings.ContextNote_Button, AutomationProperties.GetName(button));
        if (!showsText)
            Assert.True(button.Bounds.Width <= 32.5);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task ContextNoteButton_IsOn_WhenThereIsANote()
    {
        var rig = Toolbar(1200, withNote: true);
        rig.Window.UpdateLayout();
        var button = rig.Window.FindByName<ChatToolbar>("Toolbar")!.ContextNoteButton;

        Assert.True(rig.Vm.HasContextNote);
        Assert.Contains("on", button.Classes);
        Assert.Equal(Strings.ContextNote_ButtonTipOpen, ToolTip.GetTip(button));
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task ContextNoteButton_IsDisabled_WhileRespondingWithoutANote()
    {
        var rig = Toolbar(1200);
        await rig.Vm.InitializeAsync();
        rig.Window.UpdateLayout();
        var button = rig.Window.FindByName<ChatToolbar>("Toolbar")!.ContextNoteButton;

        rig.Session.StartRun();
        rig.Window.UpdateLayout();
        Assert.False(button.IsEnabled);   // 回應中、還沒有研究本：停用（C8）

        rig.Session.FinishRun();
        rig.Window.UpdateLayout();
        Assert.True(button.IsEnabled);
        rig.Window.Close();
    }
}
