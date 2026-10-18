using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.Core.Models;

namespace Lumi.App.ViewModels;

/// <summary>連線清單的一列：名稱、格式、底下的模型數、最近一次測試連線的狀態點。</summary>
public sealed record ConnectionListItem(string Id, string Name, ProviderFormat Format, int ModelCount, ConnectionStatus Status)
{
    public string FormatText => FormatNames.Of(Format);

    public string ModelCountText => string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Conn_ModelCount, ModelCount);

    public bool IsOk => Status == ConnectionStatus.Ok;

    public bool IsFailed => Status == ConnectionStatus.Failed;

    public bool IsUnknown => Status == ConnectionStatus.Unknown;
}

public static class FormatNames
{
    public static string Of(ProviderFormat format) =>
        format == ProviderFormat.Anthropic ? Strings.Format_Anthropic : Strings.Format_OpenAI;
}

public sealed record FormatOption(ProviderFormat Format)
{
    public string Text => FormatNames.Of(Format);

    public static IReadOnlyList<FormatOption> All { get; } =
        [new(ProviderFormat.Anthropic), new(ProviderFormat.OpenAICompatible)];
}
