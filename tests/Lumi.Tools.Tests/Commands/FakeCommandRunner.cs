using Lumi.Core.Commands;

namespace Lumi.Tools.Tests.Commands;

/// <summary>假的 <see cref="ICommandRunner"/>：不啟動任何程序，記錄收到的請求，回傳設定好的結果。</summary>
internal sealed class FakeCommandRunner : ICommandRunner
{
    public IReadOnlyList<CommandShell> AvailableShells { get; set; } = [CommandShell.PowerShell, CommandShell.Cmd];

    public CommandShell DefaultShell { get; set; } = CommandShell.PowerShell;

    public CommandResult Result { get; set; } = new(CommandOutcome.Completed, 0, "", false, TimeSpan.FromSeconds(1.2));

    public List<CommandRequest> Requests { get; } = [];

    public string GetScratchDirectory(string conversationId) => "/tmp/lumi-test/" + conversationId;

    public Task<CommandResult> RunAsync(CommandRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(Result);
    }
}

/// <summary>假的 <see cref="ICommandGuard"/>：回傳設定好的檢查結果，記錄被檢查的指令。</summary>
internal sealed class FakeCommandGuard : ICommandGuard
{
    public CommandCheck Next { get; set; } = new(null, []);

    public List<string> Checked { get; } = [];

    public CommandCheck Check(string command)
    {
        Checked.Add(command);
        return Next;
    }
}
