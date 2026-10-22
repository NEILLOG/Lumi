using System.Buffers.Binary;
using System.Text;
using Lumi.Core.Speech;

namespace Lumi.Core.Tests.Speech;

public class WavFormatTests
{
    [Fact]
    public void Constants_describe_16khz_mono_16bit_pcm()
    {
        Assert.Equal(16_000, WavFormat.SampleRate);
        Assert.Equal(1, WavFormat.Channels);
        Assert.Equal(16, WavFormat.BitsPerSample);
        Assert.Equal(2, WavFormat.BlockAlign);
        Assert.Equal(32_000, WavFormat.BytesPerSecond);
    }

    [Fact]
    public void Header_fields_match_pcm_wave_layout()
    {
        var wav = WavFormat.FromPcm(new byte[100]);
        var span = wav.AsSpan();

        Assert.Equal("RIFF", Encoding.ASCII.GetString(span[0..4]));
        Assert.Equal(36u + 100u, BinaryPrimitives.ReadUInt32LittleEndian(span[4..8]));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(span[8..12]));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(span[12..16]));
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(span[16..20]));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(span[20..22]));    // PCM
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(span[22..24]));    // 單聲道
        Assert.Equal(16_000u, BinaryPrimitives.ReadUInt32LittleEndian(span[24..28]));
        Assert.Equal(32_000u, BinaryPrimitives.ReadUInt32LittleEndian(span[28..32]));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(span[32..34]));    // block align
        Assert.Equal((ushort)16, BinaryPrimitives.ReadUInt16LittleEndian(span[34..36]));
        Assert.Equal("data", Encoding.ASCII.GetString(span[36..40]));
        Assert.Equal(100u, BinaryPrimitives.ReadUInt32LittleEndian(span[40..44]));
    }

    [Fact]
    public void FromPcm_keeps_the_samples_after_the_header()
    {
        byte[] pcm = [0x01, 0x02, 0xFF, 0x7F];

        var wav = WavFormat.FromPcm(pcm);

        Assert.Equal(WavFormat.HeaderSize + pcm.Length, wav.Length);
        Assert.Equal(pcm, wav[WavFormat.HeaderSize..]);
    }

    [Fact]
    public void FromPcm_with_no_samples_is_just_a_header()
    {
        var wav = WavFormat.FromPcm([]);

        Assert.Equal(WavFormat.HeaderSize, wav.Length);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40, 4)));
    }

    [Fact]
    public void FromPcm_rejects_an_odd_number_of_bytes()
    {
        Assert.Throws<ArgumentException>(() => WavFormat.FromPcm(new byte[3]));
    }

    [Fact]
    public void WriteHeader_needs_room_for_the_whole_header()
    {
        Assert.Throws<ArgumentException>(() => WavFormat.WriteHeader(new byte[WavFormat.HeaderSize - 1], 0));
    }

    [Fact]
    public void Silence_of_half_a_second_is_8000_samples_of_zeros()
    {
        var wav = WavFormat.Silence(TimeSpan.FromMilliseconds(500));

        Assert.Equal(WavFormat.HeaderSize + 16_000, wav.Length);
        Assert.Equal(16_000u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40, 4)));
        Assert.All(wav[WavFormat.HeaderSize..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Silence_of_zero_length_is_just_a_header()
    {
        Assert.Equal(WavFormat.HeaderSize, WavFormat.Silence(TimeSpan.Zero).Length);
    }

    [Fact]
    public void Silence_rejects_negative_durations()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WavFormat.Silence(TimeSpan.FromSeconds(-1)));
    }
}
