using Lumi.Core.Speech;

namespace Lumi.App.Tests.TestSupport;

/// <summary>
/// 假的語音轉文字：記錄每次呼叫（檔名、對話 ID、WAV 長度）；依序丟出 <see cref="Failures"/>，沒有失敗時回 <see cref="Result"/>。
/// <see cref="Hang"/> 為 true 時一直等到被取消（測試「轉寫中取消」用）。
/// </summary>
internal sealed class FakeSpeechToText : ISpeechToText
{
    public List<(string FileName, string? SessionId, int WavBytes)> Calls { get; } = [];

    public Queue<Exception> Failures { get; } = new();

    public TranscriptionResult Result { get; set; } = new("你好", "zh", TimeSpan.FromSeconds(1));

    public bool Hang { get; set; }

    /// <summary>因為被取消而結束的呼叫次數。</summary>
    public int CanceledCount { get; private set; }

    public async Task<TranscriptionResult> TranscribeAsync(Stream wav, string fileName, string? sessionId, CancellationToken ct)
    {
        using var copy = new MemoryStream();
        await wav.CopyToAsync(copy, ct);
        Calls.Add((fileName, sessionId, (int)copy.Length));

        try
        {
            if (Hang)
                await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            CanceledCount++;
            throw;
        }

        if (Failures.Count > 0)
            throw Failures.Dequeue();
        return Result;
    }
}
