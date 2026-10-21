using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.Core.Models;

namespace Lumi.App.ViewModels;

/// <summary>連線清單的一列（卡片）：字首圖示、名稱、副標「格式 · N 個模型」、最近一次測試連線的狀態（點＋文字）。</summary>
public sealed record ConnectionListItem(string Id, string Name, ProviderFormat Format, int ModelCount, ConnectionStatus Status)
{
    public string FormatText => FormatNames.Of(Format);

    public string ModelCountText => string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Conn_ModelCount, ModelCount);

    /// <summary>卡片副標：「OpenAI 相容 · 2 個模型」。</summary>
    public string Subtitle => $"{FormatText} · {ModelCountText}";

    /// <summary>供應商字首方塊的字元（依連線名稱，沒有名稱時依格式）。</summary>
    public string Initial => ProviderInitial.Of(Name, Format);

    public string StatusText => ConnectionStatusText.Of(Status);

    public bool IsOk => Status == ConnectionStatus.Ok;

    public bool IsFailed => Status == ConnectionStatus.Failed;

    public bool IsUnknown => Status == ConnectionStatus.Unknown;
}

public static class FormatNames
{
    public static string Of(ProviderFormat format) =>
        format == ProviderFormat.Anthropic ? Strings.Format_Anthropic : Strings.Format_OpenAI;
}

/// <summary>連線狀態的文字（連線正常／連線失敗／尚未確認）：狀態不能只靠顏色，點旁邊一定要有字。</summary>
public static class ConnectionStatusText
{
    public static string Of(ConnectionStatus status) => status switch
    {
        ConnectionStatus.Ok => Strings.Conn_StatusOk,
        ConnectionStatus.Failed => Strings.Conn_StatusFailed,
        _ => Strings.Conn_StatusUnknown,
    };
}

/// <summary>供應商字首方塊（不使用官方商標）：連線名稱的第一個字元（英文轉大寫）；名稱是空的就用格式的字首。</summary>
public static class ProviderInitial
{
    public static string Of(string? name, ProviderFormat format)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length > 0)
            return System.Globalization.StringInfo.GetNextTextElement(trimmed).ToUpperInvariant();
        return format == ProviderFormat.Anthropic ? "A" : "O";
    }
}

public sealed record FormatOption(ProviderFormat Format)
{
    public string Text => FormatNames.Of(Format);

    public static IReadOnlyList<FormatOption> All { get; } =
        [new(ProviderFormat.Anthropic), new(ProviderFormat.OpenAICompatible)];
}
