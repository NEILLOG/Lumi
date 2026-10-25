using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Mcp;

namespace Lumi.App.Tests;

public sealed class McpImportViewModelTests
{
    private const string Json = """
        {"mcpServers":{
          "github":{"command":"npx","args":["-y","@modelcontextprotocol/server-github"],"env":{"GITHUB_TOKEN":"ghp_SECRET_VALUE"}},
          "kb":{"url":"https://kb.example.com/mcp","headers":{"Authorization":"Bearer SECRET_TOKEN"}},
          "old":{"type":"sse","url":"https://old.example.com/sse"}
        }}
        """;

    private static McpImportViewModel Parsed(McpCatalogSpy catalog, string json = Json)
    {
        var vm = new McpImportViewModel(catalog) { Text = json };
        vm.ParseCommand.Execute(null);
        return vm;
    }

    [Fact]
    public void Parse_ShowsAPreview_AndErrorsAreNotCounted()
    {
        var vm = Parsed(new McpCatalogSpy());

        Assert.Equal(3, vm.Rows.Count);
        Assert.Equal(2, vm.ImportCount);
        Assert.True(vm.CanImport);
        Assert.True(vm.Rows.Single(r => r.Name == "old").HasError);   // SSE 不支援，該筆不會匯入
        Assert.Contains("SSE", vm.Rows.Single(r => r.Name == "old").Error);
        Assert.Null(vm.ParseError);
    }

    [Fact]
    public void Parse_NotJson_ShowsParseError_AndNoRows()
    {
        var vm = Parsed(new McpCatalogSpy(), "這不是 JSON {");

        Assert.True(vm.HasParseError);
        Assert.Empty(vm.Rows);
        Assert.False(vm.CanImport);
    }

    [Fact]
    public void Parse_DuplicateName_ShowsRenameNote()
    {
        var existing = new McpServerSettings("e1", "github", "github", McpTransportKind.Stdio, "npx", [], null, [], null, []);
        var vm = Parsed(new McpCatalogSpy(existing), """{"mcpServers":{"github":{"command":"npx"}}}""");

        var row = Assert.Single(vm.Rows);
        Assert.True(row.HasRenamedNote);
        Assert.Equal(string.Format(Lumi.App.Resources.Strings.Mcp_ImportRenamed, "github (2)"), row.RenamedNote);
    }

    [Fact]
    public async Task Import_WritesEachAcceptedServer_AndSelectsTheFirst()
    {
        var catalog = new McpCatalogSpy();
        var vm = Parsed(catalog);
        string? firstId = null;
        vm.Imported += id => firstId = id;

        await vm.ImportCommand.ExecuteAsync(null);

        Assert.Equal(2, catalog.Saves.Count);
        Assert.Equal(catalog.Saves[0].Server.Id, firstId);
        Assert.DoesNotContain(vm.Rows, r => !r.HasError);   // 匯入過的不留在預覽
        Assert.False(vm.IsImporting);
    }

    [Fact]
    public async Task Import_PassesSecretsOnlyForServersThatHaveThem()
    {
        var catalog = new McpCatalogSpy();
        var vm = Parsed(catalog);

        await vm.ImportCommand.ExecuteAsync(null);

        var github = catalog.Saves.Single(s => s.Server.Name == "github");
        var kb = catalog.Saves.Single(s => s.Server.Name == "kb");
        Assert.Equal("ghp_SECRET_VALUE", github.Secrets!.Env["GITHUB_TOKEN"]);
        Assert.Equal("Bearer SECRET_TOKEN", kb.Secrets!.Headers["Authorization"]);
    }

    [Fact]
    public async Task Import_MidwayFailure_StopsAndReportsTheCount()
    {
        var catalog = new McpCatalogSpy();
        var vm = Parsed(catalog);
        catalog.FailSaveFor = s => s.Name == "kb";

        await vm.ImportCommand.ExecuteAsync(null);

        Assert.Single(catalog.Saves);   // 第一筆成功，第二筆失敗
        Assert.Equal(string.Format(Lumi.App.Resources.Strings.Mcp_ImportPartialFailure, 1, 2, "測試用的寫入失敗"), vm.ImportError);
        Assert.True(vm.HasImportError);
        Assert.Single(vm.Rows, r => !r.HasError);   // 已匯入的移除，失敗的留在預覽，重按不會重複匯入
    }

    [Fact]
    public void Cancel_RaisesCancelled()
    {
        var vm = Parsed(new McpCatalogSpy());
        var cancelled = false;
        vm.Cancelled += () => cancelled = true;

        vm.CancelCommand.Execute(null);

        Assert.True(cancelled);
    }
}
