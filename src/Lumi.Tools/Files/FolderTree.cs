using System.Text;

namespace Lumi.Tools;

/// <summary>資料夾的檔案樹（送出時嵌進訊息）：只有名稱，資料夾在前，略過 <see cref="ScopeRules.IgnoredNames"/>，有項目數與深度上限。</summary>
internal static class FolderTree
{
    /// <summary>超過這個數量就不再往下數「還有幾個項目」（上萬個和上百萬個對模型沒有差別）。</summary>
    private const int CountCap = 50_000;

    public sealed record Result(string Text, int Entries, int Remaining, bool RemainingCapped);

    public static Result Build(string root, int maxEntries, int maxDepth, CancellationToken ct = default)
    {
        var text = new StringBuilder();
        text.Append(Path.GetFileName(root.TrimEnd('/', '\\')) is { Length: > 0 } name ? name : root).Append("/\n");
        var state = new State(maxEntries);
        Walk(new DirectoryInfo(root), 1, maxDepth, text, state, ct);
        if (state.Remaining > 0)
        {
            text.Append(state.RemainingCapped ? $"… 還有超過 {CountCap:N0} 個項目沒有列出\n" : $"… 還有 {state.Remaining:N0} 個項目沒有列出\n");
        }
        return new Result(text.ToString(), state.Emitted, state.Remaining, state.RemainingCapped);
    }

    private sealed class State(int maxEntries)
    {
        public int MaxEntries { get; } = maxEntries;
        public int Emitted { get; set; }
        public int Remaining { get; set; }
        public bool RemainingCapped { get; set; }
    }

    private static void Walk(DirectoryInfo directory, int level, int maxDepth, StringBuilder text, State state, CancellationToken ct)
    {
        List<FileSystemInfo> children;
        try
        {
            children = directory.EnumerateFileSystemInfos().ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var child in children
                     .OrderBy(c => c is DirectoryInfo ? 0 : 1)
                     .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (ScopeRules.IsIgnored(child.Name))
                continue;

            var isLink = child.LinkTarget is not null;
            var isDirectory = child is DirectoryInfo;
            if (state.Emitted >= state.MaxEntries)
            {
                // 已經放滿：只數剩下幾個（不再往下展開，避免走遍整個大型資料夾）
                if (state.Remaining >= CountCap)
                {
                    state.RemainingCapped = true;
                    return;
                }
                state.Remaining++;
                continue;
            }

            var indent = new string(' ', level * 2);
            state.Emitted++;
            if (isLink)
            {
                // 符號連結不跟隨（目標可能在範圍外）：只標示
                text.Append(indent).Append(child.Name).Append(isDirectory ? "/" : "").Append("（符號連結）\n");
            }
            else if (isDirectory)
            {
                if (ScopeRules.IsSecretDirectory(child.Name))
                {
                    text.Append(indent).Append(child.Name).Append("/（機密，不會讀取）\n");
                    continue;
                }
                text.Append(indent).Append(child.Name).Append("/\n");
                if (level < maxDepth)
                    Walk((DirectoryInfo)child, level + 1, maxDepth, text, state, ct);
            }
            else
            {
                text.Append(indent).Append(child.Name);
                if (ScopeRules.IsSecretFileName(child.Name))
                    text.Append("（機密，不會讀取）");
                text.Append('\n');
            }
        }
    }
}
