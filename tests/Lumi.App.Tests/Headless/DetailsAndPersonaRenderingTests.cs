using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Lumi.App.Controls;
using Lumi.App.Markdown;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Tools;
using Lumi.Tools;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Headless;

/// <summary>5C：show_details 的內容卡與「人格與主動性」設定頁實際渲染出來的樣子。</summary>
public class DetailsAndPersonaRenderingTests
{
    private sealed class NoopResponder : IToolApprovalResponder
    {
        public void Resolve(string callId, ApprovalDecision decision) { }
    }

    private const string DetailsJson = """{"title":"C# 讀檔範例","markdown":"## 範例標題\n\n先讀整個檔案，再逐行處理。"}""";

    private static (T View, Window Window) Show<T>(T view, object dataContext, double width = 700) where T : Control
    {
        view.DataContext = dataContext;
        var window = new Window { Width = width, Height = 700, Content = view };
        window.Show();
        window.UpdateLayout();
        return (view, window);
    }

    private static IEnumerable<T> All<T>(Control root) where T : Control =>
        new[] { root }.Concat(root.GetLogicalDescendants().OfType<Control>()).OfType<T>();

    private static void Click(Button button)
    {
        var window = (Window)button.GetVisualRoot()!;
        window.UpdateLayout();
        var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
    }

    /// <summary>隱藏頁面的內容還沒掛進視覺樹（<c>IsEffectivelyVisible</c> 對沒有父層的控制項是 true），所以要一起看有沒有視覺根。</summary>
    private static bool IsShown(Control control) => control.GetVisualRoot() is not null && control.IsEffectivelyVisible;

    private static ToolCardViewModel DetailsCard(FakeClipboardService? clipboard = null, bool complete = true)
    {
        var card = new ToolCardViewModel("d1", ShowDetailsTool.Name, "整理完整內容", null, "C# 讀檔範例", DetailsJson,
            new NoopResponder(), clipboard ?? new FakeClipboardService(), new FakeTimeProvider());
        if (complete)
            card.Complete(new ToolResultBlock("d1", "已放到工作台", false, "整理完整內容", "C# 讀檔範例", "20 字", 3, ToolApproval.NotRequired));
        return card;
    }

    [AvaloniaFact]
    public void FinishedDetailsCard_ShowsTheContentOpen_AndHidesTheRegularCard()
    {
        var (view, _) = Show(new ToolCardView(), DetailsCard());

        var details = view.FindControl<Border>("DetailsCard")!;
        Assert.True(details.IsEffectivelyVisible);
        Assert.False(view.FindControl<Border>("Card")!.IsEffectivelyVisible);
        var markdown = view.FindControl<MarkdownView>("DetailsMarkdown")!;
        markdown.Flush();
        view.UpdateLayout();
        Assert.True(markdown.IsEffectivelyVisible);   // 不用展開就看得到內容
        var texts = All<TextBlock>(view).Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
        Assert.Contains(texts, t => t.Contains("整理完整內容 · C# 讀檔範例"));
        Assert.Contains(texts, t => t.EndsWith(" 字"));
        var rendered = markdown.GetLogicalDescendants().OfType<MarkdownTextBlock>().Select(b => string.Concat(b.Inlines!.OfType<Run>().Select(r => r.Text))).ToList();
        Assert.Contains("範例標題", rendered);
        Assert.Contains("先讀整個檔案，再逐行處理。", rendered);
    }

    [AvaloniaFact]
    public void RunningDetailsCard_UsesTheRegularCard_ThenSwitchesWhenDone()
    {
        var card = DetailsCard(complete: false);
        var (view, _) = Show(new ToolCardView(), card);

        Assert.False(view.FindControl<Border>("DetailsCard")!.IsEffectivelyVisible);
        Assert.True(view.FindControl<Border>("Card")!.IsEffectivelyVisible);

        card.Complete(new ToolResultBlock("d1", "已放到工作台", false, "整理完整內容", "C# 讀檔範例", "20 字", 3, ToolApproval.NotRequired));
        view.UpdateLayout();

        Assert.True(view.FindControl<Border>("DetailsCard")!.IsEffectivelyVisible);
        Assert.False(view.FindControl<Border>("Card")!.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void DetailsCopyButton_CopiesTheMarkdown()
    {
        var clipboard = new FakeClipboardService();
        var (view, _) = Show(new ToolCardView(), DetailsCard(clipboard));

        Click(view.FindControl<Button>("DetailsCopyButton")!);

        Assert.Equal("## 範例標題\n\n先讀整個檔案，再逐行處理。", Assert.Single(clipboard.Texts));
    }

    [AvaloniaFact]
    public void OrdinaryToolCard_NeverShowsTheContentCard()
    {
        var card = new ToolCardViewModel("c1", "echo", "回聲", ToolPermission.ReadOnly, "檔案.txt", """{"text":"你好"}""",
            new NoopResponder(), new FakeClipboardService(), new FakeTimeProvider());
        card.Complete(new ToolResultBlock("c1", "完成", false, "回聲", "檔案.txt", "1 行", 3, ToolApproval.NotRequired));
        var (view, _) = Show(new ToolCardView(), card);

        Assert.False(view.FindControl<Border>("DetailsCard")!.IsEffectivelyVisible);
        Assert.True(view.FindControl<Border>("Card")!.IsEffectivelyVisible);
    }

    // ---- 設定頁 ----

    [AvaloniaFact]
    public async Task PersonaPage_SaveButtonFollowsTheDraft()
    {
        using var host = new SettingsHost();
        var window = new SettingsWindow(host.Settings);
        window.Show();
        await host.Settings.NavigateAsync(new SettingsNavigation(SettingsSection.Persona));
        window.UpdateLayout();

        var save = window.FindControl<Button>("PersonaSaveButton")!;
        var custom = window.FindControl<TextBox>("PersonaCustomBox")!;
        Assert.True(IsShown(save));
        Assert.True(IsShown(window.FindControl<ComboBox>("PersonaStyleBox")!));
        Assert.True(IsShown(window.FindControl<ComboBox>("PersonaProactivityBox")!));
        Assert.False(save.IsEffectivelyEnabled);   // 沒有變更（命令不能執行時按鈕停用）

        custom.Text = "叫我 Neil";
        window.UpdateLayout();
        Assert.True(save.IsEffectivelyEnabled);

        // 按鈕綁的就是儲存命令（儲存本身由 PersonaViewModelTests 涵蓋；headless 沒有 UI 的同步內容，
        // 非同步命令的延續會落在背景執行緒，沒辦法在這裡更新畫面）
        Assert.Same(host.Persona.SaveCommand, save.Command);

        custom.Text = "";   // 改回和已儲存的一樣：按鈕又停用
        window.UpdateLayout();
        Assert.False(save.IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public async Task PersonaPage_IsOnlyShownForItsCategory()
    {
        using var host = new SettingsHost();
        var window = new SettingsWindow(host.Settings);
        window.Show();
        window.UpdateLayout();

        Assert.False(IsShown(window.FindControl<Button>("PersonaSaveButton")!));   // 預設是「連線與模型」

        await host.Settings.NavigateAsync(new SettingsNavigation(SettingsSection.General));
        window.UpdateLayout();
        Assert.False(IsShown(window.FindControl<Button>("PersonaSaveButton")!));

        await host.Settings.NavigateAsync(new SettingsNavigation(SettingsSection.Persona));
        window.UpdateLayout();
        Assert.True(IsShown(window.FindControl<Button>("PersonaSaveButton")!));
    }
}
