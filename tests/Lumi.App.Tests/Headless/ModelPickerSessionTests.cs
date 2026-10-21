using Avalonia.Controls;
using TextBlock = Avalonia.Controls.TextBlock;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Headless;

/// <summary>UB-5：底部狀態列移除，用量、上下文進度條、連線狀態文字與費用說明移到模型選單（ModelPicker）頂部的「本對話」區塊。</summary>
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
    public async Task ThePicker_ShowsTheSessionBlock_UsageProgressConnectionTextAndCostNote()
    {
        var rig = new Rig();
        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = "問";
        await rig.Vm.SendCommand.ExecuteAsync(null);
        var (picker, host) = Rig.Host(rig.Vm.ModelSwitcher);

        var section = picker.FindByName<StackPanel>("SessionSection");
        Assert.True(section.IsVisible);
        var texts = section.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains(Strings.ModelPicker_Session, texts);
        Assert.Equal(rig.Vm.StatusText, picker.FindByName<TextBlock>("UsageLine").Text);
        Assert.StartsWith("輸入 ", rig.Vm.StatusText);
        Assert.Contains("回合 1", rig.Vm.StatusText);
        Assert.DoesNotContain("上下文", rig.Vm.StatusText);

        var progress = picker.FindByName<ProgressBar>("ContextProgress");
        Assert.Equal(0, progress.Minimum);
        Assert.Equal(rig.Vm.ContextWindow, progress.Maximum);
        Assert.Equal(rig.Vm.ContextUsed, progress.Value);
        Assert.Equal("上下文 " + rig.Vm.ContextText, picker.FindByName<TextBlock>("ContextLine").Text);

        Assert.Equal(Strings.Workbench_StatusConnected, picker.FindByName<TextBlock>("ConnectionText").Text);
        Assert.Equal("實際費用由供應商依模型與 token 用量計算，費用由你的帳號支付。", picker.FindByName<TextBlock>("CostNote").Text);
        Assert.Contains(section.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("divider"));
        host.Close();
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task TheConnectionLine_HasAVisibleDotAndTextForEachStatus()
    {
        var rig = new Rig();
        var (picker, host) = Rig.Host(rig.Vm.ModelSwitcher);
        var line = picker.FindByName<StackPanel>("ConnectionLine");
        var connectionId = FakeModelCatalog.Connection.Id;

        string VisibleDotFill() => ((ISolidColorBrush)line.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>()
            .Single(e => e.IsVisible).Fill!).Color.ToString();
        string Dot(string key)
        {
            Assert.True(Avalonia.Application.Current!.TryGetResource(key, host.ActualThemeVariant, out var value), $"找不到 token {key}");
            return ((ISolidColorBrush)value!).Color.ToString();
        }

        Assert.Equal(Strings.Workbench_StatusUnknown, picker.FindByName<TextBlock>("ConnectionText").Text);
        Assert.Equal(Dot("LumiWarningDotBrush"), VisibleDotFill());

        rig.Status.Mark(connectionId, ConnectionStatus.Ok);
        host.UpdateLayout();
        Assert.Equal(Strings.Workbench_StatusConnected, picker.FindByName<TextBlock>("ConnectionText").Text);
        Assert.Equal(Dot("LumiSuccessDotBrush"), VisibleDotFill());

        rig.Status.Mark(connectionId, ConnectionStatus.Failed);
        host.UpdateLayout();
        Assert.Equal(Strings.Workbench_StatusFailed, picker.FindByName<TextBlock>("ConnectionText").Text);
        Assert.Equal(Dot("LumiDangerDotBrush"), VisibleDotFill());
        host.Close();
        rig.Window.Close();
        await Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task TheSessionBlock_FollowsNewUsage()
    {
        var rig = new Rig();
        await rig.Vm.InitializeAsync();
        var (picker, host) = Rig.Host(rig.Vm.ModelSwitcher);
        Assert.Contains("回合 0", picker.FindByName<TextBlock>("UsageLine").Text!);

        rig.Vm.InputText = "問";
        await rig.Vm.SendCommand.ExecuteAsync(null);
        host.UpdateLayout();

        Assert.Contains("回合 1", picker.FindByName<TextBlock>("UsageLine").Text!);
        Assert.True(picker.FindByName<ProgressBar>("ContextProgress").Value > 0);
        host.Close();
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void TheRetrySwitchersPicker_HidesTheSessionBlock_ButStillListsModels()
    {
        var rig = new Rig();
        var (picker, host) = Rig.Host(rig.Vm.RetrySwitcher);

        Assert.False(rig.Vm.RetrySwitcher.HasSession);
        Assert.False(picker.FindByName<StackPanel>("SessionSection").IsVisible);
        Assert.NotEmpty(picker.FindByName<ListBox>("EntryList").Items);
        Assert.NotNull(picker.FindByName<TextBox>("SearchBox"));
        host.Close();
        rig.Window.Close();
    }
}
