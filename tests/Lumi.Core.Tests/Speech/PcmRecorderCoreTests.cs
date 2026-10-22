using System.Buffers.Binary;
using System.Text;
using Lumi.Core.Speech;

namespace Lumi.Core.Tests.Speech;

public class PcmRecorderCoreTests
{
    /// <summary>約 <paramref name="seconds"/> 秒的常數取樣。</summary>
    private static byte[] Constant(short value, double seconds)
    {
        var samples = (int)Math.Round(seconds * WavFormat.SampleRate);
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), value);
        return pcm;
    }

    [Fact]
    public void Duration_is_counted_from_samples()
    {
        var core = new PcmRecorderCore();
        core.Append(Constant(1000, 0.25));
        core.Append(Constant(1000, 0.75));

        Assert.Equal(1.0, core.Duration.TotalSeconds, 6);
    }

    [Fact]
    public void Append_returns_the_level_of_that_chunk()
    {
        var core = new PcmRecorderCore();
        var loud = core.Append(Constant(16000, 0.05));
        var quiet = core.Append(Constant(0, 0.05));

        Assert.True(loud.Level > 0.5f);
        Assert.Equal(0f, quiet.Level);
        Assert.False(loud.MaxReachedNow);
    }

    [Fact]
    public void Complete_produces_a_wav_with_the_accumulated_pcm()
    {
        var core = new PcmRecorderCore();
        core.Append(Constant(1000, 1.0));

        var audio = core.Complete();
        var span = audio.Wav.AsSpan();

        Assert.Equal("RIFF", Encoding.ASCII.GetString(span[0..4]));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(span[8..12]));
        Assert.Equal(WavFormat.HeaderSize + 32_000, audio.Wav.Length);
        Assert.Equal(32_000u, BinaryPrimitives.ReadUInt32LittleEndian(span[40..44]));
        Assert.Equal(TimeSpan.FromSeconds(1), audio.Duration);
        Assert.False(audio.TooShort);
        Assert.False(audio.LooksSilent);
    }

    [Fact]
    public void Recordings_shorter_than_half_a_second_are_too_short()
    {
        var core = new PcmRecorderCore();
        core.Append(Constant(1000, 0.49));

        Assert.True(core.Complete().TooShort);
    }

    [Fact]
    public void Exactly_half_a_second_is_not_too_short()
    {
        var core = new PcmRecorderCore();
        core.Append(Constant(1000, 0.5));

        Assert.False(core.Complete().TooShort);
    }

    [Fact]
    public void Empty_recording_is_too_short_and_not_silent()
    {
        var audio = new PcmRecorderCore().Complete();

        Assert.True(audio.TooShort);
        Assert.False(audio.LooksSilent);
        Assert.Equal(TimeSpan.Zero, audio.Duration);
        Assert.Equal(WavFormat.HeaderSize, audio.Wav.Length);
    }

    [Fact]
    public void All_zero_audio_looks_silent()
    {
        var core = new PcmRecorderCore();
        core.Append(Constant(0, 2));

        Assert.True(core.Complete().LooksSilent);
    }

    [Fact]
    public void Whole_recording_rms_decides_silence_not_one_quiet_chunk()
    {
        var core = new PcmRecorderCore();
        core.Append(Constant(0, 2));
        core.Append(Constant(8000, 0.5));

        Assert.False(core.Complete().LooksSilent);
    }

    [Fact]
    public void Reaching_the_limit_truncates_reports_once_and_ignores_later_data()
    {
        var core = new PcmRecorderCore();
        var first = core.Append(Constant(1000, 89.5));
        var crossing = core.Append(Constant(1000, 1.0));
        var after = core.Append(Constant(1000, 5.0));

        Assert.False(first.MaxReachedNow);
        Assert.True(crossing.MaxReachedNow);
        Assert.False(after.MaxReachedNow);
        Assert.Equal(0f, after.Level);
        Assert.True(core.ReachedMax);
        Assert.Equal(TimeSpan.FromSeconds(90), core.Duration);

        var audio = core.Complete();
        Assert.Equal(RecordingLimits.MaxPcmBytes + WavFormat.HeaderSize, audio.Wav.Length);
        Assert.Equal(TimeSpan.FromSeconds(90), audio.Duration);
    }

    [Fact]
    public void A_single_chunk_larger_than_the_limit_is_cut_at_the_limit()
    {
        var core = new PcmRecorderCore();
        var result = core.Append(Constant(1000, 100));

        Assert.True(result.MaxReachedNow);
        Assert.Equal(TimeSpan.FromSeconds(90), core.Complete().Duration);
    }

    [Fact]
    public void Odd_trailing_byte_is_dropped()
    {
        var core = new PcmRecorderCore();
        var pcm = Constant(1000, 1.0).Append((byte)0x7F).ToArray();
        core.Append(pcm);

        Assert.Equal(32_000, core.Complete().Wav.Length - WavFormat.HeaderSize);
    }

    [Fact]
    public void Data_after_complete_is_ignored_and_completing_twice_throws()
    {
        var core = new PcmRecorderCore();
        core.Append(Constant(1000, 1.0));
        core.Complete();

        Assert.Equal(0f, core.Append(Constant(1000, 1.0)).Level);
        Assert.Throws<InvalidOperationException>(() => core.Complete());
    }
}
