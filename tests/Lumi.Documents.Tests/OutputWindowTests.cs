namespace Lumi.Documents.Tests;

public class OutputWindowTests
{
    [Fact]
    public void Short_text_is_returned_whole_without_truncation()
    {
        var slice = OutputWindow.Slice("hello", 0, 8000);

        Assert.Equal(new OutputSlice("hello", false, null), slice);
    }

    [Fact]
    public void Long_text_is_cut_at_max_chars_and_offers_the_next_offset()
    {
        var text = new string('a', 25);

        var first = OutputWindow.Slice(text, 0, 10);
        Assert.Equal(10, first.Content.Length);
        Assert.True(first.Truncated);
        Assert.Equal(10, first.NextOffset);

        var second = OutputWindow.Slice(text, first.NextOffset!.Value, 10);
        Assert.Equal(10, second.Content.Length);
        Assert.True(second.Truncated);
        Assert.Equal(20, second.NextOffset);

        var last = OutputWindow.Slice(text, second.NextOffset!.Value, 10);
        Assert.Equal(5, last.Content.Length);
        Assert.False(last.Truncated);
        Assert.Null(last.NextOffset);
    }

    [Fact]
    public void Chunks_concatenate_back_to_the_original_text()
    {
        var text = string.Concat(Enumerable.Repeat("中文與 emoji 😀 混排 ", 40));
        var builder = new System.Text.StringBuilder();
        var offset = 0;
        int? next;
        do
        {
            var slice = OutputWindow.Slice(text, offset, 17);
            builder.Append(slice.Content);
            next = slice.NextOffset;
            offset = next ?? 0;
        }
        while (next is not null);

        Assert.Equal(text, builder.ToString());
    }

    [Fact]
    public void Offset_past_the_end_gives_an_empty_slice()
    {
        var slice = OutputWindow.Slice("abc", 10, 8000);

        Assert.Equal(new OutputSlice("", false, null), slice);
    }

    [Fact]
    public void Negative_offset_starts_at_the_beginning()
    {
        Assert.Equal("ab", OutputWindow.Slice("abc", -5, 2).Content);
    }

    [Fact]
    public void Zero_or_negative_max_chars_uses_the_default()
    {
        var text = new string('x', OutputWindow.DefaultMaxChars + 10);

        Assert.Equal(OutputWindow.DefaultMaxChars, OutputWindow.Slice(text, 0, 0).Content.Length);
        Assert.Equal(OutputWindow.DefaultMaxChars, OutputWindow.Slice(text, 0, -1).Content.Length);
    }

    [Fact]
    public void Max_chars_is_capped_at_the_limit()
    {
        var text = new string('x', OutputWindow.MaxCharsLimit + 500);

        Assert.Equal(16000, OutputWindow.Slice(text, 0, 999_999).Content.Length);
    }

    [Fact]
    public void Cut_never_splits_a_surrogate_pair()
    {
        // 😀 是一個代理對（兩個 UTF-16 字元）；上限正好切在中間時要往前退
        var text = "ab😀cd";

        var first = OutputWindow.Slice(text, 0, 3);

        Assert.Equal("ab", first.Content);
        Assert.Equal(2, first.NextOffset);

        var second = OutputWindow.Slice(text, first.NextOffset!.Value, 3);
        Assert.Equal("😀c", second.Content);
    }

    [Fact]
    public void Max_chars_of_one_still_takes_a_whole_pair_so_reading_progresses()
    {
        var text = "😀x";

        var slice = OutputWindow.Slice(text, 0, 1);

        Assert.Equal("😀", slice.Content);
        Assert.Equal(2, slice.NextOffset);
    }

    [Fact]
    public void Offset_inside_a_surrogate_pair_skips_the_orphan_low_surrogate()
    {
        var text = "a😀b";

        var slice = OutputWindow.Slice(text, 2, 8000);   // 2 是 😀 的低代理字元

        Assert.Equal("b", slice.Content);
    }
}
