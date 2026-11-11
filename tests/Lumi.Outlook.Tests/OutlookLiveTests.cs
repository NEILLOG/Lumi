using System.Runtime.Versioning;
using Lumi.Core.Outlook;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Outlook.Tests;

/// <summary>
/// 實機測試：只在 Windows、設定 LUMI_OUTLOOK_LIVE=1、而且 classic Outlook 已經開著時才跑。
/// 不寄信；建立的草稿只放在草稿資料夾，不會寄出。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OutlookLiveTests
{
    private const string ConversationId = "live-test";

    private const string Reason = "只在 Windows、設定 LUMI_OUTLOOK_LIVE=1，而且 classic Outlook 已經開著時才跑";

    [SkippableFact]
    public async Task Search_inbox_returns_at_most_five()
    {
        Skip.IfNot(IsEnabled(), Reason);
        using var client = CreateClient();

        var result = await SearchInboxAsync(client, 5);

        Assert.True(result.Items.Count <= 5);
    }

    [SkippableFact]
    public async Task Read_first_inbox_mail()
    {
        Skip.IfNot(IsEnabled(), Reason);
        using var client = CreateClient();
        var search = await SearchInboxAsync(client, 5);
        Skip.If(search.Items.Count == 0, "收件匣沒有信件");

        var message = await client.ReadMailAsync(ConversationId, search.Items[0].Id, CancellationToken.None);

        Assert.Equal(search.Items[0].Id, message.Summary.Id);
    }

    [SkippableFact]
    public async Task List_today_events()
    {
        Skip.IfNot(IsEnabled(), Reason);
        using var client = CreateClient();
        var today = DateTime.Today;

        var events = await client.ListEventsAsync(ConversationId, new CalendarQuery(today, today.AddDays(1), null), CancellationToken.None);

        Assert.NotNull(events);
    }

    [SkippableFact]
    public async Task Own_address_is_an_email_address()
    {
        Skip.IfNot(IsEnabled(), Reason);
        using var client = CreateClient();

        // 第一次連線時會讀取自己的地址，所以先做一次搜尋
        await SearchInboxAsync(client, 1);

        Assert.True(OutlookAddresses.IsEmailAddress(client.CachedOwnAddress), "讀到的自己地址不是電子郵件地址");
    }

    [SkippableFact]
    public async Task Create_draft_to_self_without_sending()
    {
        Skip.IfNot(IsEnabled(), Reason);
        using var client = CreateClient();
        await SearchInboxAsync(client, 1);
        var self = client.CachedOwnAddress;
        Skip.IfNot(OutlookAddresses.IsEmailAddress(self), "讀不到自己的地址");

        var draft = await client.CreateDraftAsync(
            ConversationId,
            new MailDraftRequest(DraftKind.New, null, [self!], [], [], "Lumi 實機測試草稿（可以刪除）", "這是 Lumi 實機測試建立的草稿，不會寄出。", []),
            CancellationToken.None);

        Assert.Contains(draft.To, to => to.Contains(self!, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsEnabled() =>
        OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("LUMI_OUTLOOK_LIVE") == "1";

    private static OutlookClient CreateClient() => new(NullLogger<OutlookClient>.Instance);

    private static Task<MailSearchResult> SearchInboxAsync(OutlookClient client, int max) =>
        client.SearchMailAsync(ConversationId, new MailSearchRequest(null, null, OutlookFolder.Inbox, null, null, false, false, max), CancellationToken.None);
}
