using Avalonia;
using Avalonia.Controls;
using TextBlock = Avalonia.Controls.TextBlock;
using Avalonia.Headless.XUnit;
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

namespace Lumi.App.Tests.Headless;

/// <summary>UB-2：工具列重做——secondary 按鈕加圖示、模型膠囊「供應商 · model ID」、「…」選單、回應中停用的 Tooltip。</summary>
public class ChatToolbarTests
{
    private sealed class Rig
    {
        public FakeSettingsWindowService SettingsWindow { get; } = new();
        public WorkbenchViewModel Vm { get; }
        public WorkbenchWindow Window { get; }
        public ChatToolbar Toolbar { get; }
        public FakeModelCatalog Catalog { get; } = new();

        public Rig(SequenceChatProvider? provider = null)
        {
            provider ??= new SequenceChatProvider(SequenceChatProvider.Answer("好"));
            var store = new InMemoryConversationStore();
            var catalog = Catalog;
            var service = new ConversationService(provider, store, catalog);
            Vm = new WorkbenchViewModel(service, store, catalog, new FakeApiKeyPrompt(false), SettingsWindow,
                new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
                NullLogger<WorkbenchViewModel>.Instance);
            Window = new WorkbenchWindow(Vm);
            Window.Show();
            Window.UpdateLayout();
            Toolbar = Window.FindByName<ChatToolbar>("Toolbar");
        }
    }

    [AvaloniaFact]
    public void NewConversationAndHistory_AreSecondaryButtons_WithIconTextTooltipAndAccessibleName()
    {
        var rig = new Rig();
        var buttons = new[]
        {
            (rig.Toolbar.NewConversationButton, Strings.Workbench_NewConversation, rig.Vm.NewConversationCommand),
            (rig.Toolbar.HistoryButton, Strings.Workbench_History, (System.Windows.Input.ICommand)rig.Vm.History.ToggleCommand),
        };

        foreach (var (button, label, command) in buttons)
        {
            Assert.Contains("secondary", button.Classes);
            Assert.Same(command, button.Command);
            Assert.Equal(label, ToolTip.GetTip(button));
            Assert.Equal(label, Avalonia.Automation.AutomationProperties.GetName(button));
            Assert.Contains(button.GetVisualDescendants().OfType<LumiIcon>(), i => i.Data is not null);
            Assert.Contains(button.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == label);
        }
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void TheOldStandaloneSettingsButton_IsGone()
    {
        var rig = new Rig();

        var settingsButtons = rig.Toolbar.GetVisualDescendants().OfType<Button>().Where(b => Equals(b.Content, Strings.Workbench_Settings));
        Assert.Empty(settingsButtons);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void TheCapsule_ShowsProviderAndExactModelId_WithAnIconChevron()
    {
        var rig = new Rig();
        var capsule = rig.Toolbar.CapsuleButton;
        var texts = capsule.GetVisualDescendants().OfType<TextBlock>().ToList();

        // Wide／Compact 顯示完整「供應商 · model ID」的那一個（Narrow 專用的 model ID 文字與字首方塊在 Wide 隱藏）
        var main = Assert.Single(texts, t => t.IsEffectivelyVisible && t.Text == rig.Vm.ModelSwitcher.CapsuleText);
        Assert.Equal(rig.Vm.ModelSwitcher.CapsuleText, main.Text);
        Assert.Contains(rig.Vm.ModelSwitcher.ModelId, main.Text);
        Assert.DoesNotContain(rig.Vm.ModelSwitcher.CurrentName, main.Text!);   // 顯示名稱不是真實模型名稱
        Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, main.TextTrimming);
        Assert.DoesNotContain(texts, t => t.Text == "▾");
        Assert.Contains(capsule.GetVisualDescendants().OfType<LumiIcon>(), i => i.Data is not null);
        Assert.True(capsule.MaxWidth <= 360);
        Assert.NotNull(capsule.Flyout);
        Assert.Equal(rig.Vm.ModelSwitcher.CapsuleText, Avalonia.Automation.AutomationProperties.GetName(capsule));
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void ALongModelId_IsEllipsized_AndDoesNotPushTheOtherButtonsOut()
    {
        var rig = new Rig();
        var longModel = FakeModelCatalog.Model with { ModelId = "provider/" + new string('x', 200) };
        rig.Catalog.Current = rig.Catalog.Current with { Models = [longModel] };
        rig.Catalog.Raise();
        rig.Window.UpdateLayout();

        var capsule = rig.Toolbar.CapsuleButton;
        var more = rig.Toolbar.MoreButton;
        Assert.True(capsule.Bounds.Width <= 360 + 0.5);
        Assert.True(rig.Toolbar.HistoryButton.Bounds.Right <= ((Visual)capsule).TranslatePoint(default, rig.Toolbar)!.Value.X + 0.5);
        Assert.True(((Visual)more).TranslatePoint(default, rig.Toolbar)!.Value.X + more.Bounds.Width <= rig.Toolbar.Bounds.Width + 0.5);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task WhileResponding_TheCapsuleIsDisabled_NoHintTextTakesSpace_AndTheTooltipExplains()
    {
        var rig = new Rig(new SequenceChatProvider(SequenceChatProvider.Answer("好")) { HangOnCall = 1 });
        await rig.Vm.InitializeAsync();
        var capsule = rig.Toolbar.CapsuleButton;
        Assert.True(capsule.IsEffectivelyEnabled);
        Assert.Equal(rig.Vm.ModelSwitcher.CapsuleTooltip, ToolTip.GetTip(capsule));

        rig.Vm.InputText = "x";
        var sending = rig.Vm.SendCommand.ExecuteAsync(null);
        await SettingsHost.WaitUntil(() => rig.Vm.IsResponding);

        Assert.False(capsule.IsEffectivelyEnabled);
        Assert.Equal(Strings.Workbench_SwitchBlocked, ToolTip.GetTip(capsule));
        Assert.True(ToolTip.GetShowOnDisabled(capsule));
        Assert.DoesNotContain(rig.Toolbar.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Strings.Workbench_SwitchBlocked);

        await rig.Vm.StopAndWaitAsync();
        await sending;
        Assert.True(capsule.IsEffectivelyEnabled);
        Assert.Equal(rig.Vm.ModelSwitcher.CapsuleTooltip, ToolTip.GetTip(capsule));
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void TheMoreButton_IsATertiaryIconButton_WithAMenuOfSettingsAndManage()
    {
        var rig = new Rig();
        var more = rig.Toolbar.MoreButton;

        Assert.Contains("tertiary", more.Classes);
        Assert.Equal(Strings.Workbench_More, ToolTip.GetTip(more));
        Assert.Equal(Strings.Workbench_More, Avalonia.Automation.AutomationProperties.GetName(more));
        Assert.Contains(more.GetVisualDescendants().OfType<LumiIcon>(), i => i.Data is not null);
        var menu = Assert.IsType<MenuFlyout>(more.Flyout);
        var items = menu.Items.OfType<MenuItem>().ToList();
        Assert.Equal([Strings.Workbench_Settings, Strings.ModelPicker_Manage], items.Select(i => i.Header));
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void TheMenuItems_OpenTheSettingsWindow_AtTheRightPlace()
    {
        var rig = new Rig();
        var menu = (MenuFlyout)rig.Toolbar.MoreButton.Flyout!;
        menu.ShowAt(rig.Toolbar.MoreButton);
        rig.Window.UpdateLayout();
        var items = menu.Items.OfType<MenuItem>().ToList();

        Assert.All(items, i => Assert.NotNull(i.Command));
        items[0].Command!.Execute(null);
        items[1].Command!.Execute(null);

        Assert.Equal([SettingsSection.Connections, SettingsSection.Models], rig.SettingsWindow.Shown.Select(s => s.Section));
        menu.Hide();
        rig.Window.Close();
    }
}
