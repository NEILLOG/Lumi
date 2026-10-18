namespace Lumi.Core.Tools;

public sealed class ToolLoopOptions
{
    /// <summary>一輪回應最多呼叫幾次供應商（agent.md 第 7 節：工具呼叫迴圈要有最大次數限制，預設 10）。</summary>
    public int MaxIterations { get; set; } = 10;

    /// <summary>單一工具結果送給模型的字元上限；超過就截斷並附註。</summary>
    public int MaxResultChars { get; set; } = 20_000;
}
