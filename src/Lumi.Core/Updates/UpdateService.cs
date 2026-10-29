using Lumi.Core.Models;
using Microsoft.Extensions.Logging;

namespace Lumi.Core.Updates;

/// <summary>檢查新版、記住使用者略過的版本。畫面只看這個介面。</summary>
public interface IUpdateService
{
    /// <summary>目前要提醒使用者的新版；沒有（最新、已略過、尚未檢查）時是 null。</summary>
    UpdateInfo? Available { get; }

    event EventHandler? AvailableChanged;

    /// <summary>
    /// 檢查一次。<paramref name="manual"/> 為 false（啟動時）：尊重「啟動時檢查」的開關與略過的版本，失敗時只記 log、不打擾；
    /// 為 true（設定頁的「立即檢查」）：一定檢查，已略過的版本也會回報有新版。
    /// </summary>
    Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken ct = default);

    /// <summary>略過這個版本：之後的自動檢查不再提醒它，更新的版本出現才會再提醒。</summary>
    Task SkipAsync(UpdateInfo update, CancellationToken ct = default);

    /// <summary>這次先關掉提醒（不記住）；下次啟動檢查到仍會提醒。</summary>
    void Dismiss();
}

public sealed class UpdateService(IUpdateChecker checker, IModelCatalog catalog, ILogger<UpdateService> logger) : IUpdateService
{
    /// <summary>單次檢查的時間上限：網路慢或 Proxy 卡住時不要一直佔著。</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private UpdateInfo? _available;

    public UpdateInfo? Available => Volatile.Read(ref _available);

    public event EventHandler? AvailableChanged;

    public async Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken ct = default)
    {
        var behavior = catalog.Current.Behavior;
        if (!manual && !behavior.CheckForUpdates)
            return new UpdateCheckResult(UpdateCheckOutcome.Disabled);

        UpdateInfo? latest;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            latest = await checker.GetLatestAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is UpdateCheckException or OperationCanceledException or HttpRequestException)
        {
            var message = ex is UpdateCheckException ? ex.Message : "連不上更新伺服器。";
            logger.LogInformation("檢查更新失敗：{Message}", ex.Message);
            return new UpdateCheckResult(UpdateCheckOutcome.Failed, Message: message);
        }

        if (latest is null || !AppVersion.IsNewer(latest.Version, AppVersion.Current))
        {
            Set(null);
            return new UpdateCheckResult(UpdateCheckOutcome.UpToDate);
        }

        if (!manual && AppVersion.AreSame(latest.Version, behavior.SkippedVersion))
        {
            Set(null);
            return new UpdateCheckResult(UpdateCheckOutcome.Skipped, latest);
        }

        Set(latest);
        return new UpdateCheckResult(UpdateCheckOutcome.Available, latest);
    }

    public async Task SkipAsync(UpdateInfo update, CancellationToken ct = default)
    {
        await catalog.SetBehaviorAsync(catalog.Current.Behavior with { SkippedVersion = update.Version }, ct).ConfigureAwait(false);
        Set(null);
    }

    public void Dismiss() => Set(null);

    private void Set(UpdateInfo? update)
    {
        if (Interlocked.Exchange(ref _available, update) != update)
            AvailableChanged?.Invoke(this, EventArgs.Empty);
    }
}
