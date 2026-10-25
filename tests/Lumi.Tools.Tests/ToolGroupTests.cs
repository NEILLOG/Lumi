using Lumi.Core.Chat;
using Lumi.Core.Documents;
using Lumi.Core.Tools;
using Lumi.Tools.Tests.Browser;
using Lumi.Tools.Tests.Files;
using Lumi.Tools.Tests.Memory;
using Lumi.Tools.Tests.Notes;
using Lumi.Tools.Tests.Reminders;
using Lumi.Tools.Tests.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Lumi.Tools.Tests;

/// <summary>M15 15C：每個內建工具都有群組與一句說明（工具清單頁用）。</summary>
public sealed class ToolGroupTests
{
    /// <summary>照 <c>AddLumiTools</c> 的既有組裝方式（同 <see cref="ToolTests"/>）建出全部內建工具。</summary>
    private static IReadOnlyList<ToolDescriptor> BuiltinDescriptors()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConversationStore>(new FakeSearchConversations());
        services.AddSingleton<IDocumentReader>(new FakeDocumentReader());
        services.AddFakeWebServices();
        services.AddFakeBrowserServices();
        ReminderRig.RegisterFor(services);
        MemoryRegistration.RegisterFor(services);
        services.AddLumiTools();
        using var provider = services.BuildServiceProvider();

        return provider.GetServices<ITool>().Select(t => t.Descriptor).ToList();
    }

    [Fact]
    public void Every_builtin_tool_has_a_known_group()
    {
        Assert.All(BuiltinDescriptors(), d => Assert.NotNull(ToolGroups.Find(d.Group)));
    }

    [Fact]
    public void Every_builtin_tool_has_a_summary()
    {
        Assert.All(BuiltinDescriptors(), d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Summary), $"{d.Name} 沒有 Summary");
            Assert.EndsWith("。", d.Summary);
            Assert.True(d.Summary!.Length <= 40, $"{d.Name} 的 Summary 超過 40 字：{d.Summary}");
        });
    }

    [Fact]
    public void Only_show_details_cannot_be_disabled()
    {
        var names = BuiltinDescriptors().Where(d => !d.CanDisable).Select(d => d.Name).ToList();

        Assert.Equal(["show_details"], names);
    }

    [Fact]
    public void Only_memory_save_opts_out_of_always_allow()
    {
        var names = BuiltinDescriptors().Where(d => !d.CanAlwaysAllow).Select(d => d.Name).ToList();

        Assert.Equal(["memory_save"], names);
    }
}
