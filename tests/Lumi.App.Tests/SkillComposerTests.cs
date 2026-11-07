using System.Globalization;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Skills;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

/// <summary>M29 E1、5.7：輸入框的「技能」按鈕背後的 ViewModel（清單只列可用的技能、插入文字、技能庫變動時更新）。</summary>
public class SkillComposerTests
{
    private static string Prefix(string name) => string.Format(CultureInfo.CurrentCulture, Strings.Skills_ComposerInsertFormat, name);

    private static WorkbenchViewModel NewVm(ISkillStore? skills)
    {
        var store = new InMemoryConversationStore();
        var catalog = new FakeModelCatalog();
        var time = new FakeTimeProvider();
        var service = new ConversationService(new SequenceChatProvider(SequenceChatProvider.Answer("好")), store, catalog);
        return new WorkbenchViewModel(service, store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
            new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), time, NullLogger<WorkbenchViewModel>.Instance,
            skills: skills);
    }

    private static SkillDraft Draft(string name, string description = "說明") => new(name, description, "步驟");

    // ---- 清單 ----

    [Fact]
    public void WithoutASkillStore_HasNoSkills()
    {
        var vm = NewVm(null);

        Assert.False(vm.HasSkills);
        Assert.Empty(vm.SkillChoices);
    }

    [Fact]
    public void EmptyStore_HasNoSkills()
    {
        var vm = NewVm(new InMemorySkillStore());

        Assert.False(vm.HasSkills);
        Assert.Empty(vm.SkillChoices);
    }

    [Fact]
    public void OnlyUsableSkills_AreListed_SortedByName_WithTheirDescriptions()
    {
        var store = new InMemorySkillStore();
        store.Add(Draft("週報", "每週五寫週報"));
        store.Add(Draft("會議記錄", "開會後整理"));
        store.Add(Draft("停用的", "沒有開"), enabled: false);
        store.Add(Draft("壞掉的", "讀不進來"), problem: "格式錯誤");

        var vm = NewVm(store);

        Assert.True(vm.HasSkills);
        Assert.Equal(["會議記錄", "週報"], vm.SkillChoices.Select(c => c.Name));
        Assert.Equal(["開會後整理", "每週五寫週報"], vm.SkillChoices.Select(c => c.Description));
    }

    [Fact]
    public void StoreChanged_ReloadsTheList()
    {
        var store = new InMemorySkillStore();
        var vm = NewVm(store);
        Assert.False(vm.HasSkills);

        store.Add(Draft("週報"));
        Assert.Equal(["週報"], vm.SkillChoices.Select(c => c.Name));
        Assert.True(vm.HasSkills);

        store.Add(Draft("更早的"));
        Assert.Equal(["更早的", "週報"], vm.SkillChoices.Select(c => c.Name));
    }

    [Fact]
    public void StoreChanged_DropsASkillThatBecomesUnusable()
    {
        var store = new InMemorySkillStore();
        store.Add(Draft("週報"));
        var vm = NewVm(store);
        Assert.True(vm.HasSkills);

        store.Add(Draft("週報"), problem: "格式錯誤");   // 同名重新放進來，變成讀不進來

        Assert.False(vm.HasSkills);
        Assert.Empty(vm.SkillChoices);
    }

    // ---- 插入 ----

    [Fact]
    public void InsertSkill_PutsThePrefixAtTheStartOfTheInput()
    {
        var vm = NewVm(new InMemorySkillStore());
        vm.InputText = "幫我看這段";

        vm.InsertSkillCommand.Execute("週報");

        Assert.Equal(Prefix("週報") + "幫我看這段", vm.InputText);
    }

    [Fact]
    public void InsertSkill_OnAnEmptyInput_IsJustThePrefix()
    {
        var vm = NewVm(new InMemorySkillStore());

        vm.InsertSkillCommand.Execute("週報");

        Assert.Equal("請用「週報」技能：", vm.InputText);
    }

    [Fact]
    public void InsertSkill_WhenTheStartAlreadyIsTheSameSentence_DoesNotDuplicateIt()
    {
        var vm = NewVm(new InMemorySkillStore());

        vm.InsertSkillCommand.Execute("週報");
        vm.InsertSkillCommand.Execute("週報");

        Assert.Equal("請用「週報」技能：", vm.InputText);
    }

    [Fact]
    public void InsertSkill_WithAnotherSkill_PutsItInFrontOfTheExistingPrefix()
    {
        var vm = NewVm(new InMemorySkillStore());

        vm.InsertSkillCommand.Execute("週報");
        vm.InsertSkillCommand.Execute("會議記錄");

        Assert.Equal(Prefix("會議記錄") + Prefix("週報"), vm.InputText);
    }

    [Fact]
    public void InsertSkill_RequestsTheCaretAtTheEnd()
    {
        var vm = NewVm(new InMemorySkillStore());
        var requested = 0;
        vm.FocusInputAtEndRequested += (_, _) => requested++;

        vm.InsertSkillCommand.Execute("週報");

        Assert.Equal(1, requested);
    }

    [Fact]
    public void InsertSkill_WithoutAName_DoesNothing()
    {
        var vm = NewVm(new InMemorySkillStore());
        vm.InputText = "原本";

        vm.InsertSkillCommand.Execute(null);
        vm.InsertSkillCommand.Execute("  ");

        Assert.Equal("原本", vm.InputText);
    }
}
