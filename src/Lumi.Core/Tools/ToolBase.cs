using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lumi.Core.Tools;

/// <summary>
/// 所有內建工具的基底（agent.md 第 8 節）。引數用強型別 record，Schema 自動產生；
/// 引數解析失敗時回 <see cref="ToolResult.Error"/> 給模型，由它自己修正。
/// <see cref="Descriptor"/> 沒有預設值：權限等級與可用模式是必填，沒有宣告就無法編譯。
/// </summary>
public abstract class ToolBase<TArgs> : ITool where TArgs : class
{
    private JsonNode? _schema;

    public abstract ToolDescriptor Descriptor { get; }

    public JsonNode InputSchema => (_schema ??= ToolSchema.Generate(typeof(TArgs))).DeepClone();

    /// <summary>執行工具。（不叫 <c>ExecuteAsync</c>：引數型別是 string 時會和 <see cref="ITool.ExecuteAsync"/> 撞簽章。）</summary>
    protected abstract Task<ToolResult> InvokeAsync(TArgs args, ToolContext context, CancellationToken ct);

    /// <summary>卡片標題的「對象」與確認區的影響說明；預設沒有。</summary>
    protected virtual ToolPreview Describe(TArgs args, ToolContext context) => ToolPreview.None;

    public ToolPreview Preview(string argumentsJson, ToolContext context) =>
        TryParse(argumentsJson, out var args, out _) ? Describe(args, context) : ToolPreview.None;

    public Task<ToolResult> ExecuteAsync(string argumentsJson, ToolContext context, CancellationToken ct)
    {
        if (!TryParse(argumentsJson, out var args, out var error))
            return Task.FromResult(ToolResult.Error($"參數不正確：{error}"));
        return InvokeAsync(args, context, ct);
    }

    private static bool TryParse(string json, out TArgs args, out string error)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<TArgs>(string.IsNullOrWhiteSpace(json) ? "{}" : json, ToolSchema.Options);
            if (parsed is null)
            {
                args = null!;
                error = "引數不能是 null。";
                return false;
            }
            args = parsed;
            error = "";
            return true;
        }
        catch (JsonException ex)
        {
            args = null!;
            error = ex.Message;
            return false;
        }
    }
}
