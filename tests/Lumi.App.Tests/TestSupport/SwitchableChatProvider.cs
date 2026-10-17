using Lumi.Core.Chat;

namespace Lumi.App.Tests.TestSupport;

/// <summary>可以中途換掉實際回應的 Provider（模擬「存好金鑰後就成功了」）。</summary>
internal sealed class SwitchableChatProvider(IChatProvider initial) : IChatProvider
{
    public IChatProvider Current { get; set; } = initial;

    public IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, CancellationToken ct = default) =>
        Current.StreamAsync(request, ct);
}
