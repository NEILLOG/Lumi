using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Lumi.Core.Commands;
using Lumi.Core.Platform;
using Microsoft.Extensions.Logging;

namespace Lumi.Platform.Commands;

/// <summary>
/// 啟動 shell 執行指令（M31 設計 4）。Windows：PowerShell（預設）、cmd；macOS：bash。
/// 逾時或按停止時結束整個程序樹；輸出在 <see cref="CommandOutputBuffer"/> 清理與截斷。
/// </summary>
/// <remarks>
/// 指令與輸出可能含機密，所以 log 只寫 shell、結果、結束代碼、花費時間與輸出長度，不寫內容。
/// </remarks>
public sealed class CommandRunner(IShellEnvironment shellEnvironment, ILogger<CommandRunner> logger) : ICommandRunner
{
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>執行時一定覆寫的環境變數，讓輸出是純文字、不分頁、不互動。</summary>
    private static readonly Dictionary<string, string> EnvironmentOverrides = new(StringComparer.Ordinal)
    {
        ["NO_COLOR"] = "1",
        ["TERM"] = "dumb",
        ["PAGER"] = "cat",
        ["GIT_PAGER"] = "cat",
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["npm_config_progress"] = "false",
        ["npm_config_fund"] = "false",
        ["npm_config_update_notifier"] = "false",
    };

    private const string PowerShellPrefix =
        "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $OutputEncoding=[Console]::OutputEncoding; ";

    /// <summary>cmd 的輸出用系統的 OEM 代碼頁（繁中 Windows 是 950），第一次用到前要註冊 provider。</summary>
    private static readonly Lazy<bool> CodePagesRegistered = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return true;
    });

    public IReadOnlyList<CommandShell> AvailableShells { get; } = AvailableShellsForPlatform();

    public CommandShell DefaultShell => OperatingSystem.IsWindows() ? CommandShell.PowerShell : CommandShell.Bash;

    public string GetScratchDirectory(string conversationId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(conversationId.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        var path = Path.Combine(Path.GetTempPath(), "Lumi", "commands", safe);
        Directory.CreateDirectory(path);
        return path;
    }

    public async Task<CommandResult> RunAsync(CommandRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        var env = await shellEnvironment.GetAsync(ct);
        var stopwatch = Stopwatch.StartNew();

        var psi = CreateStartInfo(request);
        psi.Environment.Clear();
        foreach (var (key, value) in env)
        {
            if (!key.StartsWith("LUMI_", StringComparison.OrdinalIgnoreCase))
                psi.Environment[key] = value;
        }
        foreach (var (key, value) in EnvironmentOverrides)
            psi.Environment[key] = value;

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            return FailedToStart(request.Shell, ex.Message, stopwatch.Elapsed);
        }
        if (process is null)
            return FailedToStart(request.Shell, "系統沒有建立程序", stopwatch.Elapsed);

        using (process)
        {
            var buffer = new CommandOutputBuffer();
            var progressGate = new object();
            TimeSpan? lastReport = null;

            // 讀到新內容時呼叫；距上次回報不到 500 毫秒就略過，最新一行是空的也不回報
            void ReportProgress()
            {
                if (progress is null)
                    return;
                lock (progressGate)
                {
                    var now = stopwatch.Elapsed;
                    if (lastReport is not null && now - lastReport.Value < ProgressInterval)
                        return;
                    var line = buffer.LatestLine;
                    if (line.Length == 0)
                        return;
                    lastReport = now;
                    progress.Report(line);
                }
            }

            // 啟動後立刻關閉 stdin，指令要讀輸入時會直接拿到 EOF，不會卡住
            process.StandardInput.Close();
            var readers = Task.WhenAll(
                PumpAsync(process.StandardOutput, buffer, ReportProgress),
                PumpAsync(process.StandardError, buffer, ReportProgress));

            using var timeoutCts = new CancellationTokenSource(request.Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            CommandOutcome outcome;
            int? exitCode = null;
            try
            {
                await process.WaitForExitAsync(linked.Token);
                exitCode = process.ExitCode;
                outcome = CommandOutcome.Completed;
                // 背景程序可能還握著輸出管線，最多等 2 秒，之後回傳已經收到的輸出
                await Task.WhenAny(readers, Task.Delay(KillWait));
            }
            catch (OperationCanceledException)
            {
                KillTree(process);
                await Task.WhenAny(readers, Task.Delay(KillWait));
                if (ct.IsCancellationRequested)
                {
                    LogResult(request.Shell, "已停止", null, stopwatch.Elapsed, 0);
                    throw new OperationCanceledException(ct);
                }
                outcome = CommandOutcome.TimedOut;
            }

            var (text, truncated) = buffer.Build();
            var elapsed = stopwatch.Elapsed;
            LogResult(request.Shell, outcome.ToString(), exitCode, elapsed, text.Length);
            return new CommandResult(outcome, exitCode, text, truncated, elapsed);
        }
    }

    private static IReadOnlyList<CommandShell> AvailableShellsForPlatform()
    {
        if (OperatingSystem.IsWindows())
            return [CommandShell.PowerShell, CommandShell.Cmd];
        if (OperatingSystem.IsMacOS())
            return [CommandShell.Bash];
        return [];
    }

    private static ProcessStartInfo CreateStartInfo(CommandRequest request)
    {
        ProcessStartInfo psi;
        Encoding encoding;
        switch (request.Shell)
        {
            case CommandShell.PowerShell:
                psi = new ProcessStartInfo("powershell.exe");
                psi.ArgumentList.Add("-NoLogo");
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(PowerShellPrefix + request.Command);
                encoding = new UTF8Encoding(false);
                break;
            case CommandShell.Cmd:
                // 用 Arguments 原始字串：cmd 看不懂 ArgumentList 的反斜線跳脫
                psi = new ProcessStartInfo("cmd.exe") { Arguments = $"/d /s /c \"{request.Command}\"" };
                encoding = OemEncoding();
                break;
            case CommandShell.Bash:
                psi = new ProcessStartInfo("/bin/bash");
                psi.ArgumentList.Add("--noprofile");
                psi.ArgumentList.Add("--norc");
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(request.Command);
                encoding = new UTF8Encoding(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Shell, "不支援的 shell");
        }

        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = encoding;
        psi.StandardErrorEncoding = encoding;
        psi.WorkingDirectory = request.WorkingDirectory;
        return psi;
    }

    private static Encoding OemEncoding()
    {
        _ = CodePagesRegistered.Value;
        return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
    }

    private static async Task PumpAsync(StreamReader reader, CommandOutputBuffer buffer, Action onRead)
    {
        // 不用 OutputDataReceived：它會吃掉 \r（進度列需要）
        var chars = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(chars, 0, chars.Length)) > 0)
        {
            buffer.Append(new string(chars, 0, read));
            onRead();
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // 程序已經結束
        }
        catch (Win32Exception)
        {
            // 無法結束（權限等）；仍然回傳，讀取 Task 會在 KillWait 後放棄
        }
    }

    private static CommandResult FailedToStart(CommandShell shell, string reason, TimeSpan elapsed)
    {
        var name = ShellName(shell);
        return new CommandResult(CommandOutcome.FailedToStart, null, string.Empty, false, elapsed, $"無法啟動 {name}：{reason}");
    }

    private static string ShellName(CommandShell shell) => shell switch
    {
        CommandShell.PowerShell => "PowerShell",
        CommandShell.Cmd => "cmd",
        _ => "bash",
    };

    private void LogResult(CommandShell shell, string result, int? exitCode, TimeSpan elapsed, int outputLength)
    {
        logger.LogInformation(
            "指令執行結束：shell={Shell}，結果={Result}，結束代碼={ExitCode}，花費={Elapsed} 毫秒，輸出長度={OutputLength}",
            ShellName(shell),
            result,
            exitCode?.ToString(CultureInfo.InvariantCulture) ?? "無",
            (long)elapsed.TotalMilliseconds,
            outputLength);
    }
}
