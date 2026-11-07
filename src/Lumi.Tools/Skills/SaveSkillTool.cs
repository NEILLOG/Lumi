using System.ComponentModel;
using Lumi.Core.Skills;
using Lumi.Core.Tools;

namespace Lumi.Tools.Skills;

public sealed record SaveSkillArgs(
    [property: Description("技能名稱（1～64 字，不能有 / \\ : * ? \" < > | 等符號）；和既有技能同名就是取代它")] string Name,
    [property: Description("什麼時候用這個技能，一兩句話，最多 1024 字")] string Description,
    [property: Description("步驟全文：之後別的對話也能照做的通用流程，最多 20000 字")] string Instructions);

/// <summary>
/// 把一套做事方法存成技能（M29 5.6）。<c>Write</c>、只在工作台、<c>AlwaysAsk</c>、不能設「永遠允許」：技能會每次對話都被讀到，
/// 等於永久寫入指示（和 <c>memory_save</c> 同理）。確認卡由 <see cref="Describe"/> 產生（<see cref="SkillProposal"/>）。
/// </summary>
public sealed class SaveSkillTool(ISkillStore skills) : ToolBase<SaveSkillArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "save_skill", "儲存技能",
        "只在使用者要求把做法存成技能時使用（例如「把剛才的流程存成技能」）。"
        + "步驟要寫成之後別的對話也能照做的通用流程，不要放這次的具體資料、機密與個資。"
        + "同名的技能會被取代，要先跟使用者確認。"
        + "記住關於使用者的事實請用 memory_save，不要用這個工具。"
        + "每次呼叫都需要使用者確認；被拒絕就不要再重試同一套內容。",
        ToolPermission.Write, ToolModes.Workbench, TimeSpan.FromSeconds(10), Group: ToolGroups.Skills,
        Summary: "把一套做事方法存成技能。", CanAlwaysAllow: false, AlwaysAsk: true);

    protected override ToolPreview Describe(SaveSkillArgs args, ToolContext context)
    {
        var name = SkillRules.NormalizeName(args.Name);
        var description = SkillRules.NormalizeDescription(args.Description);
        var body = SkillRules.NormalizeBody(args.Instructions);
        var target = name.Length == 0 ? null : name;

        // 參數不合規則：不給 Skill（確認卡只顯示名稱），執行時回錯誤
        if (SkillRules.Validate(new SkillDraft(name, description, body)) is not null)
            return new ToolPreview(target, null);

        var replaces = ExistsSync(name);
        return new ToolPreview(name, null, Skill: new SkillProposal(name, description, body, replaces));
    }

    protected override async Task<ToolResult> InvokeAsync(SaveSkillArgs args, ToolContext context, CancellationToken ct)
    {
        var name = SkillRules.NormalizeName(args.Name);
        var description = SkillRules.NormalizeDescription(args.Description);
        var body = SkillRules.NormalizeBody(args.Instructions);
        if (SkillRules.Validate(new SkillDraft(name, description, body)) is { } problem)
            return ToolResult.Error(problem, "內容不符合規則");

        try
        {
            // 同名就是取代：用 OriginalName 修改，附帶檔案保留（確認卡已經讓使用者看過這個取代）
            var replaces = (await skills.ListAsync(ct)).Any(s => SkillRules.NameComparer.Equals(s.Name, name));
            var draft = new SkillDraft(name, description, body, replaces ? name : null);
            var result = await skills.SaveAsync(draft, ct);

            switch (result.Outcome)
            {
                case SkillSaveOutcome.Saved:
                    return ToolResult.Ok($"已存成技能「{name}」，之後開新對話就會用到。", $"已儲存「{name}」");

                case SkillSaveOutcome.SavedDisabled:
                    return ToolResult.Ok(
                        $"已存成技能「{name}」，之後開新對話就會用到。{result.Message}", $"已儲存「{name}」（先停用）");

                default:
                    return ToolResult.Error(result.Message ?? "無法儲存這個技能。", "無法儲存技能");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 技能儲存層的錯誤只回給模型一句固定的話，細節不外洩
            return ToolResult.Error("無法儲存這個技能。", "無法儲存技能");
        }
    }

    /// <summary>
    /// 同步版的「同名是否存在」，給 <see cref="Describe"/> 用（它是同步方法，在對話迴圈的背景執行緒呼叫；store 有快取）。
    /// 丟例外時當作不存在。
    /// </summary>
    private bool ExistsSync(string name)
    {
        try
        {
            return skills.ListAsync(CancellationToken.None).GetAwaiter().GetResult()
                .Any(s => SkillRules.NameComparer.Equals(s.Name, name));
        }
        catch (Exception)
        {
            return false;
        }
    }
}
