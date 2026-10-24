using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Models;

namespace Lumi.App.Tests;

/// <summary>UD-2：連線／模型設定清單卡片與詳細區標頭用的唯讀顯示屬性（不改資料語意）。</summary>
public sealed class ConnectionsPageViewModelTests
{
    // ---- 字首方塊與狀態文字 ----

    [Theory]
    [InlineData("OpenAI", ProviderFormat.OpenAICompatible, "O")]
    [InlineData("anthropic 公司", ProviderFormat.Anthropic, "A")]
    [InlineData("  ollama", ProviderFormat.OpenAICompatible, "O")]
    [InlineData("公司 Gateway", ProviderFormat.OpenAICompatible, "公")]
    [InlineData("", ProviderFormat.Anthropic, "A")]
    [InlineData("   ", ProviderFormat.OpenAICompatible, "O")]
    [InlineData(null, ProviderFormat.OpenAICompatible, "O")]
    public void ProviderInitial_UsesTheNameThenTheFormat(string? name, ProviderFormat format, string expected) =>
        Assert.Equal(expected, ProviderInitial.Of(name, format));

    [Fact]
    public void StatusText_AlwaysHasWords()
    {
        Assert.Equal("連線正常", ConnectionStatusText.Of(ConnectionStatus.Ok));
        Assert.Equal("連線失敗", ConnectionStatusText.Of(ConnectionStatus.Failed));
        Assert.Equal("尚未確認", ConnectionStatusText.Of(ConnectionStatus.Unknown));
    }

    [Fact]
    public void ConnectionListItem_HasCardSubtitleInitialAndStatusText()
    {
        var item = new ConnectionListItem("c1", "Ollama", ProviderFormat.OpenAICompatible, 3, ConnectionStatus.Failed);
        Assert.Equal("OpenAI 相容 · 3 個模型", item.Subtitle);
        Assert.Equal("O", item.Initial);
        Assert.Equal("連線失敗", item.StatusText);
    }

    [Fact]
    public void ModelListItem_CardSubtitle_IsTheExactModelIdWithTheDefaultLabel()
    {
        var both = new ModelListItem("m1", "OpenAI", "我的小模型", "gpt-5-mini", true, ProviderFormat.OpenAICompatible, ConnectionStatus.Ok);
        Assert.Equal("gpt-5-mini · 預設", both.CardSubtitle);
        var plain = new ModelListItem("m2", "Anthropic", "Sonnet", "claude-sonnet-4-5", false, ProviderFormat.Anthropic);
        Assert.Equal("claude-sonnet-4-5", plain.CardSubtitle);   // 不改寫真實的 model ID
        Assert.Equal("A", plain.Initial);
        Assert.Equal("尚未確認", plain.StatusText);
        Assert.Contains("Anthropic", plain.CardTooltip);
        Assert.Equal("O", both.Initial);
    }

    [Fact]
    public void ModelListItem_Status_RaisesPropertyChanged()
    {
        var item = new ModelListItem("m1", "OpenAI", "x", "gpt", false);
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        item.Status = ConnectionStatus.Ok;
        Assert.Contains(nameof(ModelListItem.Status), changed);
        Assert.Contains(nameof(ModelListItem.StatusText), changed);
        Assert.Equal("連線正常", item.StatusText);
    }

    // ---- 連線 ----

    [Fact]
    public async Task Connections_ListTitle_CountsTheItems_AndHeaderFollowsTheSelection()
    {
        using var host = new SettingsHost();
        var vm = host.Connections;
        Assert.Equal("我的連線 · 1", vm.ListTitle);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await host.Catalog.SaveConnectionAsync(new ConnectionSettings("ollama", "Ollama", ProviderFormat.OpenAICompatible, "http://localhost:11434/v1"), null);
        await SettingsHost.WaitUntil(() => vm.Items.Count == 2);
        Assert.Equal("我的連線 · 2", vm.ListTitle);
        Assert.Contains(nameof(ConnectionsViewModel.ListTitle), changed);

        await vm.SelectAsync(vm.Items.Single(i => i.Id == "ollama"));
        Assert.Equal("Ollama", vm.HeaderTitle);
        Assert.Equal("OpenAI 相容", vm.HeaderProvider);
        Assert.Equal("O", vm.HeaderInitial);
        Assert.Equal(ConnectionStatus.Unknown, vm.EditorStatus);
        Assert.Equal("尚未確認", vm.EditorStatusText);

        vm.Name = "改到一半";
        Assert.Equal("Ollama", vm.HeaderTitle);   // 標頭是已儲存的名稱，輸入時不會跳動

        host.Status.Set("ollama", ConnectionTestResult.Ok(["llama3"]));
        await SettingsHost.WaitUntil(() => vm.EditorStatus == ConnectionStatus.Ok);
        Assert.Equal("連線正常", vm.EditorStatusText);
        Assert.Equal("連線正常", vm.Items.Single(i => i.Id == "ollama").StatusText);
    }

    [Fact]
    public async Task Connections_NewConnection_HasTheDefaultNameInTheHeader_AndHeaderProviderFollowsTheFormat()
    {
        using var host = new SettingsHost();
        var vm = host.Connections;
        await vm.NewConnectionCommand.ExecuteAsync(null);
        Assert.Equal(Strings.Conn_NewName, vm.HeaderTitle);
        Assert.Equal("OpenAI 相容", vm.HeaderProvider);
        vm.Format = FormatOption.All[0];
        Assert.Equal("Anthropic 原生", vm.HeaderProvider);
    }

    [Fact]
    public async Task Connections_SaveCommand_FollowsCanSave_AndHasFieldErrorsExplainsWhy()
    {
        using var host = new SettingsHost();
        var vm = host.Connections;
        Assert.False(vm.HasFieldErrors);
        await vm.SelectAsync(vm.Items[0]);
        Assert.True(vm.CanSave);
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.HasFieldErrors);

        vm.Name = "";
        Assert.False(vm.CanSave);
        Assert.False(vm.SaveCommand.CanExecute(null));   // 按鈕綁的命令跟著停用
        Assert.True(vm.HasFieldErrors);

        vm.Name = "好了";
        vm.BaseUrl = "沒有網址";
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.True(vm.HasFieldErrors);

        vm.BaseUrl = "https://example.com";
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.HasFieldErrors);
    }

    // ---- 模型設定 ----

    [Fact]
    public async Task Models_ListTitle_Header_AndStatusFollowTheConnection()
    {
        using var host = new SettingsHost();
        var vm = host.Models;
        Assert.Equal("我的模型設定 · 1", vm.ListTitle);
        var connectionId = host.Catalog.Current.Connections[0].Id;

        var item = vm.Items[0];
        Assert.Equal(ConnectionStatus.Unknown, item.Status);
        Assert.Equal("A", item.Initial);   // 預設連線是 Anthropic
        Assert.Equal("claude-opus-5-5 · 預設", item.CardSubtitle);

        await vm.SelectAsync(item);
        Assert.Equal(item.DisplayName, vm.HeaderTitle);
        Assert.Equal(vm.SelectedConnection!.Name, vm.HeaderProvider);
        Assert.Equal("A", vm.HeaderInitial);

        // 測試連線更新卡片的狀態：同一個清單項目，不重建清單，也不動編輯中的表單
        host.Status.Set(connectionId, ConnectionTestResult.Ok(["claude-opus-5-5"]));
        await SettingsHost.WaitUntil(() => item.Status == ConnectionStatus.Ok);
        Assert.Same(item, vm.Items[0]);
        Assert.Same(item, vm.SelectedItem);
        Assert.True(vm.HasEditor);
        Assert.Equal(ConnectionStatus.Ok, vm.EditorStatus);
        Assert.Equal("連線正常", vm.EditorStatusText);

        host.Status.Set(connectionId, FakeConnectionTester.Failure(ChatFailureKind.Network, "連不上"));
        await SettingsHost.WaitUntil(() => item.Status == ConnectionStatus.Failed);
        Assert.Equal("連線失敗", vm.EditorStatusText);
    }

    [Fact]
    public async Task Models_NewModel_HasTheNewHeader_AndHasFieldErrorsFollowsTheForm()
    {
        using var host = new SettingsHost();
        var vm = host.Models;
        await vm.NewModelCommand.ExecuteAsync(null);
        Assert.Equal(Strings.Models_New, vm.HeaderTitle);
        Assert.True(vm.HasFieldErrors);   // 模型 ID、顯示名稱還是空的
        Assert.False(vm.SaveCommand.CanExecute(null));

        vm.ModelId = "llama3";
        Assert.False(vm.HasFieldErrors);
        Assert.True(vm.SaveCommand.CanExecute(null));
        vm.MaxOutputTokens = 0;
        Assert.True(vm.HasFieldErrors);
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    // ---- 設定視窗：分段切換 ----

    [Fact]
    public async Task Settings_SegmentFlags_FollowTabIndex()
    {
        using var host = new SettingsHost();
        var vm = host.Settings;
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.True(vm.ShowConnectionsTab);
        Assert.False(vm.ShowModelsTab);

        vm.TabIndex = 1;
        Assert.False(vm.ShowConnectionsTab);
        Assert.True(vm.ShowModelsTab);
        Assert.Contains(nameof(SettingsViewModel.ShowConnectionsTab), changed);
        Assert.Contains(nameof(SettingsViewModel.ShowModelsTab), changed);

        await vm.NavigateAsync(new SettingsNavigation(SettingsSection.Connections));
        Assert.True(vm.ShowConnectionsTab);
    }
}
