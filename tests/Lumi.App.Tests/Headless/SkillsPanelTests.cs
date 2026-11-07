using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lumi.App.Controls;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.Views;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Headless;

/// <summary>M29 包 29C：設定頁「技能」的畫面——頁首與「新增技能」、次要動作與用量、清單列（開關、標籤、編輯刪除）、新增表單、空狀態、RWD。</summary>
public class SkillsPanelTests
{
    private static bool Shown(Visual root, string name) =>
        root.GetVisualDescendants().OfType<Control>().Any(c => c.Name == name && c.IsEffectivelyVisible);

    private static bool HasVisibleText(Visual root, string text) =>
        root.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == text);

    private static IEnumerable<Border> VisibleRows(Visual root) =>
        root.GetVisualDescendants().OfType<Border>().Where(b => b.IsEffectivelyVisible && b.Classes.Contains("skill-row"));

    private static IEnumerable<Border> VisibleForms(Visual root) =>
        root.GetVisualDescendants().OfType<Border>().Where(b => b.IsEffectivelyVisible && b.Classes.Contains("skill-form"));

    private static int VisibleToggles(Visual root) =>
        root.GetVisualDescendants().OfType<CheckBox>().Count(t => t.IsEffectivelyVisible && t.Content as string == Strings.Skills_EnableToggle);

    [AvaloniaTheory]
    [InlineData(1040)]
    [InlineData(700)]
    [InlineData(500)]   // 設定視窗的 MinWidth 是 720，所以 500 實際上會夾到 720；這裡只是確認寬度變小時主要控制項都還在
    public async Task Page_ShowsItsMainControls_AtEveryWidth(double width)
    {
        using var rig = await SettingsRig.CreateAsync("skills", width, 760);
        var window = rig.Window;

        var header = window.FindByName<SettingsPageHeader>("SkillsHeader");
        Assert.True(header.IsEffectivelyVisible);
        Assert.Equal("技能", header.Eyebrow);
        Assert.Equal(Strings.Skills_HeaderTitle, header.Title);

        var newButton = window.FindByName<Button>("NewSkillButton");
        Assert.True(newButton.IsEffectivelyVisible);
        Assert.Contains("primary", newButton.Classes);
        Assert.True(HasVisibleText(window, Strings.Skills_Add));

        Assert.True(window.FindByName<Button>("SkillsOpenFolderButton").IsEffectivelyVisible);
        Assert.True(window.FindByName<Button>("SkillsImportButton").IsEffectivelyVisible);
        Assert.True(Shown(window, "SkillsUsageText"));
        Assert.Equal(3, VisibleRows(window).Count());
        Assert.False(Shown(window, "SkillsEmptyText"));
        rig.Dispose();
    }

    [AvaloniaFact]
    public async Task Rows_ShowAToggle_ExceptForTheUnreadableOne()
    {
        using var rig = await SettingsRig.CreateAsync("skills");
        var window = rig.Window;

        Assert.Equal(2, VisibleToggles(window));
        Assert.True(HasVisibleText(window, "週報"));
        Assert.True(HasVisibleText(window, Strings.Skills_TagProblemFormat.Replace("{0}", "找不到說明")));
        rig.Dispose();
    }

    [AvaloniaFact]
    public async Task NewForm_OpensAboveTheList_WithTheThreeFields()
    {
        using var rig = await SettingsRig.CreateAsync("skills-new");
        var window = rig.Window;

        var forms = VisibleForms(window).ToList();
        Assert.Single(forms);
        var fieldNames = window.GetVisualDescendants().OfType<TextBox>().Where(t => t.IsEffectivelyVisible)
            .Select(Avalonia.Automation.AutomationProperties.GetName).ToList();
        Assert.Contains(Strings.Skills_FieldName, fieldNames);
        Assert.Contains(Strings.Skills_FieldBody, fieldNames);
        var buttonTexts = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string).ToList();
        Assert.Contains(Strings.Skills_Save, buttonTexts);
        rig.Dispose();
    }

    [AvaloniaFact]
    public async Task EmptyState_ShowsTheExplanation_AndNoRows()
    {
        using var rig = await SettingsRig.CreateAsync("skills-empty");
        var window = rig.Window;

        Assert.True(Shown(window, "SkillsEmptyText"));
        Assert.True(HasVisibleText(window, Strings.Skills_Empty));
        Assert.Empty(VisibleRows(window));
        rig.Dispose();
    }
}
