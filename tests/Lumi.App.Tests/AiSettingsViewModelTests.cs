using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Models;

namespace Lumi.App.Tests;

/// <summary>M22 22D：合併後「AI 連線」頁的清單（連線 → 模型 → 新增模型）與選取、兩個編輯器同時只開一個。</summary>
public sealed class AiSettingsViewModelTests
{
    private const string OllamaId = "ollama";

    /// <summary>預設目錄之外再加一個有兩個模型的 Ollama 連線（顯示名稱故意不依 ID 排序）。</summary>
    private static async Task AddOllamaAsync(SettingsHost host)
    {
        await host.Catalog.SaveConnectionAsync(new ConnectionSettings(OllamaId, "Ollama", ProviderFormat.OpenAICompatible, "http://localhost:11434/v1"), null);
        await host.Catalog.SaveModelAsync(new ModelSettings("llama-b", OllamaId, "llama3", "Llama 3", 4096, 128_000, false));
        await host.Catalog.SaveModelAsync(new ModelSettings("gemma-a", OllamaId, "gemma", "Gemma", 4096, 128_000, false));
    }

    [Fact]
    public async Task Items_AreGroupedByConnection_ModelsThenAnAddRow()
    {
        using var host = new SettingsHost();
        await AddOllamaAsync(host);
        var ai = host.Settings.AiSettings;

        var expected = new List<string>();
        foreach (var connection in host.Connections.Items)
        {
            expected.Add($"connection:{connection.Id}");
            expected.AddRange(host.Models.Items.Where(m => m.ConnectionId == connection.Id).Select(m => $"model:{m.Id}"));
            expected.Add($"add-model:{connection.Id}");
        }
        Assert.Equal(expected, ai.Items.Select(i => i.Key).ToList());

        // Ollama 這一組：連線、模型（依顯示名稱排序）、新增模型
        var group = ai.Items.SkipWhile(i => i is not AiConnectionItem { Id: OllamaId }).Take(4).ToList();
        Assert.IsType<AiConnectionItem>(group[0]);
        Assert.Equal(["Gemma", "Llama 3"], group.OfType<AiModelItem>().Select(m => m.Model.DisplayName));
        Assert.IsType<AiAddModelItem>(group[3]);
        Assert.Equal(OllamaId, ((AiAddModelItem)group[3]).ConnectionId);
        Assert.Equal(Strings.AiSettings_ListName, ai.ListTitle);
    }

    [Fact]
    public async Task OnlyTheDefaultModel_CarriesTheDefaultTag()
    {
        using var host = new SettingsHost();
        await AddOllamaAsync(host);
        var models = host.Settings.AiSettings.Items.OfType<AiModelItem>().ToList();

        var defaults = models.Where(m => m.HasTag).ToList();
        var only = Assert.Single(defaults);
        Assert.Equal(host.Catalog.Current.WorkbenchModelId, only.Id);
        Assert.Equal(Strings.AiSettings_DefaultTag, only.Tag);
        Assert.Contains(Strings.AiSettings_DefaultTag, only.AutomationName);
        Assert.All(models.Where(m => !m.HasTag), m => Assert.Null(m.Tag));
    }

    [Fact]
    public async Task SelectingAConnection_ShowsTheConnectionForm_AndAModelShowsTheModelForm()
    {
        using var host = new SettingsHost();
        await AddOllamaAsync(host);
        var ai = host.Settings.AiSettings;
        Assert.True(ai.ShowPlaceholder);
        Assert.False(ai.ShowConnectionEditor);
        Assert.False(ai.ShowModelEditor);
        Assert.Null(ai.SelectedItem);

        var connection = ai.Items.OfType<AiConnectionItem>().Single(c => c.Id == OllamaId);
        Assert.True(await ai.SelectAsync(connection));
        Assert.True(ai.ShowConnectionEditor);
        Assert.False(ai.ShowModelEditor);
        Assert.False(ai.ShowPlaceholder);
        Assert.Equal("Ollama", host.Connections.Name);
        Assert.Equal(connection, ai.SelectedItem);

        var model = ai.Items.OfType<AiModelItem>().Single(m => m.Id == "llama-b");
        Assert.True(await ai.SelectAsync(model));
        Assert.False(ai.ShowConnectionEditor);   // 兩個編輯器同時只開一個
        Assert.True(ai.ShowModelEditor);
        Assert.False(host.Connections.HasEditor);
        Assert.Equal("llama3", host.Models.ModelId);
        Assert.Equal(model, ai.SelectedItem);

        Assert.True(await ai.SelectAsync(connection));
        Assert.False(host.Models.HasEditor);
        Assert.Equal(connection, ai.SelectedItem);
    }

    [Fact]
    public async Task TheAddModelRow_StartsANewModelOnItsOwnConnection()
    {
        using var host = new SettingsHost();
        await AddOllamaAsync(host);
        var ai = host.Settings.AiSettings;
        var add = ai.Items.OfType<AiAddModelItem>().Single(a => a.ConnectionId == OllamaId);

        Assert.True(await ai.SelectAsync(add));

        Assert.True(host.Models.HasEditor);
        Assert.True(host.Models.IsNew);
        Assert.Equal(OllamaId, host.Models.SelectedConnection?.Id);   // 預先選好這個連線
        Assert.Equal(add, ai.SelectedItem);
        Assert.True(ai.ShowModelEditor);

        // 其他連線底下的「新增模型」預選它自己的連線
        var other = ai.Items.OfType<AiAddModelItem>().First(a => a.ConnectionId != OllamaId);
        Assert.True(await ai.SelectAsync(other));
        Assert.Equal(other.ConnectionId, host.Models.SelectedConnection?.Id);
        Assert.Equal(other, ai.SelectedItem);
    }

    [Fact]
    public async Task ASavedNewModel_BecomesTheSelectedRowUnderItsConnection()
    {
        using var host = new SettingsHost();
        await AddOllamaAsync(host);
        var ai = host.Settings.AiSettings;
        await ai.SelectAsync(ai.Items.OfType<AiAddModelItem>().Single(a => a.ConnectionId == OllamaId));
        host.Models.ModelId = "qwen";
        await host.Models.SaveCommand.ExecuteAsync(null);

        var selected = Assert.IsType<AiModelItem>(ai.SelectedItem);
        Assert.Equal("qwen", selected.Model.ModelId);
        Assert.Equal(OllamaId, selected.ConnectionId);
    }

    [Fact]
    public async Task SwitchingWithUnsavedChanges_AsksFirst_AndCancelKeepsTheEditor()
    {
        using var host = new SettingsHost();
        await AddOllamaAsync(host);
        var ai = host.Settings.AiSettings;
        var connection = ai.Items.OfType<AiConnectionItem>().Single(c => c.Id == OllamaId);
        var model = ai.Items.OfType<AiModelItem>().Single(m => m.Id == "llama-b");
        await ai.SelectAsync(connection);
        host.Connections.Name = "改到一半";

        host.Confirm.Answer = false;
        Assert.False(await ai.SelectAsync(model));
        Assert.Single(host.Confirm.Asked);
        Assert.True(host.Connections.HasEditor);
        Assert.False(host.Models.HasEditor);
        Assert.Equal("改到一半", host.Connections.Name);
        Assert.Equal(connection, ai.SelectedItem);

        host.Confirm.Answer = true;
        Assert.True(await ai.SelectAsync(model));
        Assert.Equal(2, host.Confirm.Asked.Count);
        Assert.False(host.Connections.HasEditor);
        Assert.Equal(model, ai.SelectedItem);

        // 模型表單改到一半再切到連線，一樣要問
        host.Models.DisplayName = "又改了";
        host.Confirm.Answer = false;
        Assert.False(await ai.SelectAsync(connection));
        Assert.True(host.Models.HasEditor);
        Assert.Equal(3, host.Confirm.Asked.Count);
    }

    [Fact]
    public async Task TheHeaderNewConnection_ClosesTheModelForm_AndStartsANewConnection()
    {
        using var host = new SettingsHost();
        var ai = host.Settings.AiSettings;
        await ai.SelectAsync(ai.Items.OfType<AiModelItem>().First());
        Assert.True(host.Models.HasEditor);

        await ai.NewConnectionCommand.ExecuteAsync(null);

        Assert.False(host.Models.HasEditor);
        Assert.True(host.Connections.HasEditor);
        Assert.True(host.Connections.IsNew);
        Assert.True(ai.ShowConnectionEditor);
        Assert.False(ai.ShowModelEditor);
    }

    [Fact]
    public async Task ANewConnectionWithUnsavedModelChanges_AsksBeforeLeavingTheModelForm()
    {
        using var host = new SettingsHost();
        var ai = host.Settings.AiSettings;
        await ai.SelectAsync(ai.Items.OfType<AiModelItem>().First());
        host.Models.DisplayName = "改到一半";
        host.Confirm.Answer = false;

        await ai.NewConnectionCommand.ExecuteAsync(null);

        Assert.Single(host.Confirm.Asked);
        Assert.True(host.Models.HasEditor);
        Assert.False(host.Connections.HasEditor);
    }

    [Fact]
    public async Task SavingANewConnection_SelectsIt_AndHintsToAddAModel()
    {
        using var host = new SettingsHost();
        var ai = host.Settings.AiSettings;
        await ai.NewConnectionCommand.ExecuteAsync(null);
        host.Connections.Name = "新的公司連線";
        host.Connections.BaseUrl = "https://gateway.example.com/v1";

        await host.Connections.SaveCommand.ExecuteAsync(null);

        var selected = Assert.IsType<AiConnectionItem>(ai.SelectedItem);
        Assert.Equal("新的公司連線", selected.Connection.Name);
        Assert.Equal(Strings.AiSettings_ConnectionSavedHint, host.Connections.Notice);
        // 新連線底下已經有「新增模型」可以按
        Assert.Contains(ai.Items, i => i is AiAddModelItem a && a.ConnectionId == selected.Id);

        // 選別的項目後，提示就不再顯示；再存一次既有連線也不會有
        await ai.SelectAsync(ai.Items.OfType<AiConnectionItem>().First(c => c.Id != selected.Id));
        Assert.Null(host.Connections.Notice);
        await host.Connections.SaveCommand.ExecuteAsync(null);
        Assert.Null(host.Connections.Notice);
    }

    [Fact]
    public async Task NavigatingToAConnection_ClosesTheModelForm_SelectsTheConnection_AndFocusesTheKey()
    {
        using var host = new SettingsHost();
        await AddOllamaAsync(host);
        var ai = host.Settings.AiSettings;
        await ai.SelectAsync(ai.Items.OfType<AiModelItem>().First());
        var focusRequests = 0;
        host.Connections.FocusApiKeyRequested += () => focusRequests++;

        await host.Settings.NavigateAsync(new SettingsNavigation(SettingsSection.Connections, OllamaId, FocusApiKey: true, Notice: "缺金鑰"));

        Assert.True(host.Settings.ShowConnectionsAndModels);
        Assert.False(host.Models.HasEditor);
        Assert.Equal(OllamaId, Assert.IsType<AiConnectionItem>(ai.SelectedItem).Id);
        Assert.True(host.Connections.ReplacingKey);
        Assert.Equal(1, focusRequests);
        Assert.Equal("缺金鑰", host.Connections.Notice);
    }

    [Fact]
    public async Task TheModelsSection_OpensTheSamePage_WithoutSelectingAnything()
    {
        using var host = new SettingsHost();
        host.Settings.CategoryIndex = (int)SettingsCategory.Home;

        await host.Settings.NavigateAsync(new SettingsNavigation(SettingsSection.Models));

        Assert.True(host.Settings.ShowConnectionsAndModels);
        Assert.Null(host.Settings.AiSettings.SelectedItem);
        Assert.True(host.Settings.AiSettings.ShowPlaceholder);
    }

    [Fact]
    public async Task WithoutConnections_TheListIsEmpty_WithTheEmptyFlag()
    {
        using var host = new SettingsHost();
        var ai = host.Settings.AiSettings;
        Assert.False(ai.IsEmpty);

        foreach (var connection in host.Catalog.Current.Connections.ToList())
            await host.Catalog.DeleteConnectionAsync(connection.Id);

        Assert.True(ai.IsEmpty);
        Assert.Empty(ai.Items);
        Assert.True(ai.ShowPlaceholder);
    }

    [Fact]
    public async Task DeletingTheSelectedModel_RemovesItsRow_AndClearsTheSelection()
    {
        using var host = new SettingsHost();
        await AddOllamaAsync(host);
        var ai = host.Settings.AiSettings;
        await ai.SelectAsync(ai.Items.OfType<AiModelItem>().Single(m => m.Id == "gemma-a"));

        await host.Models.DeleteCommand.ExecuteAsync(null);

        Assert.DoesNotContain(ai.Items, i => i is AiModelItem { Id: "gemma-a" });
        Assert.Null(ai.SelectedItem);
        Assert.True(ai.ShowPlaceholder);
    }

    [Fact]
    public async Task EveryRow_HasAnAccessibleName()
    {
        using var host = new SettingsHost();
        await AddOllamaAsync(host);
        Assert.All(host.Settings.AiSettings.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.AutomationName)));
        var add = host.Settings.AiSettings.Items.OfType<AiAddModelItem>().First();
        Assert.Equal(Strings.AiSettings_AddModel, add.AutomationName);
        var connection = host.Settings.AiSettings.Items.OfType<AiConnectionItem>().First();
        Assert.Contains(connection.Connection.StatusText, connection.AutomationName);
    }
}
