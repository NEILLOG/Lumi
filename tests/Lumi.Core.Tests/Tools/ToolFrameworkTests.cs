using System.ComponentModel;
using System.Text.Json.Nodes;
using Lumi.Core.Chat;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Lumi.Core.Tests.Tools;

public class ToolFrameworkTests
{
    private static readonly ToolContext Context = new("conv", ChatMode.Workbench, TimeProvider.System, EmptyFileScope.Instance);

    // ---- Schema ----

    [Fact]
    public void Schema_IsObject_WithSnakeCaseProperties_Descriptions_Required_AndNoAdditionalProperties()
    {
        var schema = (JsonObject)new FakeTool().InputSchema;

        Assert.Equal("object", schema["type"]!.GetValue<string>());
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        var props = (JsonObject)schema["properties"]!;
        Assert.Equal(["text", "times"], props.Select(p => p.Key));
        Assert.Equal("string", props["text"]!["type"]!.GetValue<string>());
        Assert.Equal("要回聲的文字", props["text"]!["description"]!.GetValue<string>());
        Assert.Equal("重複幾次", props["times"]!["description"]!.GetValue<string>());
        // 沒有預設值的才是必填
        Assert.Equal(["text"], ((JsonArray)schema["required"]!).Select(n => n!.GetValue<string>()));
    }

    private sealed record NestedArgs(
        [property: Description("最多幾筆")] int MaxItems,
        Mode Mode,
        string[] Tags,
        NestedArgs.Inner Options)
    {
        public sealed record Inner(bool Recursive);
    }

    private enum Mode { FastMode, SlowMode }

    private sealed class NestedTool : ToolBase<NestedArgs>
    {
        public override ToolDescriptor Descriptor { get; } = new("nested", "巢狀", "測試", ToolPermission.ReadOnly, ToolModes.Both, TimeSpan.FromSeconds(1));
        protected override Task<ToolResult> InvokeAsync(NestedArgs args, ToolContext context, CancellationToken ct) => Task.FromResult(ToolResult.Ok("ok"));
    }

    [Fact]
    public void Schema_HandlesEnumsArraysAndNestedObjects()
    {
        var schema = (JsonObject)new NestedTool().InputSchema;
        var props = (JsonObject)schema["properties"]!;

        Assert.True(props.ContainsKey("max_items"));
        Assert.Equal("最多幾筆", props["max_items"]!["description"]!.GetValue<string>());
        Assert.Equal("array", props["tags"]!["type"]!.GetValue<string>());
        Assert.Equal("object", props["options"]!["type"]!.GetValue<string>());
        Assert.False(props["options"]!["additionalProperties"]!.GetValue<bool>());
        var enumValues = ((JsonArray)props["mode"]!["enum"]!).Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(["fast_mode", "slow_mode"], enumValues);
    }

    [Fact]
    public void Schema_ReturnsAFreshCopyEachTime_SoProvidersCanModifyIt()
    {
        var tool = new FakeTool();

        var first = (JsonObject)tool.InputSchema;
        first["x"] = 1;

        Assert.False(((JsonObject)tool.InputSchema).ContainsKey("x"));
    }

    private sealed class BadSchemaTool : ToolBase<string>
    {
        public override ToolDescriptor Descriptor { get; } = new("bad", "壞", "測試", ToolPermission.ReadOnly, ToolModes.Both, TimeSpan.FromSeconds(1));
        protected override Task<ToolResult> InvokeAsync(string args, ToolContext context, CancellationToken ct) => Task.FromResult(ToolResult.Ok(""));
    }

    // ---- 引數解析 ----

    [Fact]
    public async Task Execute_ParsesArguments_AndDeserializesSnakeCaseCaseInsensitively()
    {
        var tool = new FakeTool();

        var result = await tool.ExecuteAsync("""{ "Text": "嗨", "times": 3 }""", Context, default);

        Assert.False(result.IsError);
        Assert.Equal("嗨嗨嗨", result.Content);
        Assert.Equal("回聲 3 次", result.Summary);
        Assert.Equal(("嗨", 3), (tool.Calls[0].Args.Text, tool.Calls[0].Args.Times));
    }

    [Fact]
    public async Task Execute_OptionalArgumentUsesDefault()
    {
        var tool = new FakeTool();

        await tool.ExecuteAsync("""{"text":"a"}""", Context, default);

        Assert.Equal(1, tool.Calls[0].Args.Times);
    }

    [Theory]
    [InlineData("""{"times": 2}""")]           // 缺必填欄位
    [InlineData("""{"text": 5}""")]            // 型別不對
    [InlineData("{ not json")]                 // 不是 JSON
    [InlineData("null")]                       // null
    [InlineData("[]")]                         // 不是物件
    [InlineData("")]                           // 空：等於 {}，缺必填
    public async Task Execute_BadArguments_ReturnErrorResult_NotException(string json)
    {
        var tool = new FakeTool();

        var result = await tool.ExecuteAsync(json, Context, default);

        Assert.True(result.IsError);
        Assert.StartsWith("參數不正確", result.Content);
        Assert.Empty(tool.Calls);
    }

    [Fact]
    public async Task Execute_ExtraUnknownFields_AreIgnored()
    {
        var tool = new FakeTool();

        var result = await tool.ExecuteAsync("""{"text":"a","surprise":true}""", Context, default);

        Assert.False(result.IsError);
    }

    [Fact]
    public void Preview_UsesDescribe_AndIsNoneForBadArguments()
    {
        var tool = new FakeTool();

        Assert.Equal(new ToolPreview("嗨", "將回聲 2 次"), tool.Preview("""{"text":"嗨","times":2}""", Context));
        Assert.Equal(ToolPreview.None, tool.Preview("{ broken", Context));
    }

    /// <summary>記錄 Describe 收到的 context，用來確認 Preview 有把它傳下去。</summary>
    private sealed class ContextSpyTool : ToolBase<EchoArgs>
    {
        public override ToolDescriptor Descriptor { get; } = new("spy", "偵測", "測試", ToolPermission.Write, ToolModes.Both, TimeSpan.FromSeconds(1));

        public ToolContext? Seen { get; private set; }

        protected override Task<ToolResult> InvokeAsync(EchoArgs args, ToolContext context, CancellationToken ct) => Task.FromResult(ToolResult.Ok("ok"));

        protected override ToolPreview Describe(EchoArgs args, ToolContext context)
        {
            Seen = context;
            return new(args.Text, null);
        }
    }

    [Fact]
    public void Preview_PassesTheSameContextToDescribe()
    {
        var tool = new ContextSpyTool();
        var context = new ToolContext("c-preview", ChatMode.Sprite, TimeProvider.System, EmptyFileScope.Instance);

        var preview = tool.Preview("""{"text":"嗨"}""", context);

        Assert.Equal("嗨", preview.Target);
        Assert.Same(context, tool.Seen);
    }

    // ---- 登錄 ----

    [Fact]
    public void Registry_ListsAllTools_InNameOrder_RegardlessOfMode()
    {
        var registry = new ToolRegistry([
            new FakeTool("zeta", modes: ToolModes.Both),
            new FakeTool("alpha", modes: ToolModes.Workbench),
            new FakeTool("sprite_only", modes: ToolModes.Sprite),
        ]);

        // 不依模式過濾（M5 的 Q1）：模式在執行時檢查
        Assert.Equal(["alpha", "sprite_only", "zeta"], registry.GetAll().Select(t => t.Descriptor.Name));
        Assert.True(ToolModes.Workbench.Allows(ChatMode.Workbench));
        Assert.False(ToolModes.Workbench.Allows(ChatMode.Sprite));
        Assert.True(ToolModes.Both.Allows(ChatMode.Sprite));
        Assert.NotNull(registry.Find("alpha"));
        Assert.Null(registry.Find("missing"));
        Assert.NotNull(registry.Find("sprite_only"));   // Find 不分模式
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("中文名稱")]
    [InlineData("")]
    [InlineData("a.b")]
    public void Registry_RejectsInvalidNames(string name) =>
        Assert.Throws<InvalidOperationException>(() => new ToolRegistry([new FakeTool(name)]));

    [Fact]
    public void Registry_RejectsNameLongerThan64() =>
        Assert.Throws<InvalidOperationException>(() => new ToolRegistry([new FakeTool(new string('a', 65))]));

    [Fact]
    public void Registry_RejectsDuplicates() =>
        Assert.Throws<InvalidOperationException>(() => new ToolRegistry([new FakeTool("same"), new FakeTool("same")]));

    [Fact]
    public void Registry_RejectsMissingDescription_AndMissingTimeout()
    {
        Assert.Throws<InvalidOperationException>(() => new ToolRegistry([new FakeTool(description: "  ")]));
        Assert.Throws<InvalidOperationException>(() => new ToolRegistry([new FakeTool(timeout: TimeSpan.Zero)]));
    }

    [Fact]
    public void Registry_RejectsToolWhoseArgumentsAreNotAnObject() =>
        Assert.Throws<InvalidOperationException>(() => new ToolRegistry([new BadSchemaTool()]));

    [Fact]
    public void AddLumiTool_RegistersTheToolForTheRegistry()
    {
        var services = new ServiceCollection();
        services.AddLumiTool<FakeTool>();
        services.AddSingleton<IToolRegistry, ToolRegistry>();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IToolRegistry>().Find("echo"));
    }

    // ---- 執行：逾時、例外、截斷、取消 ----

    [Fact]
    public async Task Executor_Timeout_ReturnsErrorResult()
    {
        var tool = new FakeTool(timeout: TimeSpan.FromMilliseconds(50)) { Behavior = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return ToolResult.Ok(""); } };

        var result = await ToolExecutor.RunAsync(tool, """{"text":"x"}""", Context, 1000, null, default);

        Assert.True(result.IsError);
        Assert.Contains("逾時", result.Content);
    }

    [Fact]
    public async Task Executor_Exception_BecomesGenericError_WithoutLeakingDetails()
    {
        var tool = new FakeTool { Behavior = (_, _) => throw new IOException("C:\\secret\\path.txt 存取被拒") };

        var result = await ToolExecutor.RunAsync(tool, """{"text":"x"}""", Context, 1000, null, default);

        Assert.True(result.IsError);
        Assert.DoesNotContain("secret", result.Content);
        Assert.Equal("工具執行失敗。", result.Content);
    }

    [Fact]
    public async Task Executor_UserCancellation_Propagates()
    {
        var tool = new FakeTool { Behavior = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return ToolResult.Ok(""); } };
        using var cts = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ToolExecutor.RunAsync(tool, """{"text":"x"}""", Context, 1000, null, cts.Token));
    }

    [Fact]
    public void Truncate_CutsLongContent_AddsNotice_AndKeepsShortOnesUntouched()
    {
        var shortResult = ToolResult.Ok("短");
        Assert.Same(shortResult, ToolExecutor.Truncate(shortResult, 10));

        var cut = ToolExecutor.Truncate(ToolResult.Ok(new string('a', 100), "摘要"), 10);

        Assert.Equal(new string('a', 10) + ToolExecutor.TruncatedNotice, cut.Content);
        Assert.Equal("摘要", cut.Summary);
    }

    [Fact]
    public void Truncate_DoesNotSplitSurrogatePairs()
    {
        // "aaa😀" 在第 4 個位置是代理對的後半：不能切在中間
        var result = ToolExecutor.Truncate(ToolResult.Ok("aaa😀bbb"), 4);

        Assert.Equal("aaa" + ToolExecutor.TruncatedNotice, result.Content);
    }

    // ---- 權限記憶 ----

    [Fact]
    public void PermissionMemory_RememberPerConversation_AndPerLevel()
    {
        var memory = new ToolPermissionMemory();
        Assert.False(memory.IsAllowed("a", ToolPermission.Write));

        memory.Allow("a", ToolPermission.Write);

        Assert.True(memory.IsAllowed("a", ToolPermission.Write));
        Assert.False(memory.IsAllowed("a", ToolPermission.Network));    // 只記「此等級」
        Assert.False(memory.IsAllowed("b", ToolPermission.Write));      // 另一則對話不受影響
    }

    [Fact]
    public void PermissionMemory_ReadOnlyIsAlwaysAllowed_DestructiveNeverRemembered()
    {
        var memory = new ToolPermissionMemory();

        Assert.True(memory.IsAllowed("a", ToolPermission.ReadOnly));
        memory.Allow("a", ToolPermission.Destructive);
        Assert.False(memory.IsAllowed("a", ToolPermission.Destructive));
    }

    [Fact]
    public async Task DenyAllApprovalService_Denies()
    {
        var decision = await new DenyAllToolApprovalService().RequestAsync(
            new ToolApprovalRequest("c", "t", "T", ToolPermission.Write, ToolPreview.None, "{}"), default);

        Assert.Equal(ApprovalDecision.Deny, decision);
    }

    // ---- 區塊 JSON ----

    [Fact]
    public void ToolBlocks_RoundTripThroughJson_AndOldDataStillReads()
    {
        IReadOnlyList<ContentBlock> blocks =
        [
            new TextBlock("文字"),
            new ToolUseBlock("toolu_1", "echo", """{"text":"嗨 ❤️‍🔥"}"""),
            new ToolResultBlock("toolu_1", "嗨", false, "回聲", "嗨", "回聲 1 次", 12, ToolApproval.AllowedForConversation),
        ];

        var json = System.Text.Json.JsonSerializer.Serialize(blocks);
        var back = System.Text.Json.JsonSerializer.Deserialize<List<ContentBlock>>(json)!;

        Assert.Equal(blocks, back);
        Assert.Contains("\"AllowedForConversation\"", json);   // 以名稱存，之後調整列舉順序也不會讀錯舊資料
        // 沒有工具的舊資料（只有文字與 thinking）不受影響
        var old = System.Text.Json.JsonSerializer.Deserialize<List<ContentBlock>>("""[{"type":"text","Text":"舊"}]""")!;
        Assert.Equal("舊", Assert.IsType<TextBlock>(Assert.Single(old)).Text);
    }
}
