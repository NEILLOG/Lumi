using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Lumi.App.Sprites;
using Lumi.App.Tests.TestSupport;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Sprites;

/// <summary>M17（17H）：小精靈的氣泡顯示網址警告；<see cref="ToolApprovalRequest.AllowRemember"/> 為 false 時不顯示「這則對話都允許」。</summary>
public class SpriteApprovalWarningTests
{
    private static ToolApprovalRequest Request(IReadOnlyList<string>? warnings = null, bool allowRemember = true) =>
        new("c1", "web_fetch", "讀取網頁", ToolPermission.Network, new ToolPreview("https://a.example/", "將連線到網站"), "{}")
        {
            Warnings = warnings ?? [],
            AllowRemember = allowRemember,
        };

    [Fact]
    public void Warning_IsNull_WhenThereAreNoWarnings()
    {
        var approval = new SpriteApprovalViewModel(Request());

        Assert.Null(approval.Warning);
        Assert.False(approval.HasWarning);
    }

    [Fact]
    public void Warning_JoinsSeveralWarningsWithNewlines()
    {
        var approval = new SpriteApprovalViewModel(Request(["第一則", "第二則"]));

        Assert.True(approval.HasWarning);
        Assert.Equal("第一則\n第二則", approval.Warning);
    }

    [Fact]
    public void AllowRemember_PassesThroughFromTheRequest()
    {
        Assert.True(new SpriteApprovalViewModel(Request()).AllowRemember);
        Assert.False(new SpriteApprovalViewModel(Request(allowRemember: false)).AllowRemember);
    }

    [AvaloniaFact]
    public void Headless_ExpandedOptions_HideConversationButton_WhenNotRemembered()
    {
        var bubble = new SpriteBubbleViewModel(new SpriteShellOptions(), new FakeTimeProvider());
        bubble.OnEvent(new ApprovalRequired(Request(["搜尋引擎"], allowRemember: false)));
        bubble.Approval!.IsExpanded = true;
        var view = new SpriteBubbleView { DataContext = bubble };
        var window = new Window { Width = 400, Height = 500, Content = view };
        window.Show();
        window.UpdateLayout();

        Assert.True(view.FindControl<Button>("OptionAllowOnce")!.IsEffectivelyVisible);
        Assert.False(view.FindControl<Button>("OptionAllowConversation")!.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Headless_Warning_IsShownInTheBubble()
    {
        var bubble = new SpriteBubbleViewModel(new SpriteShellOptions(), new FakeTimeProvider());
        bubble.OnEvent(new ApprovalRequired(Request(["這個網站沒有在對話中出現過"])));
        var view = new SpriteBubbleView { DataContext = bubble };
        var window = new Window { Width = 400, Height = 500, Content = view };
        window.Show();
        window.UpdateLayout();

        var warning = view.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Text == "這個網站沒有在對話中出現過");
        Assert.True(warning.IsEffectivelyVisible);
        window.Close();
    }
}
