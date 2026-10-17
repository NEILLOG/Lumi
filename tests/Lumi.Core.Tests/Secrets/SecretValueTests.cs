using Lumi.Core.Secrets;

namespace Lumi.Core.Tests.Secrets;

public class SecretValueTests
{
    private const string Plain = "sk-ant-super-secret-0123456789";

    [Fact]
    public void Reveal_returns_plain_text() => Assert.Equal(Plain, new SecretValue(Plain).Reveal());

    [Fact]
    public void ToString_is_masked() => Assert.Equal("***", new SecretValue(Plain).ToString());

    [Fact]
    public void Interpolation_and_format_are_masked()
    {
        var value = new SecretValue(Plain);

        Assert.DoesNotContain(Plain, $"key={value}");
        Assert.DoesNotContain(Plain, string.Format("key={0}", value));
    }

    [Fact]
    public void Exception_message_built_from_value_has_no_plain_text()
    {
        var ex = new InvalidOperationException($"failed with {new SecretValue(Plain)}");

        Assert.DoesNotContain(Plain, ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_value_throws(string? text) =>
        Assert.Throws<ArgumentException>(() => new SecretValue(text!));
}
