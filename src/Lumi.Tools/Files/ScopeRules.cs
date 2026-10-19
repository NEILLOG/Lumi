namespace Lumi.Tools;

/// <summary>範圍內「不讀」與「不列」的名單。集中在一處、有測試；之後可以改成設定（M4 的 Q3）。</summary>
public static class ScopeRules
{
    /// <summary>列出資料夾與搜尋時略過的名稱（明確指定這些路徑時仍可以列出，只是不會自己展開進去）。</summary>
    public static readonly IReadOnlySet<string> IgnoredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", "node_modules", "bin", "obj", "__pycache__", ".DS_Store", "Thumbs.db", ".vs", ".idea",
    };

    private static readonly string[] SecretExtensions = [".pem", ".key", ".pfx", ".p12"];

    private static readonly string[] SecretPrefixes = ["id_rsa", "id_ed25519", "id_ecdsa", "id_dsa"];

    private static readonly string[] EnvTemplateSuffixes = [".example", ".sample", ".template", ".dist"];

    public static bool IsIgnored(string name) => IgnoredNames.Contains(name);

    /// <summary>資料夾名稱本身就代表「裡面都是機密」（<c>.git</c> 內部、<c>.ssh</c>）。</summary>
    public static bool IsSecretDirectory(string name) =>
        name.Equals(".git", StringComparison.OrdinalIgnoreCase) || name.Equals(".ssh", StringComparison.OrdinalIgnoreCase);

    /// <summary>明顯是機密的檔名：<c>.env</c>（範本除外）、私鑰與憑證、<c>secrets.json</c>。</summary>
    public static bool IsSecretFileName(string name)
    {
        if (name.Equals(".env", StringComparison.OrdinalIgnoreCase) || name.Equals("secrets.json", StringComparison.OrdinalIgnoreCase))
            return true;

        if (name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
            return !EnvTemplateSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        if (SecretExtensions.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            return true;

        return SecretPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
