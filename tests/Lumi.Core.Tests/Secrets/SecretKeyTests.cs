using Lumi.Core.Secrets;

namespace Lumi.Core.Tests.Secrets;

public class SecretKeyTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("connection/dev-anthropic/api-key")]
    [InlineData("speech/whisper/token")]
    [InlineData("k1/2b")]
    public void Valid_names_are_accepted(string name)
    {
        var key = new SecretKey(name);

        Assert.Equal(name, key.Name);
        Assert.Equal(name, key.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Upper")]
    [InlineData("has space")]
    [InlineData("under_score")]
    [InlineData("中文")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("/leading")]
    [InlineData("trailing/")]
    [InlineData("double//slash")]
    [InlineData("double--dash")] // 連續的 - 視為不合法：每段只允許單一的 -
    [InlineData("a/-b")]
    public void Invalid_names_throw(string name)
    {
        Assert.Throws<ArgumentException>(() => new SecretKey(name));
        Assert.False(SecretKey.IsValid(name));
    }

    [Fact]
    public void Null_name_throws() => Assert.Throws<ArgumentException>(() => new SecretKey(null!));

    [Fact]
    public void Too_long_name_throws()
    {
        var name = new string('a', SecretKey.MaxLength + 1);

        Assert.Throws<ArgumentException>(() => new SecretKey(name));
        Assert.True(SecretKey.IsValid(new string('a', SecretKey.MaxLength)));
    }

    [Fact]
    public void Equal_names_are_equal_keys() =>
        Assert.Equal(new SecretKey("a/b"), new SecretKey("a/b"));

    [Fact]
    public void Default_key_is_rejected_by_RequireName() =>
        Assert.Throws<ArgumentException>(() => default(SecretKey).RequireName());
}
