using Lumi.App.ViewModels;
using Lumi.Core.Platform;
using Lumi.Core.Updates;

namespace Lumi.App.Tests.TestSupport;

internal sealed class FakeLaunchAtLogin(bool supported = true, bool enabled = false) : ILaunchAtLogin
{
    public bool IsSupported { get; } = supported;

    public bool Enabled { get; private set; } = enabled;

    /// <summary>設成例外時，下一次 <see cref="SetEnabled"/> 會丟出它（狀態不變）。</summary>
    public Exception? Failure { get; set; }

    public bool IsEnabled() => Enabled;

    public void SetEnabled(bool value)
    {
        if (Failure is not null)
            throw Failure;
        Enabled = value;
    }
}

internal sealed class FakeUpdateService : IUpdateService
{
    public UpdateInfo? Available { get; private set; }

    public event EventHandler? AvailableChanged;

    /// <summary>下一次檢查要回的結果。</summary>
    public UpdateCheckResult NextResult { get; set; } = new(UpdateCheckOutcome.UpToDate);

    public List<bool> Checks { get; } = [];

    public List<UpdateInfo> Skipped { get; } = [];

    public int Dismissals { get; private set; }

    public Exception? SkipFailure { get; set; }

    public static UpdateInfo Release(string version = "9.9.9", string notes = "## 新功能\n- 好用") =>
        new(version, $"Lumi {version}", notes, new Uri($"https://github.com/NEILLOG/DesktopAgent/releases/tag/v{version}"), null);

    /// <summary>模擬啟動時檢查到新版。</summary>
    public void Announce(UpdateInfo? update)
    {
        Available = update;
        AvailableChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken ct = default)
    {
        Checks.Add(manual);
        if (NextResult.Outcome == UpdateCheckOutcome.Available)
            Announce(NextResult.Update);
        return Task.FromResult(NextResult);
    }

    public Task SkipAsync(UpdateInfo update, CancellationToken ct = default)
    {
        if (SkipFailure is not null)
            return Task.FromException(SkipFailure);
        Skipped.Add(update);
        Announce(null);
        return Task.CompletedTask;
    }

    public void Dismiss()
    {
        Dismissals++;
        Announce(null);
    }
}

internal sealed class FakeUrlLauncher(bool succeeds = true) : IUrlLauncher
{
    public List<Uri> Opened { get; } = [];

    public Task<bool> OpenAsync(Uri url)
    {
        Opened.Add(url);
        return Task.FromResult(succeeds);
    }
}
