using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Lumi.Core.Tools;

/// <summary>
/// 執行一個工具並處理所有共通的失敗：逾時、例外、結果太長。工具本身只管自己的邏輯。
/// 使用者按停止（<paramref name="ct"/> 被取消）時丟 <see cref="OperationCanceledException"/>，由對話迴圈處理。
/// </summary>
public static class ToolExecutor
{
    public const string TruncatedNotice = "\n[內容過長，已截斷]";

    public static async Task<ToolResult> RunAsync(
        ITool tool, string argumentsJson, ToolContext context, int maxResultChars, ILogger? logger, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(tool.Descriptor.Timeout);

        try
        {
            var result = await tool.ExecuteAsync(argumentsJson, context, timeout.Token).ConfigureAwait(false);
            return Truncate(result, maxResultChars);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ToolResult.Error($"執行逾時（{tool.Descriptor.Timeout.TotalSeconds:0.#} 秒）。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 例外細節只進 log，不回給模型（可能含路徑等內部資訊）
            logger?.LogError(ex, "工具 {Tool} 執行失敗", tool.Descriptor.Name);
            return ToolResult.Error("工具執行失敗。");
        }
    }

    /// <summary>太長就截斷並附註；不把代理對（emoji）切成兩半。</summary>
    public static ToolResult Truncate(ToolResult result, int maxChars)
    {
        if (result.Content.Length <= maxChars)
            return result;

        var cut = maxChars;
        if (cut > 0 && char.IsLowSurrogate(result.Content[cut]))
            cut--;
        return result with { Content = result.Content[..cut] + TruncatedNotice };
    }

    public static long ElapsedMilliseconds(long startTimestamp, TimeProvider time) =>
        (long)time.GetElapsedTime(startTimestamp).TotalMilliseconds;
}
