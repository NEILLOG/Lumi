using System.Buffers.Binary;

namespace Lumi.Core.Speech;

/// <summary>PCM16（小端序）→ RMS → 0..1 的音量。純函式，平台無關。</summary>
public static class LevelMeter
{
    /// <summary>對數壓縮的底線（dBFS）：低於此值顯示 0，0 dBFS 顯示 1。讓小聲說話的波形也看得到起伏。</summary>
    public const double FloorDb = -60;

    private const double FullScale = 32768.0;

    /// <summary>平方和與取樣數。奇數長度的最後一個位元組忽略（取樣必須成對）。</summary>
    public static double SumSquares(ReadOnlySpan<byte> pcm, out int sampleCount)
    {
        sampleCount = pcm.Length / 2;
        double sum = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm[(i * 2)..]) / FullScale;
            sum += sample * sample;
        }

        return sum;
    }

    /// <summary>RMS（0..1）。空資料回 0。</summary>
    public static double Rms(ReadOnlySpan<byte> pcm)
    {
        var sum = SumSquares(pcm, out var count);
        return count == 0 ? 0 : Math.Sqrt(sum / count);
    }

    /// <summary>把 RMS 轉成 0..1 的顯示音量（dB 線性映射，<see cref="FloorDb"/> 到 0 dBFS）。</summary>
    public static float Level(double rms)
    {
        if (rms <= 0)
            return 0f;

        var db = 20 * Math.Log10(rms);
        return (float)Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);
    }

    /// <summary>直接從 PCM 算顯示音量。</summary>
    public static float Measure(ReadOnlySpan<byte> pcm) => Level(Rms(pcm));
}
