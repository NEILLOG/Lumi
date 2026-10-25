namespace Lumi.Web.Proxy;

/// <summary>下載（或讀取）PAC 檔。PAC 檔本身的下載不經過 Proxy（M17 D2）。</summary>
internal class PacScriptLoader
{
    private const long MaxBytes = 1024 * 1024;

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <exception cref="PacException">訊息「PAC 檔下載失敗：{原因}」。</exception>
    public virtual async Task<string> LoadAsync(string pacUrl, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(pacUrl, UriKind.Absolute, out var uri))
                throw new PacException("PAC 檔下載失敗：網址格式不正確");

            if (uri.IsFile)
            {
                var info = new FileInfo(uri.LocalPath);
                if (!info.Exists) throw new PacException("PAC 檔下載失敗：找不到檔案");
                if (info.Length > MaxBytes) throw new PacException("PAC 檔下載失敗：PAC 檔太大");
                return await File.ReadAllTextAsync(uri.LocalPath, ct).ConfigureAwait(false);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new PacException($"PAC 檔下載失敗：HTTP {(int)response.StatusCode}");
            if (response.Content.Headers.ContentLength > MaxBytes)
                throw new PacException("PAC 檔下載失敗：PAC 檔太大");

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxBytes) throw new PacException("PAC 檔下載失敗：PAC 檔太大");
            }
            return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        catch (PacException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new PacException("PAC 檔下載失敗：逾時");
        }
        catch (HttpRequestException)
        {
            throw new PacException("PAC 檔下載失敗：連不上");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new PacException("PAC 檔下載失敗：找不到檔案");
        }
    }
}
