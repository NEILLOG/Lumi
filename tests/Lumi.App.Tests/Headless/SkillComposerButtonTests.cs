using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Skills;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Headless;

/// <summary>M29 E1、5.7：輸入框工具列的「技能」按鈕（有可用技能才出現、選單列出名稱、選了插入文字並把焦點與游標放回輸入框）。</summary>
public class SkillComposerButtonTests
{
    private sealed class Rig
    {
        public WorkbenchViewModel Vm { get; }
        public WorkbenchWindow Window { get; }
        public ChatComposer Composer { get; }

        public Rig(ISkillStore skills)
        {
            var store = new InMemoryConversationStore();
            var contextStore = new InMemoryConversationContextStore();
            var time = new FakeTimeProvider();
            var builder = new FakeContextBuilder();
            var catalog = new FakeModelCatalog();
            var service = new ConversationService(new SequenceChatProvider(SequenceChatProvider.Answer("好")), store, catalog,
                new ToolRegistry([]), new ToolApprovalService(), new ToolPermissionMemory(), new ToolLoopOptions(), time,
                NullLogger<ConversationService>.Instance, contextStore, new EmptyFileScopeFactory(), builder);
            var bar = new ContextBarViewModel(contextStore, store, new FakeContextInspector(), builder, new HeuristicTokenEstimator(),
                new FakeFilePicker(), new FakeClipboardImage(), new FakeAttachmentStore(), time);
            Vm = new WorkbenchViewModel(service, store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
                new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), time, NullLogger<WorkbenchViewModel>.Instance,
                contextBar: bar, skills: skills);
            Window = new WorkbenchWindow(Vm) { Width = 1200 };
            Window.Show();
            Window.UpdateLayout();
            Composer = Window.FindByName<ChatComposer>("Composer");
        }

        public Button Button => Composer.FindByName<Button>("SkillButton");

        public TextBox Input => Composer.FindByName<TextBox>("InputBox");
    }

    private static SkillDraft Draft(string name, string description) => new(name, description, "步驟");

    [AvaloniaFact]
    public void WithoutSkills_TheButtonIsHidden()
    {
        var rig = new Rig(new InMemorySkillStore());
        Dispatcher.UIThread.RunJobs();
        rig.Window.UpdateLayout();

        Assert.False(rig.Button.IsEffectivelyVisible);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void WithASkill_TheButtonShowsUp_WithItsLabel()
    {
        var store = new InMemorySkillStore();
        store.Add(Draft("週報", "每週五寫週報"));
        var rig = new Rig(store);
        rig.Window.UpdateLayout();

        Assert.True(rig.Button.IsEffectivelyVisible);
        Assert.Equal(Strings.Skills_ComposerButton, AutomationProperties.GetName(rig.Button));
        Assert.Contains("collapsible", rig.Button.Classes);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void Menu_ListsTheSkillNames_WithTheDescriptionAsTooltip()
    {
        var store = new InMemorySkillStore();
        store.Add(Draft("週報", "每週五寫週報"));
        store.Add(Draft("會議記錄", "開會後整理"));
        var rig = new Rig(store);
        rig.Window.UpdateLayout();

        var menu = Assert.IsType<MenuFlyout>(rig.Button.Flyout);
        menu.ShowAt(rig.Button);

        var items = menu.Items.OfType<MenuItem>().ToList();
        Assert.Equal(["會議記錄", "週報"], items.Select(i => i.Header as string));
        Assert.Equal("開會後整理", ToolTip.GetTip(items[0]));
        menu.Hide();
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void PickingASkill_InsertsThePrefix_AndTheInputShowsIt()
    {
        var store = new InMemorySkillStore();
        store.Add(Draft("週報", "每週五寫週報"));
        var rig = new Rig(store);
        rig.Window.UpdateLayout();
        rig.Vm.InputText = "幫我看";

        var menu = Assert.IsType<MenuFlyout>(rig.Button.Flyout);
        menu.ShowAt(rig.Button);
        var item = menu.Items.OfType<MenuItem>().Single();
        item.Command!.Execute(item.CommandParameter);
        menu.Hide();
        rig.Window.UpdateLayout();

        var expected = string.Format(CultureInfo.CurrentCulture, Strings.Skills_ComposerInsertFormat, "週報") + "幫我看";
        Assert.Equal(expected, rig.Vm.InputText);
        Assert.Equal(expected, rig.Input.Text);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void PickingASkill_MovesTheCaretToTheEnd()
    {
        var store = new InMemorySkillStore();
        store.Add(Draft("週報", "每週五寫週報"));
        var rig = new Rig(store);
        rig.Window.UpdateLayout();
        rig.Vm.InputText = "幫我看";

        var menu = Assert.IsType<MenuFlyout>(rig.Button.Flyout);
        menu.ShowAt(rig.Button);
        var item = menu.Items.OfType<MenuItem>().Single();
        item.Command!.Execute(item.CommandParameter);
        menu.Hide();
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            rig.Window.UpdateLayout();
        }

        Assert.Equal(rig.Input.Text!.Length, rig.Input.CaretIndex);
        rig.Window.Close();
    }
}
