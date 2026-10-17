namespace Lumi.Core.Secrets;

/// <summary>
/// 包住金鑰明文。<see cref="ToString"/> 一律回傳 <c>***</c>，避免被 log、例外訊息、除錯顯示帶出去。
/// 只在真正要用的地方（例如組 HTTP 標頭）呼叫 <see cref="Reveal"/>。
/// </summary>
public sealed class SecretValue
{
    private readonly string _value;

    public SecretValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException("金鑰內容不可為空。", nameof(value));
        _value = value;
    }

    public string Reveal() => _value;

    public override string ToString() => "***";
}
