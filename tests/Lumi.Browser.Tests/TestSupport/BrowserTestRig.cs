using Lumi.Core.Browser;
using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Browser.Tests.TestSupport;

/// <summary>
/// Wave 2 的測試共用：一個本機網站（<see cref="LocalSite"/>）加一個用真瀏覽器的 <see cref="PlaywrightBrowserSessions"/>，
/// 設定檔放在暫存資料夾。結束時關閉瀏覽器並刪除暫存資料夾。
/// </summary>
internal sealed class BrowserTestRig : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lumi-browser-rig-" + Guid.NewGuid().ToString("N"));

    public BrowserTestRig(AppSettings? settings = null)
    {
        Site = new LocalSite();
        Sessions = new PlaywrightBrowserSessions(
            TestBrowsers.Instance, FakeCatalog.Create(settings ?? AppSettings.Empty), new MemorySecretStore(),
            new BrowserOptions { ProfileRoot = Path.Combine(_root, "profile") }, TimeProvider.System,
            NullLogger<PlaywrightBrowserSessions>.Instance);
    }

    public LocalSite Site { get; }

    public PlaywrightBrowserSessions Sessions { get; }

    /// <summary>測試用的暫存資料夾（上傳、下載的檔案放這裡）。</summary>
    public string Directory => EnsureDirectory(Path.Combine(_root, "files"));

    public Uri Url(string path) => Site.Url(path);

    /// <summary>在目前資料夾建一個檔案，回完整路徑。</summary>
    public string WriteFile(string name, string content)
    {
        var path = Path.Combine(Directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        Sessions.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Site.Dispose();
        try
        {
            if (System.IO.Directory.Exists(_root))
                System.IO.Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 暫存資料夾刪不掉不影響測試結果
        }
    }

    private static string EnsureDirectory(string path)
    {
        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        public Task<SecretValue?> GetAsync(SecretKey key, CancellationToken ct = default) => Task.FromResult<SecretValue?>(null);

        public Task SetAsync(SecretKey key, SecretValue value, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> DeleteAsync(SecretKey key, CancellationToken ct = default) => Task.FromResult(false);

        public Task<bool> ExistsAsync(SecretKey key, CancellationToken ct = default) => Task.FromResult(false);
    }
}
