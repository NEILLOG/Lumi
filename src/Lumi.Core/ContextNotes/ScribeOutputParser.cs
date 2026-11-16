namespace Lumi.Core.ContextNotes;

/// <param name="NoChange">書記回 <c>&lt;no_change/&gt;</c>。</param>
/// <param name="Note">書記回的 <c>&lt;note&gt;</c> 內容（去頭尾空白）。</param>
public sealed record ScribeOutput(bool NoChange, string? Note, string? GoalHint);

public static class ScribeOutputParser
{
    // TODO(35C)
    /// <returns>格式不對回 null。</returns>
    public static ScribeOutput? Parse(string answer) => throw new NotImplementedException();
}
