namespace Lumi.Core.Tools;

/// <summary>工具清單頁的一個群組（M15）。</summary>
public sealed record ToolGroupInfo(string Id, string Name, int Order);

/// <summary>內建工具的群組。<see cref="ToolDescriptor.Group"/> 存這裡的 Id。</summary>
public static class ToolGroups
{
    public const string General = "general";
    public const string FilesRead = "files-read";
    public const string FilesWrite = "files-write";
    public const string Notes = "notes";
    public const string Reminders = "reminders";
    public const string Web = "web";
    public const string Browser = "browser";
    public const string Memory = "memory";
    public const string Sprite = "sprite";

    public static IReadOnlyList<ToolGroupInfo> BuiltIn { get; } =
    [
        new(General, "一般", 0),
        new(FilesRead, "讀取檔案", 1),
        new(FilesWrite, "寫入檔案", 2),
        new(Notes, "筆記與對話", 3),
        new(Reminders, "提醒與計時", 4),
        new(Web, "網路", 5),
        new(Browser, "瀏覽器", 6),
        new(Memory, "長期記憶", 7),
        new(Sprite, "小精靈", 8),
    ];

    /// <summary>找不到（例如 MCP 的群組）回 null。</summary>
    public static ToolGroupInfo? Find(string id) => BuiltIn.FirstOrDefault(g => g.Id == id);
}
