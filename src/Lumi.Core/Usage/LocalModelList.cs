namespace Lumi.Core.Usage;

/// <summary>
/// 地端（開源權重）模型的名單。公司的地端與雲端模型常整合在同一個 AI Gateway 後面，從網址分不出來，
/// 所以改用模型 ID 的關鍵字判斷：符合就視為地端、自動勾選「不計費」（使用者可以取消勾選）。
/// 優先於 LiteLLM 價目表——價目表裡的 Llama、Qwen 是第三方託管服務的價格，不是公司地端的成本。
/// <para>
/// 要新增地端模型家族，加在 <see cref="Keywords"/> 就好。關鍵字不分大小寫；模型 ID 會先依非英數字元拆成詞
/// （<c>Qwen/Qwen2.5-72B-Instruct</c> → <c>qwen</c>、<c>qwen2</c>、<c>5</c>、<c>72b</c>、<c>instruct</c>），
/// 關鍵字的每一個詞要連續對上模型 ID 的詞：詞完全相同，或詞是關鍵字加上數字（<c>llama3</c> 對 <c>llama</c>、<c>qwen2</c> 對 <c>qwen</c>）。
/// 所以 <c>gpt-oss-20b</c> 符合 <c>gpt-oss</c>，但 <c>gpt-4o</c> 不符合。
/// </para>
/// </summary>
public static class LocalModelList
{
    /// <summary>地端模型家族的關鍵字（初始名單，之後依公司實際使用的模型增減）。</summary>
    public static readonly IReadOnlyList<string> Keywords =
    [
        "qwen", "qwq",
        "llama", "codellama", "tinyllama", "vicuna",
        "gemma", "codegemma",
        "phi",
        "mistral", "mixtral",
        "deepseek",
        "glm", "chatglm",
        "yi", "internlm", "baichuan", "minicpm",
        "granite", "nemotron", "olmo", "smollm", "solar", "falcon", "starcoder", "openchat", "wizardlm",
        "gpt-oss",
    ];

    private static readonly string[][] KeywordTokens = Keywords.Select(Split).ToArray();

    /// <summary>模型 ID 是否屬於地端名單。</summary>
    public static bool IsLocalModel(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return false;

        var tokens = Split(modelId);
        foreach (var keyword in KeywordTokens)
        {
            for (var start = 0; start + keyword.Length <= tokens.Length; start++)
            {
                var all = true;
                for (var i = 0; i < keyword.Length && all; i++)
                    all = TokenMatches(tokens[start + i], keyword[i]);
                if (all)
                    return true;
            }
        }
        return false;
    }

    /// <summary>詞完全相同，或詞是關鍵字後面接數字（llama3、qwen2、phi4）。</summary>
    private static bool TokenMatches(string token, string keyword) =>
        token == keyword
        || (token.Length > keyword.Length && token.StartsWith(keyword, StringComparison.Ordinal) && char.IsDigit(token[keyword.Length]));

    private static string[] Split(string text)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0)
            tokens.Add(current.ToString());
        return tokens.ToArray();
    }
}
