namespace Lumi.Core.Speech;

/// <summary>語音轉文字（agent.md 第 11 節）。第一個實作是公司內部的 Whisper 服務（<c>Lumi.Speech</c>）。</summary>
public interface ISpeechToText
{
    /// <param name="wav">16 kHz／單聲道／16-bit 的 WAV（含檔頭），整段存在記憶體裡。方法結束時會關閉這個串流。</param>
    /// <param name="fileName">原始檔名，送給服務的 <c>file</c> 與 <c>file_name</c>。</param>
    /// <param name="sessionId">對話 ID，有值才送出 <c>session_id</c>。</param>
    /// <exception cref="SpeechFailure">服務、Token、網路或回應格式的問題，訊息可以直接顯示。</exception>
    Task<TranscriptionResult> TranscribeAsync(Stream wav, string fileName, string? sessionId, CancellationToken ct);
}

/// <summary>轉寫結果。<paramref name="Text"/> 可能是空白（服務沒有聽到內容），由呼叫端決定怎麼提示。</summary>
/// <param name="Language">服務回報的語言；沒有回報時是請求的語言（可能是 null）。</param>
/// <param name="Duration">服務回報的音訊長度；沒有回報時是 null。</param>
public sealed record TranscriptionResult(string Text, string? Language, TimeSpan? Duration);
