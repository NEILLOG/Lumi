using Lumi.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.Views;
using Lumi.Core.Mcp;

namespace Lumi.App.Tests.Headless;

/// <summary>
/// M16 16F：「MCP 伺服器」畫面的版面與狀態區（清單＋詳細、匯入取代表單、連線失敗的文字）。
/// 面板放在真正的設定視窗裡（工具分類的第二個分段），和其他設定頁的測試一樣；單獨放進空白視窗會缺少設定視窗提供的樣式。
/// </summary>
public class McpServersPanelTests
{
    private static McpServerSettings Stdio(string id, string name) =>
        new(id, name.ToLowerInvariant(), name, McpTransportKind.Stdio, "npx", [], null, [], null, [], true, false, 60, "v1");

    private static async Task<(SettingsHost Host, SettingsWindow Window)> OpenAsync(params McpServerSettings[] servers)
    {
        var host = new SettingsHost();
        foreach (var server in servers)
            await host.Catalog.SaveMcpServerAsync(server, null);
        var window = new SettingsWindow(host.Settings);
        window.Show();
        host.Settings.CategoryIndex = (int)SettingsCategory.Tools;
        host.Settings.ToolsTabIndex = 1;
        window.UpdateLayout();
        return (host, window);
    }

    [AvaloniaFact]
    public async Task EmptyList_ShowsTheEmptyText()
    {
        var (host, window) = await OpenAsync();
        using var _ = host;

        Assert.True(window.FindByName<TextBlock>("EmptyText").IsEffectivelyVisible);
        Assert.Equal(Strings.Mcp_Empty, window.FindByName<TextBlock>("EmptyText").Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Import_ReplacesTheForm()
    {
        var (host, window) = await OpenAsync(Stdio("s1", "GitHub"));
        using var _ = host;
        await host.McpServers.SelectAsync(host.McpServers.Items[0]);
        window.UpdateLayout();
        Assert.True(window.FindByName<Border>("McpDetailCard").IsEffectivelyVisible);

        await host.McpServers.OpenImportCommand.ExecuteAsync(null);
        window.UpdateLayout();

        Assert.True(window.FindByName<Border>("ImportCard").IsEffectivelyVisible);
        Assert.False(window.FindByName<Border>("McpDetailCard").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task FailedServer_ShowsTheReasonAsText_WithAnIcon()
    {
        var (host, window) = await OpenAsync(Stdio("s1", "GitHub"));
        using var _ = host;
        host.McpSource.Statuses.Add(new McpServerStatus("s1", McpServerState.Failed, "找不到指令 npx", 0));
        host.McpSource.Raise();
        await host.McpServers.SelectAsync(host.McpServers.Items[0]);
        window.UpdateLayout();

        var failure = window.FindByName<Border>("EditorFailure");
        Assert.True(failure.IsEffectivelyVisible);
        var texts = failure.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains(Strings.Mcp_StatusFailed, texts);
        Assert.Contains("找不到指令 npx", texts);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Wide_PutsTheListAt260_AndTheDetailInTheRest()
    {
        var (host, window) = await OpenAsync();
        using var _ = host;

        var master = window.FindByName<ListBox>("ServerList").GetVisualAncestors().OfType<DockPanel>().First();
        // 清單欄寬 260 含右側 16 的間距（master 的 Margin），所以內容寬度是 244
        Assert.Equal(260, master.Bounds.Width + master.Margin.Right, 1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheHeaderShowsTheAddButton_OnlyOnTheMcpSegment()
    {
        var (host, window) = await OpenAsync();
        using var _ = host;
        var button = window.FindByName<Button>("NewMcpServerButton");
        Assert.True(button.IsEffectivelyVisible);

        host.Settings.ToolsTabIndex = 0;
        window.UpdateLayout();

        Assert.False(button.IsEffectivelyVisible);
        window.Close();
    }
}
