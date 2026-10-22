using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Lumi.Core.Speech;
using Lumi.Speech.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace Lumi.Speech.Tests;

public class SpeechServiceCollectionTests
{
    [Fact]
    public void AddLumiSpeech_resolves_both_services()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IModelCatalog>(new FakeSpeechCatalog(SpeechSettings.Default));
        services.AddSingleton<ISecretStore>(new InMemorySecrets());
        services.AddLumiSpeech();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.IsType<WhisperSpeechToText>(provider.GetRequiredService<ISpeechToText>());
        Assert.IsType<WhisperConnectionTester>(provider.GetRequiredService<ISpeechConnectionTester>());
    }
}
