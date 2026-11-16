using Lumi.App.ViewModels;

namespace Lumi.App.Tests.TestSupport;

/// <summary>研究本啟動對話框的假實作（M35）：回 <see cref="Answer"/>（null＝取消），並記下每次的參數。</summary>
internal sealed class FakeContextNoteStartPrompt : IContextNoteStartPrompt
{
    public ContextNoteStartRequest? Answer { get; set; }

    public List<(string SuggestedGoal, int MessageCount, string? ModelText)> Prompts { get; } = [];

    public Task<ContextNoteStartRequest?> PromptAsync(string suggestedGoal, int messageCount, string? modelText)
    {
        Prompts.Add((suggestedGoal, messageCount, modelText));
        return Task.FromResult(Answer);
    }
}
