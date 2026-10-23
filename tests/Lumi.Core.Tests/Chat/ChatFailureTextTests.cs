using Lumi.Core.Chat;

namespace Lumi.Core.Tests.Chat;

public class ChatFailureTextTests
{
    private static ChatFailure Network(string message = "無法連線到 Anthropic。") =>
        new(ChatFailureKind.Network, message, Retryable: true);

    [Fact]
    public void Network_WithoutAnyInterface_ToExternalHost_AppendsOfflineSentence()
    {
        var decorated = ChatFailureText.Decorate(Network(), hasNetworkInterface: false, targetIsLocal: false);

        Assert.Equal("無法連線到 Anthropic。看起來沒有網路連線。", decorated.Message);
        Assert.Equal(ChatFailureKind.Network, decorated.Kind);
        Assert.True(decorated.Retryable);
    }

    [Fact]
    public void Network_WithAnInterface_LeavesMessageAlone()
    {
        var failure = Network();

        Assert.Same(failure, ChatFailureText.Decorate(failure, hasNetworkInterface: true, targetIsLocal: false));
    }

    [Fact]
    public void Network_WithoutAnyInterface_ToLocalTarget_KeepsServiceHint()
    {
        // 本機或內網目標：沒有網路介面不代表服務沒開，維持原本「確認服務是否已啟動」的說明
        var failure = new ChatFailure(ChatFailureKind.Network, "無法連線到 localhost，請確認 Base URL 與服務是否已啟動。", true);

        Assert.Same(failure, ChatFailureText.Decorate(failure, hasNetworkInterface: false, targetIsLocal: true));
    }

    [Fact]
    public void RateLimited_WithRetryAfter_ReplacesRetryPhraseWithSeconds()
    {
        var failure = new ChatFailure(ChatFailureKind.RateLimited, "已達到速率限制，請稍後再試。", true, TimeSpan.FromSeconds(30));

        var decorated = ChatFailureText.Decorate(failure, hasNetworkInterface: true, targetIsLocal: false);

        Assert.Equal("已達到速率限制，約 30 秒後再試。", decorated.Message);
        Assert.Equal(TimeSpan.FromSeconds(30), decorated.RetryAfter);   // 其他欄位原樣保留
    }

    [Fact]
    public void Overloaded_WithRetryAfter_UsesTheSameWording()
    {
        var failure = new ChatFailure(ChatFailureKind.Overloaded, "服務暫時過載，請稍後再試。", true, TimeSpan.FromSeconds(5));

        Assert.Equal("服務暫時過載，約 5 秒後再試。", ChatFailureText.Decorate(failure, true, false).Message);
    }

    [Fact]
    public void RateLimited_WithoutRetryAfter_LeavesMessageAlone()
    {
        var failure = new ChatFailure(ChatFailureKind.RateLimited, "已達到速率限制，請稍後再試。", true, null);

        Assert.Same(failure, ChatFailureText.Decorate(failure, true, false));
    }

    [Fact]
    public void OtherKinds_IgnoreRetryAfter()
    {
        var failure = new ChatFailure(ChatFailureKind.ServerError, "伺服器發生錯誤，請稍後再試。", true, TimeSpan.FromSeconds(9));

        Assert.Same(failure, ChatFailureText.Decorate(failure, true, false));
    }

    [Theory]
    [InlineData(0.2, "約 1 秒")]     // 不足一秒也無條件進位到 1 秒
    [InlineData(1, "約 1 秒")]
    [InlineData(1.001, "約 2 秒")]   // 有小數就進位
    [InlineData(59.9, "約 60 秒")]
    [InlineData(60, "約 60 秒")]     // 60 秒以內仍用秒
    [InlineData(61, "約 2 分鐘")]    // 超過 60 秒改用分鐘，同樣進位
    [InlineData(120, "約 2 分鐘")]
    [InlineData(121, "約 3 分鐘")]
    [InlineData(3600, "約 60 分鐘")]
    public void FormatWait_RoundsUp_AndSwitchesToMinutesAfterSixtySeconds(double seconds, string expected) =>
        Assert.Equal(expected, ChatFailureText.FormatWait(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData("localhost")]
    [InlineData("api.localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.5")]
    [InlineData("169.254.10.10")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("gateway")]          // 沒有點的主機名稱
    [InlineData("ai.local")]
    [InlineData("llm.internal")]
    public void IsLocalOrPrivateHost_RecognizesLocalAndIntranetTargets(string host) =>
        Assert.True(ChatFailureText.IsLocalOrPrivateHost(host));

    [Theory]
    [InlineData("api.anthropic.com")]
    [InlineData("api.openai.com")]
    [InlineData("172.32.0.1")]       // 私有範圍之外
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700::1111")]
    [InlineData("")]
    public void IsLocalOrPrivateHost_TreatsExternalAndUnknownAsNotLocal(string host) =>
        Assert.False(ChatFailureText.IsLocalOrPrivateHost(host));

    [Theory]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://192.168.10.20:8000", true)]
    [InlineData("https://api.anthropic.com", false)]
    [InlineData("not a url", false)]
    public void IsLocalTarget_ReadsTheHostOfTheBaseUrl(string baseUrl, bool expected) =>
        Assert.Equal(expected, ChatFailureText.IsLocalTarget(baseUrl));

    [Fact]
    public void DecoratedTexts_ContainNoKeyLikeStringsOrPaths()
    {
        var texts = new[]
        {
            ChatFailureText.Decorate(Network(), false, false).Message,
            ChatFailureText.Decorate(new ChatFailure(ChatFailureKind.RateLimited, "已達到速率限制，請稍後再試。", true, TimeSpan.FromMinutes(3)), true, false).Message,
            ChatFailureText.Decorate(new ChatFailure(ChatFailureKind.Overloaded, "服務暫時過載，請稍後再試。", true, TimeSpan.FromSeconds(7)), true, false).Message,
        };

        foreach (var text in texts)
        {
            Assert.DoesNotContain("sk-", text);
            Assert.DoesNotContain("/", text);
            Assert.DoesNotContain("\\", text);
        }
    }
}
