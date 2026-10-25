using System.Net;
using System.Net.Sockets;
using System.Text;
using Lumi.Core.Browser;
using Lumi.Core.Mcp;
using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Lumi.Core.Web;
using Lumi.Web.Proxy;

namespace Lumi.Web.Tests.Proxy;

/// <summary>只提供網路設定與變更通知的假目錄。</summary>
internal sealed class FakeCatalog(NetworkSettings network) : IModelCatalog
{
    public AppSettings Current { get; private set; } = AppSettings.Empty with { Network = network };

    public string? LoadProblem => null;

    public event EventHandler<AppSettings>? Changed;

    /// <summary>換網路設定並通知。</summary>
    public void SetNetwork(NetworkSettings settings)
    {
        Current = Current with { Network = settings };
        Changed?.Invoke(this, Current);
    }

    /// <summary>換別的設定（主題）並通知，網路設定不變。</summary>
    public void ChangeOther()
    {
        Current = Current with { Theme = Current.Theme == ThemePreference.Dark ? ThemePreference.Light : ThemePreference.Dark };
        Changed?.Invoke(this, Current);
    }

    public Task SetBrowserAsync(BrowserSettings settings, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetNetworkAsync(NetworkSettings settings, SecretValue? newProxyPassword, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> HasProxyPasswordAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetSpeechAsync(Lumi.Core.Speech.SpeechSettings settings, SecretValue? newToken, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> HasSpeechTokenAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task ClearSpeechTokenAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task SaveConnectionAsync(ConnectionSettings connection, SecretValue? newApiKey, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> HasApiKeyAsync(string connectionId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task ClearApiKeyAsync(string connectionId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeleteConnectionAsync(string connectionId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SaveModelAsync(ModelSettings model, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeleteModelAsync(string modelSettingsId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetWorkbenchModelAsync(string modelSettingsId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetSpriteModelAsync(string modelSettingsId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetThemeAsync(ThemePreference theme, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetThemeIdAsync(string themeId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetPersonaAsync(PersonaSettings persona, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetSpriteAsync(SpriteSettings sprite, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetWorkbenchWindowAsync(WindowSettings window, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SaveMcpServerAsync(McpServerSettings server, McpServerSecrets? newSecrets, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeleteMcpServerAsync(string serverId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<McpServerSecrets> GetMcpSecretsAsync(string serverId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetToolsAsync(ToolSettings tools, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetMemoryEnabledAsync(bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetNotesFolderAsync(string folder, CancellationToken ct = default) => throw new NotSupportedException();
    public ResolvedModel? Resolve(string? modelSettingsId) => null;
}

/// <summary>記憶體版的 ISecretStore；記下讀取次數。</summary>
internal sealed class InMemorySecrets : ISecretStore
{
    public Dictionary<string, string> Items { get; } = [];

    public int GetCount { get; private set; }

    public Task<SecretValue?> GetAsync(SecretKey key, CancellationToken ct = default)
    {
        GetCount++;
        return Task.FromResult(Items.TryGetValue(key.Name, out var v) ? new SecretValue(v) : null);
    }

    public Task SetAsync(SecretKey key, SecretValue value, CancellationToken ct = default)
    {
        Items[key.Name] = value.Reveal();
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(SecretKey key, CancellationToken ct = default) => Task.FromResult(Items.Remove(key.Name));

    public Task<bool> ExistsAsync(SecretKey key, CancellationToken ct = default) => Task.FromResult(Items.ContainsKey(key.Name));
}

/// <summary>假的「系統 Proxy」。</summary>
internal sealed class FakeSystemProxy(Func<Uri, Uri?> pick) : IWebProxy
{
    public ICredentials? Credentials { get; set; }

    public Uri? GetProxy(Uri destination) => pick(destination);

    public bool IsBypassed(Uri host) => pick(host) is null;
}

/// <summary>算呼叫次數的 PAC 載入器。</summary>
internal sealed class CountingPacLoader(string script) : PacScriptLoader
{
    public int LoadCount { get; private set; }

    public override Task<string> LoadAsync(string pacUrl, CancellationToken ct)
    {
        LoadCount++;
        return Task.FromResult(script);
    }
}

/// <summary>暫存的 PAC 檔，用 file:// 網址載入。</summary>
internal sealed class TempPacFile : IDisposable
{
    public TempPacFile(string script)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lumi-test-{Guid.NewGuid():N}.pac");
        File.WriteAllText(Path, script);
    }

    public string Path { get; }

    public string Url => new Uri(Path).AbsoluteUri;

    public void Dispose()
    {
        try { File.Delete(Path); } catch (IOException) { }
    }
}

/// <summary>最簡單的本機 HTTP 伺服器（127.0.0.1、隨機 port）：每個連線讀完標頭就回固定的回應。</summary>
internal sealed class LocalHttpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly string _response;

    public LocalHttpServer(int statusCode, string reason, string body = "")
    {
        _response = $"HTTP/1.1 {statusCode} {reason}\r\nContent-Type: text/plain\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    public int Port { get; }

    public Uri Url => new($"http://127.0.0.1:{Port}/");

    private async Task AcceptLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => Serve(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var seen = new StringBuilder();
                while (!seen.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var n = await stream.ReadAsync(buffer, _cts.Token);
                    if (n == 0) return;
                    seen.Append(Encoding.ASCII.GetString(buffer, 0, n));
                }
                await stream.WriteAsync(Encoding.UTF8.GetBytes(_response), _cts.Token);
                await stream.FlushAsync(_cts.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}
