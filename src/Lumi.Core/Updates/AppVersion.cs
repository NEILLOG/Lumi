using System.Reflection;

namespace Lumi.Core.Updates;

/// <summary>程式版本與版本號比較。版本號只比數字部分（<c>1.2.3</c>）；<c>v</c> 前綴、<c>-rc1</c>、<c>+build</c> 後綴都會忽略。</summary>
public static class AppVersion
{
    /// <summary>目前執行的版本，例如 <c>1.0.0</c>（發佈時由 <c>-p:Version=</c> 寫入）。</summary>
    public static string Current { get; } = Normalize(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString());

    /// <summary>解析 <c>v1.2.3</c>、<c>1.2.3-rc1+abc</c>、<c>1.2</c>（2～4 段純數字）。不合法回 false。</summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var core = text.Trim().TrimStart('v', 'V');
        var cut = core.IndexOfAny(['-', '+']);
        if (cut >= 0)
            core = core[..cut];

        var parts = core.Split('.');
        if (parts.Length is < 2 or > 4 || !parts.All(p => p.Length is > 0 and <= 9 && p.All(char.IsAsciiDigit)))
            return false;

        return Version.TryParse(core, out version!);
    }

    /// <summary><paramref name="candidate"/> 比 <paramref name="current"/> 新。任一個不能解析時回 false（寧可不提醒，也不要誤報）。</summary>
    public static bool IsNewer(string? candidate, string? current) =>
        TryParse(candidate, out var a) && TryParse(current, out var b) && a > b;

    /// <summary>兩個版本號是同一個版本（忽略前綴與後綴）。無法解析時退回字串比較。</summary>
    public static bool AreSame(string? a, string? b) =>
        TryParse(a, out var x) && TryParse(b, out var y)
            ? x == y
            : string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>顯示用：去掉 <c>v</c> 前綴與 <c>+build</c> 後綴，不能解析時原樣回傳。</summary>
    public static string Normalize(string? text)
    {
        var value = (text ?? "").Trim().TrimStart('v', 'V');
        var plus = value.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? value[..plus] : value.Length > 0 ? value : "0.0.0";
    }
}
