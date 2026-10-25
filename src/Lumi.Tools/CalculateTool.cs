using System.ComponentModel;
using System.Globalization;
using Lumi.Core.Tools;

namespace Lumi.Tools;

public sealed record CalculateArgs(
    [property: Description("要計算的運算式，例如 (12.5 + 3) * 4 或 sqrt(2) ^ 3。支援 + - * / % ^、括號、pi、e，以及 sqrt abs round floor ceil min max pow ln log exp sin cos tan")]
    string Expression);

public sealed class CalculateTool : ToolBase<CalculateArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "calculate", "計算",
        "計算數學運算式並回傳精確的數字結果。任何需要算術的時候都用它，不要心算；只接受運算式本身，不要放單位或文字。",
        ToolPermission.ReadOnly, ToolModes.Both, TimeSpan.FromSeconds(5), Group: ToolGroups.General, Summary: "計算數學算式。");

    protected override ToolPreview Describe(CalculateArgs args, ToolContext context) => new(args.Expression?.Trim(), null);

    protected override Task<ToolResult> InvokeAsync(CalculateArgs args, ToolContext context, CancellationToken ct)
    {
        try
        {
            var value = ExpressionEvaluator.Evaluate(args.Expression);
            var text = value.ToString("G15", CultureInfo.InvariantCulture);
            return Task.FromResult(ToolResult.Ok(text, $"= {text}"));
        }
        catch (ExpressionException e)
        {
            return Task.FromResult(ToolResult.Error($"算不出來：{e.Message}", "運算式有誤"));
        }
    }
}
