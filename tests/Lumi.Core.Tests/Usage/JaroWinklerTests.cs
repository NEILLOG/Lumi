using Lumi.Core.Usage;

namespace Lumi.Core.Tests.Usage;

public sealed class JaroWinklerTests
{
    [Fact]
    public void Identical_IsOne()
    {
        Assert.Equal(1.0, JaroWinkler.Similarity("gpt-4o", "gpt-4o"), 6);
    }

    [Fact]
    public void Empty_Cases()
    {
        Assert.Equal(1.0, JaroWinkler.Similarity("", ""), 6);
        Assert.Equal(0.0, JaroWinkler.Similarity("", "abc"), 6);
        Assert.Equal(0.0, JaroWinkler.Similarity("abc", ""), 6);
    }

    [Fact]
    public void IgnoresCase()
    {
        Assert.Equal(1.0, JaroWinkler.Similarity("GPT-4O", "gpt-4o"), 6);
    }

    [Fact]
    public void KnownValues_MatchReferenceImplementation()
    {
        Assert.Equal(0.961, JaroWinkler.Similarity("MARTHA", "MARHTA"), 3);
        Assert.Equal(0.840, JaroWinkler.Similarity("DWAYNE", "DUANE"), 3);
    }

    [Fact]
    public void Unrelated_IsLow()
    {
        Assert.True(JaroWinkler.Similarity("abc", "xyz") < 0.5);
    }
}
