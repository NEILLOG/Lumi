using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

/// <summary>M19 19C：工具卡片在執行中把「執行中…」換成最新的進度文字（D12）。</summary>
public class ToolCardProgressTests
{
    private sealed class NullResponder : IToolApprovalResponder
    {
        public void Resolve(string callId, ApprovalDecision decision) { }
    }

    private static ToolCardViewModel NewCard() =>
        new("c1", "web_crawl", "爬取網站", ToolPermission.Write, "intranet.example.com", "{}", new NullResponder(),
            new FakeClipboardService(), new FakeTimeProvider());

    private static ToolApprovalRequest Request() =>
        new("c1", "web_crawl", "爬取網站", ToolPermission.Write, new ToolPreview("intranet.example.com", "將爬取網站"), "{}");

    private static ToolResultBlock Result(string summary = "8 頁", bool error = false) =>
        new("c1", "ok", error, "爬取網站", "intranet.example.com", summary, 120, ToolApproval.NotRequired);

    [Fact]
    public void WhileRunning_TheTitleEndsWithTheProgressText()
    {
        var card = NewCard();

        card.SetProgress("第 3／10 頁");

        Assert.Equal("爬取網站 · intranet.example.com · 第 3／10 頁", card.Title);
        Assert.Equal("第 3／10 頁", card.StatusText);
    }

    [Fact]
    public void WithoutProgress_StillShowsRunningText()
    {
        var card = NewCard();

        Assert.Equal(Strings.Tool_Running, card.StatusText);
        Assert.Equal($"爬取網站 · intranet.example.com · {Strings.Tool_Running}", card.Title);
    }

    [Fact]
    public void WhitespaceProgress_IsIgnored()
    {
        var card = NewCard();

        card.SetProgress("   ");

        Assert.Equal(Strings.Tool_Running, card.StatusText);
    }

    [Fact]
    public void WhileAwaitingApproval_ProgressDoesNotReplaceTheQuestion()
    {
        var card = NewCard();
        card.SetAwaiting(Request());

        card.SetProgress("第 3 頁");

        Assert.Equal(ToolCardState.AwaitingApproval, card.State);
        Assert.Equal(Strings.Tool_Awaiting, card.StatusText);
        Assert.DoesNotContain("第 3 頁", card.Title);
    }

    [Fact]
    public void ProgressSetBeforeApproval_IsClearedWhenAwaiting()
    {
        var card = NewCard();
        card.SetProgress("第 2 頁");

        card.SetAwaiting(Request());
        card.ApplyResolved(ApprovalDecision.AllowOnce);

        Assert.Equal(ToolCardState.Running, card.State);
        Assert.Equal(Strings.Tool_Running, card.StatusText);
    }

    [Fact]
    public void AfterCompletion_ShowsTheResultSummary_NotTheProgress()
    {
        var card = NewCard();
        card.SetProgress("第 9 頁");

        card.Complete(Result("8 頁"));

        Assert.Equal(ToolCardState.Succeeded, card.State);
        Assert.Equal("8 頁", card.StatusText);
        Assert.Equal("爬取網站 · intranet.example.com · 8 頁", card.Title);
    }

    [Fact]
    public void AfterFailure_ShowsTheFailureNotTheProgress()
    {
        var card = NewCard();
        card.SetProgress("第 2 頁");

        card.Complete(Result(summary: "", error: true));

        Assert.Equal(ToolCardState.Failed, card.State);
        Assert.Equal(Strings.Tool_Failed, card.StatusText);
    }

    [Fact]
    public void SetProgress_AfterCompletion_IsIgnored()
    {
        var card = NewCard();
        card.Complete(Result("8 頁"));

        card.SetProgress("遲到的進度");

        Assert.Equal("8 頁", card.StatusText);
    }

    [Fact]
    public void WorkbenchViewModel_UpdatesTheMatchingCard_AndIgnoresUnknownCallIds()
    {
        var session = new FakeChatSession();
        var store = new InMemoryConversationStore();
        var catalog = new FakeModelCatalog();
        var service = new ConversationService(new SequenceChatProvider(SequenceChatProvider.Answer("好")), store, catalog);
        var vm = new WorkbenchViewModel(service, store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
            new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
            NullLogger<WorkbenchViewModel>.Instance, session: session);

        session.StartRun();
        session.Emit(new ToolCallStarted("c1", "web_crawl", "爬取網站", ToolPermission.Write, "intranet.example.com", "{}"));
        session.Emit(new ToolCallProgress("nope", "不存在的卡片"));
        session.Emit(new ToolCallProgress("c1", "第 3／10 頁"));

        var card = Assert.Single(vm.Messages.OfType<ToolRunViewModel>().Single().Cards);
        Assert.Equal("第 3／10 頁", card.StatusText);
        Assert.Equal("爬取網站 · intranet.example.com · 第 3／10 頁", card.Title);

        session.Emit(new ToolCallCompleted("c1", Result("3 頁")));
        Assert.Equal("3 頁", card.StatusText);
    }
}
