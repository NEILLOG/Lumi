using System.Globalization;

namespace Lumi.Core.Usage;

/// <summary>費用與 tokens 的顯示格式（plan/23-usage-cost.md 第 6 節「數字格式」）。全部用 <see cref="CultureInfo.InvariantCulture"/>。</summary>
public static class MoneyFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>新台幣：取整數、千分位；絕對值 &lt; 10 留一位小數（<c>NT$3.2</c>、<c>NT$0</c>）；estimated 加前綴「≈」。</summary>
    public static string Twd(decimal usd, bool estimated = false)
    {
        var twd = usd * Money.TwdPerUsd;
        var oneDecimal = Math.Round(twd, 1, MidpointRounding.AwayFromZero);
        var text = Math.Abs(oneDecimal) < 10
            ? oneDecimal.ToString("0.#", Inv)
            : Math.Round(twd, 0, MidpointRounding.AwayFromZero).ToString("N0", Inv);
        return (estimated ? "≈" : "") + "NT$" + text;
    }

    /// <summary>美元：兩位小數、千分位（<c>US$15.25</c>）。</summary>
    public static string Usd(decimal usd) =>
        "US$" + Math.Round(usd, 2, MidpointRounding.AwayFromZero).ToString("N2", Inv);

    /// <summary>美元換成新台幣的整數金額（預算輸入框顯示用）。</summary>
    public static decimal TwdAmount(decimal usd) =>
        Math.Round(usd * Money.TwdPerUsd, 0, MidpointRounding.AwayFromZero);

    /// <summary>新台幣換成美元（儲存預算時用，不取整）。</summary>
    public static decimal UsdFromTwd(decimal twd) => twd / Money.TwdPerUsd;

    /// <summary>
    /// tokens 的縮寫：&lt; 1000 原樣；&lt; 100 萬用 K；其餘用 M。小於 10 留一位小數，否則取整數。
    /// 規則與 App 的 <c>WorkbenchViewModel.FormatTokens</c> 完全相同，兩邊要一起改。
    /// </summary>
    public static string Tokens(int tokens) => tokens switch
    {
        < 1_000 => tokens.ToString(Inv),
        < 1_000_000 => Scaled(tokens / 1_000.0, "K"),
        _ => Scaled(tokens / 1_000_000.0, "M"),
    };

    private static string Scaled(double value, string suffix) =>
        (value < 10 ? value.ToString("0.#", Inv) : value.ToString("0", Inv)) + suffix;
}
