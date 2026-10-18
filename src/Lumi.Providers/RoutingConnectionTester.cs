using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Lumi.Providers.Anthropic;
using Lumi.Providers.OpenAICompatible;

namespace Lumi.Providers;

public sealed class RoutingConnectionTester : IConnectionTester
{
    private readonly AnthropicConnectionTester _anthropic;
    private readonly OpenAICompatibleConnectionTester _openAI;

    public RoutingConnectionTester(AnthropicConnectionTester anthropic, OpenAICompatibleConnectionTester openAI)
    {
        _anthropic = anthropic;
        _openAI = openAI;
    }

    public Task<ConnectionTestResult> TestAsync(ConnectionSettings connection, SecretValue? apiKey, CancellationToken ct = default) =>
        connection.Format == ProviderFormat.Anthropic
            ? _anthropic.TestAsync(connection, apiKey, ct)
            : _openAI.TestAsync(connection, apiKey, ct);
}
