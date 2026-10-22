using Lumi.Core.Speech;

namespace Lumi.Core.Tests.Speech;

public class AudioRecorderExceptionTests
{
    [Fact]
    public void Keeps_kind_and_message()
    {
        var ex = new AudioRecorderException(AudioRecorderFailureKind.PermissionDenied, "沒有麥克風權限。");

        Assert.Equal(AudioRecorderFailureKind.PermissionDenied, ex.Kind);
        Assert.Equal("沒有麥克風權限。", ex.Message);
    }
}
