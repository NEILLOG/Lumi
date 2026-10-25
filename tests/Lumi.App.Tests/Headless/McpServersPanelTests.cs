using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Mcp;

namespace Lumi.App.Tests.Headless;

/// <summary>M16 16F：「MCP 伺服器」畫面的版面與狀態區（清單＋詳細、匯入取代表單、連線失敗的文字）。</summary>
public class McpServersPanelTests
{
    private sealed class Rig : IDisposable
    {
        public Rig(McpServersViewModel vm, double width = 1040)
        {
            Window = new Window { Width = width, Height = 760, Content = new McpServersPanel { DataContext = vm } };
            Window.Show();
            Window.UpdateLayout();
        }

        public Window Window { get; }

        public void Dispose()
        {
            Window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();   // 把關窗後排隊的工作做完，不讓它滯留到下一個測試
        }
    }

    private static McpServerSettings Stdio(string id, string name) =>
        new(id, name.ToLowerInvariant(), name, McpTransportKind.Stdio, "npx", [], null, [], null, [], true, false, 60, "v1");

    [AvaloniaFact]
    public void EmptyList_ShowsTheEmptyText()
    {
        var vm = new McpServersViewModel(new McpCatalogSpy(), new FakeMcpToolSource(), new FakeConfirmService(), new SyncUiDispatcher());
        using var rig = new Rig(vm);

        Assert.True(rig.Window.FindByName<TextBlock>("EmptyText").IsEffectivelyVisible);
        Assert.Equal(Strings.Mcp_Empty, rig.Window.FindByName<TextBlock>("EmptyText").Text);
    }

    [AvaloniaFact]
    public async Task Import_ReplacesTheForm()
    {
        var vm = new McpServersViewModel(new McpCatalogSpy(Stdio("s1", "GitHub")), new FakeMcpToolSource(), new FakeConfirmService(), new SyncUiDispatcher());
        using var rig = new Rig(vm);
        await vm.SelectAsync(vm.Items[0]);
        rig.Window.UpdateLayout();
        Assert.True(rig.Window.FindByName<Border>("DetailCard").IsEffectivelyVisible);

        await vm.OpenImportCommand.ExecuteAsync(null);
        rig.Window.UpdateLayout();

        Assert.True(rig.Window.FindByName<Border>("ImportCard").IsEffectivelyVisible);
        Assert.False(rig.Window.FindByName<Border>("DetailCard").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task FailedServer_ShowsTheReasonAsText_WithAnIcon()
    {
        var mcp = new FakeMcpToolSource();
        mcp.Statuses.Add(new McpServerStatus("s1", McpServerState.Failed, "找不到指令 npx", 0));
        var vm = new McpServersViewModel(new McpCatalogSpy(Stdio("s1", "GitHub")), mcp, new FakeConfirmService(), new SyncUiDispatcher());
        using var rig = new Rig(vm);
        await vm.SelectAsync(vm.Items[0]);
        rig.Window.UpdateLayout();

        var failure = rig.Window.FindByName<Border>("EditorFailure");
        Assert.True(failure.IsEffectivelyVisible);
        var texts = failure.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains(Strings.Mcp_StatusFailed, texts);
        Assert.Contains("找不到指令 npx", texts);
    }

    [AvaloniaFact]
    public void Wide_PutsTheListAt260_AndTheDetailInTheRest()
    {
        var vm = new McpServersViewModel(new McpCatalogSpy(), new FakeMcpToolSource(), new FakeConfirmService(), new SyncUiDispatcher());
        using var rig = new Rig(vm, 1040);

        var master = rig.Window.FindByName<ListBox>("ServerList").GetVisualAncestors().OfType<DockPanel>().First();
        // 清單欄寬 260 含右側 16 的間距（master 的 Margin），所以內容寬度是 244
        Assert.Equal(260, master.Bounds.Width + master.Margin.Right, 1);
    }
}
