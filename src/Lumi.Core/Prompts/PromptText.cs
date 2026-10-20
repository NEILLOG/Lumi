using System.Collections.Concurrent;

namespace Lumi.Core.Prompts;

/// <summary>讀取內嵌的 prompt 文字（<c>Prompts/Text/*.md</c>）。讀過就留在記憶體，同一段文字每次都逐字相同。</summary>
public static class PromptText
{
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);

    /// <param name="name">不含副檔名，例如 <c>base</c>。找不到時丟例外（是程式錯誤，不是使用者的問題）。</param>
    public static string Get(string name) => Cache.GetOrAdd(name, static n =>
    {
        using var stream = typeof(PromptText).Assembly.GetManifestResourceStream($"Lumi.Prompts.{n}.md")
            ?? throw new InvalidOperationException($"找不到 prompt 文字 {n}.md。");
        using var reader = new StreamReader(stream);
        // 統一換行：Windows 上簽出的檔案可能是 CRLF，不同平台送出的 system 應該一樣
        return reader.ReadToEnd().Replace("\r\n", "\n").Trim();
    });
}
