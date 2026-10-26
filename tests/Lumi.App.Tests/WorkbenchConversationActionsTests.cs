using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

/// <summary>M21 21C：「⋯」的重新命名、匯出、刪除。重新命名與刪除沿用清單的規則與流程（同一份程式），這裡測工作台這一側。</summary>
public class WorkbenchConversationActionsTests
{
    private sealed class FakeRename : IRenameService
    {
        /// <summary>null＝取消。</summary>
        public string? Answer { get; set; }

        public List<(string CurrentTitle, int MaxLength)> Asked { get; } = [];

        public Task<string?> PromptAsync(string currentTitle, int maxLength)
        {
            Asked.Add((currentTitle, maxLength));
            return Task.FromResult(Answer);
        }
    }

    private sealed class FakeSavePicker : IFilePickerService
    {
        /// <summary>null＝使用者取消。</summary>
        public string? Path { get; set; }

        public List<(string SuggestedName, string Extension)> Saves { get; } = [];

        public Task<IReadOnlyList<string>> PickFilesAsync() => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);

        public Task<string?> SaveFileAsync(string suggestedName, string extension)
        {
            Saves.Add((suggestedName, extension));
            return Task.FromResult(Path);
        }
    }

    private sealed record Rig(WorkbenchViewModel Vm, InMemoryConversationStore Store, FakeConfirmService Confirm, FakeRename Rename, FakeSavePicker Picker);

    private static Rig CreateRig(InMemoryConversationStore? store = null)
    {
        store ??= new InMemoryConversationStore();
        var catalog = new FakeModelCatalog();
        var confirm = new FakeConfirmService();
        var rename = new FakeRename();
        var picker = new FakeSavePicker();
        var vm = new WorkbenchViewModel(new ConversationService(new ScriptedChatProvider(), store, catalog), store, catalog,
            new FakeApiKeyPrompt(false), new FakeSettingsWindowService(), new ConnectionStatusCache(), new FakeClipboardService(),
            new SyncUiDispatcher(), new FakeTimeProvider(), NullLogger<WorkbenchViewModel>.Instance,
            deletion: HistoryTestSupport.Deletion(store), confirm: confirm, filePicker: picker, rename: rename);
        return new Rig(vm, store, confirm, rename, picker);
    }

    /// <summary>建一則有一則使用者訊息的對話（空對話不會列在歷史裡，這裡要的是有內容的）。後建立的更新時間較新。</summary>
    private static async Task<Conversation> SeedAsync(InMemoryConversationStore store, string title, string text = "內容")
    {
        await Task.Delay(5);   // 清單依更新時間排序：相鄰兩次播種的時間戳不能相同
        var conversation = await store.CreateAsync(title);
        await store.AppendMessageAsync(conversation.Id,
            new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock(text)], DateTimeOffset.UtcNow));
        return conversation;
    }

    private static async Task<string?> StoredTitleAsync(InMemoryConversationStore store, string id) =>
        (await store.ListAsync(100)).FirstOrDefault(c => c.Id == id)?.Title;

    // ---- 重新命名 ----

    [Fact]
    public async Task Rename_SavesTheCleanedTitle_AndUpdatesTheWorkbench()
    {
        var store = new InMemoryConversationStore();
        var conversation = await SeedAsync(store, "舊名字");
        var rig = CreateRig(store);
        await rig.Vm.InitializeAsync();
        await rig.Vm.OpenConversationAsync(conversation);
        rig.Rename.Answer = "  新名字  ";

        await rig.Vm.RenameConversationCommand.ExecuteAsync(null);

        Assert.Equal("新名字", rig.Vm.Title);
        Assert.Equal("新名字", await StoredTitleAsync(store, conversation.Id));
        Assert.Equal([("舊名字", rig.Vm.History.TitleMaxLength)], rig.Rename.Asked);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Rename_BlankOrCancelled_KeepsTheTitle(string? answer)
    {
        var store = new InMemoryConversationStore();
        var conversation = await SeedAsync(store, "保留的名字");
        var rig = CreateRig(store);
        await rig.Vm.InitializeAsync();
        await rig.Vm.OpenConversationAsync(conversation);
        rig.Rename.Answer = answer;

        await rig.Vm.RenameConversationCommand.ExecuteAsync(null);

        Assert.Equal("保留的名字", rig.Vm.Title);
        Assert.Equal("保留的名字", await StoredTitleAsync(store, conversation.Id));
    }

    [Fact]
    public async Task Rename_TooLong_IsClippedToTheSameLimitAsTheList()
    {
        var store = new InMemoryConversationStore();
        var conversation = await SeedAsync(store, "短的");
        var rig = CreateRig(store);
        await rig.Vm.InitializeAsync();
        await rig.Vm.OpenConversationAsync(conversation);
        rig.Rename.Answer = new string('字', 500);

        await rig.Vm.RenameConversationCommand.ExecuteAsync(null);

        var limit = rig.Vm.History.TitleMaxLength;
        Assert.True(limit < 500);
        Assert.Equal(new string('字', limit), rig.Vm.Title);
        Assert.Equal(rig.Vm.Title, await StoredTitleAsync(store, conversation.Id));
    }

    [Fact]
    public async Task Rename_IsNotEnabledForAnEmptyConversation()
    {
        var rig = CreateRig();
        await rig.Vm.InitializeAsync();

        Assert.False(rig.Vm.RenameConversationEnabled);
        Assert.False(rig.Vm.RenameConversationCommand.CanExecute(null));
    }

    // ---- 刪除 ----

    [Fact]
    public async Task Delete_Cancelled_KeepsTheConversation()
    {
        var store = new InMemoryConversationStore();
        var conversation = await SeedAsync(store, "不要刪");
        var rig = CreateRig(store);
        await rig.Vm.InitializeAsync();
        await rig.Vm.OpenConversationAsync(conversation);
        rig.Confirm.Answer = false;

        await rig.Vm.DeleteConversationCommand.ExecuteAsync(null);

        Assert.Equal("不要刪", rig.Vm.Title);
        Assert.Contains(await store.ListAsync(100), c => c.Id == conversation.Id);
        var asked = Assert.Single(rig.Confirm.Asked);
        Assert.Contains("不要刪", asked.Message);   // 和清單的刪除是同一句確認文字
        Assert.Equal(Strings.Confirm_Delete, asked.ConfirmText);
    }

    [Fact]
    public async Task Delete_Confirmed_RemovesTheConversation_AndOpensTheNextInTheList()
    {
        var store = new InMemoryConversationStore();
        var older = await SeedAsync(store, "較舊的");
        var newer = await SeedAsync(store, "較新的（目前）");
        var rig = CreateRig(store);
        await rig.Vm.InitializeAsync();
        await rig.Vm.OpenConversationAsync(newer);
        await rig.Vm.History.RefreshAsync(newer.Id);
        rig.Vm.InputText = "還沒送出的草稿";

        await rig.Vm.DeleteConversationCommand.ExecuteAsync(null);

        Assert.Equal("較舊的", rig.Vm.Title);
        Assert.DoesNotContain(await store.ListAsync(100), c => c.Id == newer.Id);
        Assert.Contains(await store.ListAsync(100), c => c.Id == older.Id);
        Assert.Equal(string.Empty, rig.Vm.InputText);   // 草稿跟著清掉（和清單刪除的目前對話一樣）
    }

    [Fact]
    public async Task Delete_IsNotEnabledForAnEmptyConversation()
    {
        var rig = CreateRig();
        await rig.Vm.InitializeAsync();

        Assert.False(rig.Vm.DeleteConversationEnabled);
        Assert.False(rig.Vm.DeleteConversationCommand.CanExecute(null));
        Assert.False(rig.Vm.ExportConversationEnabled);
        Assert.False(rig.Vm.ExportConversationCommand.CanExecute(null));
    }

    // ---- 匯出 ----

    [Fact]
    public async Task Export_WritesTheMarkdownFile_SuggestsTitleDotMd_AndShowsTheDoneNotice()
    {
        var store = new InMemoryConversationStore();
        var conversation = await SeedAsync(store, "週報", "這週的內容");
        var rig = CreateRig(store);
        await rig.Vm.InitializeAsync();
        await rig.Vm.OpenConversationAsync(conversation);
        var path = Path.Combine(Path.GetTempPath(), $"lumi-export-{Guid.NewGuid():N}.md");
        rig.Picker.Path = path;

        try
        {
            await rig.Vm.ExportConversationCommand.ExecuteAsync(null);

            Assert.Equal([("週報.md", "md")], rig.Picker.Saves);
            var text = await File.ReadAllTextAsync(path);
            Assert.StartsWith("# 週報\n", text);
            Assert.Contains("## 你\n\n這週的內容", text);
            Assert.Equal(string.Format(Strings.Export_DoneFormat, path), rig.Vm.Notice);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Export_Cancelled_WritesNothing_AndShowsNoNotice()
    {
        var store = new InMemoryConversationStore();
        var conversation = await SeedAsync(store, "取消");
        var rig = CreateRig(store);
        await rig.Vm.InitializeAsync();
        await rig.Vm.OpenConversationAsync(conversation);
        rig.Picker.Path = null;

        await rig.Vm.ExportConversationCommand.ExecuteAsync(null);

        Assert.Single(rig.Picker.Saves);
        Assert.Null(rig.Vm.Notice);
    }

    [Fact]
    public async Task Export_WriteFailure_ShowsTheFailureNotice()
    {
        var store = new InMemoryConversationStore();
        var conversation = await SeedAsync(store, "寫不進去");
        var rig = CreateRig(store);
        await rig.Vm.InitializeAsync();
        await rig.Vm.OpenConversationAsync(conversation);
        rig.Picker.Path = Path.Combine(Path.GetTempPath(), $"no-such-folder-{Guid.NewGuid():N}", "x.md");

        await rig.Vm.ExportConversationCommand.ExecuteAsync(null);

        Assert.Equal(Strings.Export_Failed, rig.Vm.Notice);
    }
}
