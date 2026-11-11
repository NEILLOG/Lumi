using System.Text.Json.Nodes;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests.Tools;

/// <summary>可用性（<see cref="IConditionalTool"/>）在工具目錄與對話迴圈的行為。</summary>
public class ConditionalToolTests
{
    /// <summary>實作 <see cref="IConditionalTool"/> 的假工具：可以設定回傳的可用性。</summary>
    private sealed class ConditionalFakeTool : ToolBase<EchoArgs>, IConditionalTool
    {
        public ConditionalFakeTool(string name, ToolAvailability availability, string group = ToolGroups.Outlook, string? optIn = null)
        {
            Descriptor = new ToolDescriptor(name, name, "測試用條件工具。", ToolPermission.ReadOnly, ToolModes.Both, TimeSpan.FromSeconds(5),
                Group: group, OptInWarning: optIn);
            Availability = availability;
        }

        public override ToolDescriptor Descriptor { get; }

        public ToolAvailability Availability { get; set; }

        public ToolAvailability CheckAvailability() => Availability;

        protected override Task<ToolResult> InvokeAsync(EchoArgs args, ToolContext context, CancellationToken ct) =>
            Task.FromResult(ToolResult.Ok(args.Text));
    }

    /// <summary>可以指定預覽的假寫入工具（對話迴圈的警告用）。</summary>
    private sealed class PreviewTool(ToolDescriptor descriptor, ToolPreview preview) : ToolBase<EchoArgs>
    {
        public override ToolDescriptor Descriptor => descriptor;

        protected override ToolPreview Describe(EchoArgs args, ToolContext context) => preview;

        protected override Task<ToolResult> InvokeAsync(EchoArgs args, ToolContext context, CancellationToken ct) =>
            Task.FromResult(ToolResult.Ok(args.Text));
    }

    private static readonly ToolAvailability NotHere = new(ToolAvailabilityKind.NotOnThisPlatform, "這台電腦沒有。");
    private static readonly ToolAvailability Broken = new(ToolAvailabilityKind.Unavailable, "還沒有開啟。");

    private static FakeModelCatalog WithTools(ToolSettings tools) =>
        new() { Current = new FakeModelCatalog().Current with { Tools = tools } };

    private static async Task<List<ChatEvent>> Collect(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events)
            list.Add(e);
        return list;
    }

    private static ToolUseBlock Use(string id, string name) => new(id, name, """{"text":"嗨"}""");

    private sealed class Rig
    {
        public InMemoryConversationStore Store { get; } = new();
        public Conversation Conversation { get; private set; } = null!;
        public ConversationService Service { get; private set; } = null!;
        public ScriptedApprovalService Approvals { get; } = new(ApprovalDecision.AllowOnce, ApprovalDecision.AllowOnce, ApprovalDecision.AllowOnce);

        public static async Task<Rig> CreateAsync(IChatProvider provider, IEnumerable<ITool> tools, FakeModelCatalog catalog)
        {
            var rig = new Rig();
            rig.Conversation = await rig.Store.CreateAsync("新對話");
            rig.Service = new ConversationService(provider, rig.Store, catalog, new ToolRegistry(tools),
                rig.Approvals, new ToolPermissionMemory(), new ToolLoopOptions(),
                TimeProvider.System, NullLogger<ConversationService>.Instance);
            return rig;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unavailable_tool_is_not_offered(bool withOptIn)
    {
        // 有沒有 OptInWarning 都一樣：不可用的工具不帶定義（開關在工具清單頁停用）
        var optIn = withOptIn ? "確認" : null;
        var settings = withOptIn ? new ToolSettings { Enabled = ["broken"] } : ToolSettings.Default;
        var registry = new ToolRegistry([new ConditionalFakeTool("broken", Broken, optIn: optIn), new FakeTool("alpha")]);

        var enabled = await new ToolCatalog(registry, WithTools(settings)).GetEnabledForNewConversationAsync(CancellationToken.None);

        Assert.Equal(["alpha"], enabled.Select(t => t.Descriptor.Name));
    }

    [Fact]
    public async Task Not_on_platform_tool_is_not_offered()
    {
        var registry = new ToolRegistry([new ConditionalFakeTool("elsewhere", NotHere), new FakeTool("alpha")]);

        var enabled = await new ToolCatalog(registry, WithTools(ToolSettings.Default)).GetEnabledForNewConversationAsync(CancellationToken.None);

        Assert.Equal(["alpha"], enabled.Select(t => t.Descriptor.Name));
    }

    [Fact]
    public async Task Available_conditional_tool_is_offered()
    {
        var registry = new ToolRegistry([new ConditionalFakeTool("ready", ToolAvailability.Yes), new FakeTool("alpha")]);

        var enabled = await new ToolCatalog(registry, WithTools(ToolSettings.Default)).GetEnabledForNewConversationAsync(CancellationToken.None);

        Assert.Equal(["alpha", "ready"], enabled.Select(t => t.Descriptor.Name));
    }

    [Fact]
    public void Not_on_platform_group_is_hidden()
    {
        var registry = new ToolRegistry([new ConditionalFakeTool("elsewhere_a", NotHere), new ConditionalFakeTool("elsewhere_b", NotHere), new FakeTool("alpha")]);

        var groups = new ToolCatalog(registry, WithTools(ToolSettings.Default)).ListGroups();

        Assert.DoesNotContain(ToolGroups.Outlook, groups.Select(g => g.Group.Id));
        Assert.Contains(ToolGroups.General, groups.Select(g => g.Group.Id));
    }

    [Fact]
    public void Partially_available_group_is_listed()
    {
        var registry = new ToolRegistry([new ConditionalFakeTool("elsewhere", NotHere), new ConditionalFakeTool("ready", ToolAvailability.Yes)]);

        var group = Assert.Single(new ToolCatalog(registry, WithTools(ToolSettings.Default)).ListGroups(), g => g.Group.Id == ToolGroups.Outlook);

        Assert.Equal(["elsewhere", "ready"], group.Tools.Select(t => t.Descriptor.Name));
    }

    [Fact]
    public void Unavailable_group_is_still_listed()
    {
        var registry = new ToolRegistry([new ConditionalFakeTool("broken_a", Broken), new ConditionalFakeTool("broken_b", Broken)]);

        var group = Assert.Single(new ToolCatalog(registry, WithTools(ToolSettings.Default)).ListGroups(), g => g.Group.Id == ToolGroups.Outlook);

        Assert.Equal(["broken_a", "broken_b"], group.Tools.Select(t => t.Descriptor.Name));
    }

    [Fact]
    public async Task Preview_warnings_follow_external_warning()
    {
        // 這則對話讀過外部內容：先放外部內容警告，工具自己的警告接在後面
        var descriptor = new ToolDescriptor("warned", "警告", "測試用寫入工具。", ToolPermission.Write, ToolModes.Both, TimeSpan.FromSeconds(5),
            WarnsAfterExternalContent: true);
        var tool = new PreviewTool(descriptor, new ToolPreview("目標", null, Warnings: ["自訂警告"]));
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(Use("w1", "web_fetch")),
            SequenceChatProvider.ToolCalls(Use("p1", "warned")),
            SequenceChatProvider.Answer("好"));
        var rig = await Rig.CreateAsync(provider, [tool, new FakeTool("web_fetch")], WithTools(ToolSettings.Default));

        await Collect(rig.Service.SendAsync(rig.Conversation.Id, "嗨"));

        var request = Assert.Single(rig.Approvals.Requests);
        Assert.Equal([ExternalContentProvenance.Warning, "自訂警告"], request.Warnings);
    }

    [Fact]
    public async Task Preview_warnings_alone()
    {
        // 沒有外部內容：只有工具自己的警告
        var descriptor = new ToolDescriptor("warned", "警告", "測試用寫入工具。", ToolPermission.Write, ToolModes.Both, TimeSpan.FromSeconds(5));
        var tool = new PreviewTool(descriptor, new ToolPreview("目標", null, Warnings: ["自訂警告"]));
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("p1", "warned")), SequenceChatProvider.Answer("好"));
        var rig = await Rig.CreateAsync(provider, [tool], WithTools(ToolSettings.Default));

        await Collect(rig.Service.SendAsync(rig.Conversation.Id, "嗨"));

        var request = Assert.Single(rig.Approvals.Requests);
        Assert.Equal(["自訂警告"], request.Warnings);
    }
}
