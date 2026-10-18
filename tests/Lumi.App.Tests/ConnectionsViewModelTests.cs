using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Models;

namespace Lumi.App.Tests;

public sealed class ConnectionsViewModelTests
{
    private const string PlainKey = "sk-openai-PLAINTEXT-5678";

    private static async Task<ConnectionsViewModel> NewEditorAsync(SettingsHost host, string name = "公司 Gateway", string url = "https://gw.example.com/v1")
    {
        var vm = host.Connections;
        await vm.NewConnectionCommand.ExecuteAsync(null);
        vm.Name = name;
        vm.BaseUrl = url;
        return vm;
    }

    [Fact]
    public async Task NewConnection_StartsWithOpenAITemplate_AndFollowsFormatUntilEdited()
    {
        using var host = new SettingsHost();
        var vm = host.Connections;

        await vm.NewConnectionCommand.ExecuteAsync(null);

        Assert.True(vm.HasEditor);
        Assert.True(vm.IsNew);
        Assert.Equal(ConnectionsViewModel.OpenAITemplateUrl, vm.BaseUrl);
        vm.Format = FormatOption.All[0];
        Assert.Equal(ConnectionsViewModel.AnthropicTemplateUrl, vm.BaseUrl);

        vm.BaseUrl = "http://localhost:11434/v1";
        vm.Format = FormatOption.All[1];
        Assert.Equal("http://localhost:11434/v1", vm.BaseUrl);   // 改過的就不再被範本覆蓋
    }

    [Fact]
    public async Task Save_NewConnection_AppearsInList_AndKeyIsStoredOnlyWhenTyped()
    {
        using var host = new SettingsHost();
        var vm = await NewEditorAsync(host);
        vm.NewApiKey = $"  {PlainKey}  ";

        await vm.SaveCommand.ExecuteAsync(null);

        var item = Assert.Single(vm.Items, i => i.Name == "公司 Gateway");
        Assert.Equal(PlainKey, host.Secrets.Items[$"connection/{item.Id}/api-key"]);
        Assert.Equal("5678", host.Catalog.Current.Connections.Single(c => c.Id == item.Id).ApiKeyHint);
        Assert.False(vm.IsNew);
        Assert.True(vm.HasStoredKey);
        Assert.Equal("5678", vm.KeyHint);
        Assert.Equal("", vm.NewApiKey);   // 輸入過的金鑰不留在 ViewModel
        Assert.Equal("已設定 · 結尾 5678", vm.KeyStatusText);
        Assert.False(vm.ShowKeyInput);
    }

    [Fact]
    public async Task Save_WithoutTypingKey_DoesNotTouchSecretStore_AndKeepsExistingKey()
    {
        using var host = new SettingsHost();
        var vm = await NewEditorAsync(host);
        vm.NewApiKey = PlainKey;
        await vm.SaveCommand.ExecuteAsync(null);
        var before = host.Secrets.Items.ToDictionary(p => p.Key, p => p.Value);

        vm.Name = "改個名字";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(before, host.Secrets.Items);
        Assert.True(vm.HasStoredKey);
        Assert.Contains(vm.Items, i => i.Name == "改個名字");
    }

    [Fact]
    public async Task Save_WithoutKey_ForOpenAICompatible_IsAllowed()
    {
        using var host = new SettingsHost();
        var vm = await NewEditorAsync(host, "Ollama", "http://localhost:11434/v1");

        Assert.True(vm.SaveCommand.CanExecute(null));
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.DoesNotContain(host.Secrets.Items.Keys, k => k.Contains("ollama", StringComparison.OrdinalIgnoreCase));
        Assert.False((await host.Catalog.HasApiKeyAsync(vm.Items.Single(i => i.Name == "Ollama").Id)));
        Assert.True(vm.ShowKeyInput);
    }

    [Theory]
    [InlineData("", "https://x.test", true, false)]
    [InlineData("名稱", "not a url", false, true)]
    [InlineData("名稱", "ftp://x.test", false, true)]
    [InlineData("名稱", "https://x.test", false, false)]
    public async Task Validation_BlocksSave(string name, string url, bool nameError, bool urlError)
    {
        using var host = new SettingsHost();
        var vm = await NewEditorAsync(host, name, url);

        Assert.Equal(nameError, vm.NameError is not null);
        Assert.Equal(urlError, vm.BaseUrlError is not null);
        Assert.Equal(!nameError && !urlError, vm.SaveCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("http://api.example.com/v1", true)]
    [InlineData("http://203.0.113.5/v1", true)]
    [InlineData("https://api.example.com/v1", false)]
    [InlineData("http://localhost:11434/v1", false)]
    [InlineData("http://127.0.0.1:8080/v1", false)]
    [InlineData("http://192.168.1.20/v1", false)]
    [InlineData("http://10.1.2.3/v1", false)]
    [InlineData("http://172.20.0.5/v1", false)]
    [InlineData("http://gateway/v1", false)]
    [InlineData("http://my-mac.local:1234/v1", false)]
    public async Task InsecureHttpToRemoteHost_ShowsWarning_ButDoesNotBlock(string url, bool warns)
    {
        using var host = new SettingsHost();
        var vm = await NewEditorAsync(host, "x", url);

        Assert.Equal(warns, vm.BaseUrlWarning is not null);
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task SelectingMigratedConnection_ShowsKeyAsSet_WithoutHint()
    {
        using var host = new SettingsHost();
        host.Secrets.Items["connection/dev-anthropic/api-key"] = "sk-ant-from-m1";
        var vm = host.Connections;

        await vm.SelectAsync(vm.Items.Single());

        Assert.True(vm.HasStoredKey);
        Assert.Null(vm.KeyHint);
        Assert.Equal(Strings.Conn_KeySet, vm.KeyStatusText);
        Assert.False(vm.ShowKeyInput);
    }

    [Fact]
    public async Task ReplacingKey_ShowsInput_AndCancelHidesAgain()
    {
        using var host = new SettingsHost();
        host.Secrets.Items["connection/dev-anthropic/api-key"] = "sk-ant-from-m1";
        var vm = host.Connections;
        await vm.SelectAsync(vm.Items.Single());
        var focusRequests = 0;
        vm.FocusApiKeyRequested += () => focusRequests++;

        vm.ReplaceKeyCommand.Execute(null);
        vm.NewApiKey = "typing";
        Assert.True(vm.ShowKeyInput);
        Assert.Equal(1, focusRequests);

        vm.CancelReplaceKeyCommand.Execute(null);
        Assert.False(vm.ShowKeyInput);
        Assert.Equal("", vm.NewApiKey);
    }

    [Fact]
    public async Task ClearKey_RequiresConfirmation_ThenRemovesSecretAndHint()
    {
        using var host = new SettingsHost();
        var vm = await NewEditorAsync(host);
        vm.NewApiKey = PlainKey;
        await vm.SaveCommand.ExecuteAsync(null);

        host.Confirm.Answer = false;
        await vm.ClearKeyCommand.ExecuteAsync(null);
        Assert.True(vm.HasStoredKey);

        host.Confirm.Answer = true;
        await vm.ClearKeyCommand.ExecuteAsync(null);

        Assert.False(vm.HasStoredKey);
        Assert.Null(vm.KeyHint);
        Assert.Empty(host.Secrets.Items);
        Assert.Contains("公司 Gateway", host.Confirm.Asked[0].Message);
    }

    [Fact]
    public async Task Delete_MentionsModelCount_AndRemovesConnectionModelsAndSecret()
    {
        using var host = new SettingsHost();
        host.Secrets.Items["connection/dev-anthropic/api-key"] = "sk-ant-from-m1";
        var vm = host.Connections;
        await vm.SelectAsync(vm.Items.Single());

        host.Confirm.Answer = false;
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Single(host.Catalog.Current.Connections);
        Assert.Contains("1 個模型設定", host.Confirm.Asked[0].Message);

        host.Confirm.Answer = true;
        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.Empty(host.Catalog.Current.Connections);
        Assert.Empty(host.Catalog.Current.Models);
        Assert.Empty(host.Secrets.Items);
        Assert.False(vm.HasEditor);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task Delete_OfUnsavedNewConnection_JustDiscardsWithoutAsking()
    {
        using var host = new SettingsHost();
        var vm = await NewEditorAsync(host);

        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.False(vm.HasEditor);
        Assert.Empty(host.Confirm.Asked);
        Assert.Single(host.Catalog.Current.Connections);
    }

    [Fact]
    public async Task UnsavedChanges_AskBeforeSwitching_OnlyWhenDirty()
    {
        using var host = new SettingsHost();
        var vm = await NewEditorAsync(host, "第二個", "https://two.example.com/v1");
        await vm.SaveCommand.ExecuteAsync(null);
        var first = vm.Items.Single(i => i.Name == "Anthropic");
        var second = vm.Items.Single(i => i.Name == "第二個");

        await vm.SelectAsync(first);                         // 乾淨：不詢問
        Assert.Empty(host.Confirm.Asked);

        vm.Name = "改到一半";
        host.Confirm.Answer = false;
        Assert.False(await vm.SelectAsync(second));          // 拒絕放棄：留在原處，內容還在
        Assert.Equal("改到一半", vm.Name);
        Assert.Single(host.Confirm.Asked);

        host.Confirm.Answer = true;
        Assert.True(await vm.SelectAsync(second));           // 同意放棄：切過去
        Assert.Equal("第二個", vm.Name);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task NewConnection_Untouched_IsNotDirty_SoSwitchingDoesNotAsk()
    {
        using var host = new SettingsHost();
        await host.Connections.NewConnectionCommand.ExecuteAsync(null);

        Assert.False(host.Connections.IsDirty);
        await host.Connections.SelectAsync(host.Connections.Items.Single());

        Assert.Empty(host.Confirm.Asked);
    }

    [Fact]
    public async Task TestConnection_UsesUnsavedKey_NeverWritesIt_AndRecordsStatus()
    {
        using var host = new SettingsHost();
        var vm = await NewEditorAsync(host);
        vm.NewApiKey = PlainKey;

        await vm.TestCommand.ExecuteAsync(null);

        var call = Assert.Single(host.Tester.Calls);
        Assert.Equal(PlainKey, call.Key);
        Assert.Equal("https://gw.example.com/v1", call.Connection.BaseUrl);
        Assert.Empty(host.Secrets.Items);                        // 測試不寫進金鑰儲存
        Assert.True(vm.ShowTestOk);
        Assert.Equal("成功 · 可用模型 2 個", vm.TestResultText);
        Assert.Equal(["model-a", "model-b"], host.Status.Get(call.Connection.Id).ModelIds);
    }

    [Fact]
    public async Task TestConnection_Failure_ShowsReason_AndStatusDotGoesRed()
    {
        using var host = new SettingsHost();
        host.Tester.Result = FakeConnectionTester.Failure(ChatFailureKind.InvalidApiKey, "金鑰無效或已失效，請重新輸入。");
        var vm = host.Connections;
        await vm.SelectAsync(vm.Items.Single());

        await vm.TestCommand.ExecuteAsync(null);

        Assert.True(vm.ShowTestFailure);
        Assert.Equal("金鑰無效或已失效，請重新輸入。", vm.TestResultText);
        Assert.True(vm.Items.Single().IsFailed);
        Assert.Null(host.Tester.Calls[0].Key);   // 沒輸入新金鑰：交給測試器用已儲存的
    }

    [Fact]
    public async Task TestConnection_CanBeCancelled()
    {
        using var host = new SettingsHost();
        host.Tester.Gate = new TaskCompletionSource();
        var vm = host.Connections;
        await vm.SelectAsync(vm.Items.Single());

        var testing = vm.TestCommand.ExecuteAsync(null);
        await SettingsHost.WaitUntil(() => vm.IsTesting);
        Assert.True(vm.CancelTestCommand.CanExecute(null));
        Assert.False(vm.TestCommand.CanExecute(null));

        vm.CancelTestCommand.Execute(null);
        await testing;

        Assert.False(vm.IsTesting);
        Assert.Null(vm.TestResultText);
        Assert.True(vm.Items.Single().IsUnknown);
    }

    [Fact]
    public async Task EditingConnection_InvalidatesOldTestResult()
    {
        using var host = new SettingsHost();
        var vm = host.Connections;
        await vm.SelectAsync(vm.Items.Single());
        await vm.TestCommand.ExecuteAsync(null);
        Assert.True(vm.Items.Single().IsOk);

        vm.BaseUrl = "https://other.example.com";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.Items.Single().IsUnknown);
    }

    [Fact]
    public async Task Navigate_FromMissingKeyFlow_SelectsConnection_ShowsKeyInputAndNotice()
    {
        using var host = new SettingsHost();
        host.Secrets.Items["connection/dev-anthropic/api-key"] = "sk-ant-from-m1";
        var vm = host.Connections;
        var focus = 0;
        vm.FocusApiKeyRequested += () => focus++;

        await vm.NavigateAsync("dev-anthropic", focusApiKey: true, notice: "金鑰無效，請重新輸入。");

        Assert.Equal("dev-anthropic", vm.SelectedItem!.Id);
        Assert.True(vm.ShowKeyInput);
        Assert.Equal("金鑰無效，請重新輸入。", vm.Notice);
        Assert.Equal(1, focus);
    }

    [Fact]
    public async Task ReadOnlyCatalog_SaveShowsError_AndNothingChanges()
    {
        var newer = $$"""{ "version": {{JsonModelCatalog_Version + 1}}, "connections": [], "models": [], "theme": "system" }""";
        using var host = new SettingsHost(newer);
        var vm = await NewEditorAsync(host);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.HasSaveError);
        Assert.Equal(newer, File.ReadAllText(host.SettingsPath));
    }

    private const int JsonModelCatalog_Version = Lumi.Infrastructure.Settings.JsonModelCatalog.CurrentVersion;
}
