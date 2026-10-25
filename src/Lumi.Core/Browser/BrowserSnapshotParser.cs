using System.Text;
using System.Text.RegularExpressions;

namespace Lumi.Core.Browser;

/// <param name="Text">處理後的快照文字。</param>
/// <param name="RefLabels">ref → 描述（例如 e3 → <c>button "送出"</c>）。</param>
public sealed record ParsedSnapshot(string Text, IReadOnlyDictionary<string, string> RefLabels, int RefCount, bool Truncated);

/// <summary>
/// 處理 Playwright AI 模式的無障礙快照（M18 D4、D13，純函式）：連結絕對化、刪掉不能點的連結、
/// 建立 ref → 描述表、依長度上限截斷。
/// </summary>
public static partial class BrowserSnapshotParser
{
    private const int MaxLabelNameChars = 60;

    [GeneratedRegex(@"^(?<indent>\s*)- /url:\s*(?<value>.*?)\s*$")]
    private static partial Regex UrlLineRegex();

    [GeneratedRegex("""^\s*- (?<role>[a-zA-Z]+)(?: "(?<name>(?:[^"\\]|\\.)*)")?.*\[ref=(?<ref>[a-z0-9]+)\]""")]
    private static partial Regex RefLineRegex();

    public static ParsedSnapshot Parse(string rawSnapshot, Uri pageUrl, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(pageUrl);
        maxChars = Math.Clamp(maxChars, BrowserLimits.SnapshotMinChars, BrowserLimits.SnapshotMaxChars);

        var lines = (rawSnapshot ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = new List<string>(lines.Length);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            var urlMatch = UrlLineRegex().Match(line);
            if (urlMatch.Success)
            {
                if (TryAbsolutize(urlMatch.Groups["value"].Value, pageUrl, out var absolute))
                    kept.Add($"{urlMatch.Groups["indent"].Value}- /url: {absolute}");
                continue;
            }

            var refMatch = RefLineRegex().Match(line);
            if (refMatch.Success)
                labels.TryAdd(refMatch.Groups["ref"].Value, Describe(refMatch));

            kept.Add(line);
        }

        var text = string.Join('\n', kept).TrimEnd();
        var truncated = false;
        if (text.Length > maxChars)
        {
            truncated = true;
            var cut = text.LastIndexOf('\n', maxChars);
            if (cut <= 0)
            {
                cut = maxChars;
                if (char.IsHighSurrogate(text[cut - 1]))
                    cut--;
            }

            text = text[..cut].TrimEnd();
        }

        return new ParsedSnapshot(text, labels, CountRefs(text), truncated);
    }

    private static string Describe(Match match)
    {
        var role = match.Groups["role"].Value;
        var nameGroup = match.Groups["name"];
        if (!nameGroup.Success)
            return role;

        var name = nameGroup.Value;
        if (name.Length > MaxLabelNameChars)
        {
            var length = MaxLabelNameChars;
            if (char.IsHighSurrogate(name[length - 1]))
                length--;
            name = name[..length] + "…";
        }

        return $"{role} \"{name}\"";
    }

    /// <summary>保留下來的文字裡有幾個不同的 ref。</summary>
    private static int CountRefs(string text)
    {
        if (text.Length == 0)
            return 0;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var match = RefLineRegex().Match(line);
            if (match.Success)
                seen.Add(match.Groups["ref"].Value);
        }

        return seen.Count;
    }

    /// <summary>http／https 才保留；相對網址以頁面網址為基準，並去掉 <c>#</c> 之後。</summary>
    private static bool TryAbsolutize(string value, Uri pageUrl, out string absolute)
    {
        absolute = "";
        value = value.Trim();
        if (value.Length >= 2 && value[0] == value[^1] && value[0] is '"' or '\'')
            value = value[1..^1].Trim();

        if (!Uri.TryCreate(pageUrl, value, out var uri) || uri.Scheme is not ("http" or "https"))
            return false;

        absolute = uri.GetLeftPart(UriPartial.Query);
        return true;
    }
}
