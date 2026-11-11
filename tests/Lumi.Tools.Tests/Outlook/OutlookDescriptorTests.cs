using Lumi.Core.Chat;
using Lumi.Core.Outlook;
using Lumi.Core.Tools;
using Lumi.Tools.Outlook;
using Xunit;
using static Lumi.Tools.Tests.Outlook.OutlookTestSupport;

namespace Lumi.Tools.Tests.Outlook;

/// <summary>M30 設計 1、3：九個工具的描述符、可用性與基本的支援函式。</summary>
public sealed class OutlookDescriptorTests
{
    public static IEnumerable<object[]> ToolNames() => OutlookTestSupport.All.Select(t => new object[] { t.Name });

    [Fact]
    public void Nine_tools_are_defined_with_the_expected_names()
    {
        Assert.Equal(9, OutlookTestSupport.All.Length);
        var expected = new[]
        {
            "outlook_mail_search", "outlook_mail_read", "outlook_calendar_list", "outlook_mail_save_attachment", "outlook_mail_draft",
            "outlook_calendar_save", "outlook_meeting_draft", "outlook_mail_send_to_self", "outlook_mail_send",
        };
        Assert.Equal(expected, OutlookTestSupport.All.Select(t => t.Name).ToArray());
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public void Descriptors_are_opt_in_and_grouped(string name)
    {
        var descriptor = Make(name, new FakeOutlookClient()).Descriptor;

        Assert.Equal(OutlookToolText.OptInWarning, descriptor.OptInWarning);
        Assert.Equal(ToolGroups.Outlook, descriptor.Group);
        Assert.Equal("outlook", descriptor.Group);
        Assert.NotNull(descriptor.Summary);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.Description));
    }

    [Fact]
    public void Read_only_tools_run_in_both_modes_others_workbench_only()
    {
        foreach (var (name, make, _) in OutlookTestSupport.All)
        {
            var descriptor = make(new FakeOutlookClient()).Descriptor;
            var readOnly = name is "outlook_mail_search" or "outlook_mail_read" or "outlook_calendar_list";

            if (readOnly)
            {
                Assert.Equal(ToolPermission.ReadOnly, descriptor.Permission);
                Assert.Equal(ToolModes.Both, descriptor.Modes);
            }
            else
            {
                Assert.NotEqual(ToolPermission.ReadOnly, descriptor.Permission);
                Assert.Equal(ToolModes.Workbench, descriptor.Modes);
                Assert.False(descriptor.Modes.Allows(ChatMode.Sprite));
            }
        }
    }

    [Fact]
    public void Timeouts_match_the_plan()
    {
        Assert.Equal(TimeSpan.FromSeconds(70), Make("outlook_mail_search", new FakeOutlookClient()).Descriptor.Timeout);
        foreach (var (name, make, _) in OutlookTestSupport.All.Where(t => t.Name != "outlook_mail_search"))
            Assert.Equal(TimeSpan.FromSeconds(40), make(new FakeOutlookClient()).Descriptor.Timeout);
    }

    [Fact]
    public void Every_tool_has_the_exact_summary_text()
    {
        var expected = new Dictionary<string, string>
        {
            ["outlook_mail_search"] = "搜尋 Outlook 信箱裡的信件。",
            ["outlook_mail_read"] = "讀取一封信的內容與附件清單。",
            ["outlook_calendar_list"] = "查看 Outlook 行事曆的行程。",
            ["outlook_mail_save_attachment"] = "把信件的附件存到你加入的資料夾。",
            ["outlook_mail_draft"] = "建立新信、回覆或轉寄的草稿，在 Outlook 打開讓你自己寄出。",
            ["outlook_calendar_save"] = "新增或修改你自己的行程。",
            ["outlook_meeting_draft"] = "建立會議邀請草稿，在 Outlook 打開讓你自己寄出。",
            ["outlook_mail_send_to_self"] = "把內容寄到你自己的信箱。",
            ["outlook_mail_send"] = "寄出信件，每次都會先讓你確認收件人與內文。",
        };

        foreach (var (name, expectedSummary) in expected)
            Assert.Equal(expectedSummary, Make(name, new FakeOutlookClient()).Descriptor.Summary);
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public void Not_installed_tools_are_unavailable(string name)
    {
        var client = new FakeOutlookClient { Status = new OutlookStatus(OutlookState.NotInstalled, OutlookToolText.NotInstalled) };
        var tool = Assert.IsAssignableFrom<IConditionalTool>(Make(name, client));

        var availability = tool.CheckAvailability();

        Assert.Equal(ToolAvailabilityKind.Unavailable, availability.Kind);
        Assert.Equal(OutlookToolText.NotInstalled, availability.Reason);
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public void Not_supported_tools_are_not_on_platform(string name)
    {
        var client = new FakeOutlookClient { Status = new OutlookStatus(OutlookState.NotSupported, "平台不支援") };
        var tool = Assert.IsAssignableFrom<IConditionalTool>(Make(name, client));

        var availability = tool.CheckAvailability();

        Assert.Equal(ToolAvailabilityKind.NotOnThisPlatform, availability.Kind);
        Assert.Equal("平台不支援", availability.Reason);
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public void Running_or_not_running_outlook_is_available(string name)
    {
        var tool = Assert.IsAssignableFrom<IConditionalTool>(Make(name, new FakeOutlookClient { Status = new OutlookStatus(OutlookState.NotRunning, OutlookToolText.NotRunning) }));

        Assert.Equal(ToolAvailability.Yes, tool.CheckAvailability());
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public async Task Not_running_returns_error_without_calling_client(string name)
    {
        var client = new FakeOutlookClient { Status = new OutlookStatus(OutlookState.NotRunning, OutlookToolText.NotRunning) };
        var args = OutlookTestSupport.All.Single(t => t.Name == name).Args;

        var result = await Make(name, client).ExecuteAsync(args, Context(), default);

        Assert.True(result.IsError);
        Assert.Equal(OutlookToolText.NotRunning, result.Content);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task OutlookException_becomes_error_result()
    {
        var client = new FakeOutlookClient { Throw = new OutlookException("Outlook 沒有回應，請看一下 Outlook 是不是跳出了視窗。") };

        var result = await Make("outlook_mail_search", client).ExecuteAsync("{}", Context(), default);

        Assert.True(result.IsError);
        Assert.Equal("Outlook 沒有回應，請看一下 Outlook 是不是跳出了視窗。", result.Content);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        var client = new FakeOutlookClient { Throw = new OperationCanceledException() };

        await Assert.ThrowsAsync<OperationCanceledException>(() => Make("outlook_mail_search", client).ExecuteAsync("{}", Context(), default));
    }
}
