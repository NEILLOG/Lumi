using Lumi.Core.Skills;
using Lumi.Core.Tests.TestSupport;

namespace Lumi.Core.Tests.Skills;

/// <summary>M29 Phase 0 的記憶體版技能儲存；FileSkillStore 的行為以它為準。</summary>
public sealed class InMemorySkillStoreTests
{
    private static InMemorySkillStore NewStore(FakeModelCatalog? catalog = null) => new(catalog);

    private static SkillDraft Draft(string name, string body = "步驟", string? original = null) =>
        new(name, "什麼時候用", body, original);

    [Fact]
    public async Task Save_NewSkill_IsListedAndReadBack()
    {
        var store = NewStore();

        var result = await store.SaveAsync(Draft("週報", "1. 整理"), default);

        Assert.Equal(SkillSaveOutcome.Saved, result.Outcome);
        var list = await store.ListAsync(default);
        Assert.Equal(["週報"], list.Select(s => s.Name));
        var doc = await store.GetAsync("週報", default);
        Assert.Equal("1. 整理", doc!.Body);
    }

    [Fact]
    public async Task Save_InvalidDraft_IsRejectedWithTheRulesMessage()
    {
        var result = await NewStore().SaveAsync(Draft("CON"), default);

        Assert.Equal(SkillSaveOutcome.Invalid, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Equal(SkillRules.ValidateName("CON"), result.Message);
    }

    [Fact]
    public async Task Save_NameTaken_IgnoresCase()
    {
        var store = NewStore();
        await store.SaveAsync(Draft("Weekly"), default);

        var result = await store.SaveAsync(Draft("weekly"), default);

        Assert.Equal(SkillSaveOutcome.NameTaken, result.Outcome);
    }

    [Fact]
    public async Task Save_Modify_NotFound_WhenOriginalIsMissing()
    {
        var result = await NewStore().SaveAsync(Draft("新", original: "舊"), default);

        Assert.Equal(SkillSaveOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task Save_Rename_MovesTheSkillAndItsDisabledState()
    {
        var catalog = new FakeModelCatalog();
        var store = NewStore(catalog);
        store.Add(Draft("週報"), enabled: false);

        var result = await store.SaveAsync(Draft("週報2", "新步驟", original: "週報"), default);

        Assert.Equal(SkillSaveOutcome.Saved, result.Outcome);
        var list = await store.ListAsync(default);
        Assert.Equal(["週報2"], list.Select(s => s.Name));
        Assert.False(list[0].Enabled);
        Assert.Equal("新步驟", (await store.GetAsync("週報2", default))!.Body);
    }

    [Fact]
    public async Task Save_RenameToAnotherExistingName_IsNameTaken()
    {
        var store = NewStore();
        store.Add(Draft("A"));
        store.Add(Draft("B"));

        var result = await store.SaveAsync(Draft("b", original: "A"), default);

        Assert.Equal(SkillSaveOutcome.NameTaken, result.Outcome);
        Assert.Equal(2, (await store.ListAsync(default)).Count);
    }

    [Fact]
    public async Task Save_CaseOnlyRename_IsAllowed()
    {
        var store = NewStore();
        store.Add(Draft("abc"));

        var result = await store.SaveAsync(Draft("ABC", original: "abc"), default);

        Assert.Equal(SkillSaveOutcome.Saved, result.Outcome);
        Assert.Equal(["ABC"], (await store.ListAsync(default)).Select(s => s.Name));
    }

    [Fact]
    public async Task Save_Modify_KeepsAttachedFiles()
    {
        var store = NewStore();
        store.Add(Draft("週報"), files: new Dictionary<string, string> { ["templates/週報.md"] = "範本" });

        await store.SaveAsync(Draft("週報", "改過的步驟", original: "週報"), default);

        var doc = await store.GetAsync("週報", default);
        Assert.Equal(["templates/週報.md"], doc!.Files);
        Assert.Equal("範本", await store.ReadFileAsync("週報", "templates/週報.md", default));
    }

    [Fact]
    public async Task Save_WhenEnabledIsFull_SavesDisabled()
    {
        var store = NewStore();
        for (var i = 0; i < SkillRules.MaxEnabled; i++)
            store.Add(Draft($"技能{i:D2}"));

        var result = await store.SaveAsync(Draft("第三十一個"), default);

        Assert.Equal(SkillSaveOutcome.SavedDisabled, result.Outcome);
        Assert.True(result.Succeeded);
        Assert.NotNull(result.Message);
        var added = (await store.ListAsync(default)).Single(s => s.Name == "第三十一個");
        Assert.False(added.Enabled);
    }

    [Fact]
    public async Task Delete_RemovesTheSkill_AndMissingIsNoOp()
    {
        var store = NewStore();
        store.Add(Draft("週報"));

        await store.DeleteAsync("週報", default);
        await store.DeleteAsync("不存在", default);

        Assert.Empty(await store.ListAsync(default));
    }

    [Fact]
    public async Task ReadFile_RejectsUnsafeAndUnknownPaths()
    {
        var store = NewStore();
        store.Add(Draft("週報"), files: new Dictionary<string, string> { ["a.md"] = "內容" });

        Assert.Equal("內容", await store.ReadFileAsync("週報", "a.md", default));
        Assert.Null(await store.ReadFileAsync("週報", "../a.md", default));
        Assert.Null(await store.ReadFileAsync("週報", "missing.md", default));
        Assert.Null(await store.ReadFileAsync("不存在", "a.md", default));
    }

    [Fact]
    public async Task Disabled_SkillIsNotUsable()
    {
        var store = NewStore();
        store.Add(Draft("停用的"), enabled: false);
        store.Add(Draft("有問題的"), problem: "缺少描述");

        var list = await store.ListAsync(default);

        Assert.All(list, s => Assert.False(s.IsUsable));
    }

    [Fact]
    public async Task Changed_IsRaisedOnMutations()
    {
        var store = NewStore();
        var count = 0;
        store.Changed += (_, _) => count++;

        await store.SaveAsync(Draft("週報"), default);
        await store.DeleteAsync("週報", default);
        await store.RefreshAsync(default);

        Assert.Equal(3, count);
        Assert.Equal(1, store.RefreshCount);
    }

    [Fact]
    public async Task PreviewImport_IsNotSupported_ByDefault()
    {
        var preview = await NewStore().PreviewImportAsync("/tmp/x", default);

        Assert.False(preview.CanInstall);
        Assert.NotNull(preview.Problem);
    }

    [Fact]
    public async Task Install_WithoutAUsablePreview_IsInvalid()
    {
        var store = NewStore();
        var preview = new SkillImportPreview("x", Draft("週報"), [], [], [], false, "有問題");

        var result = await store.InstallAsync(preview, default);

        Assert.Equal(SkillSaveOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task Install_UsablePreview_IsSavedAndReplacesSameName()
    {
        var store = NewStore();
        store.Add(Draft("週報", "舊的"));
        var preview = new SkillImportPreview("x", Draft("週報", "新的"), [], [], [], true, null);

        var result = await store.InstallAsync(preview, default);

        Assert.Equal(SkillSaveOutcome.Saved, result.Outcome);
        Assert.Equal("新的", (await store.GetAsync("週報", default))!.Body);
    }
}
