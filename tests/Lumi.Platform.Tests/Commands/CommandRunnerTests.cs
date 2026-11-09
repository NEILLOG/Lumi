using System.Collections;
using System.Diagnostics;
using Lumi.Core.Commands;
using Lumi.Core.Platform;
using Lumi.Platform.Commands;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Platform.Tests.Commands;

public class CommandRunnerTests
{
    private static CommandRunner CreateRunner() => new(new FakeShellEnvironment(), NullLogger<CommandRunner>.Instance);

    private static CommandRequest BashRequest(string command, TimeSpan? timeout = null) =>
        new(CommandShell.Bash, command, Path.GetTempPath(), timeout ?? TimeSpan.FromSeconds(20));

    [SkippableFact]
    public async Task Bash_runs_and_returns_exit_code()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "只在 macOS 執行");

        var result = await CreateRunner().RunAsync(BashRequest("echo 中文; exit 3"), null, CancellationToken.None);

        Assert.Equal(CommandOutcome.Completed, result.Outcome);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("中文", result.Output);
    }

    [SkippableFact]
    public async Task Stdin_is_closed()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "只在 macOS 執行");

        var result = await CreateRunner().RunAsync(BashRequest("read x; echo done:$x", TimeSpan.FromSeconds(5)), null, CancellationToken.None);

        Assert.Equal(CommandOutcome.Completed, result.Outcome);
        Assert.Contains("done:", result.Output);
    }

    [SkippableFact]
    public async Task Timeout_kills_process_tree()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "只在 macOS 執行");

        var stopwatch = Stopwatch.StartNew();
        var result = await CreateRunner().RunAsync(BashRequest("sleep 31 & sleep 31; wait", TimeSpan.FromSeconds(1)), null, CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(CommandOutcome.TimedOut, result.Outcome);
        Assert.Null(result.ExitCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2.5), $"逾時後花了 {stopwatch.Elapsed.TotalSeconds:F1} 秒才返回");

        await Task.Delay(300);
        Assert.Equal(string.Empty, RunAndReadOutput("pgrep", "-f", "sleep 31"));
    }

    [SkippableFact]
    public async Task Cancel_throws_and_kills()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "只在 macOS 執行");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CreateRunner().RunAsync(BashRequest("sleep 37 & sleep 37; wait"), null, cts.Token));

        await Task.Delay(300);
        Assert.Equal(string.Empty, RunAndReadOutput("pgrep", "-f", "sleep 37"));
    }

    [SkippableFact]
    public async Task Environment_has_no_LUMI_vars_and_has_NO_COLOR()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "只在 macOS 執行");

        var result = await CreateRunner().RunAsync(BashRequest("env"), null, CancellationToken.None);

        Assert.DoesNotContain("LUMI_SECRET", result.Output);
        Assert.Contains("NO_COLOR=1", result.Output);
    }

    [Fact]
    public void Scratch_directory_is_per_conversation()
    {
        var runner = CreateRunner();
        var first = runner.GetScratchDirectory($"test-{Guid.NewGuid():N}");
        var second = runner.GetScratchDirectory($"test-{Guid.NewGuid():N}");
        try
        {
            Assert.NotEqual(first, second);
            Assert.True(Directory.Exists(first));
            Assert.True(Directory.Exists(second));
        }
        finally
        {
            Directory.Delete(first, recursive: true);
            Directory.Delete(second, recursive: true);
        }
    }

    [SkippableFact]
    public async Task Bad_working_directory_fails_to_start()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "只在 macOS 執行");

        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}");
        var request = new CommandRequest(CommandShell.Bash, "echo hi", missing, TimeSpan.FromSeconds(5));

        var result = await CreateRunner().RunAsync(request, null, CancellationToken.None);

        Assert.Equal(CommandOutcome.FailedToStart, result.Outcome);
        Assert.Null(result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.StartsWith("無法啟動 bash：", result.StartError);
    }

    [SkippableFact]
    public async Task PowerShell_quotes_and_utf8()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "只在 Windows 執行");

        var request = new CommandRequest(CommandShell.PowerShell, "Write-Output \"a \"\"b\"\" 'c' & 中文\"", Path.GetTempPath(), TimeSpan.FromSeconds(20));
        var result = await CreateRunner().RunAsync(request, null, CancellationToken.None);

        Assert.Equal(CommandOutcome.Completed, result.Outcome);
        Assert.Contains("a \"b\" 'c' & 中文", result.Output);
    }

    [SkippableFact]
    public async Task Cmd_quotes_and_oem_output()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "只在 Windows 執行");

        var request = new CommandRequest(CommandShell.Cmd, "echo \"quoted\" & echo done", Path.GetTempPath(), TimeSpan.FromSeconds(20));
        var result = await CreateRunner().RunAsync(request, null, CancellationToken.None);

        Assert.Equal(CommandOutcome.Completed, result.Outcome);
        Assert.Contains("quoted", result.Output);
        Assert.Contains("done", result.Output);
    }

    [SkippableFact]
    public async Task PowerShell_timeout_kills_tree()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "只在 Windows 執行");

        // 用一個不會重複的標記找殘留的程序；標記用字串相加拼出，查詢程序自己的命令列不會符合
        var marker = Guid.NewGuid().ToString("N");
        var request = new CommandRequest(CommandShell.PowerShell, $"Start-Sleep 30; # mark{marker[..8]}{marker[8..]}", Path.GetTempPath(), TimeSpan.FromSeconds(1));

        var stopwatch = Stopwatch.StartNew();
        var result = await CreateRunner().RunAsync(request, null, CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(CommandOutcome.TimedOut, result.Outcome);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"逾時後花了 {stopwatch.Elapsed.TotalSeconds:F1} 秒才返回");

        await Task.Delay(300);
        // 查詢字串也要拆開，否則查詢程序自己的命令列會符合
        var query = $"(Get-CimInstance Win32_Process | Where-Object {{ $_.CommandLine -like ('*mark' + '{marker}*') }}).Count";
        Assert.Equal("0", RunAndReadOutput("powershell.exe", "-NoProfile", "-Command", query).Trim());
    }

    private static string RunAndReadOutput(string fileName, params string[] arguments)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"無法啟動 {fileName}");
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private sealed class FakeShellEnvironment : IShellEnvironment
    {
        public Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken ct)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string key && entry.Value is string value)
                    result[key] = value;
            }
            result["LUMI_SECRET"] = "x";
            return Task.FromResult<IReadOnlyDictionary<string, string>>(result);
        }
    }
}
