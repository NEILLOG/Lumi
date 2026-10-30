using System.Text.RegularExpressions;
using Lumi.Core.Models;

namespace Lumi.Core.Usage;

public enum PriceMatchKind { Exact, Normalized, Candidates, NotFound, Local }

/// <summary>比對結果。<see cref="PriceMatchKind.Exact"/>／<see cref="PriceMatchKind.Normalized"/> 才會自動帶入；<see cref="PriceMatchKind.Candidates"/> 只給使用者挑。</summary>
/// <param name="Entry">Exact／Normalized 時有值。</param>
/// <param name="Candidates">Candidates 時最多 3 筆（前綴相符的結果排第一）；其他情況是空的。</param>
public sealed record PriceMatch(PriceMatchKind Kind, PriceEntry? Entry, IReadOnlyList<PriceEntry> Candidates);

/// <summary>把模型 ID 對到價目表（plan/23-usage-cost.md 第 4 節）。純邏輯，不碰網路與檔案。</summary>
public static class PriceMatcher
{
    private const int MaxCandidates = 3;
    private const double FuzzyThreshold = 0.85;

    private static readonly string[] AzureHostSuffixes =
        [".openai.azure.com", ".services.ai.azure.com", ".cognitiveservices.azure.com"];

    private static readonly Regex RegionPrefix = new("^(us|eu|apac|global)\\.", RegexOptions.CultureInvariant);
    private static readonly Regex BedrockVendorPrefix = new("^(anthropic|meta|amazon|mistral|cohere|ai21|deepseek|openai)\\.", RegexOptions.CultureInvariant);
    private static readonly Regex VersionSuffix = new("-v[0-9]+(:[0-9]+)?$", RegexOptions.CultureInvariant);
    private static readonly Regex DateSuffix = new("-([0-9]{8}|[0-9]{4}-[0-9]{2}-[0-9]{2})$", RegexOptions.CultureInvariant);

    public static PriceMatch Match(PriceTable table, string modelId, ConnectionSettings connection)
    {
        if (LocalConnection.IsLoopback(connection.BaseUrl))
            return new(PriceMatchKind.Local, null, []);

        if (string.IsNullOrWhiteSpace(modelId))
            return new(PriceMatchKind.NotFound, null, []);

        var entries = table.Entries;

        // 1. 完全相符（不分大小寫）
        var exact = entries.FirstOrDefault(e => string.Equals(e.Key, modelId, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return new(PriceMatchKind.Exact, exact, []);

        var preferred = PreferredProvider(connection);
        var target = Normalize(modelId);
        var indexed = entries.Select(e => (Entry: e, Norm: Normalize(e.Key))).ToList();

        // 2. 正規化後相符：供應商符合連線的優先，其次鍵名最短
        var normalized = indexed
            .Where(x => x.Norm == target)
            .OrderBy(x => IsPreferred(x.Entry, preferred) ? 0 : 1)
            .ThenBy(x => x.Entry.Key.Length)
            .ThenBy(x => x.Entry.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        if (normalized.Entry is not null)
            return new(PriceMatchKind.Normalized, normalized.Entry, []);

        // 3. 前綴相符（以 - 為邊界，最長的優先）：只列候選
        var prefixes = indexed
            .Where(x => x.Norm.Length > 0 && target.StartsWith(x.Norm + "-", StringComparison.Ordinal))
            .OrderByDescending(x => x.Norm.Length)
            .ThenBy(x => IsPreferred(x.Entry, preferred) ? 0 : 1)
            .ThenBy(x => x.Entry.Key.Length)
            .ThenBy(x => x.Entry.Key, StringComparer.Ordinal)
            .Select(x => x.Entry);

        // 4. 近似（Jaro-Winkler ≥ 0.85）：只列候選
        var fuzzy = indexed
            .Select(x => (x.Entry, Score: JaroWinkler.Similarity(x.Norm, target)))
            .Where(x => x.Score >= FuzzyThreshold)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => IsPreferred(x.Entry, preferred) ? 0 : 1)
            .ThenBy(x => x.Entry.Key.Length)
            .ThenBy(x => x.Entry.Key, StringComparer.Ordinal)
            .Select(x => x.Entry);

        // 候選：前綴結果在前，接近似結果，依鍵名去重（不分大小寫），最多 3 筆
        var candidates = prefixes
            .Concat(fuzzy)
            .DistinctBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .Take(MaxCandidates)
            .ToList();
        if (candidates.Count > 0)
            return new(PriceMatchKind.Candidates, null, candidates);

        return new(PriceMatchKind.NotFound, null, []);
    }

    /// <summary>
    /// 去掉供應商前綴（最後一段）、<c>:latest</c>、Bedrock 區域前綴（us.／eu.／apac.／global.）、Bedrock 廠商前綴（anthropic.／meta.／…）、
    /// 版本後綴與日期後綴；轉小寫。
    /// </summary>
    public static string Normalize(string key)
    {
        var s = key.Trim().ToLowerInvariant();

        var slash = s.LastIndexOf('/');
        if (slash >= 0)
            s = s[(slash + 1)..];
        if (s.EndsWith(":latest", StringComparison.Ordinal))
            s = s[..^":latest".Length];

        s = RegionPrefix.Replace(s, string.Empty, 1);
        s = BedrockVendorPrefix.Replace(s, string.Empty, 1);
        s = VersionSuffix.Replace(s, string.Empty);
        s = DateSuffix.Replace(s, string.Empty);
        return s;
    }

    /// <summary>Azure 主機 → <c>azure</c>；Anthropic 格式 → <c>anthropic</c>；其他 OpenAI 相容 → <c>openai</c>。</summary>
    public static string PreferredProvider(ConnectionSettings connection)
    {
        if (IsAzureHost(connection.BaseUrl))
            return "azure";
        return connection.Format == ProviderFormat.Anthropic ? "anthropic" : "openai";
    }

    /// <summary>自動帶入的單價：來源是「已自動帶入」，還沒有經過使用者確認。</summary>
    public static ModelPricing ToPricing(PriceEntry entry) =>
        new(entry.InputPerMTok, entry.OutputPerMTok, entry.CacheReadPerMTok, entry.CacheWritePerMTok, IsFree: false, Source: PriceSource.Suggested);

    private static bool IsPreferred(PriceEntry entry, string preferred) =>
        string.Equals(entry.Provider, preferred, StringComparison.OrdinalIgnoreCase);

    private static bool IsAzureHost(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            return false;
        return AzureHostSuffixes.Any(suffix => uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }
}
