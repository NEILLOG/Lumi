using Lumi.App.Resources;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Usage;

namespace Lumi.App.Services;

/// <summary>提示按鈕的動作鍵（<see cref="UsageHintCoordinator.TryShowAsync"/> 的 runAction 收到的字串）。</summary>
public static class UsageHintActions
{
    public const string NewConversation = "new-conversation";
    public const string CompactAndContinue = "compact-and-continue";
    public const string ManageTools = "manage-tools";

    /// <summary>「繼續」：只記為已處理，沒有動作。</summary>
    public const string Continue = "continue";
}

/// <summary>換模型確認區的提示（第 1 條）：紀錄 id 與確認區的文字。</summary>
public sealed record ModelSwitchPrompt(string EntryId, string Text);

/// <summary>
/// 使用習慣提示的決定與紀錄（plan/23-usage-cost.md 第 7 節）：依 <see cref="UsageHintIds.All"/> 的優先順序挑出第一個可以顯示的，
/// 其餘寫為 Suppressed；靜音、同對話只一次、第 4 條同檔案只一次、第 6 條 7 天限制都在這裡判斷。
/// 小精靈模式只記錄，不顯示。
/// </summary>
public sealed class UsageHintCoordinator
{
    /// <summary>規則的優先順序（<see cref="UsageHintIds.All"/> 的位置，小的先）。</summary>
    private static readonly IReadOnlyDictionary<string, int> Priority =
        UsageHintIds.All.Index().ToDictionary(p => p.Item, p => p.Index, StringComparer.Ordinal);

    private readonly IUsageHintLog _log;
    private readonly IModelCatalog _catalog;
    private readonly TimeProvider _time;

    /// <summary>第 4 條：這次執行期間已經提示過的「對話 ＋ 規則 ＋ 檔名」（重開 Lumi 後可能重複提示，可接受）。</summary>
    private readonly HashSet<string> _shownKeys = new(StringComparer.Ordinal);

    public UsageHintCoordinator(IUsageHintLog log, IModelCatalog catalog, TimeProvider time)
    {
        _log = log;
        _catalog = catalog;
        _time = time;
    }

    /// <summary>
    /// 回應結束或聚焦輸入框時呼叫。<paramref name="runAction"/> 收到按下的動作按鈕對應的 <see cref="UsageHintActions"/> 字串。
    /// 回傳是否真的顯示了一則。
    /// </summary>
    public async Task<bool> TryShowAsync(string conversationId, IReadOnlyList<UsageHintMatch> matches, ChatMode mode,
        ComposerHintViewModel target, Func<string, Task>? runAction = null)
    {
        var now = _time.GetUtcNow();
        var ordered = matches
            .Where(m => Priority.ContainsKey(m.Rule))
            .OrderBy(m => Priority[m.Rule])
            .ToList();

        UsageHintMatch? chosen = null;
        foreach (var match in ordered)
        {
            if (chosen is null && mode != ChatMode.Sprite && await IsEligibleAsync(match, conversationId, now))
                chosen = match;
            else
                await _log.AddAsync(NewEntry(match.Rule, conversationId, now, UsageHintOutcome.Suppressed));
        }

        if (chosen is not { } shown)
            return false;

        var id = NewId();
        await _log.AddAsync(new UsageHintEntry(id, shown.Rule, conversationId, now, UsageHintOutcome.Shown));
        if (shown.Rule == UsageHintIds.BigFile)
            _shownKeys.Add(FileKey(conversationId, shown));

        await target.CloseAsync();   // 新的提示取代還在顯示的舊提示（舊的記為 Dismissed）
        target.Show(BuildContent(id, shown, runAction));
        return true;
    }

    /// <summary>
    /// 換模型時選了別的模型（第 1 條）：符合條件（上下文夠大、規則沒靜音、這則對話還沒提示過）就寫入 Shown 並回傳確認區的內容；否則回 null，直接換。
    /// </summary>
    public async Task<ModelSwitchPrompt?> BeginModelSwitchAsync(string conversationId, int contextTokens)
    {
        if (contextTokens < UsageHintThresholds.ModelSwitchContextTokens || IsMuted(UsageHintIds.ModelSwitch))
            return null;
        if (await _log.WasShownAsync(UsageHintIds.ModelSwitch, conversationId))
            return null;

        var now = _time.GetUtcNow();
        var id = NewId();
        await _log.AddAsync(new UsageHintEntry(id, UsageHintIds.ModelSwitch, conversationId, now, UsageHintOutcome.Shown));
        var text = TextFor(new UsageHintMatch(UsageHintIds.ModelSwitch, [MoneyFormat.Tokens(contextTokens)]));
        return new ModelSwitchPrompt(id, text);
    }

    /// <summary>換模型確認區的按鈕：寫入結果（Acted 或 Dismissed）。</summary>
    public Task ResolveModelSwitchAsync(string entryId, UsageHintOutcome outcome) => _log.SetOutcomeAsync(entryId, outcome);

    private bool IsMuted(string rule) => _catalog.Current.Usage.Muted.Contains(rule, StringComparer.Ordinal);

    private async Task<bool> IsEligibleAsync(UsageHintMatch match, string conversationId, DateTimeOffset now)
    {
        if (IsMuted(match.Rule))
            return false;

        switch (match.Rule)
        {
            case UsageHintIds.StaleResume:
                // 第 3 條不受「這則對話只一次」限制；每次打開只檢查一次由輸入框的聚焦處理決定
                return true;

            case UsageHintIds.BigFile:
                return !_shownKeys.Contains(FileKey(conversationId, match));

            case UsageHintIds.McpTools:
            {
                // 每 7 天最多一次（跨對話）
                var last = await _log.LastShownAsync(match.Rule);
                return last is null || now - last.Value >= UsageHintThresholds.McpToolsInterval;
            }

            default:
                return !await _log.WasShownAsync(match.Rule, conversationId);
        }
    }

    private ComposerHintContent BuildContent(string id, UsageHintMatch match, Func<string, Task>? runAction)
    {
        var (primaryText, primaryAction, secondaryText, secondaryAction) = ButtonsFor(match.Rule);
        return new ComposerHintContent(
            Text: TextFor(match),
            PrimaryText: primaryText,
            Primary: primaryAction is null ? null : () => ActAsync(id, primaryAction, runAction),
            SecondaryText: secondaryText,
            Secondary: secondaryAction is null ? null : () => ActAsync(id, secondaryAction, runAction),
            Dismiss: () => _log.SetOutcomeAsync(id, UsageHintOutcome.Dismissed),
            Mute: () => MuteAsync(id, match.Rule));
    }

    private async Task ActAsync(string id, string action, Func<string, Task>? runAction)
    {
        await _log.SetOutcomeAsync(id, UsageHintOutcome.Acted);
        if (runAction is not null)
            await runAction(action);
    }

    private async Task MuteAsync(string id, string rule)
    {
        await _log.SetOutcomeAsync(id, UsageHintOutcome.Muted);
        var usage = _catalog.Current.Usage;
        await _catalog.SetUsageAsync(usage with { MutedHints = [.. usage.Muted.Where(r => r != rule), rule] });
    }

    /// <summary>各條規則的按鈕（plan/23 第 7.2 節的「動作按鈕」欄與備註）。</summary>
    private static (string? PrimaryText, string? PrimaryAction, string? SecondaryText, string? SecondaryAction) ButtonsFor(string rule) => rule switch
    {
        UsageHintIds.LongConversation => (Strings.Hint_NewConversation, UsageHintActions.NewConversation, null, null),
        UsageHintIds.StaleResume => (Strings.Hint_NewConversation, UsageHintActions.NewConversation, Strings.Hint_Continue, UsageHintActions.Continue),
        UsageHintIds.BigFile or UsageHintIds.Images => (Strings.Usage_CompactAndContinue, UsageHintActions.CompactAndContinue, Strings.Hint_NewConversation, UsageHintActions.NewConversation),
        UsageHintIds.McpTools => (Strings.Hint_ManageTools, UsageHintActions.ManageTools, null, null),
        _ => (null, null, null, null),
    };

    /// <summary>依規則與 <see cref="UsageHintMatch.Args"/> 組出提示文字（plan/23 第 7.2 節的提示文字欄）。</summary>
    private static string TextFor(UsageHintMatch match) => match.Rule switch
    {
        UsageHintIds.ModelSwitch => string.Format(Strings.Hint_ModelSwitchFormat, Arg(match, 0)),
        UsageHintIds.LongConversation => Strings.Hint_LongConversation,
        UsageHintIds.StaleResume => string.Format(Strings.Hint_StaleResumeFormat, ElapsedText(Arg(match, 0)), Arg(match, 1)),
        UsageHintIds.BigFile => string.Format(Strings.Hint_BigFileFormat, Arg(match, 0), Arg(match, 1)),
        UsageHintIds.Images => string.Format(Strings.Hint_ImagesFormat, Arg(match, 0)),
        UsageHintIds.McpTools => string.Format(Strings.Hint_McpToolsFormat, Arg(match, 0)),
        UsageHintIds.Regenerate => string.Format(Strings.Hint_RegenerateFormat, Arg(match, 0)),
        UsageHintIds.LongOutput => Strings.Hint_LongOutput,
        UsageHintIds.RepeatPaste => string.Format(Strings.Hint_RepeatPasteFormat, Arg(match, 0)),
        _ => "",
    };

    /// <summary>第 3 條的經過時間代碼：<c>h:3</c> 是 3 小時，<c>d:2</c> 是 2 天。</summary>
    private static string ElapsedText(string code) =>
        code.StartsWith("d:", StringComparison.Ordinal)
            ? string.Format(Strings.Hint_ElapsedDaysFormat, code[2..])
            : string.Format(Strings.Hint_ElapsedHoursFormat, code[2..]);

    private static string Arg(UsageHintMatch match, int index) => index < match.Args.Count ? match.Args[index] : "";

    private static string FileKey(string conversationId, UsageHintMatch match) => conversationId + "\n" + match.Rule + ":" + match.Key;

    private static UsageHintEntry NewEntry(string rule, string conversationId, DateTimeOffset now, UsageHintOutcome outcome) =>
        new(NewId(), rule, conversationId, now, outcome);

    private static string NewId() => Guid.CreateVersion7().ToString();
}
