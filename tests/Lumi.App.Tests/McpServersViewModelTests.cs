using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Mcp;
using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Lumi.Core.Speech;

namespace Lumi.App.Tests;

/// <summary>
/// 記錄 MCP 寫入與祕密的目錄替身：<see cref="FakeModelCatalog"/> 的 MCP 方法不保存祕密，測試祕密時改用這個。
/// 其他方法不會被 MCP 畫面呼叫，直接丟例外。
/// </summary>
internal sealed class McpCatalogSpy : IModelCatalog
{
    public McpCatalogSpy(params McpServerSettings[] servers)
    {
        Current = AppSettings.Empty with { McpServers = servers };
    }

    public AppSettings Current { get; set; }

    public string? LoadProblem => null;

    public event EventHandler<AppSettings>? Changed;

    /// <summary>目前保存的祕密（依伺服器 Id）。</summary>
    public Dictionary<string, McpServerSecrets> Secrets { get; } = [];

    /// <summary>每次 <see cref="SaveMcpServerAsync"/> 收到的參數（祕密為 null 表示不變）。</summary>
    public List<(McpServerSettings Server, McpServerSecrets? Secrets)> Saves { get; } = [];

    public List<string> Deletes { get; } = [];

    public Func<McpServerSettings, bool> FailSaveFor { get; set; } = _ => false;

    public Task SaveMcpServerAsync(McpServerSettings server, McpServerSecrets? newSecrets, CancellationToken ct = default)
    {
        if (FailSaveFor(server))
            throw new InvalidOperationException("測試用的寫入失敗");
        Saves.Add((server, newSecrets));
        if (newSecrets is not null)
            Secrets[server.Id] = newSecrets;
        Current = Current with { McpServers = Current.McpServers.Where(s => s.Id != server.Id).Append(server).ToList() };
        Changed?.Invoke(this, Current);
        return Task.CompletedTask;
    }

    public Task DeleteMcpServerAsync(string serverId, CancellationToken ct = default)
    {
        Deletes.Add(serverId);
        Secrets.Remove(serverId);
        Current = Current with { McpServers = Current.McpServers.Where(s => s.Id != serverId).ToList() };
        Changed?.Invoke(this, Current);
        return Task.CompletedTask;
    }

    public Task<McpServerSecrets> GetMcpSecretsAsync(string serverId, CancellationToken ct = default) =>
        Task.FromResult(Secrets.TryGetValue(serverId, out var secrets) ? secrets : McpServerSecrets.Empty);

    public Task SaveConnectionAsync(ConnectionSettings connection, SecretValue? newApiKey, CancellationToken ct) => throw new NotSupportedException();
    public Task<bool> HasApiKeyAsync(string connectionId, CancellationToken ct) => throw new NotSupportedException();
    public Task ClearApiKeyAsync(string connectionId, CancellationToken ct) => throw new NotSupportedException();
    public Task DeleteConnectionAsync(string connectionId, CancellationToken ct) => throw new NotSupportedException();
    public Task SaveModelAsync(ModelSettings model, CancellationToken ct) => throw new NotSupportedException();
    public Task DeleteModelAsync(string modelSettingsId, CancellationToken ct) => throw new NotSupportedException();
    public Task SetWorkbenchModelAsync(string modelSettingsId, CancellationToken ct) => throw new NotSupportedException();
    public Task SetSpriteModelAsync(string modelSettingsId, CancellationToken ct) => throw new NotSupportedException();
    public Task SetThemeAsync(ThemePreference theme, CancellationToken ct) => throw new NotSupportedException();
    public Task SetThemeIdAsync(string themeId, CancellationToken ct) => throw new NotSupportedException();
    public Task SetPersonaAsync(PersonaSettings persona, CancellationToken ct) => throw new NotSupportedException();
    public Task SetSpriteAsync(SpriteSettings sprite, CancellationToken ct) => throw new NotSupportedException();
    public Task SetWorkbenchWindowAsync(WindowSettings window, CancellationToken ct) => throw new NotSupportedException();
    public Task SetSpeechAsync(SpeechSettings settings, SecretValue? newToken, CancellationToken ct) => throw new NotSupportedException();
    public Task<bool> HasSpeechTokenAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task ClearSpeechTokenAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task SetNotesFolderAsync(string folder, CancellationToken ct) => throw new NotSupportedException();
    public Task SetMemoryEnabledAsync(bool enabled, CancellationToken ct) => throw new NotSupportedException();
    public Task SetToolsAsync(ToolSettings tools, CancellationToken ct) => throw new NotSupportedException();
    public ResolvedModel? Resolve(string? modelSettingsId) => throw new NotSupportedException();
}

public sealed class McpServersViewModelTests
{
    private static McpServerSettings Stdio(string id, string key, string name, params string[] envNames) =>
        new(id, key, name, McpTransportKind.Stdio, "npx", ["-y", "server"], null, envNames, null, [], true, false, 60, "v1");

    private static McpServersViewModel Create(McpServerSettings[]? servers = null, FakeMcpToolSource? mcp = null,
        FakeConfirmService? confirm = null, McpCatalogSpy? catalog = null)
    {
        catalog ??= new McpCatalogSpy(servers ?? []);
        return new McpServersViewModel(catalog, mcp ?? new FakeMcpToolSource(), confirm ?? new FakeConfirmService(), new SyncUiDispatcher());
    }

    private static async Task NewStdioAsync(McpServersViewModel vm, string name = "GitHub", string command = "npx")
    {
        await vm.NewServerCommand.ExecuteAsync(null);
        vm.Name = name;
        vm.Command = command;
    }

    [Fact]
    public async Task NewStdioServer_ValidForm_CanSave_AndKeyFollowsTheName()
    {
        var catalog = new McpCatalogSpy();
        var vm = Create(catalog: catalog);
        await NewStdioAsync(vm, "GitHub Tools");

        Assert.True(vm.IsNew);
        Assert.True(vm.IsStdio);
        Assert.Equal("github-tools", vm.Key);   // 代號跟著名稱產生
        Assert.True(vm.CanSave);

        await vm.SaveCommand.ExecuteAsync(null);

        var saved = Assert.Single(catalog.Saves).Server;
        Assert.Equal(McpTransportKind.Stdio, saved.Transport);
        Assert.Equal("npx", saved.Command);
        Assert.Null(catalog.Saves[0].Secrets);   // 沒有環境變數，祕密不變
        Assert.False(vm.IsNew);
        Assert.Equal("GitHub Tools", Assert.Single(vm.Items).Name);
    }

    [Fact]
    public async Task NewHttpServer_NeedsAnHttpsUrl_ExceptLocalhost()
    {
        var vm = Create();
        await vm.NewServerCommand.ExecuteAsync(null);
        vm.IsHttp = true;
        vm.Name = "知識庫";

        vm.Url = "http://kb.corp/mcp";
        Assert.Equal(McpServerRules.ValidateUrl("http://kb.corp/mcp"), vm.UrlError);
        Assert.NotNull(vm.UrlError);
        Assert.False(vm.CanSave);

        vm.Url = "http://localhost:8080/mcp";
        Assert.Null(vm.UrlError);
        Assert.True(vm.CanSave);
    }

    [Fact]
    public async Task EmptyName_AndEmptyCommand_ShowTheRuleText_AndBlockSave()
    {
        var vm = Create();
        await vm.NewServerCommand.ExecuteAsync(null);
        vm.Name = "";
        vm.Command = "  ";

        Assert.Equal(McpServerRules.ValidateName(""), vm.NameError);
        Assert.Equal(McpServerRules.ValidateCommand("  "), vm.CommandError);
        Assert.True(vm.HasFieldErrors);
        Assert.False(vm.CanSave);
    }

    [Fact]
    public async Task KeyAlreadyUsedByAnotherServer_IsAnError()
    {
        var vm = Create([Stdio("a", "github", "GitHub")]);
        await vm.NewServerCommand.ExecuteAsync(null);
        vm.Name = "Other";
        vm.Key = "github";

        Assert.Equal(McpServerRules.ValidateKey("github", ["github"]), vm.KeyError);
        Assert.True(vm.AdvancedHasError);
        Assert.False(vm.CanSave);
    }

    [Fact]
    public async Task EnvNames_AreCheckedByRules_AndDuplicatesAreRejected()
    {
        var vm = Create();
        await NewStdioAsync(vm);
        vm.AddEnvCommand.Execute(null);
        vm.AddEnvCommand.Execute(null);
        var first = vm.EnvRows[0];
        var second = vm.EnvRows[1];

        first.Name = "1BAD";
        Assert.Equal(McpServerRules.ValidateEnvName("1BAD"), first.NameError);
        first.Name = "TOKEN";
        second.Name = "token";
        Assert.Equal(Strings.Mcp_SecretNameDuplicate, second.NameError);
        Assert.False(vm.CanSave);
    }

    [Fact]
    public async Task Save_WithoutSecretChanges_PassesNull()
    {
        var server = Stdio("s1", "github", "GitHub", "GITHUB_TOKEN");
        var catalog = new McpCatalogSpy(server);
        catalog.Secrets["s1"] = new McpServerSecrets(new Dictionary<string, string> { ["GITHUB_TOKEN"] = "ghp_OLD" }, new Dictionary<string, string>());
        var vm = Create(catalog: catalog);
        await vm.SelectAsync(Assert.Single(vm.Items));

        vm.TimeoutText = "90";   // 只改其他欄位
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(Assert.Single(catalog.Saves).Secrets);
        Assert.Equal("ghp_OLD", catalog.Secrets["s1"].Env["GITHUB_TOKEN"]);
    }

    [Fact]
    public async Task ReplacingOneEnvValue_PassesTheWholeSecretSet()
    {
        var server = Stdio("s1", "github", "GitHub", "GITHUB_TOKEN", "OTHER");
        var catalog = new McpCatalogSpy(server);
        catalog.Secrets["s1"] = new McpServerSecrets(
            new Dictionary<string, string> { ["GITHUB_TOKEN"] = "ghp_OLD", ["OTHER"] = "keep" }, new Dictionary<string, string>());
        var vm = Create(catalog: catalog);
        await vm.SelectAsync(Assert.Single(vm.Items));

        Assert.True(vm.EnvRows[0].IsSaved);
        Assert.True(vm.EnvRows[0].ShowStored);   // 已儲存的值不讀回畫面，只顯示「已設定」
        Assert.Equal("", vm.EnvRows[0].Value);

        vm.EnvRows[0].ReplaceCommand.Execute(null);
        vm.EnvRows[0].Value = "ghp_NEW";
        await vm.SaveCommand.ExecuteAsync(null);

        var secrets = Assert.Single(catalog.Saves).Secrets!;
        Assert.Equal("ghp_NEW", secrets.Env["GITHUB_TOKEN"]);
        Assert.Equal("keep", secrets.Env["OTHER"]);   // 沒更換的值沿用
    }

    [Fact]
    public async Task RemovingAStoredEnvRow_PassesSecretsWithoutIt()
    {
        var server = Stdio("s1", "github", "GitHub", "GITHUB_TOKEN");
        var catalog = new McpCatalogSpy(server);
        catalog.Secrets["s1"] = new McpServerSecrets(new Dictionary<string, string> { ["GITHUB_TOKEN"] = "ghp_OLD" }, new Dictionary<string, string>());
        var vm = Create(catalog: catalog);
        await vm.SelectAsync(Assert.Single(vm.Items));

        vm.EnvRows[0].RemoveCommand.Execute(null);
        await vm.SaveCommand.ExecuteAsync(null);

        var secrets = Assert.Single(catalog.Saves).Secrets!;
        Assert.Empty(secrets.Env);
        Assert.Empty(vm.EnvRows.Select(r => r.Name));
    }

    [Fact]
    public async Task DeleteAsksFirst_AndCancelLeavesTheServer()
    {
        var server = Stdio("s1", "github", "GitHub");
        var catalog = new McpCatalogSpy(server);
        var confirm = new FakeConfirmService { Answer = false };
        var vm = Create(catalog: catalog, confirm: confirm);
        await vm.SelectAsync(Assert.Single(vm.Items));

        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.Single(confirm.Asked);
        Assert.Empty(catalog.Deletes);
        Assert.True(vm.HasEditor);
    }

    [Fact]
    public async Task DeleteConfirmed_RemovesTheServer()
    {
        var catalog = new McpCatalogSpy(Stdio("s1", "github", "GitHub"));
        var vm = Create(catalog: catalog, confirm: new FakeConfirmService { Answer = true });
        await vm.SelectAsync(Assert.Single(vm.Items));

        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.Equal("s1", Assert.Single(catalog.Deletes));
        Assert.False(vm.HasEditor);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task TestConnection_UsesUnsavedValues_AndTheMergedSecrets()
    {
        var mcp = new FakeMcpToolSource { NextTestResult = new McpTestResult(true, null, ["search", "read"]) };
        var vm = Create(mcp: mcp);
        await NewStdioAsync(vm, "Draft", "uvx");
        vm.TimeoutText = "30";
        vm.AddEnvCommand.Execute(null);
        vm.EnvRows[0].Name = "API_KEY";
        vm.EnvRows[0].Value = "unsaved-value";

        await vm.TestCommand.ExecuteAsync(null);

        var call = Assert.Single(mcp.TestCalls);
        Assert.Equal("uvx", call.Server.Command);
        Assert.Equal(30, call.Server.TimeoutSeconds);
        Assert.Equal("unsaved-value", call.Secrets.Env["API_KEY"]);
        Assert.True(vm.ShowTestOk);
        Assert.Equal("search、read", vm.TestToolNames);
        Assert.DoesNotContain(vm.Items, i => i.Name == "Draft");   // 測試不會儲存
    }

    [Fact]
    public async Task TestFailure_ShowsTheReason()
    {
        var mcp = new FakeMcpToolSource { NextTestResult = new McpTestResult(false, "找不到指令 uvx", []) };
        var vm = Create(mcp: mcp);
        await NewStdioAsync(vm, "Draft", "uvx");

        await vm.TestCommand.ExecuteAsync(null);

        Assert.True(vm.ShowTestFailure);
        Assert.Equal("找不到指令 uvx", vm.TestResultText);
    }

    [Fact]
    public async Task ChangingAField_ClearsTheTestResult()
    {
        var mcp = new FakeMcpToolSource { NextTestResult = new McpTestResult(true, null, ["a"]) };
        var vm = Create(mcp: mcp);
        await NewStdioAsync(vm, "Draft", "uvx");
        await vm.TestCommand.ExecuteAsync(null);
        Assert.True(vm.HasTestResult);

        vm.Command = "npx";

        Assert.False(vm.HasTestResult);
        Assert.Null(vm.TestToolNames);
    }

    [Fact]
    public async Task StatusFollowsChangedEvents()
    {
        var mcp = new FakeMcpToolSource();
        var server = Stdio("s1", "github", "GitHub");
        var vm = Create([server], mcp);
        mcp.Statuses.Add(new McpServerStatus("s1", McpServerState.Connecting, null, 0));
        mcp.Raise();
        Assert.Equal(McpServerState.Connecting, Assert.Single(vm.Items).State);

        mcp.Statuses[0] = new McpServerStatus("s1", McpServerState.Ready, null, 12);
        mcp.Raise();
        Assert.Equal("已連線 · 12 個工具", Assert.Single(vm.Items).StatusText);
    }

    [Fact]
    public async Task IsDirty_TracksUnsavedEdits()
    {
        var vm = Create([Stdio("s1", "github", "GitHub")]);
        await vm.SelectAsync(Assert.Single(vm.Items));
        Assert.False(vm.IsDirty);

        vm.Name = "GitHub 2";
        Assert.True(vm.IsDirty);

        vm.Name = "GitHub";
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task SwitchingWithUnsavedChanges_AsksFirst_AndCancelKeepsTheEditor()
    {
        var confirm = new FakeConfirmService { Answer = false };
        var vm = Create([Stdio("s1", "github", "GitHub"), Stdio("s2", "other", "Other")], confirm: confirm);
        await vm.SelectAsync(vm.Items[0]);
        vm.Name = "Changed";

        var kept = await vm.SelectAsync(vm.Items[1]);

        Assert.False(kept);
        Assert.Single(confirm.Asked);
        Assert.Equal("Changed", vm.Name);
    }

    [Fact]
    public async Task SaveFailure_ShowsTheMessage_AndKeepsTheForm()
    {
        var catalog = new McpCatalogSpy { FailSaveFor = _ => true };
        var vm = Create(catalog: catalog);
        await NewStdioAsync(vm, "Broken");

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.HasSaveError);
        Assert.True(vm.IsNew);
        Assert.Equal("測試用的寫入失敗", vm.SaveError);
    }
}
