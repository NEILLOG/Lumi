using Lumi.Core.Speech;

namespace Lumi.Core.Tests.Speech;

public class RecordingLimitsTests
{
    [Fact]
    public void Limits_match_the_plan()
    {
        Assert.Equal(TimeSpan.FromSeconds(90), RecordingLimits.MaxDuration);
        Assert.Equal(TimeSpan.FromSeconds(0.5), RecordingLimits.MinDuration);
    }

    [Fact]
    public void Max_bytes_is_ninety_seconds_of_16khz_mono_16bit()
    {
        Assert.Equal(2_880_000, RecordingLimits.MaxPcmBytes);
        Assert.Equal((int)(90 * WavFormat.BytesPerSecond), RecordingLimits.MaxPcmBytes);
    }

    [Fact]
    public void Silence_threshold_is_a_small_positive_value()
    {
        Assert.InRange(RecordingLimits.SilentRmsThreshold, 0, 0.01);
        Assert.True(RecordingLimits.SilentRmsThreshold > 0);
    }
}
