namespace Lumi.Core.Outlook;

/// <summary>電子郵件地址的簡單判斷（Outlook 工具與確認卡共用）。</summary>
public static class OutlookAddresses
{
    /// <summary>寄給自己的主旨前綴：讓使用者一眼看出這封信是 Lumi 寄的。</summary>
    public const string SelfSubjectPrefix = "[Lumi] ";

    /// <summary>有剛好一個 @、前後都有字、沒有空白、@ 後面有「.」而且不在頭尾。不做完整的 RFC 檢查。</summary>
    public static bool IsEmailAddress(string? value) => DomainOf(value) is not null;

    /// <summary>@ 後面的網域（小寫）；不是地址時回 null。</summary>
    public static string? DomainOf(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Any(char.IsWhiteSpace))
            return null;
        // 「名稱 <地址>」這類寫法不算地址：確認卡要能原樣顯示實際的收件人
        if (value.IndexOfAny(['<', '>', ',', ';', '"']) >= 0)
            return null;

        var at = value.IndexOf('@');
        if (at <= 0 || at != value.LastIndexOf('@') || at == value.Length - 1)
            return null;

        var domain = value[(at + 1)..];
        var dot = domain.IndexOf('.');
        if (dot <= 0 || domain[^1] == '.' || domain.Contains("..", StringComparison.Ordinal))
            return null;

        return domain.ToLowerInvariant();
    }
}
