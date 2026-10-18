using Lumi.Core;
using Lumi.Core.Chat;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Lumi.Core.Tests;

public class CoreServiceCollectionTests
{
    [Fact]
    public void AddLumiCore_BuildsServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddLumiCore();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        Assert.NotNull(provider);
    }

    [Fact]
    public void AddLumiCore_ToolServices_AreResolvable_WithDenyAllAsDefaultApproval()
    {
        var services = new ServiceCollection();
        services.AddLumiCore();
        services.AddSingleton<ITool, FakeToolForDi>();

        using var provider = services.BuildServiceProvider(validateScopes: true);

        Assert.Equal("fake_di", Assert.Single(provider.GetRequiredService<IToolRegistry>().GetTools(ChatMode.Workbench)).Descriptor.Name);
        Assert.IsType<DenyAllToolApprovalService>(provider.GetRequiredService<IToolApprovalService>());
        Assert.Same(provider.GetRequiredService<ToolPermissionMemory>(), provider.GetRequiredService<ToolPermissionMemory>());
        Assert.Equal(10, provider.GetRequiredService<ToolLoopOptions>().MaxIterations);
    }

    [Fact]
    public void AddLumiCore_CustomApprovalService_ReplacesDefault()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IToolApprovalService>(new ScriptedApprovalService());
        services.AddLumiCore();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<ScriptedApprovalService>(provider.GetRequiredService<IToolApprovalService>());
    }

    private sealed class FakeToolForDi : ToolBase<NoArgs>
    {
        public override ToolDescriptor Descriptor { get; } =
            new("fake_di", "測試", "測試用。", ToolPermission.ReadOnly, ToolModes.Both, TimeSpan.FromSeconds(1));

        protected override Task<ToolResult> InvokeAsync(NoArgs args, ToolContext context, CancellationToken ct) =>
            Task.FromResult(ToolResult.Ok("ok"));
    }
}
