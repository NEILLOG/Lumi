using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.Core.Models;

namespace Lumi.App.ViewModels;

/// <summary>
/// 模型設定清單的一列（卡片）。依連線排序，連線名稱顯示在每一列上。
/// 卡片：字首圖示（依連線）、顯示名稱、副標「model ID · 預設標籤」、所屬連線的狀態（<see cref="Status"/> 會隨測試連線更新）。
/// </summary>
public sealed partial class ModelListItem : ObservableObject
{
    public ModelListItem(string id, string connectionName, string displayName, string modelId, bool isWorkbenchDefault, bool isSpriteDefault,
        ProviderFormat format = ProviderFormat.OpenAICompatible, ConnectionStatus status = ConnectionStatus.Unknown)
    {
        Id = id;
        ConnectionName = connectionName;
        DisplayName = displayName;
        ModelId = modelId;
        IsWorkbenchDefault = isWorkbenchDefault;
        IsSpriteDefault = isSpriteDefault;
        Format = format;
        _status = status;
    }

    public string Id { get; }

    public string ConnectionName { get; }

    public string DisplayName { get; }

    public string ModelId { get; }

    public bool IsWorkbenchDefault { get; }

    public bool IsSpriteDefault { get; }

    public ProviderFormat Format { get; }

    public string Subtitle => $"{ConnectionName} · {ModelId}";

    public string Badges => (IsWorkbenchDefault, IsSpriteDefault) switch
    {
        (true, true) => $"{Strings.Models_BadgeWorkbench} · {Strings.Models_BadgeSprite}",
        (true, false) => Strings.Models_BadgeWorkbench,
        (false, true) => Strings.Models_BadgeSprite,
        _ => "",
    };

    public bool HasBadges => IsWorkbenchDefault || IsSpriteDefault;

    /// <summary>卡片副標：「gpt-5-mini · 工作台預設」；不是預設就只有確切的 model ID。</summary>
    public string CardSubtitle => HasBadges ? $"{ModelId} · {Badges}" : ModelId;

    /// <summary>卡片的完整說明（Tooltip）：副標被省略時看得到所屬連線與完整內容。</summary>
    public string CardTooltip => $"{DisplayName}\n{ConnectionName} · {CardSubtitle}";

    /// <summary>供應商字首方塊的字元（依所屬連線的名稱）。</summary>
    public string Initial => ProviderInitial.Of(ConnectionName, Format);

    /// <summary>所屬連線最近一次測試連線的狀態。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StatusText))]
    private ConnectionStatus _status;

    public string StatusText => ConnectionStatusText.Of(Status);
}

/// <summary>預設模型下拉的選項。</summary>
public sealed record ModelOption(string Id, string Text);

public sealed record ConnectionOption(string Id, string Name);
