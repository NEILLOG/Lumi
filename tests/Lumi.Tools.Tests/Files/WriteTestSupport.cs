using System.Text.Json;
using Lumi.Core.Chat;
using Lumi.Core.Tools;

namespace Lumi.Tools.Tests.Files;

/// <summary>寫入類工具測試共用：引數 JSON、範圍與上下文、目錄快照（確認沒有寫到任何地方）。</summary>
internal static class WriteTestSupport
{
    public static string Json(object o) => JsonSerializer.Serialize(o);

    public static ToolContext Context(IFileScope scope, ChatMode mode = ChatMode.Workbench) =>
        new("c1", mode, TimeProvider.System, scope);

    public static IFileScope ScopeOf(TempTree tree) => new FileScope([new ScopeRoot("專案", tree.Root, true)]);

    /// <summary>資料夾裡所有項目的相對路徑（含子資料夾），排序後回傳。</summary>
    public static string[] Snapshot(string root) =>
        Directory.Exists(root)
            ? Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(root, p)).Order().ToArray()
            : [];
}
