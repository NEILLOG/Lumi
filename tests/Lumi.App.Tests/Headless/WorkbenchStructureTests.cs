using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Headless;

/// <summary>UB-1：工作台拆成 ChatToolbar／ConversationView／ChatComposer 之後，測試依賴的 x:Name 都還在視覺樹裡。</summary>
public class WorkbenchStructureTests
{
    private static (WorkbenchWindow Window, WorkbenchViewModel Vm) ShowWithVm()
    {
        var store = new InMemoryConversationStore();
        var catalog = new FakeModelCatalog();
        var service = new ConversationService(new SequenceChatProvider(SequenceChatProvider.Answer("好")), store, catalog);
        var vm = new WorkbenchViewModel(service, store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
            new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
            NullLogger<WorkbenchViewModel>.Instance);
        var window = new WorkbenchWindow(vm);
        window.Show();
        window.UpdateLayout();
        return (window, vm);
    }

    private static WorkbenchWindow Show() => ShowWithVm().Window;

    [AvaloniaFact]
    public void TheStoppedRow_IsShownOnlyAfterStoppingWithoutAReply_AndItsButtonRetries()
    {
        // FX-07：停止列是一般的淡色文字加「重試」，不是錯誤列
        var (window, vm) = ShowWithVm();
        var row = window.FindByName<Grid>("StoppedRow");
        var retry = window.FindByName<Button>("StoppedRetryButton");
        Assert.False(row.IsEffectivelyVisible);

        vm.IsStoppedWithoutReply = true;
        window.UpdateLayout();

        Assert.True(row.IsEffectivelyVisible);
        Assert.Contains(Strings.Workbench_Stopped, row.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Select(t => t.Text));
        Assert.Same(vm.RetryCommand, retry.Command);
        Assert.Equal(Strings.Workbench_Retry, retry.Content);
        Assert.Contains("secondary", retry.Classes);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheMessageActions_AreBoundToTheWorkbenchCommands()
    {
        var (window, vm) = ShowWithVm();
        await vm.InitializeAsync();
        vm.InputText = "嗨";
        await vm.SendCommand.ExecuteAsync(null);
        window.UpdateLayout();

        var conversation = window.FindByName<ConversationView>("Conversation");
        var buttons = conversation.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("small")).ToList();
        Assert.Contains(buttons, b => ReferenceEquals(b.Command, vm.CopyCommand));
        Assert.Contains(buttons, b => ReferenceEquals(b.Command, vm.RegenerateCommand));
        window.Close();
    }

    [AvaloniaFact]
    public void TheWorkbench_IsMadeOfTheFourViews()
    {
        var window = Show();

        Assert.NotNull(window.FindByName<CustomTitleBar>("TitleBar"));
        Assert.NotNull(window.FindByName<ChatToolbar>("Toolbar"));
        Assert.NotNull(window.FindByName<ConversationView>("Conversation"));
        Assert.NotNull(window.FindByName<ChatComposer>("Composer"));
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("SendButton", typeof(Button), "Composer")]
    [InlineData("StopButton", typeof(Button), "Composer")]
    [InlineData("InputBox", typeof(TextBox), "Composer")]
    [InlineData("InputArea", typeof(Border), "Composer")]
    [InlineData("ContextBar", typeof(StackPanel), "Composer")]
    [InlineData("ContextChips", typeof(ItemsControl), "Composer")]
    [InlineData("AddFileButton", typeof(Button), "Composer")]
    [InlineData("AddFolderButton", typeof(Button), "Composer")]
    [InlineData("SpriteModeButton", typeof(Button), "TitleBar")]
    [InlineData("CapsuleButton", typeof(Button), "Toolbar")]
    [InlineData("MessageScroller", typeof(ScrollViewer), "Conversation")]
    [InlineData("RetryOtherModelButton", typeof(Button), "Conversation")]
    [InlineData("ViewOnlyBanner", typeof(Border), null)]
    public void EveryReservedName_IsFoundInTheWorkbenchVisualTree(string name, Type type, string? owner)
    {
        var window = Show();

        var matches = window.GetVisualDescendants().OfType<Control>().Where(c => c.Name == name).ToList();
        // Send／Stop 在回應中互相切換顯示，但兩個都在視覺樹裡；RetryOtherModelButton 在錯誤列裡，也一直在視覺樹裡（只是隱藏）
        var found = Assert.Single(matches);
        Assert.IsAssignableFrom(type, found);
        if (owner is not null)
            Assert.Contains(window.FindByName<Control>(owner).GetVisualDescendants(), v => ReferenceEquals(v, found));
        window.Close();
    }
}
