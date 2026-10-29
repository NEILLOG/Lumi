namespace Lumi.Core.Updates;

/// <summary>一個已發佈的版本。<paramref name="Notes"/> 是發佈說明（Markdown），可能是空字串。</summary>
public sealed record UpdateInfo(string Version, string Title, string Notes, Uri PageUrl, DateTimeOffset? PublishedAt);

/// <summary>取得「最新的正式版本」。實作在 Lumi.Web（GitHub Releases）。</summary>
public interface IUpdateChecker
{
    /// <returns>最新版本；還沒有任何發佈時回 null。連不上或回應不能用時丟 <see cref="UpdateCheckException"/>（訊息可直接顯示）。</returns>
    Task<UpdateInfo?> GetLatestAsync(CancellationToken ct);
}

public sealed class UpdateCheckException(string message, Exception? inner = null) : Exception(message, inner);

public enum UpdateCheckOutcome
{
    /// <summary>目前就是最新版。</summary>
    UpToDate,

    /// <summary>有新版（<see cref="UpdateCheckResult.Update"/>）。</summary>
    Available,

    /// <summary>有新版，但使用者已略過這個版本（只有自動檢查會回這個）。</summary>
    Skipped,

    /// <summary>設定裡關閉了啟動時檢查（只有自動檢查會回這個）。</summary>
    Disabled,

    /// <summary>檢查失敗（<see cref="UpdateCheckResult.Message"/> 是原因）。</summary>
    Failed,
}

public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, UpdateInfo? Update = null, string? Message = null);

/// <summary>沒有更新來源時的預設：永遠沒有新版。</summary>
public sealed class NullUpdateChecker : IUpdateChecker
{
    public Task<UpdateInfo?> GetLatestAsync(CancellationToken ct) => Task.FromResult<UpdateInfo?>(null);
}
