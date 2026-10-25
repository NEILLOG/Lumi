using System.ComponentModel;
using System.Text;
using Lumi.Core.Tools;

namespace Lumi.Tools;

public sealed record ListDirArgs(
    [property: Description("要列出的資料夾：相對於使用者加入的資料夾；不給或空字串就是範圍的根（有多個範圍時列出各範圍的名稱）")] string? Path = null,
    [property: Description("往下列幾層，預設 1，最多 3")] int? Depth = null);

public sealed class ListDirTool(FileToolOptions options) : ToolBase<ListDirArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "list_dir", "列出資料夾",
        "列出使用者加入的資料夾範圍內的檔案與子資料夾（名稱與大小）。想知道有哪些檔案、專案結構是什麼，先用它；再用 read_file 讀內容。",
        ToolPermission.ReadOnly, ToolModes.Both, TimeSpan.FromSeconds(15), RequiresScope: true, Group: ToolGroups.FilesRead, Summary: "列出你加入的資料夾裡有哪些檔案。");

    protected override ToolPreview Describe(ListDirArgs args, ToolContext context) =>
        new(string.IsNullOrWhiteSpace(args.Path) ? null : args.Path.Trim(), args.Depth is > 1 ? $"往下 {args.Depth} 層" : null);

    protected override Task<ToolResult> InvokeAsync(ListDirArgs args, ToolContext context, CancellationToken ct) =>
        Task.Run(() => List(args, context, ct), ct);

    private sealed class Listing
    {
        public StringBuilder Text { get; } = new();
        public int Entries { get; set; }
        public bool Truncated { get; set; }
        public int Skipped { get; set; }
        public SortedSet<string> SkippedNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private ToolResult List(ListDirArgs args, ToolContext context, CancellationToken ct)
    {
        var scope = context.Scope;
        var path = args.Path?.Trim() ?? "";
        var depth = Math.Clamp(args.Depth ?? 1, 1, options.ListMaxDepth);

        // 有多個範圍又沒指定路徑：列出各範圍
        if (path.Length == 0 && scope.Roots.Count > 1)
        {
            var roots = new StringBuilder("可用的範圍：\n");
            foreach (var root in scope.Roots)
                roots.Append("  ").Append(root.Name).Append(root.IsDirectory ? "/" : "").Append('\n');
            return ToolResult.Ok(roots.ToString().TrimEnd('\n'), $"{scope.Roots.Count} 個範圍");
        }

        var resolved = scope.Resolve(path);
        if (!resolved.IsAllowed)
            return ToolResult.Error(resolved.Error!, "無法列出");

        var full = resolved.FullPath!;
        var display = scope.ToDisplayPath(full);
        if (File.Exists(full))
            return ToolResult.Ok($"{display} 是檔案（{TextFiles.FormatSize(new FileInfo(full).Length)}）。", "是檔案");
        if (!Directory.Exists(full))
            return ToolResult.Error($"找不到資料夾：{display}", "找不到資料夾");

        var listing = new Listing();
        var visited = new HashSet<string>(StringComparer.Ordinal) { full };
        Walk(scope, new DirectoryInfo(full), 1, depth, listing, visited, ct);

        var builder = new StringBuilder();
        builder.Append(display.Length == 0 ? "（範圍的根）" : display + "/").Append('\n');
        if (listing.Entries == 0 && listing.Skipped == 0)
            builder.Append("  （空的）\n");
        builder.Append(listing.Text);
        if (listing.Truncated)
            builder.Append($"…已達 {options.ListMaxEntries} 個項目的上限；請指定更深的 path，或用 search。\n");
        if (listing.Skipped > 0)
            builder.Append($"（已略過 {listing.Skipped} 個項目：{string.Join("、", listing.SkippedNames)}）\n");

        return ToolResult.Ok(builder.ToString().TrimEnd('\n'), $"{listing.Entries} 個項目");
    }

    private void Walk(IFileScope scope, DirectoryInfo directory, int level, int maxDepth, Listing listing, HashSet<string> visited, CancellationToken ct)
    {
        List<FileSystemInfo> children;
        try
        {
            children = directory.EnumerateFileSystemInfos().ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            listing.Text.Append(' ', level * 2).Append("（無法讀取）\n");
            return;
        }

        var ordered = children
            .OrderBy(c => c is DirectoryInfo ? 0 : 1)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var child in ordered)
        {
            ct.ThrowIfCancellationRequested();
            if (ScopeRules.IsIgnored(child.Name))
            {
                listing.Skipped++;
                listing.SkippedNames.Add(child.Name);
                continue;
            }
            if (listing.Entries >= options.ListMaxEntries)
            {
                listing.Truncated = true;
                return;
            }

            var indent = new string(' ', level * 2);
            var target = child;
            if (child.LinkTarget is not null)
            {
                // 符號連結：目標在範圍內才跟隨，否則只標示
                var resolved = scope.Resolve(child.FullName);
                if (!resolved.IsAllowed)
                {
                    listing.Text.Append(indent).Append(child.Name).Append(" → （指向範圍外，不會跟隨）\n");
                    listing.Entries++;
                    continue;
                }
                target = Directory.Exists(resolved.FullPath!) ? new DirectoryInfo(resolved.FullPath!) : new FileInfo(resolved.FullPath!);
            }

            if (target is DirectoryInfo dir)
            {
                listing.Entries++;
                if (ScopeRules.IsSecretDirectory(child.Name))
                {
                    listing.Text.Append(indent).Append(child.Name).Append("/（機密，不會讀取）\n");
                    continue;
                }
                listing.Text.Append(indent).Append(child.Name).Append("/\n");
                if (level < maxDepth && visited.Add(dir.FullName))
                    Walk(scope, dir, level + 1, maxDepth, listing, visited, ct);
            }
            else if (target is FileInfo file)
            {
                listing.Entries++;
                listing.Text.Append(indent).Append(child.Name).Append("  (").Append(TextFiles.FormatSize(SafeLength(file))).Append(')');
                if (ScopeRules.IsSecretFileName(child.Name))
                    listing.Text.Append("（機密，不會讀取）");
                else if (DocumentExtensions.IsDocument(child.Name))
                    listing.Text.Append("（可用 read_document 讀取）");
                listing.Text.Append('\n');
            }
        }
    }

    private static long SafeLength(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
