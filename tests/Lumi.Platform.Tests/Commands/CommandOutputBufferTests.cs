using Lumi.Platform.Commands;

namespace Lumi.Platform.Tests.Commands;

public class CommandOutputBufferTests
{
    [Fact]
    public void Strips_ansi_codes()
    {
        var buffer = new CommandOutputBuffer();
        buffer.Append("\x1B[31mred\x1B[0m \x1B]0;title\x07ok");

        var (text, truncated) = buffer.Build();

        Assert.Equal("red ok", text);
        Assert.False(truncated);
    }

    [Fact]
    public void Keeps_last_segment_after_carriage_return()
    {
        var buffer = new CommandOutputBuffer();
        buffer.Append("10%\r50%\r100%\ndone");

        var (text, _) = buffer.Build();

        Assert.Equal("100%\ndone", text);
    }

    [Fact]
    public void Truncates_middle_and_reports()
    {
        // 50,000 字：開頭 10,000 個 a、中間 20,000 個 m、結尾 20,000 個 z
        var buffer = new CommandOutputBuffer();
        buffer.Append(new string('a', 10_000) + new string('m', 20_000) + new string('z', 20_000));

        var (text, truncated) = buffer.Build();

        Assert.True(truncated);
        Assert.StartsWith(new string('a', 10_000) + "\n", text);
        Assert.EndsWith("\n" + new string('z', 20_000), text);
        Assert.Contains("…（中間省略 20,000 字）…", text);
        Assert.DoesNotContain("m", text);
    }

    [Fact]
    public void Short_output_is_not_truncated()
    {
        var buffer = new CommandOutputBuffer();
        buffer.Append(new string('x', 30_000));

        var (text, truncated) = buffer.Build();

        Assert.False(truncated);
        Assert.Equal(30_000, text.Length);
    }

    [Fact]
    public void LatestLine_is_last_non_empty_line()
    {
        var buffer = new CommandOutputBuffer();
        Assert.Equal(string.Empty, buffer.LatestLine);

        buffer.Append("a\nb\n\n   \n");

        Assert.Equal("b", buffer.LatestLine);
    }

    [Fact]
    public void Huge_output_keeps_head_and_tail_with_bounded_memory()
    {
        // 模擬印出一個大檔案：一行一個編號，總長遠超過原始緩衝的上限
        var buffer = new CommandOutputBuffer();
        for (var i = 0; i < 200_000; i++)
            buffer.Append($"line{i}\n");

        var (text, truncated) = buffer.Build();

        Assert.True(truncated);
        Assert.StartsWith("line0\n", text);
        Assert.EndsWith("line199999\n", text);
        Assert.Contains("中間省略", text);
        Assert.True(text.Length < CommandOutputBuffer.HeadChars + CommandOutputBuffer.TailChars + 100);
        Assert.Equal("line199999", buffer.LatestLine);
    }
}
