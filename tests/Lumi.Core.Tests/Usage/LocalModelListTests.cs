using Lumi.Core.Usage;

namespace Lumi.Core.Tests.Usage;

/// <summary>地端模型名單：模型 ID 的關鍵字比對（Gateway 後面的地端模型從網址看不出來）。</summary>
public sealed class LocalModelListTests
{
    [Theory]
    [InlineData("qwen")]
    [InlineData("Qwen2.5-72B-Instruct")]
    [InlineData("Qwen/Qwen2.5-72B-Instruct")]
    [InlineData("qwen2.5:7b")]
    [InlineData("qwen3-coder-30b-a3b")]
    [InlineData("qwq-32b")]
    [InlineData("llama3")]
    [InlineData("llama-3.1-70b-instruct")]
    [InlineData("meta-llama/Llama-3.3-70B")]
    [InlineData("codellama:13b")]
    [InlineData("gemma-2-9b-it")]
    [InlineData("gemma3:27b")]
    [InlineData("phi4")]
    [InlineData("phi-3-mini")]
    [InlineData("mistral-7b-instruct")]
    [InlineData("mixtral-8x7b")]
    [InlineData("deepseek-r1:14b")]
    [InlineData("DeepSeek-V3")]
    [InlineData("gpt-oss-20b")]
    [InlineData("openai/gpt-oss-120b")]
    [InlineData("hf.co/bartowski/Qwen2.5-7B-GGUF")]
    [InlineData("glm-4-9b-chat")]
    public void LocalFamilies_AreRecognized(string modelId) => Assert.True(LocalModelList.IsLocalModel(modelId));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("claude-sonnet-4-5")]
    [InlineData("claude-opus-5-5")]
    [InlineData("gpt-4o")]
    [InlineData("gpt-4.1-mini")]
    [InlineData("gpt-5")]
    [InlineData("o3-mini")]
    [InlineData("gemini-2.5-pro")]
    [InlineData("text-embedding-3-small")]
    [InlineData("gpt-image-1")]
    [InlineData("whisper-1")]
    [InlineData("phishing-detector")]       // 詞要完全相同，或關鍵字後接數字：phishing 不算 phi
    [InlineData("yield-model")]             // yield 不算 yi
    [InlineData("gpt-ossify")]
    [InlineData("my-private-model")]
    public void CloudAndUnrelatedNames_AreNot(string? modelId) => Assert.False(LocalModelList.IsLocalModel(modelId));

    [Fact]
    public void Null_IsNotLocal() => Assert.False(LocalModelList.IsLocalModel(null));

    [Fact]
    public void KeywordList_HasNoEmptyOrDuplicateEntries()
    {
        Assert.All(LocalModelList.Keywords, k => Assert.False(string.IsNullOrWhiteSpace(k)));
        Assert.Equal(LocalModelList.Keywords.Count, LocalModelList.Keywords.Select(k => k.ToLowerInvariant()).Distinct().Count());
    }
}
