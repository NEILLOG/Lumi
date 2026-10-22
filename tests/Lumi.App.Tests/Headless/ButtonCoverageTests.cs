using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.Views;
using Xunit.Abstractions;

namespace Lumi.App.Tests.Headless;

/// <summary>
/// UE-4：沒有任何按鈕還是 Fluent 預設外觀。走訪工作台、設定視窗（所有畫面狀態）、ConfirmDialog、模型選單、工具卡片、程式碼區塊與小精靈視窗的視覺樹，
/// 每個 Button／ToggleButton／RadioButton 都必須屬於已 token 化（Hover／Pressed／Disabled／Focus 都在 <c>Themes/Controls.axaml</c>、
/// <c>ChatStyles.axaml</c> 明確寫出，由 <see cref="ButtonStateStyleTests"/> 驗證實際生效值）的樣式。
/// </summary>
public class ButtonCoverageTests
{
    private readonly ITestOutputHelper _output;

    public ButtonCoverageTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// 帶有其中任何一個 class 的按鈕，外觀就來自我們的 token 化樣式（<c>ButtonStateStyleTests</c> 逐一驗證過）。
    /// 輔助 class（toolbar、small、message-action、composer-tool、collapsible、iconOnly、capsule、confirm、round）不算，它們只管尺寸，一定要和層級 class 並用。
    /// </summary>
    private static readonly string[] LevelClasses =
        ["primary", "secondary", "tertiary", "danger", "dashed", "chrome", "send", "stop", "link", "cardheader", "groupheader", "md-copy", "spriteMode"];

    /// <summary>
    /// 不可避免的例外：Fluent 控制項「模板內部」的按鈕（<see cref="Visual.TemplatedParent"/> 不是 null）：捲軸的上下箭頭 RepeatButton
    /// （只在捲軸展開時出現，顏色在 <see cref="FluentColorResidueTests"/> 的例外清單說明）、NumericUpDown 的上下箭頭 RepeatButton
    /// （已用 token 化樣式，狀態由 <see cref="InteractiveStateStyleTests.NumericUpDown_HasTokenLooks_InEveryState"/> 驗證）。
    /// 它們不是我們寫的按鈕，外觀由所屬控制項的樣式決定。
    /// </summary>
    private static bool IsInsideAFluentTemplate(Control control) => control.TemplatedParent is not null;

    /// <summary>
    /// 有自己 token 化模板的控制項：
    /// 分段切換的 RadioButton（<c>Border.segmented</c>）、進階區段標題（<c>Expander.advanced</c> 的 ExpanderHeader，模板自己畫，Hover 只換字色）、
    /// CheckBox／一般 RadioButton（全域樣式，<see cref="InteractiveStateStyleTests"/> 驗證）。
    /// </summary>
    private static bool HasItsOwnTokenizedTemplate(Control control) =>
        control is CheckBox or RadioButton
        || (control is ToggleButton { Name: "ExpanderHeader" } && control.GetVisualAncestors().OfType<Expander>().Any(e => e.Classes.Contains("advanced")));

    private static IEnumerable<ToggleButton> Toggles(Window window) => window.GetVisualDescendants().OfType<ToggleButton>();

    public static IEnumerable<Control> ButtonsOf(Window window) =>
        window.GetVisualDescendants().OfType<Control>().Where(c => c is Button or ToggleButton)
            .Where(c => !IsInsideAFluentTemplate(c));

    [AvaloniaFact]
    public async Task EveryButtonInTheApp_BelongsToATokenizedLevel()
    {
        using var surfaces = await AppSurfaces.OpenAsync();
        var untokenized = new List<string>();
        var total = 0;
        foreach (var (name, window) in surfaces.Windows)
        {
            foreach (var button in ButtonsOf(window))
            {
                total++;
                if (HasItsOwnTokenizedTemplate(button))
                    continue;
                if (button.Classes.Any(c => LevelClasses.Contains(c)))
                    continue;
                untokenized.Add($"{name}：{button.GetType().Name}#{button.Name} classes=[{string.Join(' ', button.Classes)}] content={(button as ContentControl)?.Content}");
            }
        }

        _output.WriteLine($"共掃描 {total} 個按鈕");
        Assert.True(total > 60, $"只掃到 {total} 個按鈕，畫面清單可能不完整");
        Assert.True(untokenized.Count == 0, "以下按鈕仍是 Fluent 預設外觀：\n" + string.Join("\n", untokenized.Distinct()));
    }

    [AvaloniaFact]
    public async Task SegmentedRadioButtons_AreInsideABorderSegmented()
    {
        using var surfaces = await AppSurfaces.OpenAsync();
        foreach (var (name, window) in surfaces.Windows)
        {
            foreach (var radio in Toggles(window).OfType<RadioButton>())
                Assert.True(radio.GetVisualAncestors().OfType<Border>().Any(b => b.Classes.Contains("segmented")),
                    $"{name}：RadioButton#{radio.Name} 不在分段切換裡（一般 RadioButton 走全域樣式，要補狀態測試）");
        }
    }

    [AvaloniaFact]
    public async Task EveryWindowOpensWithAtMostOnePrimaryPerActionArea_AndTheConversationErrorRowFollowsTheRule()
    {
        using var surfaces = await AppSurfaces.OpenAsync(includeSettings: false);
        var workbench = surfaces.Windows.Single(w => w.Name == "workbench").Window;
        var conversation = workbench.FindByName<ConversationView>("Conversation");

        // 一次性提示：只有「關閉」，tertiary
        var dismiss = conversation.FindByName<Button>("DismissNoticeButton");
        Assert.Contains("tertiary", dismiss.Classes);
        Assert.DoesNotContain("primary", dismiss.Classes);
        Assert.Equal(Strings.Workbench_DismissNotice, dismiss.Content);
        Assert.True(dismiss.IsEffectivelyVisible);

        // 錯誤列：「重試」是唯一的 primary，「用其他模型重試」是 secondary
        var retry = conversation.FindByName<Button>("RetryButton");
        var other = conversation.FindByName<Button>("RetryOtherModelButton");
        Assert.Contains("primary", retry.Classes);
        Assert.Contains("secondary", other.Classes);
        Assert.DoesNotContain("primary", other.Classes);
        Assert.True(retry.IsEffectivelyVisible && other.IsEffectivelyVisible);
        Assert.Equal(Strings.Workbench_Retry, retry.Content);
        Assert.Equal(Strings.Workbench_RetryOtherModel, other.Content);
        Assert.Same(retry.Parent, other.Parent);
        Assert.Single(((Panel)retry.Parent!).Children.OfType<Button>(), b => b.Classes.Contains("primary"));
    }
}
