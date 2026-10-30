using Lumi.Core.Usage;

namespace Lumi.Core.Tests.Usage;

public sealed class LocalConnectionTests
{
    [Theory]
    [InlineData("http://localhost:11434/v1")]
    [InlineData("http://LOCALHOST:11434")]
    [InlineData("https://dev.localhost:8080")]
    [InlineData("http://127.0.0.1:1234/v1")]
    [InlineData("http://127.5.6.7/v1")]
    [InlineData("http://[::1]:11434/v1")]
    [InlineData("http://0.0.0.0:8000")]
    public void Loopback_IsLocal(string baseUrl)
    {
        Assert.True(LocalConnection.IsLoopback(baseUrl));
    }

    [Theory]
    [InlineData("http://10.0.0.5:8000/v1")]
    [InlineData("http://172.20.1.1/v1")]
    [InlineData("http://192.168.1.2:8080/v1")]
    [InlineData("http://gateway/v1")]
    [InlineData("https://api.openai.com/v1")]
    public void IntranetAndRemote_AreNotLocal(string baseUrl)
    {
        Assert.False(LocalConnection.IsLoopback(baseUrl));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("localhost:11434")]
    public void Unparseable_IsNotLocal(string baseUrl)
    {
        Assert.False(LocalConnection.IsLoopback(baseUrl));
    }
}
