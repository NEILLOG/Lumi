using System.Buffers.Binary;
using System.Text;

namespace Lumi.Core.Speech;

/// <summary>
/// 錄音與測試連線用的 WAV 格式：16 kHz、單聲道、16-bit PCM（agent.md 第 11 節）。純函式，平台無關。
/// </summary>
public static class WavFormat
{
    public const int SampleRate = 16_000;
    public const int Channels = 1;
    public const int BitsPerSample = 16;

    /// <summary>標準 PCM 檔頭的長度（RIFF + fmt + data 區塊頭）。</summary>
    public const int HeaderSize = 44;

    public const int BlockAlign = Channels * BitsPerSample / 8;
    public const int BytesPerSecond = SampleRate * BlockAlign;

    /// <summary>寫入 <see cref="HeaderSize"/> 位元組的檔頭，<paramref name="dataLength"/> 是後面 PCM 資料的位元組數。</summary>
    public static void WriteHeader(Span<byte> destination, int dataLength)
    {
        if (destination.Length < HeaderSize)
            throw new ArgumentException($"檔頭需要 {HeaderSize} 位元組。", nameof(destination));
        if (dataLength < 0)
            throw new ArgumentOutOfRangeException(nameof(dataLength), "資料長度不可為負數。");
        if (dataLength % BlockAlign != 0)
            throw new ArgumentException($"資料長度必須是 {BlockAlign} 的倍數（每個取樣 2 位元組）。", nameof(dataLength));

        Encoding.ASCII.GetBytes("RIFF", destination[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], (uint)(36 + dataLength));
        Encoding.ASCII.GetBytes("WAVE", destination[8..12]);
        Encoding.ASCII.GetBytes("fmt ", destination[12..16]);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], 16);                 // fmt 區塊長度
        BinaryPrimitives.WriteUInt16LittleEndian(destination[20..], 1);                  // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(destination[22..], Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[24..], SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[28..], BytesPerSecond);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[32..], BlockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[34..], BitsPerSample);
        Encoding.ASCII.GetBytes("data", destination[36..40]);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[40..], (uint)dataLength);
    }

    /// <summary>把 16-bit 小端序的 PCM 包成完整的 WAV（檔頭＋資料）。資料長度必須是 2 的倍數。</summary>
    public static byte[] FromPcm(ReadOnlySpan<byte> pcm)
    {
        var wav = new byte[HeaderSize + pcm.Length];
        WriteHeader(wav, pcm.Length);
        pcm.CopyTo(wav.AsSpan(HeaderSize));
        return wav;
    }

    /// <summary>指定長度的靜音 WAV（取樣值全為 0）。長度依取樣數四捨五入到整個取樣。</summary>
    public static byte[] Silence(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "靜音長度不可為負數。");

        var samples = (int)Math.Round(duration.TotalSeconds * SampleRate, MidpointRounding.AwayFromZero);
        return FromPcm(new byte[samples * BlockAlign]);
    }
}
