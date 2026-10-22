using System.Text;
using Lumi.Core.Speech;
using Microsoft.Extensions.DependencyInjection;

namespace Lumi.Platform.Tests.Recording;

/// <summary>
/// 手動 smoke 測試：會真的開啟麥克風。只有設定環境變數 <c>LUMI_RUN_MIC_TESTS=1</c> 才執行，預設略過（CI 沒有麥克風）。
/// 錄 1 秒後停止，檢查長度與 WAV 檔頭。
/// </summary>
public class MicrophoneSmokeTests
{
    [SkippableFact]
    public async Task Records_about_one_second_and_returns_a_valid_wav()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("LUMI_RUN_MIC_TESTS") == "1", "手動測試：設定 LUMI_RUN_MIC_TESTS=1 才會開啟麥克風");
        Skip.IfNot(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), "只支援 Windows 與 macOS");

        var services = new ServiceCollection();
        services.AddLumiPlatform();
        using var provider = services.BuildServiceProvider();
        var recorder = provider.GetRequiredService<IAudioRecorder>();

        await recorder.StartAsync();
        Assert.True(recorder.IsRecording);
        await Task.Delay(TimeSpan.FromSeconds(1));
        var audio = await recorder.StopAsync();

        Assert.False(recorder.IsRecording);
        Assert.InRange(audio.Duration.TotalSeconds, 0.8, 1.3);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(audio.Wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(audio.Wav, 8, 4));
        Assert.Equal(audio.Wav.Length - WavFormat.HeaderSize, (int)BitConverter.ToUInt32(audio.Wav, 40));
        Assert.Equal(16_000, (int)BitConverter.ToUInt32(audio.Wav, 24));
    }
}
