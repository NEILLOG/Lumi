using System.Text;
using System.Text.RegularExpressions;
using Lumi.Core.Context;
using Lumi.Core.Tools;

namespace Lumi.Tools;

/// <summary>路徑比對的平台規則。預設依目前的作業系統；測試可以指定，驗證另一個平台的規則。</summary>
public sealed record FileScopeRules(bool CaseInsensitive, bool WindowsPathRules, bool NormalizeUnicode)
{
    public static FileScopeRules ForCurrentOs { get; } = new(
        CaseInsensitive: OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(),
        WindowsPathRules: OperatingSystem.IsWindows(),
        NormalizeUnicode: OperatingSystem.IsMacOS());
}

/// <summary>
/// <see cref="IFileScope"/> 的實作。路徑安全是這個專案最重要的防線之一（agent.md 第 8 節），所以做法保守：
/// 先正規化，再把符號連結逐段展開成真實路徑，最後才確認「真實路徑」在某個根之下。
/// </summary>
public sealed class FileScope : IFileScope
{
    private const int MaxLinkHops = 40;

    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>網路路徑在範圍外時的說明（給模型看，不出現「UNC」等術語）。</summary>
    public const string NetworkOutsideScopeMessage = "只能讀你自己加入的網路資料夾或檔案，這個網路路徑不在範圍內。";

    /// <summary>網路路徑讀不到（網路慢、斷線、VPN 沒連）時的說明。</summary>
    public const string NetworkUnreachableMessage = "連不到這個網路資料夾，請確認網路或 VPN。";

    private readonly IReadOnlyList<ScopeRoot> _roots;
    private readonly Lazy<string>[] _realRoots;   // 延後解析：建立範圍時不碰檔案系統（網路路徑可能很慢）
    private readonly FileScopeRules _rules;
    private readonly StringComparison _comparison;

    public FileScope(IReadOnlyList<ScopeRoot> roots, FileScopeRules? rules = null)
    {
        _rules = rules ?? FileScopeRules.ForCurrentOs;
        _comparison = _rules.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        _roots = roots;
        _realRoots = roots.Select(r => new Lazy<string>(() => SafeReal(r.Path))).ToArray();
    }

    public IReadOnlyList<ScopeRoot> Roots => _roots;

    public ScopeResolution Resolve(string path)
    {
        if (_roots.Count == 0)
            return ScopeResolution.Denied("目前沒有加入任何檔案或資料夾。");

        path ??= "";
        if (path.Contains('\0'))
            return ScopeResolution.Denied("路徑含有不合法的字元。");

        if (_rules.WindowsPathRules && CheckWindowsRules(path) is { } windowsError)
            return ScopeResolution.Denied(windowsError);

        string full;
        try
        {
            var candidate = CandidateFullPath(path.Trim(), out var error);
            if (candidate is null)
                return ScopeResolution.Denied(error!);
            full = candidate;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return ScopeResolution.Denied($"路徑不合法：{path}");
        }

        // 網路路徑：在做任何檔案系統存取之前，先確認它在使用者加入的根之下（不連線、不解析符號連結）
        if (!NetworkPathAllowed(full))
            return ScopeResolution.Denied(NetworkOutsideScopeMessage);

        string real;
        try
        {
            real = ResolveReal(full, 0);
        }
        catch (OutsideNetworkScopeException)
        {
            return ScopeResolution.Denied(NetworkOutsideScopeMessage);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (IsNetworkPath(full))
                return ScopeResolution.Denied(NetworkUnreachableMessage);
            return ScopeResolution.Denied($"無法解析路徑（符號連結層數過多或無法存取）：{path}");
        }

        for (var i = 0; i < _roots.Count; i++)
        {
            var root = _roots[i];
            var rootReal = _realRoots[i].Value;
            if (root.IsDirectory ? !IsUnderDirectory(real, rootReal) : !Same(real, rootReal))
                continue;

            // 使用者自己加入的單一檔案不擋；範圍內其他檔案依機密名單
            if (root.IsDirectory && SecretReason(real, rootReal) is { } secret)
                return ScopeResolution.Denied(secret);

            return new ScopeResolution(real, root, null);
        }

        return ScopeResolution.Denied($"路徑不在可存取的範圍內：{path}");
    }

    public string ToDisplayPath(string fullPath)
    {
        string real;
        try
        {
            real = ResolveReal(fullPath, 0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return fullPath;
        }

        for (var i = 0; i < _roots.Count; i++)
        {
            var root = _roots[i];
            var rootReal = _realRoots[i].Value;
            if (root.IsDirectory ? IsUnderDirectory(real, rootReal) : Same(real, rootReal))
            {
                var relative = Same(real, rootReal) ? "" : Path.GetRelativePath(rootReal, real).Replace('\\', '/');
                if (!root.IsDirectory)
                    return root.Name;
                if (_roots.Count == 1)
                    return relative;
                return relative.Length == 0 ? root.Name : $"{root.Name}/{relative}";
            }
        }
        return fullPath;
    }

    // ---- 候選路徑 ----

    private string? CandidateFullPath(string path, out string? error)
    {
        error = null;

        if (Path.IsPathRooted(path) && !IsWindowsRootRelative(path))
            return Path.GetFullPath(path);

        if (_roots.Count == 1)
        {
            var only = _roots[0];
            if (!only.IsDirectory)
            {
                if (path.Length == 0 || path == "." || Same(Normalize(path), Normalize(only.Name)))
                    return only.Path;
                error = $"這個範圍只有單一檔案「{only.Name}」。";
                return null;
            }
            return Path.GetFullPath(Path.Combine(only.Path, path.Length == 0 ? "." : path));
        }

        // 多個根：相對路徑要以根的名稱開頭
        var segments = path.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments[0] == ".")
        {
            error = $"有多個範圍，路徑要以名稱開頭：{string.Join("、", _roots.Select(r => r.Name))}。";
            return null;
        }

        var match = _roots.FirstOrDefault(r => Same(Normalize(r.Name), Normalize(segments[0])));
        if (match is null)
        {
            error = $"找不到範圍「{segments[0]}」，可用的名稱：{string.Join("、", _roots.Select(r => r.Name))}。";
            return null;
        }

        if (!match.IsDirectory)
            return segments.Length == 1 ? match.Path : Deny("這個範圍是單一檔案，沒有子路徑。", out error);

        return Path.GetFullPath(Path.Combine(new[] { match.Path }.Concat(segments.Skip(1)).ToArray()));
    }

    private static string? Deny(string message, out string? error)
    {
        error = message;
        return null;
    }

    /// <summary>Windows 上 <c>\foo</c>（磁碟機根目錄相對）不當成絕對路徑：交給範圍的相對規則，最後一定會被範圍檢查擋下。</summary>
    private bool IsWindowsRootRelative(string path) =>
        _rules.WindowsPathRules && path.Length > 0 && (path[0] == '\\' || path[0] == '/') && !(path.Length > 1 && (path[1] == '\\' || path[1] == '/'));

    // ---- Windows 規則（字串層級，可在任何平台測試） ----

    private static readonly Regex DeviceName = new(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 裝置路徑（<c>\\?\</c>、<c>\\.\</c>）一律擋；保留的裝置名稱、替代資料流（<c>a.txt:stream</c>）、結尾是點或空白的名稱也擋。
    /// 網路路徑（<c>\\server\share</c>）不在這裡擋：要看是不是使用者加入的根之下（<see cref="IsNetworkPathInScope"/>）。
    /// </summary>
    public static string? CheckWindowsRules(string path)
    {
        if (IsDevicePath(path))
            return "不支援裝置路徑。";

        var rest = path;
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
            rest = path[2..];

        foreach (var segment in rest.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Contains(':'))
                return "路徑不能包含「:」（替代資料流）。";
            if (DeviceName.IsMatch(segment))
                return "路徑不能使用系統保留的裝置名稱。";
            if (segment != "." && segment != ".." && (segment.EndsWith('.') || segment.EndsWith(' ')))
                return "名稱的結尾不能是句點或空白。";
        }
        return null;
    }

    // ---- 網路路徑（只看字串，不碰檔案系統） ----

    /// <summary>裝置路徑：<c>\\?\</c>、<c>\\.\</c>（含 <c>//?/</c>、<c>//./</c>）。</summary>
    public static bool IsDevicePath(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)
        || path.StartsWith("//?/", StringComparison.Ordinal) || path.StartsWith("//./", StringComparison.Ordinal);

    /// <summary>網路路徑（<c>\\server\share\...</c>）。裝置路徑不算。</summary>
    public static bool IsNetworkPath(string path) =>
        (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) && !IsDevicePath(path);

    /// <summary>
    /// 網路路徑是否落在某個根之下。純字串比對：不連線、不解析符號連結，所以可以在任何檔案系統存取之前呼叫。
    /// 先收合 <c>.</c>、<c>..</c>，<c>..</c> 跳出共用資料夾的分享名稱就視為不在範圍內。非網路路徑一律回 true（由其他規則判斷）。
    /// </summary>
    public static bool IsNetworkPathInScope(string fullPath, IReadOnlyList<ScopeRoot> roots, bool ignoreCase)
    {
        if (!IsNetworkPath(fullPath))
            return true;

        var candidate = LexicalWindowsPath(fullPath);
        if (candidate is null)
            return false;

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var root in roots)
        {
            var rootPath = LexicalWindowsPath(root.Path);
            if (rootPath is null)
                continue;
            if (string.Equals(candidate, rootPath, comparison))
                return true;
            if (root.IsDirectory && candidate.StartsWith(rootPath + '\\', comparison))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 收合 Windows 路徑的 <c>.</c>、<c>..</c>，統一用 <c>\</c>。UNC 的前兩段（主機、分享名稱）保留；
    /// <c>..</c> 不能跳出分享名稱，這種路徑回 null。
    /// </summary>
    private static string? LexicalWindowsPath(string path)
    {
        var unc = path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);
        var parts = path.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        var fixedCount = unc ? 2 : 0;
        if (unc && parts.Length < 2)
            return null;

        var stack = new List<string>();
        foreach (var part in parts)
        {
            if (part == ".")
                continue;
            if (part == "..")
            {
                if (stack.Count <= fixedCount)
                    return null;
                stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(part);
        }
        return (unc ? @"\\" : "") + string.Join('\\', stack);
    }

    // ---- 符號連結：逐段展開成真實路徑 ----

    /// <summary>網路路徑在解析途中走到使用者加入的根之外（例如符號連結指向別的主機）。不做任何存取，直接停止。</summary>
    private sealed class OutsideNetworkScopeException() : IOException("網路路徑不在範圍內。");

    /// <summary>網路路徑是否在範圍內（只有 Windows 規則才有網路路徑）。</summary>
    private bool NetworkPathAllowed(string fullPath) =>
        !_rules.WindowsPathRules || IsNetworkPathInScope(fullPath, _roots, _rules.CaseInsensitive);

    private string SafeReal(string path)
    {
        try
        {
            return ResolveReal(Path.GetFullPath(path), 0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return Path.GetFullPath(path);
        }
    }

    private string ResolveReal(string fullPath, int depth)
    {
        if (depth > MaxLinkHops)
            throw new InvalidOperationException("符號連結層數過多。");
        if (!NetworkPathAllowed(fullPath))
            throw new OutsideNetworkScopeException();

        var root = Path.GetPathRoot(fullPath) ?? "";
        var segments = fullPath[root.Length..].Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            var next = Path.Combine(current, segments[i]);

            // 先用「不跟隨連結」的方式讀連結目標：Directory.Exists 會跟隨連結，連結指向範圍外的網路主機時就會去連那台主機
            var linkText = new FileInfo(next).LinkTarget ?? new DirectoryInfo(next).LinkTarget;
            if (linkText is not null)
            {
                // 連結（含懸空的連結）：照目標展開。懸空時也要展開，否則之後寫入新檔會跟著連結跑到範圍外（寫入是新檔的情境）
                var target = Path.IsPathRooted(linkText) ? linkText : Path.Combine(Path.GetDirectoryName(next) ?? current, linkText);
                var rebuilt = Path.Combine(new[] { Path.GetFullPath(target) }.Concat(segments.Skip(i + 1)).ToArray());
                return ResolveReal(Path.GetFullPath(rebuilt), depth + 1);   // 目標路徑本身也可能經過符號連結（例如 macOS 的 /var）
            }

            if (!Directory.Exists(next) && !File.Exists(next))
            {
                // 路徑不存在：後面的部分原樣接上（之後讀取自然會失敗；範圍檢查用的還是已展開的前綴）
                return Path.Combine(new[] { next }.Concat(segments.Skip(i + 1)).ToArray());
            }
            current = next;
        }
        return current;
    }

    // ---- 比對 ----

    private string Normalize(string path) => _rules.NormalizeUnicode ? path.Normalize(NormalizationForm.FormC) : path;

    private bool Same(string a, string b) => string.Equals(Normalize(TrimEnd(a)), Normalize(TrimEnd(b)), _comparison);

    private bool IsUnderDirectory(string candidate, string directory)
    {
        var dir = Normalize(TrimEnd(directory));
        var cand = Normalize(candidate);
        if (string.Equals(TrimEnd(cand), dir, _comparison))
            return true;
        // 以目錄分隔字元結尾再比前綴：/a/b 不能把 /a/bc 當成在裡面
        return cand.StartsWith(dir + Path.DirectorySeparatorChar, _comparison)
               || (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar && cand.StartsWith(dir + Path.AltDirectorySeparatorChar, _comparison));
    }

    private static string TrimEnd(string path)
    {
        var root = Path.GetPathRoot(path) ?? "";
        return path.Length > root.Length ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;
    }

    /// <summary>根底下的路徑如果落在機密位置，回傳說明。</summary>
    private string? SecretReason(string real, string rootReal)
    {
        var relative = Path.GetRelativePath(rootReal, real);
        if (relative == ".")
            return null;
        var segments = relative.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            if (ScopeRules.IsSecretDirectory(segments[i]))
                return $"「{segments[i]}」內的檔案被視為機密，不會讀取。";
        }
        if (segments.Length > 0 && ScopeRules.IsSecretFileName(segments[^1]))
            return $"檔案「{segments[^1]}」被視為機密（金鑰、憑證或環境變數），不會讀取。";
        return null;
    }
}

/// <summary>由對話的上下文項目建立範圍：檔案與資料夾各一個根；圖片是附件，不在範圍內。</summary>
public sealed class FileScopeFactory : IFileScopeFactory
{
    public IFileScope Create(IReadOnlyList<ContextItem> items)
    {
        var roots = new List<ScopeRoot>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Where(i => i.Kind is ContextKind.File or ContextKind.Folder))
        {
            var baseName = string.IsNullOrWhiteSpace(item.DisplayName) ? Path.GetFileName(item.Path.TrimEnd('/', '\\')) : item.DisplayName;
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = item.Path;
            var name = baseName;
            for (var n = 2; !used.Add(name); n++)
                name = $"{baseName} ({n})";
            roots.Add(new ScopeRoot(name, item.Path, item.Kind == ContextKind.Folder));
        }
        return roots.Count == 0 ? EmptyFileScope.Instance : new FileScope(roots);
    }
}
