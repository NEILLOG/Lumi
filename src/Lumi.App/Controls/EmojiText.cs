using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Lumi.App.Controls;

/// <summary>
/// 把文字切成「字形叢集」（grapheme cluster），emoji 叢集整段指定 emoji 字型。
/// 原因：Avalonia 逐字挑字型，像 ❤ 這種一般字型也有的字元，會被畫成黑色文字，
/// 並把 ❤️‍🔥 這類 ZWJ 組合拆開。由我們決定字型，就不會被拆。
/// </summary>
public static class EmojiText
{
    public static FontFamily EmojiFont { get; set; } = new(
        OperatingSystem.IsMacOS() ? "Apple Color Emoji"
        : OperatingSystem.IsWindows() ? "Segoe UI Emoji"
        : "Noto Color Emoji");

    public static void Apply(TextBlock target, string? text)
    {
        var inlines = new InlineCollection();
        AddText(inlines, text);
        target.Inlines = inlines;
    }

    /// <summary>
    /// 把文字切成字形叢集加進 <paramref name="inlines"/>：一般文字一段、emoji 叢集一段（指定 emoji 字型），
    /// 換行變 <see cref="LineBreak"/>。<paramref name="style"/> 會套用到每一段 <see cref="Run"/>
    /// （例如粗體、等寬字型），emoji 段的字型仍由這裡指定，所以粗體裡、程式碼裡的 ❤️‍🔥 也不會被拆開。
    /// </summary>
    public static void AddText(InlineCollection inlines, string? text, Action<Run>? style = null)
    {
        var buffer = new StringBuilder();
        bool bufferIsEmoji = false;

        void Flush()
        {
            if (buffer.Length == 0) return;
            var run = new Run(buffer.ToString());
            style?.Invoke(run);
            if (bufferIsEmoji) run.FontFamily = EmojiFont;
            inlines.Add(run);
            buffer.Clear();
        }

        var e = StringInfo.GetTextElementEnumerator(text ?? "");
        while (e.MoveNext())
        {
            var g = e.GetTextElement();
            if (g == "\n" || g == "\r\n" || g == "\r")
            {
                Flush();
                inlines.Add(new LineBreak());
                continue;
            }
            bool isEmoji = IsEmoji(g);
            if (buffer.Length > 0 && isEmoji != bufferIsEmoji) Flush();
            bufferIsEmoji = isEmoji;
            buffer.Append(g);
        }
        Flush();
    }

    /// <summary>判斷一個字形叢集是否應以彩色 emoji 呈現。</summary>
    public static bool IsEmoji(string grapheme)
    {
        if (grapheme.Contains('︎')) return false;   // VS15：明確要求文字樣式（✔︎）
        if (grapheme.Contains('️')                   // VS16：明確要求 emoji 樣式（❤️）
            || grapheme.Contains('‍')                // ZWJ 組合（👨‍👩‍👧、❤️‍🔥）
            || grapheme.Contains('⃣'))               // 數字鍵帽（1️⃣）
            return true;

        foreach (var rune in grapheme.EnumerateRunes())
        {
            int v = rune.Value;
            if (v >= 0x1F000 && v <= 0x1FAFF) return true;   // 大部分 emoji、膚色、國旗（區域指示符）
            if (EmojiPresentationBmp(v)) return true;         // ✅ ⭐ ⚡ ☕ 這類預設就是 emoji 樣式的 BMP 字元
        }
        return false;   // ★ ✔ ❤（沒有 VS16）等維持文字樣式
    }

    // Unicode Emoji_Presentation=Yes 的 BMP 字元
    static bool EmojiPresentationBmp(int v) => v switch
    {
        >= 0x231A and <= 0x231B => true,
        >= 0x23E9 and <= 0x23EC => true,
        0x23F0 or 0x23F3 => true,
        >= 0x25FD and <= 0x25FE => true,
        >= 0x2614 and <= 0x2615 => true,
        >= 0x2648 and <= 0x2653 => true,
        0x267F or 0x2693 or 0x26A1 => true,
        >= 0x26AA and <= 0x26AB => true,
        >= 0x26BD and <= 0x26BE => true,
        >= 0x26C4 and <= 0x26C5 => true,
        0x26CE or 0x26D4 or 0x26EA => true,
        >= 0x26F2 and <= 0x26F3 => true,
        0x26F5 or 0x26FA or 0x26FD or 0x2705 => true,
        >= 0x270A and <= 0x270B => true,
        0x2728 or 0x274C or 0x274E => true,
        >= 0x2753 and <= 0x2755 => true,
        0x2757 => true,
        >= 0x2795 and <= 0x2797 => true,
        0x27B0 or 0x27BF => true,
        >= 0x2B1B and <= 0x2B1C => true,
        0x2B50 or 0x2B55 => true,
        _ => false,
    };
}
