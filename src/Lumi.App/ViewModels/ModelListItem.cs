using Lumi.App.Resources;

namespace Lumi.App.ViewModels;

/// <summary>模型設定清單的一列。依連線排序，連線名稱顯示在每一列上。</summary>
public sealed record ModelListItem(string Id, string ConnectionName, string DisplayName, string ModelId, bool IsWorkbenchDefault, bool IsSpriteDefault)
{
    public string Subtitle => $"{ConnectionName} · {ModelId}";

    public string Badges => (IsWorkbenchDefault, IsSpriteDefault) switch
    {
        (true, true) => $"{Strings.Models_BadgeWorkbench} · {Strings.Models_BadgeSprite}",
        (true, false) => Strings.Models_BadgeWorkbench,
        (false, true) => Strings.Models_BadgeSprite,
        _ => "",
    };

    public bool HasBadges => IsWorkbenchDefault || IsSpriteDefault;
}

/// <summary>預設模型下拉的選項。</summary>
public sealed record ModelOption(string Id, string Text);

public sealed record ConnectionOption(string Id, string Name);
