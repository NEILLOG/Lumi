namespace Lumi.Core.Commands;

/// <summary>執行指令用的 shell（M31 D2）。Windows：PowerShell（預設）、Cmd；macOS：Bash。</summary>
public enum CommandShell { PowerShell, Cmd, Bash }

/// <param name="WorkingDirectory">已經解析好的完整路徑；runner 不再檢查範圍。</param>
public sealed record CommandRequest(CommandShell Shell, string Command, string WorkingDirectory, TimeSpan Timeout);

public enum CommandOutcome { Completed, TimedOut, FailedToStart }

/// <param name="ExitCode">逾時或啟動失敗時是 null。</param>
/// <param name="Output">已經清掉控制碼、截斷過的輸出（M31 設計 4）。</param>
/// <param name="StartError">啟動失敗的原因（繁中），其他情況是 null。</param>
public sealed record CommandResult(CommandOutcome Outcome, int? ExitCode, string Output, bool Truncated, TimeSpan Elapsed, string? StartError = null);

/// <summary>執行指令（M31）。實作在 Lumi.Platform；按停止（ct 取消）時結束整個程序樹並丟 <see cref="OperationCanceledException"/>。</summary>
public interface ICommandRunner
{
    /// <summary>這台電腦能用的 shell；不支援的平台是空清單。</summary>
    IReadOnlyList<CommandShell> AvailableShells { get; }

    CommandShell DefaultShell { get; }

    /// <summary>這則對話專屬的暫存資料夾（第一次呼叫時建立），回完整路徑。</summary>
    string GetScratchDirectory(string conversationId);

    Task<CommandResult> RunAsync(CommandRequest request, IProgress<string>? progress, CancellationToken ct);
}

/// <summary>確認卡的風險提示（M31 設計 2），依列舉順序顯示。</summary>
public enum CommandRisk { Deletes, InstallsPackages, Network, ChangesSystem, TouchesSkills }

/// <param name="BlockedReason">有值＝直接擋掉（不詢問），值是原因文字。</param>
public sealed record CommandCheck(string? BlockedReason, IReadOnlyList<CommandRisk> Risks)
{
    public bool IsBlocked => BlockedReason is not null;
}

/// <summary>擋掉明顯危險的寫法、找出風險提示。防手滑用，不是安全邊界（M31 設計 2）。</summary>
public interface ICommandGuard
{
    CommandCheck Check(string command);
}

/// <summary>指令確認卡要顯示的內容（M31 設計 6），由 <c>run_command</c> 的 Describe 產生，放在 <see cref="Tools.ToolPreview.Command"/>。</summary>
/// <param name="ShellName">給使用者看的 shell 名稱：PowerShell、cmd、bash。</param>
/// <param name="IsScratchDirectory">true＝執行位置是這則對話的暫存資料夾。</param>
public sealed record CommandProposal(
    string ShellName,
    string Command,
    string Description,
    string WorkingDirectory,
    bool IsScratchDirectory,
    IReadOnlyList<CommandRisk> Risks);
