using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Models;
using Lumi.Core.Notes;

namespace Lumi.App.Tests;

/// <summary>M10 10B：設定頁「一般」的筆記資料夾——輸入、套用、選擇資料夾、恢復預設，以及外部改動時的同步。</summary>
public sealed class NotesFolderSettingsTests
{
    private static string Folder(string name) => Path.Combine(Path.GetTempPath(), "lumi-notes-vm", name);

    private static (GeneralViewModel Vm, FakeModelCatalog Catalog, FakeFilePicker Picker) Make(string saved = "")
    {
        var catalog = new FakeModelCatalog();
        catalog.Current = catalog.Current with { Notes = new NotesSettings(saved) };
        var picker = new FakeFilePicker();
        return (new GeneralViewModel(catalog, new SyncUiDispatcher(), picker), catalog, picker);
    }

    [Fact]
    public void Starts_WithTheSavedFolder_AndDefaultWatermark()
    {
        var (vm, _, _) = Make(Folder("saved"));

        Assert.Equal(Folder("saved"), vm.NotesFolderInput);
        Assert.True(vm.IsNotesFolderCustomized);
        Assert.Equal(NoteFolderRules.DefaultFolder, vm.NotesFolderDefault);
    }

    [Fact]
    public void Starts_Default_ShowsNoCustomFolder()
    {
        var (vm, _, _) = Make();

        Assert.Equal("", vm.NotesFolderInput);
        Assert.False(vm.IsNotesFolderCustomized);
        Assert.Null(vm.NotesFolderError);
    }

    [Fact]
    public async Task ApplyValidFolder_SavesTrimmed_AndUpdatesState()
    {
        var (vm, catalog, _) = Make();

        await vm.ApplyNotesFolderAsync($"  {Folder("a")}  ");

        Assert.Equal([Folder("a")], catalog.NotesFolderSaves);
        Assert.Equal(Folder("a"), vm.NotesFolderInput);
        Assert.True(vm.IsNotesFolderCustomized);
        Assert.Null(vm.NotesFolderError);
    }

    [Fact]
    public async Task InvalidFolder_IsNotSaved_KeepsWhatWasTyped_AndExplainsWhy()
    {
        var (vm, catalog, _) = Make(Folder("kept"));

        await vm.ApplyNotesFolderAsync("relative/typed");

        Assert.Empty(catalog.NotesFolderSaves);
        Assert.Equal("relative/typed", vm.NotesFolderInput);
        Assert.Contains("不合法", vm.NotesFolderError);
        Assert.Contains("完整路徑", vm.NotesFolderError);
        Assert.True(vm.IsNotesFolderCustomized);
    }

    [Fact]
    public async Task Commit_WithUnchangedText_DoesNotSave()
    {
        var (vm, catalog, _) = Make(Folder("same"));

        await vm.CommitNotesFolderCommand.ExecuteAsync(null);

        Assert.Empty(catalog.NotesFolderSaves);
    }

    [Fact]
    public async Task Commit_SavesTheTypedText()
    {
        var (vm, catalog, _) = Make();
        vm.NotesFolderInput = Folder("typed");

        await vm.CommitNotesFolderCommand.ExecuteAsync(null);

        Assert.Equal([Folder("typed")], catalog.NotesFolderSaves);
    }

    [Fact]
    public async Task ChooseFolder_AppliesThePickedFolder()
    {
        var (vm, catalog, picker) = Make();
        picker.Folder = Folder("picked");

        await vm.ChooseNotesFolderCommand.ExecuteAsync(null);

        Assert.Equal([Folder("picked")], catalog.NotesFolderSaves);
        Assert.Equal(1, picker.FolderPicks);
    }

    [Fact]
    public async Task ChooseFolder_Cancelled_ChangesNothing()
    {
        var (vm, catalog, picker) = Make(Folder("keep"));
        picker.Folder = null;

        await vm.ChooseNotesFolderCommand.ExecuteAsync(null);

        Assert.Empty(catalog.NotesFolderSaves);
        Assert.Equal(Folder("keep"), vm.NotesFolderInput);
        Assert.Null(vm.NotesFolderError);
    }

    [Fact]
    public async Task ChooseFolder_WithoutAPicker_DoesNothing()
    {
        var catalog = new FakeModelCatalog();
        var vm = new GeneralViewModel(catalog, new SyncUiDispatcher());

        await vm.ChooseNotesFolderCommand.ExecuteAsync(null);

        Assert.Empty(catalog.NotesFolderSaves);
    }

    [Fact]
    public async Task ResetToDefault_SavesEmpty_AndDisablesReset()
    {
        var (vm, catalog, _) = Make(Folder("custom"));

        await vm.ResetNotesFolderCommand.ExecuteAsync(null);

        Assert.Equal([""], catalog.NotesFolderSaves);
        Assert.Equal("", vm.NotesFolderInput);
        Assert.False(vm.IsNotesFolderCustomized);
    }

    [Fact]
    public void ExternalChange_UpdatesTheField_WhenTheSavedValueChanged()
    {
        var (vm, catalog, _) = Make();
        catalog.Current = catalog.Current with { Notes = new NotesSettings(Folder("elsewhere")) };

        catalog.Raise();

        Assert.Equal(Folder("elsewhere"), vm.NotesFolderInput);
        Assert.True(vm.IsNotesFolderCustomized);
    }

    [Fact]
    public void ThemeChange_DoesNotOverwriteWhatIsBeingTyped()
    {
        var (vm, catalog, _) = Make(Folder("saved"));
        vm.NotesFolderInput = Folder("editing");

        // 別的設定（主題）改了，筆記資料夾的設定沒變：正在編輯的文字不能被蓋掉
        catalog.Current = catalog.Current with { Theme = ThemePreference.Dark };
        catalog.Raise();

        Assert.Equal(Folder("editing"), vm.NotesFolderInput);
    }
}
