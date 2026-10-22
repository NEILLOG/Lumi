using Lumi.Core.Speech;
using Microsoft.Extensions.DependencyInjection;

namespace Lumi.Speech;

public static class SpeechServiceCollectionExtensions
{
    /// <summary>註冊語音服務（<see cref="ISpeechToText"/>、<see cref="ISpeechConnectionTester"/>）。</summary>
    public static IServiceCollection AddLumiSpeech(this IServiceCollection services)
    {
        // 同 AddLumiProviders：認證標頭只設在每次請求上，HttpClientFactory 的 log 不會看到 Token。
        // 逾時由每次請求自己的 CancellationTokenSource 管（才分得出「本機逾時」與「使用者取消」），HttpClient 本身不設逾時。
        services.AddHttpClient<WhisperSpeechToText>(c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient<WhisperConnectionTester>(c => c.Timeout = Timeout.InfiniteTimeSpan);

        services.AddTransient<ISpeechToText>(sp => sp.GetRequiredService<WhisperSpeechToText>());
        services.AddTransient<ISpeechConnectionTester>(sp => sp.GetRequiredService<WhisperConnectionTester>());
        return services;
    }
}
