using System.Text.Json;
using Lumi.Core.Chat;
using Lumi.Core.Commands;
using Lumi.Core.Tools;
using Lumi.Tools.Commands;
using Lumi.Tools.Tests.Files;

namespace Lumi.Tools.Tests.Commands;

/// <summary>M31 31D：<c>run_command</c> 的描述符、參數、執行位置、確認卡與結果文字（假的 runner 與 guard）。</summary>
public sealed class RunCommandToolTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FakeCommandRunner _runner = new();
    private readonly FakeCommandGuard _guard = new();
    private readonly RunCommandTool _tool;

    public RunCommandToolTests()
    {
        _tool = new RunCommandTool(_runner, _guard);
    }

    public void Dispose() => _tree.Dispose();

    private static ToolContext Context(IFileScope? scope = null) =>
        new("c1", ChatMode.Workbench, TimeProvider.System, scope ?? EmptyFileScope.Instance);

    private static IFileScope DirScope(string path) => new FileScope([new ScopeRoot("專案", path, true)]);

    /// <summary>用字典組引數 JSON，路徑裡的反斜線不用自己跳脫；沒給的參數不出現在 JSON 裡。</summary>
    private static string Args(string command, string description = "測試指令", string? shell = null, string? workingDirectory = null, int? timeout = null)
    {
        var dict = new Dictionary<string, object?> { ["command"] = command, ["description"] = description };
        if (shell is not null)
            dict["shell"] = shell;
        if (workingDirectory is not null)
            dict["working_directory"] = workingDirectory;
        if (timeout is not null)
            dict["timeout_seconds"] = timeout;
        return JsonSerializer.Serialize(dict);
    }

    private Task<ToolResult> Run(string json, IFileScope? scope = null) => _tool.ExecuteAsync(json, Context(scope), default);

    private ToolPreview Preview(string json, IFileScope? scope = null) => _tool.Preview(json, Context(scope));

    // ---- 描述符與說明 ----

    [Fact]
    public async Task Descriptor_is_destructive_opt_in_and_always_asks()
    {
        var d = _tool.Descriptor;

        Assert.Equal("run_command", d.Name);
        Assert.Equal("執行指令", d.DisplayName);
        Assert.Equal(ToolPermission.Destructive, d.Permission);
        Assert.Equal(ToolModes.Workbench, d.Modes);
        Assert.Equal(TimeSpan.FromSeconds(615), d.Timeout);
        Assert.Equal(ToolGroups.Commands, d.Group);
        Assert.True(d.AlwaysAsk);
        Assert.False(d.CanAlwaysAllow);
        Assert.True(d.WarnsAfterExternalContent);
        Assert.Equal(CommandToolText.OptInWarning, d.OptInWarning);
    }

    [Fact]
    public async Task Description_lists_platform_shells()
    {
        var windows = new RunCommandTool(new FakeCommandRunner { AvailableShells = [CommandShell.PowerShell, CommandShell.Cmd] }, _guard);
        var mac = new RunCommandTool(new FakeCommandRunner { AvailableShells = [CommandShell.Bash], DefaultShell = CommandShell.Bash }, _guard);
        var none = new RunCommandTool(new FakeCommandRunner { AvailableShells = [] }, _guard);

        Assert.Contains("可用的 shell：powershell（Windows PowerShell 5.1，預設）、cmd。", windows.Descriptor.Description);
        Assert.Contains("可用的 shell：bash（/bin/bash 3.2）。", mac.Descriptor.Description);
        Assert.Contains("可用的 shell：（這台電腦不支援）。", none.Descriptor.Description);
    }

    // ---- 參數與 shell ----

    [Fact]
    public async Task Default_shell_is_used_when_missing()
    {
        _runner.DefaultShell = CommandShell.Cmd;

        var result = await Run(Args("dir"), DirScope(_tree.Dir("專案")));

        Assert.False(result.IsError);
        Assert.Equal(CommandShell.Cmd, _runner.Requests.Single().Shell);
    }

    [Fact]
    public async Task Unknown_shell_returns_error()
    {
        var result = await Run(Args("ls", shell: "zsh"));

        Assert.True(result.IsError);
        Assert.Equal("這台電腦不能用 zsh，可用的是：powershell、cmd。", result.Content);
        Assert.Empty(_runner.Requests);
    }

    [Fact]
    public async Task Shell_name_is_case_insensitive()
    {
        await Run(Args("dir", shell: "CMD"));

        Assert.Equal(CommandShell.Cmd, _runner.Requests.Single().Shell);
    }

    [Fact]
    public async Task Empty_shell_list_returns_error()
    {
        var tool = new RunCommandTool(new FakeCommandRunner { AvailableShells = [] }, _guard);

        var result = await tool.ExecuteAsync(Args("ls"), Context(), default);

        Assert.True(result.IsError);
        Assert.Equal("這台電腦不支援執行指令。", result.Content);
    }

    [Fact]
    public async Task Invalid_arguments_return_errors()
    {
        Assert.Equal("command 不能是空的，最多 8,000 字。", (await Run(Args("   "))).Content);
        Assert.Equal("command 不能是空的，最多 8,000 字。", (await Run(Args(new string('a', 8_001)))).Content);
        Assert.Equal("description 必填，最多 200 字。", (await Run(Args("ls", description: ""))).Content);
        Assert.Equal("timeout_seconds 要在 1 到 600 之間。", (await Run(Args("ls", timeout: 0))).Content);
        Assert.Equal("timeout_seconds 要在 1 到 600 之間。", (await Run(Args("ls", timeout: 601))).Content);
        Assert.Empty(_runner.Requests);
    }

    [Fact]
    public async Task Preview_is_none_for_invalid_arguments()
    {
        var preview = Preview(Args("ls", timeout: 0));

        Assert.Null(preview.Command);
        Assert.Null(preview.Rejection);
        Assert.Null(preview.Target);
    }

    // ---- 執行位置（D8） ----

    [Fact]
    public async Task Working_directory_must_be_in_scope()
    {
        var scope = DirScope(_tree.Dir("專案"));
        var outside = _tree.Dir("其他");

        var result = await Run(Args("ls", workingDirectory: outside), scope);

        Assert.True(result.IsError);
        Assert.Contains("不在可存取的範圍內", result.Content);
        Assert.Empty(_runner.Requests);
    }

    [Fact]
    public async Task Working_directory_must_be_folder()
    {
        var file = _tree.Write("note.txt");
        var scope = new FileScope([new ScopeRoot("note.txt", file, false)]);

        var result = await Run(Args("ls", workingDirectory: file), scope);

        Assert.True(result.IsError);
        Assert.Equal("working_directory 必須是資料夾。", result.Content);
        Assert.Empty(_runner.Requests);
    }

    [Fact]
    public async Task Single_folder_scope_is_default_location()
    {
        var dir = _tree.Dir("專案");

        var result = await Run(Args("ls"), DirScope(dir));

        Assert.False(result.IsError);
        Assert.Equal(dir, _runner.Requests.Single().WorkingDirectory);
        Assert.Contains($"位置：專案（{dir}）", result.Content);
        var proposal = Preview(Args("ls"), DirScope(dir)).Command!;
        Assert.False(proposal.IsScratchDirectory);
        Assert.Equal(dir, proposal.WorkingDirectory);
    }

    [Fact]
    public async Task Multiple_roots_use_scratch_directory()
    {
        var scope = new FileScope([new ScopeRoot("A", _tree.Dir("a"), true), new ScopeRoot("B", _tree.Dir("b"), true)]);

        var result = await Run(Args("ls"), scope);

        Assert.Equal("/tmp/lumi-test/c1", _runner.Requests.Single().WorkingDirectory);
        Assert.Contains("暫存資料夾（/tmp/lumi-test/c1）", result.Content);
        Assert.True(Preview(Args("ls"), scope).Command!.IsScratchDirectory);
    }

    [Fact]
    public async Task Explicit_working_directory_is_used_when_in_scope()
    {
        var root = _tree.Dir("專案");
        var sub = _tree.Dir("專案/src");

        var result = await Run(Args("ls", workingDirectory: sub), DirScope(root));

        Assert.False(result.IsError);
        // 明確指定的位置經過 Scope.Resolve，拿到的是真實路徑（macOS 的暫存資料夾會多一層 /private）
        Assert.EndsWith(Path.Combine("專案", "src"), _runner.Requests.Single().WorkingDirectory);
        Assert.Contains("位置：src（", result.Content);
    }

    // ---- 確認卡與擋掉 ----

    [Fact]
    public async Task Blocked_command_sets_rejection()
    {
        _guard.Next = new CommandCheck("要求系統管理員權限", []);

        var preview = Preview(Args("sudo rm x"));

        Assert.Equal("這段指令被 Lumi 擋下：要求系統管理員權限。這類操作請使用者自己在終端機執行。", preview.Rejection);
        Assert.Null(preview.Command);
    }

    [Fact]
    public async Task Invoke_rechecks_guard_and_blocks()
    {
        _guard.Next = new CommandCheck("會影響整台電腦（格式化、關機、登錄檔、開機設定）", []);

        var result = await Run(Args("shutdown now"));

        Assert.True(result.IsError);
        Assert.Equal("已擋下", result.Summary);
        Assert.Contains("這段指令被 Lumi 擋下", result.Content);
        Assert.Empty(_runner.Requests);
    }

    [Fact]
    public async Task Proposal_has_risks_and_description()
    {
        _guard.Next = new CommandCheck(null, [CommandRisk.InstallsPackages, CommandRisk.Network]);

        var preview = Preview(Args("npm install left-pad", "安裝 left-pad 套件", shell: "cmd"));

        Assert.Null(preview.Rejection);
        Assert.Equal("安裝 left-pad 套件", preview.Target);
        var proposal = preview.Command!;
        Assert.Equal("cmd", proposal.ShellName);
        Assert.Equal("npm install left-pad", proposal.Command);
        Assert.Equal("安裝 left-pad 套件", proposal.Description);
        Assert.Equal([CommandRisk.InstallsPackages, CommandRisk.Network], proposal.Risks);
    }

    [Fact]
    public async Task Long_description_is_truncated_in_target()
    {
        var description = new string('字', 45);

        var preview = Preview(Args("ls", description));

        Assert.Equal(new string('字', 40) + "…", preview.Target);
    }

    // ---- 執行結果 ----

    [Fact]
    public async Task Successful_run_is_ok_with_summary_done()
    {
        _runner.Result = new CommandResult(CommandOutcome.Completed, 0, "hello", false, TimeSpan.FromSeconds(3.25));

        var result = await Run(Args("echo hello", shell: "powershell"), DirScope(_tree.Dir("專案")));

        Assert.False(result.IsError);
        Assert.Equal("完成", result.Summary);
        Assert.Equal("shell：PowerShell　位置：專案（" + _tree.Dir("專案") + "）\n結束代碼：0　花費：3.3秒\n---\nhello", result.Content);
    }

    [Fact]
    public async Task Exit_code_nonzero_is_error_with_summary()
    {
        _runner.Result = new CommandResult(CommandOutcome.Completed, 1, "boom", false, TimeSpan.FromSeconds(3.2));

        var result = await Run(Args("false"));

        Assert.True(result.IsError);
        Assert.Equal("結束代碼 1", result.Summary);
        Assert.Contains("結束代碼：1　花費：3.2秒\n---\nboom", result.Content);
    }

    [Fact]
    public async Task Timeout_result_text()
    {
        _runner.Result = new CommandResult(CommandOutcome.TimedOut, null, "partial", false, TimeSpan.FromSeconds(120));

        var result = await Run(Args("npm install"));

        Assert.True(result.IsError);
        Assert.Equal("逾時", result.Summary);
        Assert.Contains("逾時（120 秒），已結束整個程序\n---\npartial", result.Content);
        Assert.Equal(TimeSpan.FromSeconds(120), _runner.Requests.Single().Timeout);
    }

    [Fact]
    public async Task Custom_timeout_is_passed_to_runner()
    {
        await Run(Args("ls", timeout: 600));

        Assert.Equal(TimeSpan.FromSeconds(600), _runner.Requests.Single().Timeout);
    }

    [Fact]
    public async Task Truncated_result_has_hint()
    {
        _runner.Result = new CommandResult(CommandOutcome.Completed, 0, "開頭…結尾", true, TimeSpan.FromSeconds(1));

        var result = await Run(Args("ls"));

        Assert.False(result.IsError);
        Assert.EndsWith("輸出太長，只保留開頭與結尾。需要完整內容請把輸出寫進檔案再讀取。", result.Content);
    }

    [Fact]
    public async Task Empty_output_is_marked()
    {
        var result = await Run(Args("ls"));

        Assert.EndsWith("---\n（沒有輸出）", result.Content);
    }

    [Fact]
    public async Task Failed_to_start_is_error_with_start_error()
    {
        _runner.Result = new CommandResult(CommandOutcome.FailedToStart, null, "", false, TimeSpan.Zero, "找不到 bash，無法啟動。");

        var result = await Run(Args("ls"));

        Assert.True(result.IsError);
        Assert.Equal("無法啟動", result.Summary);
        Assert.Equal("找不到 bash，無法啟動。", result.Content);
    }
}
