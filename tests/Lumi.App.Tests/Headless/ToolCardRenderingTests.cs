using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Headless;

/// <summary>3C：工具卡片與群組卡實際渲染出來的結構（收合／展開、確認按鈕、群組標題）。</summary>
public class ToolCardRenderingTests
{
    private sealed class Responder : IToolApprovalResponder
    {
        public List<(string Id, ApprovalDecision Decision)> Calls { get; } = [];
        public void Resolve(string callId, ApprovalDecision decision) => Calls.Add((callId, decision));
    }

    private static ToolCardViewModel Card(Responder? responder = null, string id = "c1", string name = "回聲") =>
        new(id, "echo", name, ToolPermission.Write, "檔案.txt", """{"text":"你好"}""", responder ?? new Responder(),
            new FakeClipboardService(), new FakeTimeProvider());

    private static ToolResultBlock Ok(string content = "完成了", string id = "c1", string name = "回聲") =>
        new(id, content, false, name, "檔案.txt", "182 行", 320, ToolApproval.NotRequired);

    private static (T View, Window Window) Show<T>(T view, object dataContext) where T : Control
    {
        view.DataContext = dataContext;
        var window = new Window { Width = 700, Height = 700, Content = view };
        window.Show();
        window.UpdateLayout();
        return (view, window);
    }

    private static IEnumerable<T> All<T>(Control root) where T : Control =>
        new[] { root }.Concat(root.GetLogicalDescendants().OfType<Control>()).OfType<T>();

    /// <summary>用真的滑鼠事件點擊（RaiseEvent(Click) 不會觸發 Command）。</summary>
    private static void Click(Button button)
    {
        var window = (Window)button.GetVisualRoot()!;
        window.UpdateLayout();
        var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
    }

    [AvaloniaFact]
    public void FinishedCard_IsCollapsed_ThenExpandsOnHeaderClick()
    {
        var card = Card();
        card.Complete(Ok());
        var (view, _) = Show(new ToolCardView(), card);
        var result = view.FindControl<TextBlock>("ResultText")!;

        Assert.False(result.IsEffectivelyVisible);   // 預設收合：只看得到標題
        var header = All<Button>(view).First(b => b.Classes.Contains("cardheader"));
        Assert.Contains("回聲 · 檔案.txt · 182 行", All<TextBlock>(view).Select(t => t.Text));

        Click(header);
        view.UpdateLayout();

        Assert.True(result.IsEffectivelyVisible);
        Assert.Equal("完成了", result.Text);
    }

    [AvaloniaFact]
    public void RunningCard_IsOpen_AndHeaderIsNotClickable()
    {
        var card = Card();
        var (view, _) = Show(new ToolCardView(), card);

        var header = All<Button>(view).First(b => b.Classes.Contains("cardheader"));
        Assert.False(header.Command!.CanExecute(null));
        Assert.True(card.IsOpen);
    }

    [AvaloniaFact]
    public void AwaitingWriteCard_ShowsThreeButtons_AndClicksReachTheResponder()
    {
        var responder = new Responder();
        var card = Card(responder);
        card.SetAwaiting(new ToolApprovalRequest("c1", "echo", "回聲", ToolPermission.Write, new ToolPreview("檔案.txt", "將寫入 3 行"), "{}"));
        var (view, _) = Show(new ToolCardView(), card);

        Assert.True(view.FindControl<Button>("AllowOnceButton")!.IsEffectivelyVisible);
        Assert.True(view.FindControl<Button>("AllowConversationButton")!.IsEffectivelyVisible);
        Assert.True(view.FindControl<Button>("DenyButton")!.IsEffectivelyVisible);
        Assert.False(view.FindControl<Button>("AllowExecuteButton")!.IsEffectivelyVisible);
        Assert.Contains("將寫入 3 行", All<TextBlock>(view).Where(t => t.IsEffectivelyVisible).Select(t => t.Text));

        Click(view.FindControl<Button>("AllowConversationButton")!);

        Assert.Equal([("c1", ApprovalDecision.AllowForConversation)], responder.Calls);
        view.UpdateLayout();
        Assert.False(view.FindControl<Button>("AllowOnceButton")!.IsEffectivelyVisible);   // 選過之後確認區消失
    }

    [AvaloniaFact]
    public void AwaitingDestructiveCard_HasOnlyExecuteAndDeny_AndWarnsItCannotBeUndone()
    {
        var responder = new Responder();
        var card = Card(responder);
        card.SetAwaiting(new ToolApprovalRequest("c1", "echo", "回聲", ToolPermission.Destructive, new ToolPreview("a.md", "將永久刪除"), "{}"));
        var (view, _) = Show(new ToolCardView(), card);

        Assert.True(view.FindControl<Button>("AllowExecuteButton")!.IsEffectivelyVisible);
        Assert.False(view.FindControl<Button>("AllowOnceButton")!.IsEffectivelyVisible);
        Assert.False(view.FindControl<Button>("AllowConversationButton")!.IsEffectivelyVisible);
        Assert.True(view.FindControl<Button>("DenyButton")!.IsEffectivelyVisible);
        Assert.Contains(All<TextBlock>(view).Where(t => t.IsEffectivelyVisible), t => t.Text == Lumi.App.Resources.Strings.Tool_Irreversible);

        Click(view.FindControl<Button>("DenyButton")!);

        Assert.Equal([("c1", ApprovalDecision.Deny)], responder.Calls);
    }

    [AvaloniaFact]
    public void CardClasses_FollowTheState()
    {
        var card = Card();
        var (view, _) = Show(new ToolCardView(), card);
        var border = view.FindControl<Border>("Card")!;

        card.SetAwaiting(new ToolApprovalRequest("c1", "echo", "回聲", ToolPermission.Write, ToolPreview.None, "{}"));
        Assert.Contains("awaiting", border.Classes);

        card.Complete(new ToolResultBlock("c1", "壞了", true, "回聲", null, "失敗", 5, ToolApproval.NotRequired));
        Assert.Contains("failed", border.Classes);
        Assert.DoesNotContain("awaiting", border.Classes);
    }

    [AvaloniaFact]
    public void LongResult_ShowsOnlyThePreview_UntilShowFullIsClicked()
    {
        var card = Card();
        var content = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"第 {i} 行"));
        card.Complete(Ok(content));
        card.ToggleCommand.Execute(null);
        var (view, _) = Show(new ToolCardView(), card);
        var result = view.FindControl<TextBlock>("ResultText")!;

        Assert.DoesNotContain("第 7 行", result.Text);
        Assert.Contains(All<Button>(view), b => b.IsEffectivelyVisible && b.Content as string == Lumi.App.Resources.Strings.Tool_ShowFull);

        card.ToggleFullOutputCommand.Execute(null);
        view.UpdateLayout();

        Assert.Contains("第 10 行", result.Text);
    }

    // ---- 群組 ----

    private static ToolRunViewModel RunOf(int count, Responder? responder = null)
    {
        var run = new ToolRunViewModel();
        for (var i = 0; i < count; i++)
        {
            var card = Card(responder, $"c{i}", i % 2 == 0 ? "讀取" : "搜尋");
            card.Complete(Ok(id: $"c{i}", name: i % 2 == 0 ? "讀取" : "搜尋"));
            run.Cards.Add(card);
        }
        return run;
    }

    [AvaloniaFact]
    public void TwoCalls_ShowAsTwoSeparateCards_NoGroup()
    {
        var run = RunOf(2);
        var (view, _) = Show(new ToolRunView(), run);

        Assert.Equal(2, All<ToolCardView>(view).Count(c => c.IsEffectivelyVisible));
        Assert.False(view.FindControl<Button>("GroupHeader")!.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void ThreeCalls_ShowAsOneCollapsedGroup_ThatExpandsToTheCards()
    {
        var run = RunOf(3);
        var (view, _) = Show(new ToolRunView(), run);
        var header = view.FindControl<Button>("GroupHeader")!;

        Assert.True(header.IsEffectivelyVisible);
        Assert.Contains("3 個工具呼叫 · 讀取 2 次、搜尋 1 次，全部成功", All<TextBlock>(view).Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
        Assert.DoesNotContain(All<ToolCardView>(view), c => c.IsEffectivelyVisible);

        Click(header);
        view.UpdateLayout();

        Assert.Equal(3, All<ToolCardView>(view).Count(c => c.IsEffectivelyVisible));
    }

    [AvaloniaFact]
    public void Group_WithAwaitingCard_ShowsItsButtons_WithoutExpanding()
    {
        var responder = new Responder();
        var run = RunOf(3, responder);
        var waiting = Card(responder, "w1", "儲存");
        waiting.SetAwaiting(new ToolApprovalRequest("w1", "save", "儲存", ToolPermission.Write, ToolPreview.None, "{}"));
        run.Cards.Add(waiting);
        var (view, _) = Show(new ToolRunView(), run);

        var allow = All<Button>(view).Single(b => b.Name == "AllowOnceButton" && b.IsEffectivelyVisible);

        Click(allow);
        Assert.Equal([("w1", ApprovalDecision.AllowOnce)], responder.Calls);
    }

    [AvaloniaFact]
    public void FailedGroup_UsesTheFailedStyle_AndNamesTheFailure()
    {
        var run = RunOf(2);
        var bad = Card(id: "bad", name: "壞工具");
        bad.Complete(new ToolResultBlock("bad", "爆", true, "壞工具", "x.txt", "失敗", 1, ToolApproval.NotRequired));
        run.Cards.Add(bad);
        var (view, _) = Show(new ToolRunView(), run);

        Assert.Contains(All<Border>(view), b => b.Classes.Contains("toolgroup") && b.Classes.Contains("failed"));
        Assert.Contains(All<TextBlock>(view).Where(t => t.IsEffectivelyVisible), t => t.Text!.Contains("1 個失敗（壞工具 檔案.txt）"));
    }
}
