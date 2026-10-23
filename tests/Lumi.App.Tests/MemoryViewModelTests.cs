using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Memory;

namespace Lumi.App.Tests;

/// <summary>M14 14D：設定頁「記憶」的 ViewModel——清單與分組、用量、新增／編輯／刪除、表單驗證、開關、ChangeToken 自動更新。</summary>
public sealed class MemoryViewModelTests : IDisposable
{
    private readonly FakeModelCatalog _catalog = new();
    private readonly FakeMemoryStore _store = new();
    private readonly FakeConfirmService _confirm = new();
    private readonly MemoryViewModel _vm;

    public MemoryViewModelTests()
    {
        _vm = new MemoryViewModel(_store, _catalog, _confirm, new SyncUiDispatcher(), new FakeTimeProvider());
    }

    public void Dispose() => _vm.Dispose();

    private static MemoryCategoryOption Category(MemoryCategory category) => MemoryCategoryOption.All.First(o => o.Category == category);

    private IEnumerable<MemoryItemViewModel> Rows => _vm.Groups.SelectMany(g => g.Items);

    private MemoryItemViewModel Row(int id) => Rows.Single(r => r.Id == id);

    [Fact]
    public void StartsEmpty_WithZeroUsage_AndNoClearAll()
    {
        Assert.False(_vm.HasItems);
        Assert.Empty(_vm.Groups);
        Assert.Equal("0／100 則 · 0／2,000 字", _vm.UsageText);
        Assert.False(_vm.IsNearLimit);
        Assert.False(_vm.ClearAllCommand.CanExecute(null));
        Assert.Null(_vm.NewForm);
    }

    [Fact]
    public void Groups_FollowCategoryOrder_SkipEmptyCategories_AndSortByIdInside()
    {
        _store.Seed("專案甲", MemoryCategory.Project);
        _store.Seed("偏好甲", MemoryCategory.Preference);
        _store.Seed("本人甲", MemoryCategory.Profile);
        _store.Seed("偏好乙", MemoryCategory.Preference);

        Assert.Equal(["本人", "偏好", "專案"], _vm.Groups.Select(g => g.Title).ToArray());
        Assert.Equal(["偏好甲", "偏好乙"], _vm.Groups[1].Items.Select(i => i.Content).ToArray());
        Assert.Equal("m2", _vm.Groups[1].Items[0].IdText);
    }

    [Fact]
    public void Usage_CountsEveryStoredItem_AndItsCharacters()
    {
        _store.Seed("一二三四五", MemoryCategory.Profile);
        _store.Seed("六七八", MemoryCategory.Work);

        Assert.Equal("2／100 則 · 8／2,000 字", _vm.UsageText);
        Assert.True(_vm.HasItems);
    }

    [Fact]
    public void Usage_TurnsNearLimit_AtNinetyPercent_ByItemsOrByCharacters()
    {
        var options = new MemoryOptions { MaxItems = 10, MaxTotalLength = 100 };
        var store = new FakeMemoryStore(options);
        using var vm = new MemoryViewModel(store, _catalog, _confirm, new SyncUiDispatcher(), new FakeTimeProvider(), options);

        for (var i = 0; i < 8; i++)
            store.Seed($"記{i}", MemoryCategory.Profile);
        Assert.False(vm.IsNearLimit);   // 8／10 還不到 90%

        store.Seed("記8", MemoryCategory.Profile);
        Assert.True(vm.IsNearLimit);    // 9／10 達到 90%
        Assert.Equal("9／10 則 · 18／100 字", vm.UsageText);   // 「記0」到「記8」每則 2 字

        var characters = new FakeMemoryStore(options);
        using var byCharacters = new MemoryViewModel(characters, _catalog, _confirm, new SyncUiDispatcher(), new FakeTimeProvider(), options);
        characters.Seed(new string('字', 90), MemoryCategory.Profile);
        Assert.True(byCharacters.IsNearLimit);   // 90／100 字
    }

    [Fact]
    public void ExpiredItems_AreMarked_AndShowTheirDate_PermanentOnesDoNot()
    {
        _store.Seed("已經過期", MemoryCategory.Project, expires: new DateOnly(2026, 10, 1));   // 今天是 2026-10-19
        _store.Seed("還有效", MemoryCategory.Project, expires: new DateOnly(2026, 12, 31));
        _store.Seed("永久", MemoryCategory.Profile);

        Assert.True(Row(1).IsExpired);
        Assert.Equal("有效至 2026-10-01", Row(1).ValidUntilText);
        Assert.False(Row(2).IsExpired);
        Assert.Equal("有效至 2026-12-31", Row(2).ValidUntilText);
        Assert.False(Row(3).IsExpired);
        Assert.Null(Row(3).ValidUntilText);
        Assert.Equal("手動新增", Row(3).SourceText);
    }

    [Fact]
    public void SourceText_NamesTheThreeSources()
    {
        _store.Seed("要求", MemoryCategory.Profile, MemorySource.Explicit);
        _store.Seed("建議", MemoryCategory.Profile, MemorySource.Suggested);
        _store.Seed("手動", MemoryCategory.Profile, MemorySource.Manual);

        Assert.Equal(["你要求的", "Lumi 建議的", "手動新增"], Rows.Select(r => r.SourceText).ToArray());
    }

    // ---- 新增 ----

    [Fact]
    public async Task Add_Success_ClosesTheForm_SavesAsManual_AndReloads()
    {
        _vm.NewCommand.Execute(null);
        Assert.NotNull(_vm.NewForm);
        _vm.Form.Content = "  偏好用\n繁體中文  ";
        _vm.Form.SelectedCategory = Category(MemoryCategory.Preference);
        _vm.Form.Expires = "2026-12-31";

        await _vm.Form.SaveCommand.ExecuteAsync(null);

        var call = Assert.Single(_store.AddCalls);
        Assert.Equal(MemorySource.Manual, call.Source);
        Assert.Equal(MemoryCategory.Preference, call.Category);
        Assert.Equal(new DateOnly(2026, 12, 31), call.ExpiresAt);
        Assert.Null(_vm.NewForm);
        Assert.Equal("偏好用 繁體中文", Assert.Single(_vm.Groups[0].Items).Content);
        Assert.Equal(Strings.Memory_Saved, _vm.Message);
        Assert.False(_vm.MessageIsError);
    }

    [Fact]
    public async Task Add_Duplicate_KeepsTheForm_AndNamesTheExistingId()
    {
        _store.Seed("I am a backend engineer", MemoryCategory.Profile);
        _vm.NewCommand.Execute(null);
        _vm.Form.Content = "i am a BACKEND engineer";

        await _vm.Form.SaveCommand.ExecuteAsync(null);

        Assert.Equal("已經有相同的記憶（m1）。", _vm.Form.FormError);
        Assert.True(_vm.Form.HasFormError);
        Assert.NotNull(_vm.NewForm);
        Assert.Single(_store.Items);
    }

    [Theory]
    [InlineData(MemoryAddOutcome.TooManyItems)]
    [InlineData(MemoryAddOutcome.TooManyCharacters)]
    [InlineData(MemoryAddOutcome.Invalid)]
    public async Task Add_OtherFailures_ShowTheStoreMessageInTheForm(MemoryAddOutcome outcome)
    {
        _vm.NewCommand.Execute(null);
        _vm.Form.Content = "一句話";
        _store.NextAddResult = new MemoryAddResult(outcome, null, $"原因：{outcome}");

        await _vm.Form.SaveCommand.ExecuteAsync(null);

        Assert.Equal($"原因：{outcome}", _vm.Form.FormError);
        Assert.NotNull(_vm.NewForm);
        Assert.False(_vm.HasItems);
    }

    [Fact]
    public void Form_ChangingAField_ClearsTheStoreError()
    {
        _vm.NewCommand.Execute(null);
        _vm.Form.Content = "一句話";
        _vm.Form.FormError = "上一次失敗";

        _vm.Form.Content = "一句話，改過了";

        Assert.Null(_vm.Form.FormError);
    }

    [Fact]
    public void Form_BlocksSave_WhenContentIsEmptyOrTooLong()
    {
        _vm.NewCommand.Execute(null);
        var form = _vm.Form;

        form.Content = "   ";
        Assert.Equal(0, form.ContentLength);
        Assert.False(form.CanSave);
        Assert.False(form.SaveCommand.CanExecute(null));
        Assert.Equal(Strings.Memory_ContentEmpty, form.SaveBlockedReason);
        Assert.Null(form.ContentError);   // 空白不顯示錯誤文字，改由停用原因說明

        form.Content = new string('字', 201);
        Assert.Equal(201, form.ContentLength);
        Assert.NotNull(form.ContentError);
        Assert.True(form.HasContentError);
        Assert.False(form.CanSave);
        Assert.False(form.SaveCommand.CanExecute(null));
        Assert.Equal(Strings.Memory_ContentBlocked, form.SaveBlockedReason);
        Assert.Equal("201／200", form.ContentCounter);

        form.Content = new string('字', 200);
        Assert.True(form.CanSave);
        Assert.True(form.SaveCommand.CanExecute(null));
        Assert.Null(form.SaveBlockedReason);
    }

    [Fact]
    public void Form_CountsNormalizedLength_SoLineBreaksAndSpacesDoNotInflateIt()
    {
        _vm.NewCommand.Execute(null);

        _vm.Form.Content = "一   二\n\n三";

        Assert.Equal(5, _vm.Form.ContentLength);   // 「一 二 三」
    }

    [Fact]
    public void Form_BlocksSave_WhenTheExpiryIsNotADate()
    {
        _vm.NewCommand.Execute(null);
        _vm.Form.Content = "一句話";

        _vm.Form.Expires = "2026/12/31";
        Assert.NotNull(_vm.Form.ExpiresError);
        Assert.True(_vm.Form.HasExpiresError);
        Assert.False(_vm.Form.CanSave);
        Assert.False(_vm.Form.SaveCommand.CanExecute(null));
        Assert.Equal(Strings.Memory_ExpiresBlocked, _vm.Form.SaveBlockedReason);

        _vm.Form.Expires = "2026-02-30";
        Assert.NotNull(_vm.Form.ExpiresError);

        _vm.Form.Expires = "";   // 留空＝永久
        Assert.Null(_vm.Form.ExpiresError);
        Assert.Null(_vm.Form.ExpiresDate);
        Assert.True(_vm.Form.CanSave);
    }

    // ---- 編輯 ----

    [Fact]
    public async Task Edit_LoadsTheItem_AndSavesContentCategoryAndExpiry_SkippingTheSecretCheck()
    {
        _store.Seed("舊的內容", MemoryCategory.Profile);
        Row(1).EditCommand.Execute(null);

        Assert.Equal("編輯 m1", _vm.Form.Title);
        Assert.Equal("舊的內容", _vm.Form.Content);
        Assert.Equal(MemoryCategory.Profile, _vm.Form.SelectedCategory.Category);
        Assert.Same(_vm.Form, Row(1).EditForm);
        Assert.Null(_vm.NewForm);

        _vm.Form.Content = "新的內容";
        _vm.Form.SelectedCategory = Category(MemoryCategory.Work);
        _vm.Form.Expires = "2027-01-31";
        await _vm.Form.SaveCommand.ExecuteAsync(null);

        var (id, patch) = Assert.Single(_store.UpdateCalls);
        Assert.Equal(1, id);
        Assert.Equal("新的內容", patch.Content);
        Assert.Equal(MemoryCategory.Work, patch.Category);
        Assert.Equal(new DateOnly(2027, 1, 31), patch.ExpiresAt);
        Assert.False(patch.ClearExpiresAt);
        Assert.True(patch.SkipSecretCheck);
        Assert.Null(Row(1).EditForm);   // 表單已收合
        Assert.Equal("新的內容", Row(1).Content);
        Assert.Equal(Strings.Memory_Saved, _vm.Message);
    }

    [Fact]
    public async Task Edit_ClearsTheExpiry_WhenTheDateIsLeftEmpty()
    {
        _store.Seed("有期限", MemoryCategory.Project, expires: new DateOnly(2026, 12, 31));
        Row(1).EditCommand.Execute(null);
        Assert.Equal("2026-12-31", _vm.Form.Expires);

        _vm.Form.Expires = "";
        await _vm.Form.SaveCommand.ExecuteAsync(null);

        var (_, patch) = Assert.Single(_store.UpdateCalls);
        Assert.True(patch.ClearExpiresAt);
        Assert.Null(patch.ExpiresAt);
        Assert.Null(_store.Items.Single().ExpiresAt);
    }

    [Fact]
    public void Edit_OverLimit_DisablesSave()
    {
        _store.Seed("舊的內容", MemoryCategory.Profile);
        Row(1).EditCommand.Execute(null);

        _vm.Form.Content = new string('字', 201);

        Assert.False(_vm.Form.CanSave);
        Assert.False(_vm.Form.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Edit_NotFound_ClosesTheForm_AndSaysItsGone()
    {
        _store.Seed("舊的內容", MemoryCategory.Profile);
        Row(1).EditCommand.Execute(null);
        _store.NextUpdateResult = new MemoryUpdateResult(MemoryUpdateOutcome.NotFound, null, "找不到這則記憶。");

        await _vm.Form.SaveCommand.ExecuteAsync(null);

        Assert.Null(Row(1).EditForm);
        Assert.Equal(Strings.Memory_Gone, _vm.Message);
        Assert.True(_vm.MessageIsError);
    }

    [Fact]
    public async Task Edit_Duplicate_KeepsTheForm_AndNamesTheOtherItem()
    {
        _store.Seed("第一則", MemoryCategory.Profile);
        var other = _store.Seed("第二則", MemoryCategory.Profile);
        Row(1).EditCommand.Execute(null);
        _store.NextUpdateResult = new MemoryUpdateResult(MemoryUpdateOutcome.Duplicate, other, null);

        await _vm.Form.SaveCommand.ExecuteAsync(null);

        Assert.Equal("已經有相同的記憶（m2）。", _vm.Form.FormError);
        Assert.Same(_vm.Form, Row(1).EditForm);
    }

    [Fact]
    public void OnlyOneForm_IsOpenAtATime_NewOrEdit()
    {
        _store.Seed("第一則", MemoryCategory.Profile);
        _store.Seed("第二則", MemoryCategory.Profile);

        _vm.NewCommand.Execute(null);
        Row(1).EditCommand.Execute(null);

        Assert.Null(_vm.NewForm);
        Assert.Same(_vm.Form, Row(1).EditForm);
        Assert.Null(Row(2).EditForm);

        Row(2).EditCommand.Execute(null);
        Assert.Null(Row(1).EditForm);
        Assert.Same(_vm.Form, Row(2).EditForm);
    }

    [Fact]
    public void Cancel_ClosesTheForm_WithoutWriting()
    {
        _store.Seed("舊的內容", MemoryCategory.Profile);
        Row(1).EditCommand.Execute(null);
        _vm.Form.Content = "不要存";

        _vm.Form.CancelCommand.Execute(null);

        Assert.Null(Row(1).EditForm);
        Assert.Empty(_store.UpdateCalls);
        Assert.Equal("舊的內容", _store.Items.Single().Content);
    }

    // ---- 重新載入（ChangeToken）----

    [Fact]
    public void StoreChange_ReloadsAutomatically_WithoutAnyCommand()
    {
        Assert.False(_vm.HasItems);

        _store.Seed("模型剛存的記憶", MemoryCategory.Preference, MemorySource.Explicit);

        Assert.True(_vm.HasItems);
        Assert.Equal("偏好", _vm.Groups.Single().Title);
    }

    [Fact]
    public void StoreChange_KeepsTheEditForm_WhileTheUserIsTyping()
    {
        _store.Seed("第一則", MemoryCategory.Profile);
        Row(1).EditCommand.Execute(null);
        _vm.Form.Content = "正在輸入一半";
        _vm.Form.Expires = "2027-06-30";

        _store.Seed("別處新增的", MemoryCategory.Work);

        Assert.Same(_vm.Form, Row(1).EditForm);
        Assert.Equal("正在輸入一半", _vm.Form.Content);
        Assert.Equal("2027-06-30", _vm.Form.Expires);
        Assert.Equal(2, _vm.Groups.SelectMany(g => g.Items).Count());
    }

    [Fact]
    public void StoreChange_KeepsTheNewForm_WhileTheUserIsTyping()
    {
        _vm.NewCommand.Execute(null);
        _vm.Form.Content = "還沒存";

        _store.Seed("別處新增的", MemoryCategory.Work);

        Assert.NotNull(_vm.NewForm);
        Assert.Equal("還沒存", _vm.Form.Content);
    }

    [Fact]
    public async Task StoreChange_ClosesTheEditForm_WhenTheEditedItemIsGone()
    {
        _store.Seed("第一則", MemoryCategory.Profile);
        Row(1).EditCommand.Execute(null);

        await _store.DeleteAsync(1);

        Assert.Null(_vm.NewForm);
        Assert.Empty(_vm.Groups);
        Assert.Equal(Strings.Memory_Gone, _vm.Message);
    }

    [Fact]
    public void DisposedViewModel_NoLongerReloads()
    {
        _vm.Dispose();

        _store.Seed("之後的變動", MemoryCategory.Profile);

        Assert.False(_vm.HasItems);
        Assert.Single(_store.Items);
    }

    // ---- 刪除 ----

    [Fact]
    public async Task Delete_AsksFirst_AndOnlyDeletesWhenConfirmed()
    {
        _store.Seed("要忘掉的事", MemoryCategory.Profile);

        _confirm.Answer = false;
        await Row(1).DeleteCommand.ExecuteAsync(null);
        Assert.Empty(_store.DeleteCalls);
        Assert.True(_vm.HasItems);

        var (message, confirmText) = Assert.Single(_confirm.Asked);
        Assert.StartsWith("忘掉這則記憶？", message);
        Assert.Contains("要忘掉的事", message);
        Assert.Equal(Strings.Memory_Forget, confirmText);

        _confirm.Answer = true;
        await Row(1).DeleteCommand.ExecuteAsync(null);
        Assert.Equal([1], _store.DeleteCalls);
        Assert.False(_vm.HasItems);
    }

    [Fact]
    public async Task Delete_OfTheItemBeingEdited_ClosesTheForm()
    {
        _store.Seed("第一則", MemoryCategory.Profile);
        _store.Seed("第二則", MemoryCategory.Profile);
        Row(1).EditCommand.Execute(null);
        _confirm.Answer = true;

        await Row(1).DeleteCommand.ExecuteAsync(null);

        Assert.Equal([2], Rows.Select(r => r.Id).ToArray());
        Assert.All(Rows, r => Assert.Null(r.EditForm));
        Assert.Null(_vm.NewForm);
    }

    // ---- 全部清除 ----

    [Fact]
    public async Task ClearAll_IsDisabledWithoutMemories_AndConfirmsWithTheCount()
    {
        Assert.False(_vm.ClearAllCommand.CanExecute(null));

        _store.Seed("一", MemoryCategory.Profile);
        _store.Seed("二", MemoryCategory.Work);
        Assert.True(_vm.ClearAllCommand.CanExecute(null));

        _confirm.Answer = false;
        await _vm.ClearAllCommand.ExecuteAsync(null);
        Assert.Equal(0, _store.DeleteAllCalls);
        Assert.Contains("2", Assert.Single(_confirm.Asked).Message);
        Assert.True(_vm.HasItems);

        _confirm.Answer = true;
        await _vm.ClearAllCommand.ExecuteAsync(null);
        Assert.Equal(1, _store.DeleteAllCalls);
        Assert.False(_vm.HasItems);
        Assert.False(_vm.ClearAllCommand.CanExecute(null));
        Assert.Equal("已清除 2 則記憶。", _vm.Message);
    }

    // ---- 開關 ----

    [Fact]
    public void Toggle_CallsSetMemoryEnabled_AndUpdatesTheNote()
    {
        Assert.True(_vm.IsEnabled);
        Assert.Null(_vm.EnabledNote);

        _vm.IsEnabled = false;

        Assert.Equal([false], _catalog.MemoryEnabledSaves);
        Assert.False(_catalog.Current.Memory.Enabled);
        Assert.False(_vm.IsEnabled);
        Assert.Equal(Strings.Memory_DisabledNote, _vm.EnabledNote);

        _vm.IsEnabled = true;
        Assert.Null(_vm.EnabledNote);
    }

    [Fact]
    public void Toggle_ToTheSameValue_DoesNotSave()
    {
        _vm.IsEnabled = true;

        Assert.Empty(_catalog.MemoryEnabledSaves);
    }

    [Fact]
    public void ExternalToggle_UpdatesTheSwitch()
    {
        _catalog.Current = _catalog.Current with { Memory = new Lumi.Core.Models.MemorySettings(false) };

        _catalog.Raise();

        Assert.False(_vm.IsEnabled);
        Assert.Equal(Strings.Memory_DisabledNote, _vm.EnabledNote);
    }
}
