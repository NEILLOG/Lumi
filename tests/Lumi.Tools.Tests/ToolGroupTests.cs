using Lumi.Core.Chat;
using Lumi.Core.Documents;
using Lumi.Core.Skills;
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
        services.AddSingleton<ISkillStore>(new InMemorySkillStore());   // 技能工具（M29）需要儲存
        services.AddSingleton<Lumi.Core.Commands.ICommandRunner>(new Commands.FakeCommandRunner());   // 執行指令（M31）
        services.AddSingleton<Lumi.Core.Commands.ICommandGuard>(new Commands.FakeCommandGuard());
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
    public void Only_memory_save_save_skill_and_browser_tools_opt_out_of_always_allow()
    {
        // memory_save（永久性的提示注入）、browser_upload（把本機檔案送到網站）、browser_wait_for_user（打開視窗等使用者，永遠允許沒有意義）
        // create_scheduled_prompt（建立排程要每次確認，M24）、save_skill（永久寫入指示，和 memory_save 同理，M29）、run_command（執行指令每次都問，M31）、outlook_mail_send（寄出信件每次都問）
        var names = BuiltinDescriptors().Where(d => !d.CanAlwaysAllow).Select(d => d.Name).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["browser_upload", "browser_wait_for_user", "create_scheduled_prompt", "memory_save", "outlook_mail_send", "run_command", "save_skill"], names);
    }
}
