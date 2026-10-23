using System.Globalization;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Lumi.App.Controls;

namespace Lumi.App.Tests;

/// <summary>
/// 9E：EmojiText 的切段行為對驗證字串（<see cref="EmojiSamples"/>）成立。
/// 只測「切段」，不測字型能否畫出彩色字形；後者只能在 Windows 實機驗證（poc/Lumi.EmojiPoc/README.md）。
/// </summary>
public class EmojiSegmentationTests
{
    public static TheoryData<string> SampleTexts()
    {
        var data = new TheoryData<string>();
        foreach (var set in EmojiSamples.Sets) data.Add(set.Text);
        return data;
    }

    /// <summary>單一叢集：每組字串以空白切開（組合內部沒有空白）。混合句子不是單一叢集的集合，不列入。</summary>
    public static TheoryData<string> SingleClusters()
    {
        var data = new TheoryData<string>();
        foreach (var set in EmojiSamples.Sets.Where(s => s.Name != "混合句子"))
            foreach (var cluster in set.Text.Split(' '))
                data.Add(cluster);
        return data;
    }

    private static List<string> Graphemes(string text)
    {
        var list = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext()) list.Add(e.GetTextElement());
        return list;
    }

    /// <summary>切段結果：每一段 Run 的文字與是否為 emoji 段（字型指定為 <see cref="EmojiText.EmojiFont"/>）。</summary>
    private static List<(string Text, bool Emoji)> Segments(string text, Action<Run>? style = null)
    {
        var inlines = new InlineCollection();
        EmojiText.AddText(inlines, text, style);
        return inlines.OfType<Run>()
            .Select(r => (r.Text ?? "", ReferenceEquals(r.FontFamily, EmojiText.EmojiFont)))
            .ToList();
    }

    [AvaloniaTheory]
    [MemberData(nameof(SampleTexts))]
    public void Segments_concatenate_back_to_the_original_text(string text)
    {
        var joined = string.Concat(Segments(text).Select(s => s.Text));
        Assert.Equal(text, joined);
    }

    [AvaloniaTheory]
    [MemberData(nameof(SampleTexts))]
    public void No_grapheme_cluster_is_split_across_runs(string text)
    {
        // 每個 Run 的結束位置（UTF-16 索引）就是一個切點；切點不能落在任何叢集內部
        var cuts = new HashSet<int>();
        int pos = 0;
        foreach (var seg in Segments(text))
        {
            pos += seg.Text.Length;
            cuts.Add(pos);
        }

        int start = 0;
        foreach (var g in Graphemes(text))
        {
            int end = start + g.Length;
            for (int i = start + 1; i < end; i++)
                Assert.False(cuts.Contains(i), $"叢集 {g} (U+{string.Join(" U+", g.EnumerateRunes().Select(r => r.Value.ToString("X4")))}) 被拆開於索引 {i}");
            start = end;
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(SampleTexts))]
    public void Every_cluster_lies_wholly_in_a_segment_whose_kind_matches_IsEmoji(string text)
    {
        // 逐字元標記所屬段是否為 emoji 段，再檢查每個叢集的所有字元都與 IsEmoji 的判定一致
        var flags = new List<bool>();
        foreach (var seg in Segments(text))
            flags.AddRange(Enumerable.Repeat(seg.Emoji, seg.Text.Length));

        int start = 0;
        foreach (var g in Graphemes(text))
        {
            bool expected = EmojiText.IsEmoji(g);
            for (int i = start; i < start + g.Length; i++)
                Assert.True(flags[i] == expected, $"叢集 {g} 的 emoji 判定與段落不一致（索引 {i}）");
            start += g.Length;
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(SingleClusters))]
    public void Single_cluster_becomes_exactly_one_emoji_run(string cluster)
    {
        var segs = Segments(cluster);
        var single = Assert.Single(segs);
        Assert.Equal(cluster, single.Text);
        Assert.True(single.Emoji, $"{cluster} 應整段標成 emoji 段");
    }

    [AvaloniaFact]
    public void Each_regional_indicator_pair_is_one_cluster()
    {
        // 🇹🇼🇯🇵🇺🇸🇬🇧 是四個國旗；每個國旗是兩個 Regional Indicator（各為一個代理對，共 4 個 UTF-16 字元）
        var flags = Graphemes(EmojiSamples.Sets.Single(s => s.Name == "國旗").Text);
        Assert.Equal(4, flags.Count);
        Assert.All(flags, f => Assert.Equal(4, f.Length));
        Assert.All(flags, f => Assert.True(EmojiText.IsEmoji(f)));
    }

    [AvaloniaFact]
    public void Flags_set_becomes_one_emoji_run()
    {
        var text = EmojiSamples.Sets.Single(s => s.Name == "國旗").Text;
        var single = Assert.Single(Segments(text));
        Assert.True(single.Emoji);
        Assert.Equal(text, single.Text);
    }

    [AvaloniaFact]
    public void Mixed_sentence_emoji_segments_are_the_expected_clusters()
    {
        var text = EmojiSamples.Sets.Single(s => s.Name == "混合句子").Text;
        var emojiSegments = Segments(text).Where(s => s.Emoji).Select(s => s.Text).ToList();
        Assert.Equal(new[] { "☀️", "🇹🇼", "👋🏿", "❤️‍🔥" }, emojiSegments);
    }

    [AvaloniaTheory]
    [MemberData(nameof(SampleTexts))]
    public void Style_callback_does_not_change_segmentation(string text)
    {
        // 粗體、等寬（Markdown 的 Run）套用樣式後，emoji 段與切點仍與無樣式時相同
        var plain = Segments(text);
        var bold = Segments(text, r => r.FontWeight = FontWeight.Bold);
        var mono = Segments(text, r => r.FontFamily = new FontFamily("Menlo"));
        Assert.Equal(plain, bold);
        Assert.Equal(plain, mono);
    }

    [AvaloniaFact]
    public void Newlines_become_line_breaks_not_emoji_runs()
    {
        var inlines = new InlineCollection();
        EmojiText.AddText(inlines, "👍🏽\n🇯🇵");
        Assert.Single(inlines.OfType<LineBreak>());
        Assert.Equal(2, inlines.OfType<Run>().Count());
    }
}
