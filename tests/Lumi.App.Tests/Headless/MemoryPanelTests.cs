using Lumi.App.ViewModels;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lumi.App.Controls;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.Views;
using Lumi.Core.Memory;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Headless;

/// <summary>M14 14D：設定頁「記憶」的畫面——頁首與主要動作、開關與說明、用量與接近上限、清單與徽章、列內表單、空狀態、全部清除、RWD。</summary>
public class MemoryPanelTests
{
    private static bool Shown(Visual root, string name) =>
        root.GetVisualDescendants().OfType<Control>().Any(c => c.Name == name && c.IsEffectivelyVisible);

    private static bool HasVisibleText(Visual root, string text) =>
        root.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == text);

    private static IEnumerable<Border> VisibleRows(Visual root) =>
        root.GetVisualDescendants().OfType<Border>().Where(b => b.IsEffectivelyVisible && b.Classes.Contains("memory-row"));

    private static IEnumerable<Border> VisibleForms(Visual root) =>
        root.GetVisualDescendants().OfType<Border>().Where(b => b.IsEffectivelyVisible && b.Classes.Contains("memory-form"));

    /// <summary>設定視窗的寬度分段 class（wide／compact／narrow）：只留一個。</summary>
    private static void SetRootClass(Window window, string value)
    {
        foreach (var name in new[] { "wide", "compact", "narrow" })
            window.Classes.Remove(name);
        window.Classes.Add(value);
    }

    private static Button ButtonIn(Visual root, string text) =>
        root.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && b.Content as string == text);

    private static TextBox TextBoxNamed(Visual root, string automationName) =>
        root.GetVisualDescendants().OfType<TextBox>().Single(t => t.IsEffectivelyVisible && AutomationProperties.GetName(t) == automationName);

    [AvaloniaTheory]
    [InlineData(1040)]
    [InlineData(700)]
    [InlineData(500)]   // 設定視窗的 MinWidth 是 720，所以 500 實際上會夾到 720；這裡只是確認寬度變小時主要控制項都還在
    public async Task Panel_ShowsItsMainControls_AtEveryWidth(double width)
    {
        using var rig = await SettingsRig.CreateAsync("memory", width, 760);
        var window = rig.Window;

        var header = window.FindByName<SettingsPageHeader>("MemoryHeader");
        Assert.True(header.IsEffectivelyVisible);
        Assert.Equal("記憶", header.Eyebrow);
        Assert.Equal("Lumi 記得的事", header.Title);
        var newButton = window.FindByName<Button>("NewMemoryButton");
        Assert.True(newButton.IsEffectivelyVisible);
        Assert.Contains("primary", newButton.Classes);

        Assert.True(Shown(window, "MemoryEnabledBox"));
        Assert.True(HasVisibleText(window, Strings.Memory_StoredHere));
        Assert.True(HasVisibleText(window, Strings.Memory_SentToProvider));
        Assert.True(Shown(window, "MemoryUsageText"));
        Assert.Equal(3, VisibleRows(window).Count());
        Assert.True(window.FindByName<Button>("MemoryClearAllButton").IsEffectivelyEnabled);
        rig.Dispose();
    }

    [AvaloniaFact]
    public async Task Usage_ShowsTheCountsAndTheLimits()
    {
        using var rig = await SettingsRig.CreateAsync("memory");

        var usage = rig.Window.FindByName<TextBlock>("MemoryUsageText");
        Assert.Equal(rig.Host.Memory.UsageText, usage.Text);
        Assert.StartsWith("3／100 則 · ", usage.Text);
        Assert.Contains("／2,000 字", usage.Text);
        Assert.False(Shown(rig.Window, "MemoryNearLimitBadge"));
        Assert.DoesNotContain("warn", usage.Classes);
        rig.Dispose();
    }

    [AvaloniaFact]
    public async Task NearLimit_ShowsTheWordAsWellAsTheWarningColor()
    {
        using var rig = await SettingsRig.CreateAsync("memory");
        for (var i = 0; i < 87; i++)
            rig.Host.MemoryStore.Seed($"記憶 {i}", MemoryCategory.Profile);
        rig.Settle();

        var usage = rig.Window.FindByName<TextBlock>("MemoryUsageText");
        Assert.StartsWith("90／100 則 · ", usage.Text);
        Assert.Contains("warn", usage.Classes);
        Assert.True(Shown(rig.Window, "MemoryNearLimitBadge"));
        Assert.True(HasVisibleText(rig.Window, Strings.Memory_NearLimit));   // 文字，不只靠顏色
        rig.Dispose();
    }

    [AvaloniaFact]
    public async Task Rows_ShowTheSourceBadge_TheId_AndTheValidityNote()
    {
        using var rig = await SettingsRig.CreateAsync("memory");
        var window = rig.Window;

        Assert.True(HasVisibleText(window, Strings.Memory_SourceExplicit));
        Assert.True(HasVisibleText(window, Strings.Memory_SourceSuggested));
        Assert.True(HasVisibleText(window, Strings.Memory_SourceManual));
        Assert.True(HasVisibleText(window, "m1"));
        Assert.True(HasVisibleText(window, "有效至 2026-12-31"));
        Assert.True(HasVisibleText(window, Strings.Memory_Expired));   // 已過期的那則（2026-10-01）
        Assert.True(HasVisibleText(window, "有效至 2026-10-01"));
        rig.Dispose();
    }

    [AvaloniaFact]
    public async Task Groups_ShowOnlyTheCategoriesThatHaveItems()
    {
        using var rig = await SettingsRig.CreateAsync("memory");
        var groups = rig.Window.FindByName<ItemsControl>("MemoryGroups");

        var titles = groups.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("group-title") && t.IsEffectivelyVisible)
            .Select(t => t.Text ?? "").ToArray();
        Assert.Equal(["本人", "偏好", "專案"], titles);   // 工作沒有記憶，不出現
        rig.Dispose();
    }

    [AvaloniaFact]
    public async Task EmptyState_ShowsTheExampleSentence_AndDisablesClearAll()
    {
        using var rig = await SettingsRig.CreateAsync("connections-list");
        rig.Host.Settings.CategoryIndex = (int)SettingsCategory.Memory;
        rig.Settle();

        var empty = rig.Window.FindByName<TextBlock>("MemoryEmptyText");
        Assert.True(empty.IsEffectivelyVisible);
        Assert.Equal("還沒有記憶。試著對 Lumi 說：『記住我偏好用繁體中文』。", empty.Text);
        Assert.Empty(VisibleRows(rig.Window));

        var clear = rig.Window.FindByName<Button>("MemoryClearAllButton");
        Assert.False(clear.IsEffectivelyEnabled);
        Assert.True(rig.Window.FindByName<TextBlock>("MemoryClearAllReason").IsEffectivelyVisible);
        rig.Dispose();
    }

    [AvaloniaFact]
    public async Task DisabledSwitch_KeepsTheListVisible_AndSaysWhyBeside()
    {
        using var rig = await SettingsRig.CreateAsync("memory");
        var box = rig.Window.FindByName<CheckBox>("MemoryEnabledBox");
        Assert.True(box.IsChecked == true);
        Assert.False(Shown(rig.Window, "MemoryDisabledNote"));

        box.IsChecked = false;
        await SettingsHost.WaitUntil(() => !rig.Host.Catalog.Current.Memory.Enabled);   // 寫檔是非同步的
        rig.Settle();

        Assert.False(rig.Host.Catalog.Current.Memory.Enabled);
        Assert.True(Shown(rig.Window, "MemoryDisabledNote"));
        Assert.True(HasVisibleText(rig.Window, Strings.Memory_DisabledNote));
        Assert.Equal(3, VisibleRows(rig.Window).Count());   // 關閉時清單仍可看
        rig.Dispose();
    }

    [AvaloniaFact]
    public async Task NewForm_OpensAboveTheList_WithSaveDisabledAndAReason()
    {
        using var rig = await SettingsRig.CreateAsync("memory-new");
        var window = rig.Window;

        Assert.True(Shown(window, "MemoryNewForm"));
        Assert.Single(VisibleForms(window));
        var save = ButtonIn(window, Strings.Memory_Save);
        Assert.Contains("primary", save.Classes);
        Assert.False(save.IsEffectivelyEnabled);
        Assert.True(HasVisibleText(window, Strings.Memory_ContentEmpty));   // 停用的原因寫出來
        Assert.Contains("secondary", ButtonIn(window, Strings.Memory_Cancel).Classes);
        rig.Dispose();
    }

    [AvaloniaFact]
    public async Task NewForm_OverLimit_ShowsTheCounterAndAnErrorText_AndDisablesSave()
    {
        using var rig = await SettingsRig.CreateAsync("memory-new");
        var window = rig.Window;
        var content = TextBoxNamed(window, Strings.Memory_ContentLabel);

        content.Text = new string('字', 201);
        rig.Settle();

        Assert.True(HasVisibleText(window, "201／200"));
        Assert.Contains("error", content.Classes);
        Assert.True(HasVisibleText(window, Strings.Memory_ContentBlocked));
        Assert.False(ButtonIn(window, Strings.Memory_Save).IsEffectivelyEnabled);
        rig.Dispose();
    }

    [AvaloniaFact]
    public async Task NewForm_BadExpiry_ShowsTheErrorText_AndDisablesSave()
    {
        using var rig = await SettingsRig.CreateAsync("memory-new");
        var window = rig.Window;
        TextBoxNamed(window, Strings.Memory_ContentLabel).Text = "一句話";
        var expires = TextBoxNamed(window, Strings.Memory_ExpiresLabel);

        expires.Text = "明年底";
        rig.Settle();

        Assert.Contains("error", expires.Classes);
        Assert.True(HasVisibleText(window, Strings.Memory_ExpiresError));
        Assert.False(ButtonIn(window, Strings.Memory_Save).IsEffectivelyEnabled);
        rig.Dispose();
    }

    [AvaloniaFact]
    public async Task EditForm_AppearsInsideTheEditedRow_AndOnlyThere()
    {
        using var rig = await SettingsRig.CreateAsync("memory-editing");
        var window = rig.Window;

        var form = Assert.Single(VisibleForms(window));
        Assert.False(Shown(window, "MemoryNewForm"));
        var row = form.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("memory-row"));
        Assert.Equal("我是後端工程師", row.GetVisualDescendants().OfType<EmojiTextBlock>().First().Text);
        Assert.True(HasVisibleText(window, "編輯 m1"));
        rig.Dispose();
    }

    [AvaloniaFact]
    public async Task Row_ActionsAreEditAndDelete_WithTheRightLevels()
    {
        using var rig = await SettingsRig.CreateAsync("memory");
        var row = VisibleRows(rig.Window).First();

        var edit = row.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == Strings.Memory_Edit);
        var delete = row.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == Strings.Memory_Delete);
        Assert.Contains("tertiary", edit.Classes);
        Assert.Contains("danger", delete.Classes);   // 刪除是破壞性動作
        rig.Dispose();
    }

    [AvaloniaFact]
    public async Task Delete_OpensTheConfirmation_AndCancelKeepsTheRow()
    {
        using var rig = await SettingsRig.CreateAsync("memory");
        rig.Host.Confirm.Answer = false;
        var row = VisibleRows(rig.Window).First();
        var delete = row.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == Strings.Memory_Delete);

        delete.Command!.Execute(null);
        rig.Settle();

        Assert.Single(rig.Host.Confirm.Asked);
        Assert.Empty(rig.Host.MemoryStore.DeleteCalls);
        Assert.Equal(3, VisibleRows(rig.Window).Count());
        rig.Dispose();
    }

    [AvaloniaFact]
    public async Task ClearAll_ConfirmsWithTheCount_AndClearsOnlyWhenConfirmed()
    {
        using var rig = await SettingsRig.CreateAsync("memory");
        var clear = rig.Window.FindByName<Button>("MemoryClearAllButton");
        Assert.True(clear.IsEffectivelyEnabled);
        Assert.Contains("danger", clear.Classes);

        rig.Host.Confirm.Answer = false;
        clear.Command!.Execute(null);
        rig.Settle();
        Assert.Contains("3", rig.Host.Confirm.Asked.Single().Message);
        Assert.Equal(0, rig.Host.MemoryStore.DeleteAllCalls);

        rig.Host.Confirm.Answer = true;
        clear.Command!.Execute(null);
        rig.Settle();
        Assert.Equal(1, rig.Host.MemoryStore.DeleteAllCalls);
        Assert.Empty(VisibleRows(rig.Window));
        Assert.True(rig.Window.FindByName<TextBlock>("MemoryEmptyText").IsEffectivelyVisible);
        rig.Dispose();
    }

    [AvaloniaFact]
    public async Task StoreChange_UpdatesAnOpenPanel_WithoutReopening()
    {
        using var rig = await SettingsRig.CreateAsync("memory");
        Assert.Equal(3, VisibleRows(rig.Window).Count());

        rig.Host.MemoryStore.Seed("模型在對話中存的新記憶", MemoryCategory.Work, MemorySource.Explicit);
        rig.Settle();

        Assert.Equal(4, VisibleRows(rig.Window).Count());
        Assert.True(HasVisibleText(rig.Window, "模型在對話中存的新記憶"));
        rig.Dispose();
    }

    [AvaloniaFact]
    public async Task StoreChange_KeepsTheOpenFormAndWhatWasTyped()
    {
        using var rig = await SettingsRig.CreateAsync("memory-editing");
        var content = TextBoxNamed(rig.Window, Strings.Memory_ContentLabel);
        content.Text = "正在改";

        rig.Host.MemoryStore.Seed("別處的新記憶", MemoryCategory.Work);
        rig.Settle();

        Assert.Single(VisibleForms(rig.Window));
        Assert.Equal("正在改", TextBoxNamed(rig.Window, Strings.Memory_ContentLabel).Text);
        rig.Dispose();
    }

    [AvaloniaFact]
    public async Task Narrow_MovesTheRowActionsBelowTheContent()
    {
        using var rig = await SettingsRig.CreateAsync("memory");
        var window = rig.Window;
        var actions = window.GetVisualDescendants().OfType<StackPanel>().First(p => p.Classes.Contains("memory-actions"));

        // 寬視窗：動作在內容的右邊（第 0 列）
        SetRootClass(window, "wide");
        rig.Settle();
        Assert.Equal(0, Grid.GetRow(actions));

        // 窄視窗（Window.narrow）：動作在第 1 列、靠左，內容下方
        SetRootClass(window, "narrow");
        rig.Settle();
        Assert.Equal(1, Grid.GetRow(actions));
        Assert.Equal(0, Grid.GetColumn(actions));
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Left, actions.HorizontalAlignment);
        rig.Dispose();
    }
}
