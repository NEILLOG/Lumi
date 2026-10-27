namespace Lumi.Core.Chat;

/// <summary>
/// 程式檔案不完整（載入組件失敗）的判斷（M22）。供應商的 <c>catch (Exception)</c> 要放過它，
/// 交給 <see cref="ChatSession"/> 回報 <see cref="ChatFailureKind.AppFilesMissing"/>，不要被當成連線中斷。
/// </summary>
public static class MissingAppFiles
{
    /// <summary>例外鏈（含內層例外與彙總例外）裡有沒有 <see cref="FileNotFoundException"/>／<see cref="FileLoadException"/>。</summary>
    public static bool Is(Exception? exception) => exception switch
    {
        null => false,
        FileNotFoundException or FileLoadException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(Is),
        _ => Is(exception.InnerException),
    };
}
