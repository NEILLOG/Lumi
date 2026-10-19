using System.ComponentModel;
using Lumi.Core.Tools;

namespace Lumi.Providers.Tests.TestSupport;

public sealed record EchoArgs(
    [property: Description("要回聲的文字")] string Text,
    [property: Description("重複幾次")] int Times = 1);

/// <summary>只用來產生工具定義（Schema）給請求轉換測試；不會真的被執行。</summary>
public sealed class EchoTool : ToolBase<EchoArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "echo", "回聲", "把文字回聲回來。", ToolPermission.ReadOnly, ToolModes.Both, TimeSpan.FromSeconds(5));

    protected override Task<ToolResult> InvokeAsync(EchoArgs args, ToolContext context, CancellationToken ct) =>
        Task.FromResult(ToolResult.Ok(args.Text));
}
