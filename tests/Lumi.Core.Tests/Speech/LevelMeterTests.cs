using System.Buffers.Binary;
using Lumi.Core.Speech;

namespace Lumi.Core.Tests.Speech;

public class LevelMeterTests
{
    private static byte[] Constant(short value, int samples)
    {
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), value);
        return pcm;
    }

    [Fact]
    public void Empty_or_silent_input_is_zero()
    {
        Assert.Equal(0f, LevelMeter.Measure([]));
        Assert.Equal(0f, LevelMeter.Measure(Constant(0, 1600)));
        Assert.Equal(0d, LevelMeter.Rms([]));
    }

    [Fact]
    public void Full_scale_is_one_and_rms_is_about_one()
    {
        var pcm = Constant(short.MaxValue, 1600);
        Assert.InRange(LevelMeter.Rms(pcm), 0.999, 1.0);
        Assert.InRange(LevelMeter.Measure(pcm), 0.999f, 1f);
    }

    [Fact]
    public void Rms_of_a_constant_signal_is_its_amplitude()
    {
        // 3277 / 32768 ≈ 0.1
        Assert.InRange(LevelMeter.Rms(Constant(3277, 800)), 0.0999, 0.1001);
    }

    [Fact]
    public void Level_is_logarithmic_so_quiet_speech_still_moves_the_meter()
    {
        // -20 dBFS 在 -60..0 的範圍內約 2/3，而線性會只有 0.1
        Assert.InRange(LevelMeter.Level(0.1), 0.66, 0.68);
        Assert.InRange(LevelMeter.Level(0.01), 0.32, 0.34);
    }

    [Fact]
    public void Level_clamps_to_zero_and_one()
    {
        Assert.Equal(0f, LevelMeter.Level(0));
        Assert.Equal(0f, LevelMeter.Level(1e-9));   // 低於底線
        Assert.Equal(1f, LevelMeter.Level(2.0));    // 超過 0 dBFS
    }

    [Fact]
    public void Louder_input_gives_a_higher_level()
    {
        Assert.True(LevelMeter.Measure(Constant(1000, 800)) < LevelMeter.Measure(Constant(8000, 800)));
    }

    [Fact]
    public void Odd_trailing_byte_is_ignored()
    {
        var pcm = Constant(3277, 800);
        var padded = pcm.Append((byte)0xFF).ToArray();

        Assert.Equal(LevelMeter.Rms(pcm), LevelMeter.Rms(padded));
        LevelMeter.SumSquares(padded, out var count);
        Assert.Equal(800, count);
    }
}
