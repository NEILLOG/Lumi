using System.Runtime.CompilerServices;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Providers.Anthropic;

namespace Lumi.Providers;

/// <summary>依請求的連線格式轉給對應的 Provider。UI 與對話流程只認得 <see cref="IChatProvider"/>。</summary>
public sealed class RoutingChatProvider : IChatProvider
{
    private readonly AnthropicChatProvider _anthropic;

    public RoutingChatProvider(AnthropicChatProvider anthropic)
    {
        _anthropic = anthropic;
    }

    public IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, CancellationToken ct = default) =>
        request.Model.Connection.Format switch
        {
            ProviderFormat.Anthropic => _anthropic.StreamAsync(request, ct),
            _ => NotSupportedAsync(request.Model.Connection.Format, ct),
        };

    // TODO(2B)：OpenAI 相容 Provider
    private static async IAsyncEnumerable<ChatEvent> NotSupportedAsync(ProviderFormat format, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        ct.ThrowIfCancellationRequested();
        yield return new Failed(new ChatFailure(ChatFailureKind.Unknown, $"還不支援 {format} 格式的連線。", Retryable: false));
    }
}
