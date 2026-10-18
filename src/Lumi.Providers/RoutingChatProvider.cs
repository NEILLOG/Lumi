using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Providers.Anthropic;
using Lumi.Providers.OpenAICompatible;

namespace Lumi.Providers;

/// <summary>依請求的連線格式轉給對應的 Provider。UI 與對話流程只認得 <see cref="IChatProvider"/>。</summary>
public sealed class RoutingChatProvider : IChatProvider
{
    private readonly AnthropicChatProvider _anthropic;
    private readonly OpenAICompatibleChatProvider _openAI;

    public RoutingChatProvider(AnthropicChatProvider anthropic, OpenAICompatibleChatProvider openAI)
    {
        _anthropic = anthropic;
        _openAI = openAI;
    }

    public IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, CancellationToken ct = default) =>
        request.Model.Connection.Format == ProviderFormat.Anthropic
            ? _anthropic.StreamAsync(request, ct)
            : _openAI.StreamAsync(request, ct);
}
