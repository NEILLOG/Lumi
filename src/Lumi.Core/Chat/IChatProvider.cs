namespace Lumi.Core.Chat;

/// <summary>
/// 傳送對話、回傳串流事件。各供應商（Anthropic、M2 的 OpenAI 相容）各自實作，
/// UI 與對話迴圈只透過這個介面，不直接接觸供應商格式（agent.md 第 5、7 節）。
/// </summary>
/// <remarks>
/// 事件順序的約定：
/// <list type="bullet">
/// <item>一次呼叫剛好以一個 <see cref="Completed"/> 或一個 <see cref="Failed"/> 結束，之後不再有事件。</item>
/// <item><see cref="Completed"/> 的訊息是完整的助理訊息，包含 thinking 區塊與 signature，可以直接存檔、
/// 原樣送回下一次呼叫。</item>
/// <item>取消：丟 <see cref="OperationCanceledException"/>，不發 <see cref="Failed"/>。已收到的文字由
/// 呼叫端自己累積存成「已停止」；未完成的思考內容沒有 signature、不能送回，不保留。</item>
/// <item>介面以外的例外不會發生：串流中的任何錯誤都轉成 <see cref="Failed"/>（取消除外）。</item>
/// </list>
/// </remarks>
public interface IChatProvider
{
    IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, CancellationToken ct = default);
}
