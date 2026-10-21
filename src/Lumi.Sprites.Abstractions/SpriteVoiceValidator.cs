namespace Lumi.Sprites.Abstractions;

/// <summary>檢查的結果：通過時 <see cref="Voice"/> 是整理好的語氣（可能是 null＝沒有語氣）；沒通過時 <see cref="Rejection"/> 是給作者看的一句說明。</summary>
public sealed record SpriteVoiceCheck(string? Voice, string? Rejection)
{
    public bool IsRejected => Rejection is not null;
}

/// <summary>
/// 角色語氣的載入檢查（<c>docs/sprite-dev-guide.md</c> 第 8 節）。語氣會進 prompt 的最後一層，
/// 而素材型角色的 manifest 來自外部，所以要當作不可信的輸入：
/// 只能是單行純文字、不超過 <see cref="SpriteLimits.VoiceMaxLength"/> 字、不能含 prompt 標籤字元與指令式的字眼。
/// 這是安全網，不是保證——真正的防線是語氣層本來就在最低優先，而且使用者可以在設定關閉。
/// </summary>
public static class SpriteVoiceValidator
{
    /// <summary>指令式的字眼（大小寫不分）。之後可以增補，每一個都有測試。</summary>
    public static IReadOnlyList<string> Forbidden { get; } =
    [
        "忽略", "無視", "不要遵守", "不必遵守", "取代", "覆蓋", "以上的", "之前的", "以前的", "忘記",
        "系統提示", "system prompt", "ignore", "disregard", "override", "jailbreak", "你現在是", "從現在起你是",
    ];

    private static readonly char[] TagCharacters = ['<', '>', '＜', '＞', '`'];

    public static SpriteVoiceCheck Check(string? voice)
    {
        var text = voice?.Trim();
        if (string.IsNullOrEmpty(text))
            return new SpriteVoiceCheck(null, null);

        if (text.Length > SpriteLimits.VoiceMaxLength)
            return Reject($"語氣超過 {SpriteLimits.VoiceMaxLength} 字（目前 {text.Length} 字）");
        if (text.Any(char.IsControl))
            return Reject("語氣只能是一行純文字，不能有換行或控制字元");
        if (text.IndexOfAny(TagCharacters) >= 0)
            return Reject("語氣不能含有 < > 或反引號");

        foreach (var word in Forbidden)
        {
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase))
                return Reject($"語氣含有指令式的字眼「{word}」（語氣只描述說話風格，不能改變規則）");
        }
        return new SpriteVoiceCheck(text, null);
    }

    private static SpriteVoiceCheck Reject(string reason) => new(null, $"語氣被忽略：{reason}");
}
