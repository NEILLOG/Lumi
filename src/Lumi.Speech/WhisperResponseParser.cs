using System.Text.Json;
using Lumi.Core.Speech;

namespace Lumi.Speech;

/// <summary>
/// Whisper 回應的解析（純函式，不碰網路）。規則依舊版整合說明（outter_sys_spec/whisper.md）寫成，格式寬鬆但**不猜**：
/// 找不到文字就回 null，由呼叫端丟 <see cref="SpeechFailureKind.UnrecognizedResponse"/>。
/// </summary>
internal static class WhisperResponseParser
{
    /// <summary>包裝層最多幾層（<c>result.data.output</c> 這種）。</summary>
    internal const int MaxWrapperDepth = 3;

    private static readonly string[] WrapperNames = ["result", "data", "transcription", "transcript", "output"];

    /// <param name="contentType">回應的 media type（不含參數），例如 <c>application/json</c>。</param>
    /// <param name="body">回應內文。</param>
    /// <param name="requestLanguage">請求送出的語言；回應沒有 <c>language</c> 時採用。</param>
    /// <returns>找到文字（可能是空白）時回傳結果；都找不到回 null。</returns>
    public static TranscriptionResult? Parse(string? contentType, string body, string? requestLanguage)
    {
        var requested = string.IsNullOrWhiteSpace(requestLanguage) ? null : requestLanguage.Trim();
        var trimmed = (body ?? "").Trim();

        // 空白回應視為「找到但為空」，由上層提示「沒有聽到內容」
        if (trimmed.Length == 0)
            return new TranscriptionResult("", requested, null);

        // text/* 且不像 JSON：純文字直接採用
        if (!LooksLikeJson(trimmed) && IsTextMediaType(contentType))
            return new TranscriptionResult(trimmed, requested, null);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(trimmed);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var found = Find(root, wrapperLevel: 0);
            if (found is null)
                return null;

            var language = ReadString(found.Source, "language") ?? ReadString(root, "language") ?? requested;
            var duration = ReadDuration(found.Source) ?? ReadDuration(root);
            var text = found.Text ?? JoinSegments(found.Segments!, language);
            return new TranscriptionResult(text.Trim(), language, duration);
        }
    }

    /// <summary>
    /// 每一層的順序：先 <c>text</c>，再往包裝層（最多 <see cref="MaxWrapperDepth"/> 層）找，最後才看 <c>segments</c>。
    /// </summary>
    private static Found? Find(JsonElement obj, int wrapperLevel)
    {
        if (obj.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            return new Found(text.GetString() ?? "", null, obj);

        if (wrapperLevel < MaxWrapperDepth)
        {
            foreach (var name in WrapperNames)
            {
                if (obj.TryGetProperty(name, out var inner) && inner.ValueKind == JsonValueKind.Object
                    && Find(inner, wrapperLevel + 1) is { } found)
                    return found;
            }
        }

        if (obj.TryGetProperty("segments", out var segments) && segments.ValueKind == JsonValueKind.Array)
        {
            var texts = new List<string>();
            foreach (var segment in segments.EnumerateArray())
            {
                if (segment.ValueKind == JsonValueKind.Object && segment.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    texts.Add(t.GetString() ?? "");
            }
            return new Found(null, texts, obj);
        }

        return null;
    }

    /// <summary>中文與日文的 segments 之間不補空白，其他語言以單一空白串接。</summary>
    internal static string JoinSegments(IReadOnlyList<string> segments, string? language)
    {
        var separator = IsNoSpaceLanguage(language) ? "" : " ";
        return string.Join(separator, segments.Select(s => s.Trim()).Where(s => s.Length > 0));
    }

    internal static bool IsNoSpaceLanguage(string? language) =>
        language is not null && (language.Equals("zh", StringComparison.OrdinalIgnoreCase)
            || language.StartsWith("zh-", StringComparison.OrdinalIgnoreCase)
            || language.Equals("ja", StringComparison.OrdinalIgnoreCase)
            || language.StartsWith("ja-", StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeJson(string trimmed) => trimmed[0] is '{' or '[';

    private static bool IsTextMediaType(string? contentType) =>
        contentType is not null && contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase);

    private static string? ReadString(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()?.Trim();
            if (!string.IsNullOrEmpty(text))
                return text;
        }
        return null;
    }

    private static TimeSpan? ReadDuration(JsonElement obj)
    {
        if (obj.TryGetProperty("duration", out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var seconds)
            && double.IsFinite(seconds) && seconds >= 0)
            return TimeSpan.FromSeconds(seconds);
        return null;
    }

    /// <summary>找到的結果：<see cref="Text"/> 與 <see cref="Segments"/> 二選一；<see cref="Source"/> 是找到它的那個物件（讀 language、duration 用）。</summary>
    private sealed record Found(string? Text, List<string>? Segments, JsonElement Source);
}
