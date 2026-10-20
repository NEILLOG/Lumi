namespace Lumi.Core.Prompts;

/// <summary>依 <see cref="IPromptLayer.Order"/> 組合各層：system 層以空行串接，尾端層串接後包進 <c>&lt;lumi_mode&gt;</c>。</summary>
public sealed class PromptComposer : IPromptComposer
{
    public const string SuffixTag = "lumi_mode";

    private readonly IReadOnlyList<IPromptLayer> _layers;

    /// <summary>順序重複時丟例外：寧可啟動就失敗。</summary>
    public PromptComposer(IEnumerable<IPromptLayer> layers)
    {
        _layers = layers.OrderBy(l => l.Order).ToList();
        var duplicate = _layers.GroupBy(l => l.Order).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Prompt 層的順序 {duplicate.Key} 重複：{string.Join("、", duplicate.Select(l => l.GetType().Name))}。");
    }

    public ComposedPrompt Compose(PromptContext context)
    {
        var system = Join(PromptPlacement.System, context);
        var suffix = Join(PromptPlacement.TurnSuffix, context);
        return new ComposedPrompt(system, suffix is null ? null : $"<{SuffixTag}>\n{suffix}\n</{SuffixTag}>");
    }

    private string? Join(PromptPlacement placement, PromptContext context)
    {
        var parts = _layers
            .Where(l => l.Placement == placement)
            .Select(l => l.Render(context)?.Trim())
            .Where(text => !string.IsNullOrEmpty(text))
            .ToList();
        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }
}
