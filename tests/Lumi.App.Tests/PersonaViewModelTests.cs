using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Models;

namespace Lumi.App.Tests;

/// <summary>5C：設定視窗「人格與主動性」——草稿、儲存、驗證、未儲存提示。</summary>
public sealed class PersonaViewModelTests
{
    [Fact]
    public void StartsFromTheSavedPersona_NotDirty()
    {
        using var host = new SettingsHost();

        var vm = host.Persona;

        Assert.Equal(PersonaStyle.Warm, vm.SelectedStyle.Style);
        Assert.Equal(Proactivity.Balanced, vm.SelectedProactivity.Proactivity);
        Assert.Equal("", vm.CustomPersona);
        Assert.False(vm.IsDirty);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.Equal(4, vm.Styles.Count);
        Assert.Equal(3, vm.Proactivities.Count);
        Assert.All(vm.Styles, s => { Assert.False(string.IsNullOrEmpty(s.Text)); Assert.False(string.IsNullOrEmpty(s.Hint)); });
        Assert.All(vm.Proactivities, p => { Assert.False(string.IsNullOrEmpty(p.Text)); Assert.False(string.IsNullOrEmpty(p.Hint)); });
    }

    [Fact]
    public async Task Edit_MakesItDirty_SavePersistsAndSurvivesReload()
    {
        using var host = new SettingsHost();
        var vm = host.Persona;

        vm.SelectedStyle = vm.Styles.Single(s => s.Style == PersonaStyle.Playful);
        vm.SelectedProactivity = vm.Proactivities.Single(p => p.Proactivity == Proactivity.Proactive);
        vm.CustomPersona = "  叫我 Neil  ";
        Assert.True(vm.IsDirty);
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.Equal(PersonaSettings.Default, host.Catalog.Current.Persona);   // 還沒儲存，設定沒變

        await vm.SaveCommand.ExecuteAsync(null);

        var expected = new PersonaSettings(PersonaStyle.Playful, Proactivity.Proactive, "叫我 Neil");
        Assert.Equal(expected, host.Catalog.Current.Persona);
        Assert.False(vm.IsDirty);
        Assert.False(vm.MessageIsError);
        Assert.Contains("下一則", vm.Message);
        var reloaded = new Lumi.Infrastructure.Settings.JsonModelCatalog(
            Microsoft.Extensions.Options.Options.Create(new Lumi.Infrastructure.Settings.ModelCatalogOptions { SettingsPath = host.SettingsPath }),
            host.Secrets, Microsoft.Extensions.Logging.Abstractions.NullLogger<Lumi.Infrastructure.Settings.JsonModelCatalog>.Instance);
        Assert.Equal(expected, reloaded.Current.Persona);
    }

    [Fact]
    public void EditingBack_ToTheSavedValues_IsNotDirty()
    {
        using var host = new SettingsHost();
        var vm = host.Persona;

        vm.CustomPersona = "暫時";
        Assert.True(vm.IsDirty);
        vm.CustomPersona = "   ";   // 空白視為沒有，和已儲存的一樣

        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void TooLong_ShowsError_AndCannotSave()
    {
        using var host = new SettingsHost();
        var vm = host.Persona;

        vm.CustomPersona = new string('字', PersonaSettings.CustomPersonaMaxLength + 1);

        Assert.True(vm.HasCustomError);
        Assert.Contains("2000", vm.CustomError);
        Assert.Equal("2001 / 2000", vm.CustomCounter);
        Assert.False(vm.SaveCommand.CanExecute(null));

        vm.CustomPersona = new string('字', PersonaSettings.CustomPersonaMaxLength);
        Assert.False(vm.HasCustomError);
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void Counter_IgnoresSurroundingWhitespace()
    {
        using var host = new SettingsHost();

        host.Persona.CustomPersona = "  嗨  \n";

        Assert.Equal("1 / 2000", host.Persona.CustomCounter);
    }

    [Fact]
    public async Task Reset_RestoresDefaultsInTheDraft_ButDoesNotSave()
    {
        using var host = new SettingsHost();
        await host.Catalog.SetPersonaAsync(new PersonaSettings(PersonaStyle.Calm, Proactivity.Reserved, "x"));
        await SettingsHost.WaitUntil(() => host.Persona.SelectedStyle.Style == PersonaStyle.Calm);

        host.Persona.ResetCommand.Execute(null);

        Assert.Equal(PersonaStyle.Warm, host.Persona.SelectedStyle.Style);
        Assert.Equal("", host.Persona.CustomPersona);
        Assert.True(host.Persona.IsDirty);   // 和已儲存的不同，要按儲存才算數
        Assert.Equal(PersonaStyle.Calm, host.Catalog.Current.Persona.Style);
    }

    [Fact]
    public async Task ExternalChange_UpdatesTheDraft_UnlessYouAreEditing()
    {
        using var host = new SettingsHost();

        await host.Catalog.SetPersonaAsync(new PersonaSettings(PersonaStyle.Professional, Proactivity.Balanced, null));
        Assert.Equal(PersonaStyle.Professional, host.Persona.SelectedStyle.Style);   // 沒有改到一半：跟著設定
        Assert.False(host.Persona.IsDirty);

        host.Persona.CustomPersona = "編輯中";
        await host.Catalog.SetPersonaAsync(new PersonaSettings(PersonaStyle.Calm, Proactivity.Balanced, null));

        Assert.Equal(PersonaStyle.Professional, host.Persona.SelectedStyle.Style);   // 草稿不被蓋掉
        Assert.Equal("編輯中", host.Persona.CustomPersona);
        Assert.True(host.Persona.IsDirty);
    }

    [Fact]
    public async Task ReadOnlyCatalog_ShowsTheReason_AndStaysDirty()
    {
        var newer = $$"""{ "version": {{Lumi.Infrastructure.Settings.JsonModelCatalog.CurrentVersion + 1}}, "connections": [], "models": [], "theme": "system" }""";
        using var host = new SettingsHost(newer);
        host.Persona.CustomPersona = "x";

        await host.Persona.SaveCommand.ExecuteAsync(null);

        Assert.True(host.Persona.MessageIsError);
        Assert.True(host.Persona.HasMessage);
        Assert.True(host.Persona.IsDirty);
    }

    [Fact]
    public async Task EditingClearsTheSavedMessage()
    {
        using var host = new SettingsHost();
        host.Persona.CustomPersona = "a";
        await host.Persona.SaveCommand.ExecuteAsync(null);
        Assert.True(host.Persona.HasMessage);

        host.Persona.CustomPersona = "ab";

        Assert.False(host.Persona.HasMessage);
    }

    [Fact]
    public async Task UnsavedPersonaEdits_AskBeforeClosingTheSettingsWindow()
    {
        using var host = new SettingsHost();
        Assert.True(await host.Settings.ConfirmCloseAsync());
        Assert.Empty(host.Confirm.Asked);

        host.Persona.CustomPersona = "改到一半";
        Assert.True(host.Settings.HasUnsavedChanges);
        host.Confirm.Answer = false;

        Assert.False(await host.Settings.ConfirmCloseAsync());
        Assert.Single(host.Confirm.Asked);
    }
}
