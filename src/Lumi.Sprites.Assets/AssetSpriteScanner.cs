namespace Lumi.Sprites.Assets;

/// <summary>掃描的結果：載入成功的角色（依 id 排序，結果穩定）與所有問題（含載入成功的角色的警告）。</summary>
public sealed record SpriteScanResult(IReadOnlyList<AssetSpriteDefinition> Sprites, IReadOnlyList<SpriteLoadIssue> Issues)
{
    public static SpriteScanResult Empty { get; } = new([], []);
}

/// <summary>
/// 掃描「角色」資料夾：只看第一層的子資料夾，每個子資料夾是一個角色。一個角色出錯不影響其他的；
/// id 重複時先到的（依資料夾名稱排序）留下，後面的拒載並說明。
/// </summary>
public static class AssetSpriteScanner
{
    public static SpriteScanResult Scan(string rootDirectory, IReadOnlySet<string>? reservedIds = null)
    {
        if (!Directory.Exists(rootDirectory))
            return SpriteScanResult.Empty;   // 還沒有角色資料夾：視為空，不是錯誤

        var taken = new Dictionary<string, string>(StringComparer.Ordinal);   // id → 資料夾名稱
        var reserved = reservedIds ?? new HashSet<string>();   // 內建角色已經用掉的 id；素材型角色彼此重複由下面的 taken 處理（說得出是哪個資料夾）
        var sprites = new List<AssetSpriteDefinition>();
        var issues = new List<SpriteLoadIssue>();

        IEnumerable<string> folders;
        try
        {
            folders = Directory.EnumerateDirectories(rootDirectory)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(Path.GetFileName, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add(new SpriteLoadIssue(Path.GetFileName(rootDirectory), null, $"讀取角色資料夾失敗（{ex.Message}）", true));
            return new SpriteScanResult([], issues);
        }

        foreach (var folder in folders)
        {
            var name = Path.GetFileName(folder);
            var info = new DirectoryInfo(folder);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
            {
                issues.Add(new SpriteLoadIssue(name, null, "角色資料夾不能是捷徑或符號連結", true));
                continue;
            }

            var result = AssetSpriteLoader.Load(folder, reserved);
            issues.AddRange(result.Issues);
            if (result.Definition is not { } definition)
                continue;

            var id = definition.Manifest.Id;
            if (taken.TryGetValue(id, out var owner))
            {
                issues.Add(new SpriteLoadIssue(name, "id", $"id \"{id}\" 已經被資料夾 {owner} 使用", true));
                continue;
            }
            taken[id] = name;
            sprites.Add(definition);
        }

        sprites.Sort((a, b) => string.CompareOrdinal(a.Manifest.Id, b.Manifest.Id));
        return new SpriteScanResult(sprites, issues);
    }
}
