using Avalonia;
using Lumi.Sprites.Abstractions;

namespace Lumi.Sprites.Tests;

public class SpriteStateResolverTests
{
    internal static SpriteManifest Manifest(
        IEnumerable<SpriteState>? supported = null, IReadOnlyDictionary<SpriteState, SpriteState>? fallback = null,
        Size? box = null, double margin = 0.2, string id = "test.sprite") =>
        new(SpriteManifest.CurrentSchemaVersion, id, "測試", "我", "1.0.0", box ?? new Size(80, 80), margin, SpriteHitShape.Ellipse,
            new Point(0.5, 0), null, new HashSet<SpriteState>(supported ?? [SpriteState.Idle]), fallback ?? new Dictionary<SpriteState, SpriteState>());

    public static TheoryData<SpriteState> AllStates() => [.. Enum.GetValues<SpriteState>()];

    [Theory]
    [MemberData(nameof(AllStates))]
    public void WhenOnlyIdleIsSupported_EveryStateEndsAtIdle(SpriteState state)
    {
        Assert.Equal(SpriteState.Idle, SpriteStateResolver.Resolve(state, Manifest()));
    }

    [Theory]
    [MemberData(nameof(AllStates))]
    public void WhenEverythingIsSupported_EveryStateStaysAsIs(SpriteState state)
    {
        Assert.Equal(state, SpriteStateResolver.Resolve(state, Manifest(Enum.GetValues<SpriteState>())));
    }

    [Theory]
    [MemberData(nameof(AllStates))]
    public void EvenWithAnEmptySupportedSet_TheResultIsIdle(SpriteState state)
    {
        // 殼永遠可以退到 Idle：就算 manifest 忘了列 Idle
        var resolved = SpriteStateResolver.Resolve(state, Manifest(supported: []));

        Assert.Equal(SpriteState.Idle, resolved);
    }

    [Fact]
    public void ChainsFallbacks_TranscribingToListeningToIdle()
    {
        Assert.Equal(SpriteState.Listening, SpriteStateResolver.Resolve(SpriteState.Transcribing, Manifest([SpriteState.Idle, SpriteState.Listening])));
        Assert.Equal(SpriteState.Idle, SpriteStateResolver.Resolve(SpriteState.Transcribing, Manifest()));
    }

    [Fact]
    public void DefaultRules_ToolFallsBackToThinking_AttentionToDone()
    {
        Assert.Equal(SpriteState.Thinking, SpriteStateResolver.Resolve(SpriteState.Tool, Manifest([SpriteState.Idle, SpriteState.Thinking])));
        Assert.Equal(SpriteState.Done, SpriteStateResolver.Resolve(SpriteState.Attention, Manifest([SpriteState.Idle, SpriteState.Done])));
    }

    [Fact]
    public void ManifestFallback_TakesPrecedenceOverTheDefaultRule()
    {
        var manifest = Manifest(
            [SpriteState.Idle, SpriteState.Thinking, SpriteState.Streaming],
            new Dictionary<SpriteState, SpriteState> { [SpriteState.Tool] = SpriteState.Streaming });

        Assert.Equal(SpriteState.Streaming, SpriteStateResolver.Resolve(SpriteState.Tool, manifest));   // 預設會是 Thinking
    }

    [Fact]
    public void ManifestFallback_CanBeChained()
    {
        var manifest = Manifest(
            [SpriteState.Idle, SpriteState.Done],
            new Dictionary<SpriteState, SpriteState>
            {
                [SpriteState.Error] = SpriteState.Attention,
                [SpriteState.Attention] = SpriteState.Done,
            });

        Assert.Equal(SpriteState.Done, SpriteStateResolver.Resolve(SpriteState.Error, manifest));
    }

    [Fact]
    public void CyclicFallbacks_StopAtIdle()
    {
        var manifest = Manifest(
            [SpriteState.Idle],
            new Dictionary<SpriteState, SpriteState>
            {
                [SpriteState.Tool] = SpriteState.Streaming,
                [SpriteState.Streaming] = SpriteState.Thinking,
                [SpriteState.Thinking] = SpriteState.Tool,
            });

        Assert.Equal(SpriteState.Idle, SpriteStateResolver.Resolve(SpriteState.Tool, manifest));
    }

    [Fact]
    public void SelfReferencingFallback_StopsAtIdle()
    {
        var manifest = Manifest([SpriteState.Idle], new Dictionary<SpriteState, SpriteState> { [SpriteState.Error] = SpriteState.Error });

        Assert.Equal(SpriteState.Idle, SpriteStateResolver.Resolve(SpriteState.Error, manifest));
    }

    [Fact]
    public void DefaultFallbackTable_EveryChainReachesIdle()
    {
        foreach (var state in Enum.GetValues<SpriteState>())
        {
            var seen = new HashSet<SpriteState>();
            var current = state;
            while (current != SpriteState.Idle)
            {
                Assert.True(seen.Add(current), $"{state} 的預設退化有循環");
                Assert.True(SpriteStateResolver.DefaultFallback.TryGetValue(current, out current), $"{state} 的退化鏈斷了");
            }
        }
    }
}
