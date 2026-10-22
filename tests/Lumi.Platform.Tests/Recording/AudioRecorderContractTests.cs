using System.Reflection;
using System.Runtime.Versioning;
using Lumi.Core.Speech;
using Lumi.Platform.Recording;

namespace Lumi.Platform.Tests.Recording;

/// <summary>錄音實作的型別契約：兩個平台都實作 <see cref="IAudioRecorder"/>，並標註自己的作業系統。不會開啟麥克風。</summary>
public class AudioRecorderContractTests
{
    [Fact]
    public void Windows_recorder_implements_the_contract_and_is_marked_windows_only()
    {
        AssertContract(typeof(WindowsWaveInRecorder), "windows");
    }

    [Fact]
    public void macOS_recorder_implements_the_contract_and_is_marked_macos_only()
    {
        AssertContract(typeof(MacAudioQueueRecorder), "macos");
    }

    [Fact]
    public void Recorders_are_disposable_so_the_microphone_can_be_released()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(WindowsWaveInRecorder)));
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(MacAudioQueueRecorder)));
    }

    private static void AssertContract(Type type, string platform)
    {
        Assert.True(typeof(IAudioRecorder).IsAssignableFrom(type));
        Assert.Contains(type.GetCustomAttributes<SupportedOSPlatformAttribute>(), a => a.PlatformName == platform);
        Assert.True(type.IsPublic);
        Assert.Single(type.GetConstructors());   // 建構子不碰原生資源
    }
}
