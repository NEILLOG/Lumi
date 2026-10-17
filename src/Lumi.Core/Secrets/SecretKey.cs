namespace Lumi.Core.Secrets;

/// <summary>
/// 金鑰的名稱（這是哪個連線的金鑰，不是金鑰內容）。
/// 格式：以 / 分隔的區段，每段為小寫英數與單一的 -，例如 <c>connection/dev-anthropic/api-key</c>。
/// </summary>
public readonly record struct SecretKey
{
    public const int MaxLength = 128;

    public string Name { get; }

    public SecretKey(string name)
    {
        if (!IsValid(name))
            throw new ArgumentException(
                $"金鑰名稱格式不正確：只能使用小寫英數、- 與 /，每段不可為空，長度不超過 {MaxLength}。", nameof(name));
        Name = name;
    }

    public static bool IsValid(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxLength)
            return false;

        var previousWasSeparator = true;   // 開頭不能是 - 或 /
        foreach (var c in name)
        {
            var isAlnum = c is (>= 'a' and <= 'z') or (>= '0' and <= '9');
            if (isAlnum)
            {
                previousWasSeparator = false;
            }
            else if (c is '-' or '/')
            {
                if (previousWasSeparator) return false;   // 連續分隔符號
                previousWasSeparator = true;
            }
            else
            {
                return false;
            }
        }
        return !previousWasSeparator;   // 結尾不能是 - 或 /
    }

    /// <summary>實作端用來防止 <c>default(SecretKey)</c>（Name 為 null）被當成有效的金鑰。</summary>
    public string RequireName() =>
        Name ?? throw new ArgumentException("SecretKey 尚未初始化（default）。");

    public override string ToString() => Name ?? "";
}
