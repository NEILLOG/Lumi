using Avalonia.Controls;
using TextBlock = Avalonia.Controls.TextBlock;
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

/// <summary>
/// 用量移出模型選單（ui-usage-and-topbar.md 第 5 節）：底部狀態列與模型選單的「本對話」區塊都已移除，
/// 用量改由工具列的用量圓環與面板提供（見 <see cref="UsageFlyoutTests"/>）。
/// </summary>
public class ModelPickerSessionTests
{
    private sealed class Rig
    {
        public ConnectionStatusCache Status { get; } = new();
        public WorkbenchViewModel Vm { get; }
        public WorkbenchWindow Window { get; }

        public Rig()
        {
            var store = new InMemoryConversationStore();
            var catalog = new FakeModelCatalog();
            var service = new ConversationService(new SequenceChatProvider(SequenceChatProvider.Answer("好")), store, catalog);
            Vm = new WorkbenchViewModel(service, store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
                Status, new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
                NullLogger<WorkbenchViewModel>.Instance);
            Window = new WorkbenchWindow(Vm);
            Window.Show();
            Window.UpdateLayout();
        }

        /// <summary>把 ModelPicker 放進視窗渲染（彈出選單的內容在測試裡不會自己出現，直接承載同一個控制項）。</summary>
        public static (ModelPicker Picker, Window Host) Host(ModelSwitcherViewModel switcher)
        {
            var picker = new ModelPicker { DataContext = switcher };
            var host = new Window { Content = picker, Width = 400, Height = 600 };
            host.Show();
            host.UpdateLayout();
            return (picker, host);
        }
    }

    [AvaloniaFact]
    public void TheWorkbenchWindow_NoLongerHasAStatusBar()
    {
        var rig = new Rig();

        var texts = rig.Window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.DoesNotContain(texts, t => t is not null && t.StartsWith("輸入 ") && t.Contains("回合"));
        Assert.Empty(rig.Window.GetVisualDescendants().OfType<ProgressBar>());
        // 工具列、訊息流、輸入區三個區塊之外沒有別的底部列：主 Grid 只剩標題列、工具列、內容三列
        Assert.Equal(3, rig.Window.GetVisualDescendants().OfType<Grid>().First(g => g.RowDefinitions.Count == 3).RowDefinitions.Count);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task TheModelPicker_HasNoUsageBlock_OnlySearchAndModels()
    {
        var rig = new Rig();
        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = "問";
        await rig.Vm.SendCommand.ExecuteAsync(null);
        var (picker, host) = Rig.Host(rig.Vm.ModelSwitcher);

        Assert.DoesNotContain(picker.GetVisualDescendants().OfType<Control>(), c => c.Name == "SessionSection");
        var texts = picker.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.DoesNotContain(Strings.ModelPicker_Session, texts);
        Assert.DoesNotContain(texts, t => t is not null && t.StartsWith("輸入 "));
        Assert.Empty(picker.GetVisualDescendants().OfType<ProgressBar>());
        Assert.NotNull(picker.FindByName<TextBox>("SearchBox"));
        Assert.NotEmpty(picker.FindByName<ListBox>("EntryList").Items);
        Assert.Equal(320, picker.Width);
        host.Close();
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void TheRetrySwitchersPicker_StillListsModels_AndSearch()
    {
        var rig = new Rig();
        var (picker, host) = Rig.Host(rig.Vm.RetrySwitcher);

        Assert.NotEmpty(picker.FindByName<ListBox>("EntryList").Items);
        Assert.NotNull(picker.FindByName<TextBox>("SearchBox"));
        host.Close();
        rig.Window.Close();
    }
}
