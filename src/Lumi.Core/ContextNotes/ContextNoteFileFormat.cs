namespace Lumi.Core.ContextNotes;

/// <summary>note.md 的格式（計畫 4.2）：開頭三行（標題、空行、目標）由程式產生，後面是版本的 content。</summary>
public static class ContextNoteFileFormat
{
    public const string GoalPrefix = "**目標**：";

    // TODO(35B)
    public static string Compose(string title, string goal, string content) => throw new NotImplementedException();

    // TODO(35B)
    public static (string Goal, string Content) Parse(string fileText, string fallbackGoal) => throw new NotImplementedException();
}
