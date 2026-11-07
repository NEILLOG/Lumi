using Lumi.Core.Chat;
using Lumi.Core.Skills;
using Lumi.Core.Tools;
using Lumi.Tools.Skills;

namespace Lumi.Tools.Tests.Skills;

/// <summary>M29 29B：<c>save_skill</c>——確認卡內容、取代、附帶檔案保留、停用結果、描述子的旗標。</summary>
public sealed class SaveSkillToolTests
{
    private static ToolContext Context(ChatMode mode = ChatMode.Workbench) =>
        new("c1", mode, TimeProvider.System, EmptyFileScope.Instance);

    private static string Json(string name, string description, string instructions) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["name"] = name, ["description"] = description, ["instructions"] = instructions,
        });

    private static SaveSkillTool Tool(ISkillStore store) => new(store);

    // ---- 描述子 ----

    [Fact]
    public void Descriptor_IsWriteWorkbenchOnly_AlwaysAsk_AndNotAlwaysAllowable()
    {
        var descriptor = Tool(new InMemorySkillStore()).Descriptor;

        Assert.Equal("save_skill", descriptor.Name);
        Assert.Equal("儲存技能", descriptor.DisplayName);
        Assert.Equal(ToolPermission.Write, descriptor.Permission);
        Assert.Equal(ToolModes.Workbench, descriptor.Modes);
        Assert.Equal(TimeSpan.FromSeconds(10), descriptor.Timeout);
        Assert.Equal(ToolGroups.Skills, descriptor.Group);
        Assert.Equal("把一套做事方法存成技能。", descriptor.Summary);
        Assert.True(descriptor.AlwaysAsk);
        Assert.False(descriptor.CanAlwaysAllow);
        Assert.False(descriptor.RequiresScope);
    }

    [Fact]
    public void Description_TellsTheModelToConfirmReplacementAndUseMemoryForFacts()
    {
        var description = Tool(new InMemorySkillStore()).Descriptor.Description;

        Assert.Contains("要求把做法存成技能", description);
        Assert.Contains("個資", description);
        Assert.Contains("取代", description);
        Assert.Contains("memory_save", description);
    }

    // ---- Describe：確認卡 ----

    [Fact]
    public void Preview_ValidNewSkill_ProducesAProposal_NotReplacing()
    {
        var tool = Tool(new InMemorySkillStore());

        var preview = tool.Preview(Json("週報", "每週五整理", "1. 收集\n2. 整理"), Context());

        Assert.Equal("週報", preview.Target);
        Assert.Equal(new SkillProposal("週報", "每週五整理", "1. 收集\n2. 整理", ReplacesExisting: false), preview.Skill);
    }

    [Fact]
    public void Preview_ExistingName_IsReplacing_CaseInsensitively()
    {
        var store = new InMemorySkillStore();
        store.Add(new SkillDraft("Weekly", "舊的", "舊步驟"));

        var preview = Tool(store).Preview(Json("weekly", "新的", "新步驟"), Context());

        Assert.True(preview.Skill!.ReplacesExisting);
    }

    [Fact]
    public void Preview_NormalizesDescription_ToOneLine_AndBody()
    {
        var preview = Tool(new InMemorySkillStore()).Preview(Json(" 週報 ", "第一行\n第二行", "\r\n步驟\r\n"), Context());

        Assert.Equal(new SkillProposal("週報", "第一行 第二行", "步驟", ReplacesExisting: false), preview.Skill);
    }

    [Theory]
    [InlineData("", "描述", "步驟")]                 // 名稱空白
    [InlineData("a/b", "描述", "步驟")]              // 名稱含非法字元
    [InlineData("CON", "描述", "步驟")]              // 保留名稱
    [InlineData("週報", "", "步驟")]                 // 沒有「什麼時候用」
    [InlineData("週報", "描述", "   ")]              // 沒有步驟
    public void Preview_InvalidArguments_HasNoProposal(string name, string description, string instructions)
    {
        var preview = Tool(new InMemorySkillStore()).Preview(Json(name, description, instructions), Context());

        Assert.Null(preview.Skill);
    }

    [Fact]
    public void Preview_InvalidArguments_ShowsOnlyTheName()
    {
        var preview = Tool(new InMemorySkillStore()).Preview(Json("週報", "", "步驟"), Context());

        Assert.Equal("週報", preview.Target);
        Assert.Null(preview.Skill);
    }

    [Fact]
    public void Preview_StoreFailure_IsNotReplacing_AndStillProposes()
    {
        var preview = Tool(new BrokenSkillStore()).Preview(Json("週報", "每週五", "步驟"), Context());

        Assert.Equal(new SkillProposal("週報", "每週五", "步驟", ReplacesExisting: false), preview.Skill);
    }

    // ---- 執行 ----

    [Fact]
    public async Task Execute_NewSkill_IsSaved()
    {
        var store = new InMemorySkillStore();

        var result = await Tool(store).ExecuteAsync(Json("週報", "每週五", "步驟"), Context(), default);

        Assert.False(result.IsError);
        Assert.Equal("已存成技能「週報」，之後開新對話就會用到。", result.Content);
        Assert.Equal("已儲存「週報」", result.Summary);
        var listed = await store.ListAsync(default);
        Assert.Contains(listed, s => s.Name == "週報" && s.Description == "每週五" && s.Enabled);
    }

    [Fact]
    public async Task Execute_SameName_ReplacesBody_AndKeepsAttachedFiles()
    {
        var store = new InMemorySkillStore();
        store.Add(new SkillDraft("週報", "舊的", "舊步驟"), new Dictionary<string, string> { ["template.md"] = "範本內容" });

        var result = await Tool(store).ExecuteAsync(Json("週報", "新的", "新步驟"), Context(), default);

        Assert.False(result.IsError);
        var document = await store.GetAsync("週報", default);
        Assert.Equal("新步驟", document!.Body);
        Assert.Equal("新的", document.Info.Description);
        Assert.Equal(["template.md"], document.Files);
        Assert.Equal("範本內容", await store.ReadFileAsync("週報", "template.md", default));
    }

    [Fact]
    public async Task Execute_InvalidArguments_IsAnErrorAndSavesNothing()
    {
        var store = new InMemorySkillStore();

        var result = await Tool(store).ExecuteAsync(Json("週報", "", "步驟"), Context(), default);

        Assert.True(result.IsError);
        Assert.Empty(await store.ListAsync(default));
    }

    [Fact]
    public async Task Execute_WhenTheEnabledLimitIsReached_SavesDisabled_WithTheStoreMessage()
    {
        var store = new InMemorySkillStore();
        for (var i = 1; i <= SkillRules.MaxEnabled; i++)
            store.Add(new SkillDraft($"技能{i:00}", "描述", "步驟"));

        var result = await Tool(store).ExecuteAsync(Json("週報", "每週五", "步驟"), Context(), default);

        Assert.False(result.IsError);
        Assert.Contains("已存成技能「週報」", result.Content);
        Assert.Contains($"已經有 {SkillRules.MaxEnabled} 個啟用中的技能，這個技能先存成停用。", result.Content);
        Assert.Equal("已儲存「週報」（先停用）", result.Summary);
        var saved = (await store.ListAsync(default)).Single(s => s.Name == "週報");
        Assert.False(saved.Enabled);
    }

    [Fact]
    public async Task Execute_StoreFailure_ReturnsAFixedMessage()
    {
        var result = await Tool(new BrokenSkillStore()).ExecuteAsync(Json("週報", "每週五", "步驟"), Context(), default);

        Assert.True(result.IsError);
        Assert.Equal("無法儲存這個技能。", result.Content);
    }

    /// <summary>所有讀寫都丟例外（只有測試需要的部分會被呼叫）。</summary>
    private sealed class BrokenSkillStore : ISkillStore
    {
        public event EventHandler? Changed { add { } remove { } }

        public string Folder => "";

        public Task<IReadOnlyList<SkillInfo>> ListAsync(CancellationToken ct) => throw new IOException("讀取失敗");

        public Task<SkillDocument?> GetAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> ReadFileAsync(string name, string relativePath, CancellationToken ct) => throw new NotSupportedException();
        public Task<SkillSaveResult> SaveAsync(SkillDraft draft, CancellationToken ct) => throw new IOException("寫入失敗");
        public Task DeleteAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task<SkillImportPreview> PreviewImportAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<SkillSaveResult> InstallAsync(SkillImportPreview preview, CancellationToken ct) => throw new NotSupportedException();
        public Task DiscardImportAsync(SkillImportPreview preview, CancellationToken ct) => throw new NotSupportedException();
        public Task RefreshAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}
