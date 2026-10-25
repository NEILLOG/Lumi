namespace Lumi.Core.Browser;

/// <param name="Text">處理後的快照文字。</param>
/// <param name="RefLabels">ref → 描述（例如 e3 → <c>button "送出"</c>）。</param>
public sealed record ParsedSnapshot(string Text, IReadOnlyDictionary<string, string> RefLabels, int RefCount, bool Truncated);

public static class BrowserSnapshotParser
{
    public static ParsedSnapshot Parse(string rawSnapshot, Uri pageUrl, int maxChars) => throw new NotImplementedException();
}
