using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Lumi.Core.Tools;

/// <summary>
/// 執行一個工具並處理所有共通的失敗：逾時、例外、結果太長。工具本身只管自己的邏輯。
/// 使用者按停止（<paramref name="ct"/> 被取消）時丟 <see cref="OperationCanceledException"/>，由對話迴圈處理。
/// </summary>
public static class ToolExecutor
{
    /// <summary>
    /// 截斷時省略說明的開頭；完整格式是 <c>{TruncatedNotice}{N} 行；需要的話可以用讀檔工具指定行範圍再讀]\n</c>（N 是被省略的行數）。
    /// </summary>
    public const string TruncatedNotice = "\n[中間省略了 ";

    /// <summary>切點往前或往後找換行的最大距離（字元）：找太遠會把開頭或結尾縮得太短，超過就照字元切。</summary>
    private const int LineAlignWindow = 200;

    public static async Task<ToolResult> RunAsync(
        ITool tool, string argumentsJson, ToolContext context, int maxResultChars, ILogger? logger, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(tool.Descriptor.Timeout);

        try
        {
            var result = await tool.ExecuteAsync(argumentsJson, context, timeout.Token).ConfigureAwait(false);
            var truncated = Truncate(result, maxResultChars);
            // 只記觸發的事實與長度，不記內容
            if (!ReferenceEquals(truncated, result))
                logger?.LogInformation("工具 {Tool} 的結果超過 {Max} 字元，已截斷（原長 {Length} 字元）", tool.Descriptor.Name, maxResultChars, result.Content.Length);
            return truncated;
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

    /// <summary>
    /// 太長就截斷：保留開頭約 70% 與結尾約 30%（build log 的錯誤常在最後），中間換成省略說明。
    /// 切點盡量對齊到換行（開頭往前、結尾往後找近處的換行，找不到就照字元切）；不把代理對（emoji）切成兩半。
    /// </summary>
    public static ToolResult Truncate(ToolResult result, int maxChars)
    {
        var content = result.Content;
        if (content.Length <= maxChars)
            return result;

        var headLength = maxChars * 7 / 10;
        var tailLength = maxChars - headLength;

        // 開頭：不切斷代理對；往前找最近的換行，換行本身省略掉（省略說明自己會換行）
        var headEnd = headLength;
        if (headEnd > 0 && char.IsHighSurrogate(content[headEnd - 1]))
            headEnd--;
        var omitStart = headEnd;
        if (headEnd > 0)
        {
            var newline = content.LastIndexOf('\n', headEnd - 1, Math.Min(LineAlignWindow, headEnd));
            if (newline >= 0)
            {
                headEnd = newline;
                omitStart = newline + 1;
            }
        }

        // 結尾：不切斷代理對；往後找最近的換行，換行本身省略掉
        var tailStart = content.Length - tailLength;
        if (tailStart < content.Length && char.IsLowSurrogate(content[tailStart]))
            tailStart++;
        var omitEnd = tailStart;
        if (tailStart < content.Length)
        {
            var newline = content.IndexOf('\n', tailStart, Math.Min(LineAlignWindow, content.Length - tailStart));
            if (newline >= 0)
            {
                omitEnd = newline;
                tailStart = newline + 1;
            }
        }

        // 被省略的中間部分有幾個換行就是幾行再加一（切在行中間的那一行也算）
        var omittedLines = 1;
        for (var i = omitStart; i < omitEnd; i++)
        {
            if (content[i] == '\n')
                omittedLines++;
        }

        var notice = $"{TruncatedNotice}{omittedLines} 行；需要的話可以用讀檔工具指定行範圍再讀]\n";
        return result with { Content = content[..headEnd] + notice + content[tailStart..] };
    }

    public static long ElapsedMilliseconds(long startTimestamp, TimeProvider time) =>
        (long)time.GetElapsedTime(startTimestamp).TotalMilliseconds;
}
