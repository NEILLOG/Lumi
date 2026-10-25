using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests;

/// <summary>M17（17H）：確認卡片顯示網址警告；<see cref="ToolApprovalRequest.AllowRemember"/> 為 false 時不提供「本次對話都允許」。</summary>
public class ToolCardWarningTests
{
    private sealed class NullResponder : IToolApprovalResponder
    {
        public void Resolve(string callId, ApprovalDecision decision) { }
    }

    private static ToolCardViewModel NewCard() =>
        new("c1", "web_fetch", "讀取網頁", ToolPermission.Network, "https://a.example/", "{}", new NullResponder(),
            new FakeClipboardService(), new FakeTimeProvider());

    private static ToolApprovalRequest Request(ToolPermission permission, IReadOnlyList<string>? warnings = null, bool allowRemember = true) =>
        new("c1", "web_fetch", "讀取網頁", permission, new ToolPreview("https://a.example/", "將連線到網站"), "{}")
        {
            Warnings = warnings ?? [],
            AllowRemember = allowRemember,
        };

    [Fact]
    public void WithoutWarnings_HasWarningsIsFalse()
    {
        var card = NewCard();
        card.SetAwaiting(Request(ToolPermission.Network));

        Assert.Empty(card.Warnings);
        Assert.False(card.HasWarnings);
    }

    [Fact]
    public void OneWarning_IsListed_AndHasWarningsIsTrue()
    {
        var card = NewCard();
        card.SetAwaiting(Request(ToolPermission.Network, ["這個網站沒有在對話中出現過"]));

        Assert.True(card.HasWarnings);
        Assert.Equal(["這個網站沒有在對話中出現過"], card.Warnings);
    }

    [Fact]
    public void AllowRememberFalse_HidesAllowForConversation_ForNetwork()
    {
        var card = NewCard();
        card.SetAwaiting(Request(ToolPermission.Network, ["搜尋引擎"], allowRemember: false));

        Assert.False(card.ShowAllowForConversation);
    }

    [Fact]
    public void AllowRememberTrue_KeepsAllowForConversation_ForNetwork()
    {
        var card = NewCard();
        card.SetAwaiting(Request(ToolPermission.Network));

        Assert.True(card.ShowAllowForConversation);
    }

    [Fact]
    public void AllowRememberFalse_OnWrite_AlsoHidesAllowForConversation()
    {
        var card = NewCard();
        card.SetAwaiting(Request(ToolPermission.Write, allowRemember: false));

        Assert.False(card.ShowAllowForConversation);
    }

    [Fact]
    public void Destructive_StillHidesAllowForConversation()
    {
        var card = NewCard();
        card.SetAwaiting(Request(ToolPermission.Destructive));

        Assert.False(card.ShowAllowForConversation);
        Assert.True(card.IsDestructive);
    }

    [Fact]
    public void Completing_ClearsWarnings_AndAllowForConversation()
    {
        var card = NewCard();
        card.SetAwaiting(Request(ToolPermission.Network, ["警告"]));

        card.Complete(new ToolResultBlock("c1", "完成", false, "讀取網頁", null, null, 10, ToolApproval.AllowedOnce));

        Assert.False(card.HasWarnings);
        Assert.Empty(card.Warnings);
        Assert.False(card.ShowAllowForConversation);
    }

    [AvaloniaFact]
    public void Headless_AwaitingCard_ShowsEachWarning()
    {
        var card = NewCard();
        card.SetAwaiting(Request(ToolPermission.Network, ["第一則警告", "第二則警告"]));
        var (view, window) = Show(new ToolCardView(), card);

        var warnings = view.FindControl<ItemsControl>("ApprovalWarnings")!;
        Assert.True(warnings.IsEffectivelyVisible);
        var texts = view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("第一則警告", texts);
        Assert.Contains("第二則警告", texts);
        window.Close();
    }

    [AvaloniaFact]
    public void Headless_WithoutWarnings_WarningListIsHidden()
    {
        var card = NewCard();
        card.SetAwaiting(Request(ToolPermission.Network));
        var (view, window) = Show(new ToolCardView(), card);

        Assert.False(view.FindControl<ItemsControl>("ApprovalWarnings")!.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Headless_AllowRememberFalse_HidesTheConversationButton()
    {
        var card = NewCard();
        card.SetAwaiting(Request(ToolPermission.Network, ["搜尋引擎"], allowRemember: false));
        var (view, window) = Show(new ToolCardView(), card);

        Assert.False(view.FindControl<Button>("AllowConversationButton")!.IsEffectivelyVisible);
        Assert.True(view.FindControl<Button>("AllowOnceButton")!.IsEffectivelyVisible);
        window.Close();
    }

    private static (T View, Window Window) Show<T>(T view, object dataContext) where T : Control
    {
        view.DataContext = dataContext;
        var window = new Window { Width = 700, Height = 700, Content = view };
        window.Show();
        window.UpdateLayout();
        return (view, window);
    }
}
