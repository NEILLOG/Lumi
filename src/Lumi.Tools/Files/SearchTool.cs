using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Lumi.Core.Tools;

namespace Lumi.Tools;

public sealed record SearchArgs(
    [property: Description("要在檔案內容裡找的正規表示式（例如 TODO 或 class\\s+\\w+Service）；只想依檔名找檔案時可以不給")] string? Pattern = null,
    [property: Description("只在這個資料夾或檔案裡找；不給就找整個範圍")] string? Path = null,
    [property: Description("限制檔名的 glob，例如 *.cs 或 src/**/*.ts；沒有斜線時只比對檔名")] string? Glob = null,
    [property: Description("比對內容時是否忽略大小寫，預設否")] bool? IgnoreCase = null);

public sealed class SearchTool(FileToolOptions options) : ToolBase<SearchArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "search", "搜尋",
        "在使用者加入的資料夾範圍內搜尋：用正規表示式找檔案內容（回傳「檔案:行號: 內容」），或用 glob 找檔名。想知道某個東西在哪裡被用到、哪個檔案有某段文字時用它。",
        ToolPermission.ReadOnly, ToolModes.Both, TimeSpan.FromSeconds(30), RequiresScope: true);

    protected override ToolPreview Describe(SearchArgs args)
    {
        var target = !string.IsNullOrWhiteSpace(args.Pattern) ? args.Pattern.Trim() : args.Glob?.Trim();
        return new(string.IsNullOrEmpty(target) ? null : target, string.IsNullOrWhiteSpace(args.Path) ? null : $"在 {args.Path.Trim()} 裡");
    }

    protected override Task<ToolResult> InvokeAsync(SearchArgs args, ToolContext context, CancellationToken ct) =>
        Task.Run(() => Search(args, context, ct), ct);

    private sealed class Counters
    {
        public int FilesScanned;
        public int Matches;
        public int SkippedLarge;
        public int SkippedBinary;
        public int SkippedSecret;
        public bool FileLimitReached;
        public bool ResultLimitReached;
    }

    private ToolResult Search(SearchArgs args, ToolContext context, CancellationToken ct)
    {
        var hasPattern = !string.IsNullOrWhiteSpace(args.Pattern);
        var hasGlob = !string.IsNullOrWhiteSpace(args.Glob);
        if (!hasPattern && !hasGlob)
            return ToolResult.Error("請提供 pattern（找內容）或 glob（找檔名）。", "參數不正確");

        Regex? content = null;
        if (hasPattern)
        {
            if (args.Pattern!.Length > options.SearchMaxPatternLength)
                return ToolResult.Error($"pattern 太長（上限 {options.SearchMaxPatternLength} 個字元）。", "參數不正確");
            try
            {
                // NonBacktracking：比對時間與輸入長度成正比，惡意的 pattern 不會造成災難性回溯
                var flags = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | (args.IgnoreCase == true ? RegexOptions.IgnoreCase : RegexOptions.None);
                content = new Regex(args.Pattern, flags);
            }
            catch (ArgumentException e)
            {
                return ToolResult.Error($"正規表示式有誤：{e.Message}", "pattern 有誤");
            }
            catch (NotSupportedException)
            {
                return ToolResult.Error("不支援這種正規表示式（不能使用反向參照、lookahead／lookbehind 等）。", "pattern 不支援");
            }
        }

        Regex? glob = hasGlob ? GlobToRegex(args.Glob!.Trim()) : null;
        var globMatchesName = hasGlob && !args.Glob!.Contains('/');

        var scope = context.Scope;
        var starts = new List<(string Full, string Display)>();
        if (string.IsNullOrWhiteSpace(args.Path))
        {
            foreach (var root in scope.Roots)
            {
                var resolved = scope.Resolve(scope.Roots.Count == 1 ? "" : root.Name);
                if (resolved.IsAllowed)
                    starts.Add((resolved.FullPath!, scope.ToDisplayPath(resolved.FullPath!)));
            }
        }
        else
        {
            var resolved = scope.Resolve(args.Path.Trim());
            if (!resolved.IsAllowed)
                return ToolResult.Error(resolved.Error!, "無法搜尋");
            starts.Add((resolved.FullPath!, scope.ToDisplayPath(resolved.FullPath!)));
        }

        var counters = new Counters();
        var output = new StringBuilder();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (full, display) in starts)
        {
            if (File.Exists(full))
            {
                VisitFile(new FileInfo(full), display, content, glob, globMatchesName, counters, output, ct, scope);
            }
            else if (Directory.Exists(full) && visited.Add(full))
            {
                WalkDirectory(new DirectoryInfo(full), display, content, glob, globMatchesName, counters, output, visited, ct, scope);
            }
            if (counters.ResultLimitReached || counters.FileLimitReached)
                break;
        }

        var notes = new List<string>();
        if (counters.ResultLimitReached)
            notes.Add($"只顯示前 {options.SearchMaxResults} 筆");
        if (counters.FileLimitReached)
            notes.Add($"掃描到 {options.SearchMaxFiles} 個檔案就停了，請縮小 path 或加上 glob");
        var skipped = new List<string>();
        if (counters.SkippedLarge > 0) skipped.Add($"{counters.SkippedLarge} 個過大（> {TextFiles.FormatSize(options.SearchMaxFileBytes)}）");
        if (counters.SkippedBinary > 0) skipped.Add($"{counters.SkippedBinary} 個非文字檔");
        if (counters.SkippedSecret > 0) skipped.Add($"{counters.SkippedSecret} 個機密檔案");
        if (skipped.Count > 0)
            notes.Add("略過 " + string.Join("、", skipped));

        if (counters.Matches == 0)
        {
            var none = $"沒有找到符合的{(hasPattern ? "內容" : "檔案")}（掃描了 {counters.FilesScanned} 個檔案）。";
            return ToolResult.Ok(notes.Count > 0 ? none + "（" + string.Join("；", notes) + "）" : none, "沒有符合");
        }

        if (notes.Count > 0)
            output.Append('（').Append(string.Join("；", notes)).Append('）');
        return ToolResult.Ok(output.ToString().TrimEnd('\n'), $"{counters.Matches} 筆符合 / 掃描 {counters.FilesScanned} 個檔案");
    }

    private void WalkDirectory(DirectoryInfo directory, string displayDir, Regex? content, Regex? glob, bool globMatchesName,
        Counters counters, StringBuilder output, HashSet<string> visited, CancellationToken ct, IFileScope scope)
    {
        List<FileSystemInfo> children;
        try
        {
            children = directory.EnumerateFileSystemInfos().OrderBy(c => c is DirectoryInfo ? 1 : 0).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var child in children)
        {
            ct.ThrowIfCancellationRequested();
            if (counters.ResultLimitReached || counters.FileLimitReached)
                return;
            if (ScopeRules.IsIgnored(child.Name))
                continue;

            var display = displayDir.Length == 0 ? child.Name : $"{displayDir}/{child.Name}";
            var target = child;
            if (child.LinkTarget is not null)
            {
                var resolved = scope.Resolve(child.FullName);
                if (!resolved.IsAllowed)
                    continue;
                target = Directory.Exists(resolved.FullPath!) ? new DirectoryInfo(resolved.FullPath!) : new FileInfo(resolved.FullPath!);
            }

            if (target is DirectoryInfo dir)
            {
                if (ScopeRules.IsSecretDirectory(child.Name))
                    continue;
                if (visited.Add(dir.FullName))
                    WalkDirectory(dir, display, content, glob, globMatchesName, counters, output, visited, ct, scope);
            }
            else if (target is FileInfo file)
            {
                VisitFile(file, display, content, glob, globMatchesName, counters, output, ct, scope, secretCheckName: child.Name);
            }
        }
    }

    private void VisitFile(FileInfo file, string display, Regex? content, Regex? glob, bool globMatchesName,
        Counters counters, StringBuilder output, CancellationToken ct, IFileScope scope, string? secretCheckName = null)
    {
        if (glob is not null && !glob.IsMatch(globMatchesName ? Path.GetFileName(display) : display))
            return;

        if (counters.FilesScanned >= options.SearchMaxFiles)
        {
            counters.FileLimitReached = true;
            return;
        }
        counters.FilesScanned++;

        if (secretCheckName is not null && ScopeRules.IsSecretFileName(secretCheckName))
        {
            counters.SkippedSecret++;
            return;
        }

        if (content is null)
        {
            // 只依檔名找
            Add(output, counters, display);
            return;
        }

        try
        {
            if (file.Length > options.SearchMaxFileBytes)
            {
                counters.SkippedLarge++;
                return;
            }

            using var reader = TextFiles.Open(file.FullName, out var error);
            if (reader is null)
            {
                counters.SkippedBinary++;
                return;
            }

            var number = 0;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                number++;
                if ((number & 0x3FF) == 0)
                    ct.ThrowIfCancellationRequested();
                if (!content.IsMatch(line))
                    continue;

                Add(output, counters, $"{display}:{number}: {TextFiles.ClipLine(line.Trim(), options.SearchMaxLineChars)}");
                if (counters.ResultLimitReached)
                    return;
            }
        }
        catch (DecoderFallbackException)
        {
            counters.SkippedBinary++;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 讀不到的檔案略過
        }
    }

    private void Add(StringBuilder output, Counters counters, string line)
    {
        if (counters.Matches >= options.SearchMaxResults)
        {
            counters.ResultLimitReached = true;
            return;
        }
        output.Append(line).Append('\n');
        counters.Matches++;
        if (counters.Matches >= options.SearchMaxResults)
            counters.ResultLimitReached = true;
    }

    /// <summary><c>**</c> 跨資料夾、<c>*</c> 不跨、<c>?</c> 一個字元；其餘字元照字面比對。</summary>
    internal static Regex GlobToRegex(string glob)
    {
        var pattern = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                pattern.Append(".*");
                i++;
                if (i + 1 < glob.Length && glob[i + 1] == '/')
                    i++;
            }
            else if (c == '*')
            {
                pattern.Append("[^/]*");
            }
            else if (c == '?')
            {
                pattern.Append("[^/]");
            }
            else
            {
                pattern.Append(Regex.Escape(c.ToString()));
            }
        }
        pattern.Append('$');
        return new Regex(pattern.ToString(), RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }
}
