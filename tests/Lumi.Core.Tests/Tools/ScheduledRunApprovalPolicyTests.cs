using Lumi.Core.Reminders;
using Lumi.Core.Tools;

namespace Lumi.Core.Tests.Tools;

public class ScheduledRunApprovalPolicyTests
{
    private static readonly ScheduleGrant WriteOnly = new(Write: true, Network: false, Destructive: false);
    private static readonly ScheduleGrant Everything = new(Write: true, Network: true, Destructive: true);

    private static ToolDescriptor Desc(ToolPermission permission, string group = ToolGroups.General, bool requiresMemory = false,
        bool canAlwaysAllow = true, bool alwaysAsk = false) =>
        new("tool", "測試", "測試用。", permission, ToolModes.Both, TimeSpan.FromSeconds(5),
            RequiresMemory: requiresMemory, Group: group, CanAlwaysAllow: canAlwaysAllow, AlwaysAsk: alwaysAsk);

    private static ToolApprovalRequest Req(ToolPermission permission, bool allowRemember = true, params string[] warnings) =>
        new("call", "tool", "測試", permission, ToolPreview.None, "{}") { AllowRemember = allowRemember, Warnings = warnings };

    [Fact]
    public void Permission_inside_grant_is_allowed()
    {
        Assert.Equal(ScheduledRunVerdict.Allow,
            ScheduledRunApprovalPolicy.Decide(Desc(ToolPermission.Write), Req(ToolPermission.Write), WriteOnly));
    }

    [Fact]
    public void Permission_outside_grant_asks()
    {
        Assert.Equal(ScheduledRunVerdict.Ask,
            ScheduledRunApprovalPolicy.Decide(Desc(ToolPermission.Network), Req(ToolPermission.Network), WriteOnly));
        Assert.Equal(ScheduledRunVerdict.Ask,
            ScheduledRunApprovalPolicy.Decide(Desc(ToolPermission.Destructive), Req(ToolPermission.Destructive), WriteOnly));
    }

    [Fact]
    public void Read_only_is_always_allowed_without_any_grant()
    {
        Assert.Equal(ScheduledRunVerdict.Allow,
            ScheduledRunApprovalPolicy.Decide(Desc(ToolPermission.ReadOnly), Req(ToolPermission.ReadOnly), ScheduleGrant.None));
    }

    [Fact]
    public void Memory_tools_ask_even_with_everything_granted()
    {
        var descriptor = Desc(ToolPermission.Write, requiresMemory: true);
        Assert.Equal(ScheduledRunVerdict.Ask, ScheduledRunApprovalPolicy.Decide(descriptor, Req(ToolPermission.Write), Everything));
    }

    [Fact]
    public void Tools_that_cannot_always_allow_ask_even_with_everything_granted()
    {
        var descriptor = Desc(ToolPermission.Write, canAlwaysAllow: false);   // browser_wait_for_user 這類
        Assert.Equal(ScheduledRunVerdict.Ask, ScheduledRunApprovalPolicy.Decide(descriptor, Req(ToolPermission.Write), Everything));
    }

    [Theory]
    [InlineData(ToolPermission.Write)]
    [InlineData(ToolPermission.Destructive)]
    public void Reminder_management_asks_even_with_everything_granted(ToolPermission permission)
    {
        // cancel_reminder、set_reminder_enabled 這類排程管理工具
        var descriptor = Desc(permission, group: ToolGroups.Reminders);
        Assert.Equal(ScheduledRunVerdict.Ask, ScheduledRunApprovalPolicy.Decide(descriptor, Req(permission), Everything));
    }

    [Fact]
    public void Url_warning_asks_even_when_network_is_granted()
    {
        var descriptor = Desc(ToolPermission.Network);
        var request = Req(ToolPermission.Network, warnings: "沒出現過的網站");
        Assert.Equal(ScheduledRunVerdict.Ask, ScheduledRunApprovalPolicy.Decide(descriptor, request, Everything));
    }

    [Fact]
    public void Requests_that_cannot_be_remembered_ask()
    {
        var descriptor = Desc(ToolPermission.Network);
        Assert.Equal(ScheduledRunVerdict.Ask,
            ScheduledRunApprovalPolicy.Decide(descriptor, Req(ToolPermission.Network, allowRemember: false), Everything));
    }

    [Fact]
    public void Always_ask_tools_ask_even_with_everything_granted()
    {
        var descriptor = Desc(ToolPermission.Write, alwaysAsk: true);
        Assert.Equal(ScheduledRunVerdict.Ask, ScheduledRunApprovalPolicy.Decide(descriptor, Req(ToolPermission.Write), Everything));
    }

    [Fact]
    public void Exclusion_flags_match_the_rules()
    {
        Assert.True(ScheduledRunApprovalPolicy.IsExcluded(Desc(ToolPermission.Write, requiresMemory: true), Req(ToolPermission.Write)));
        Assert.True(ScheduledRunApprovalPolicy.IsExcluded(Desc(ToolPermission.Write, group: ToolGroups.Reminders), Req(ToolPermission.Write)));
        Assert.True(ScheduledRunApprovalPolicy.IsExcluded(Desc(ToolPermission.Write, alwaysAsk: true), Req(ToolPermission.Write)));
        Assert.False(ScheduledRunApprovalPolicy.IsExcluded(Desc(ToolPermission.Write), Req(ToolPermission.Write)));
    }
}
