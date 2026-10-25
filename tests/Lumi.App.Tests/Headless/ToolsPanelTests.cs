using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Models;
using Lumi.Core.Tools;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Headless;

/// <summary>M15 15D：設定頁「工具」的畫面——群組與工具列、三態與不能關的勾選框、確認方式（下拉與文字）、危險徽章、模型與記憶的說明、窄視窗的版面。</summary>
public class ToolsPanelTests
{
    private static readonly string[] GroupTitles = ["一般", "寫入檔案", "筆記與對話", "長期記憶", "小精靈"];

    private static ToolsViewModel NewViewModel(FakeModelCatalog? catalog = null)
    {
        var tools = new FakeToolCatalog(
            FakeCatalogTool.Make("get_current_time", "查詢目前時間"),
            FakeCatalogTool.Make("write_file", "寫入檔案", ToolPermission.Write, ToolGroups.FilesWrite),
            FakeCatalogTool.Make("overwrite_file", "覆寫檔案", ToolPermission.Destructive, ToolGroups.FilesWrite),
            FakeCatalogTool.Make("add_note", "新增筆記", ToolPermission.Write, ToolGroups.Notes),
            FakeCatalogTool.Make("memory_save", "儲存記憶", ToolPermission.Write, ToolGroups.Memory, canAlwaysAllow: false),
            FakeCatalogTool.Make("show_details", "顯示詳細內容", group: ToolGroups.Sprite, canDisable: false, modes: ToolModes.Sprite),
            FakeCatalogTool.Make("sprite_note", "小精靈筆記", group: ToolGroups.Sprite, modes: ToolModes.Sprite));
        return new ToolsViewModel(tools, catalog ?? new FakeModelCatalog(), new FakeConfirmService());
    }

    /// <summary>把面板放進獨立的視窗；寬度與窄視窗 class 和設定視窗相同（<c>Window.narrow</c> 規則在面板內）。</summary>
    private static Window Host(ToolsViewModel vm, double width = 1040, bool narrow = false)
    {
        var window = new Window { Width = width, Height = 900, Content = new ToolsPanel { DataContext = vm } };
        // 設定視窗提供 muted 文字樣式；獨立視窗用同一份共用樣式
        window.Styles.Add(new StyleInclude(new Uri("avares://Lumi.App/")) { Source = new Uri("avares://Lumi.App/Themes/ChatStyles.axaml") });
        window.Classes.Add(narrow ? "narrow" : "wide");
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static IEnumerable<T> All<T>(Visual root) where T : Visual => root.GetVisualDescendants().OfType<T>();

    private static bool HasVisibleText(Visual root, string text) =>
        All<TextBlock>(root).Any(t => t.IsEffectivelyVisible && t.Text == text);

    /// <summary>工具列裡的勾選框（群組標題的勾選框也叫同一個名字，例如「寫入檔案」，所以只看工具列）。</summary>
    private static CheckBox CheckBoxNamed(Visual root, string automationName) =>
        All<CheckBox>(root).Single(c => AutomationProperties.GetName(c) == automationName
            && c.GetVisualAncestors().OfType<Border>().Any(b => b.Classes.Contains("tool-row")));

    [AvaloniaTheory]
    [InlineData(1040)]
    [InlineData(720)]
    public void Renders_all_groups_and_tools_at_each_width(double width)
    {
        var window = Host(NewViewModel(), width);

        foreach (var title in GroupTitles)
            Assert.True(HasVisibleText(window, title), title);
        foreach (var name in new[] { "查詢目前時間", "寫入檔案", "覆寫檔案", "新增筆記", "儲存記憶", "顯示詳細內容", "小精靈筆記" })
            Assert.True(CheckBoxNamed(window, name).IsEffectivelyVisible, name);
        window.Close();
    }

    [AvaloniaFact]
    public void Renders_without_horizontal_clipping_at_minimum_width()
    {
        var window = Host(NewViewModel(), 720);

        foreach (var text in All<TextBlock>(window).Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)))
        {
            var right = text.TranslatePoint(new Point(text.Bounds.Width, 0), window);
            Assert.True(right is not null && right.Value.X <= window.ClientSize.Width + 0.5, $"「{text.Text}」超出視窗寬度");
        }
        window.Close();
    }

    [AvaloniaFact]
    public void Narrow_window_moves_the_confirm_column_below_the_description()
    {
        var wide = Host(NewViewModel(), 1040);
        var confirmWide = All<StackPanel>(wide).First(s => s.Classes.Contains("tool-confirm"));
        Assert.Equal((0, 2), (Grid.GetRow(confirmWide), Grid.GetColumn(confirmWide)));
        wide.Close();

        var narrow = Host(NewViewModel(), 720, narrow: true);
        var confirm = All<StackPanel>(narrow).First(s => s.Classes.Contains("tool-confirm"));
        Assert.Equal((1, 1), (Grid.GetRow(confirm), Grid.GetColumn(confirm)));
        Assert.Equal(HorizontalAlignment.Left, confirm.HorizontalAlignment);
        narrow.Close();
    }

    [AvaloniaFact]
    public void Locked_tool_checkbox_is_checked_and_disabled_with_the_reason()
    {
        var window = Host(NewViewModel());

        var locked = CheckBoxNamed(window, "顯示詳細內容");
        Assert.True(locked.IsChecked);
        Assert.False(locked.IsEnabled);
        Assert.True(HasVisibleText(window, Strings.Tools_CannotDisable));
        window.Close();
    }

    [AvaloniaFact]
    public void Destructive_badge_uses_the_danger_style()
    {
        var window = Host(NewViewModel());

        var badge = All<Border>(window).Single(b => b.Classes.Contains("danger-badge") && b.IsEffectivelyVisible);
        Assert.True(window.TryFindResource("LumiDangerBackgroundBrush", window.ActualThemeVariant, out var expected));
        Assert.Equal(expected, badge.Background);
        Assert.True(HasVisibleText(window, Strings.Tools_PermissionDestructive));
        window.Close();
    }

    [AvaloniaFact]
    public void Only_write_and_network_tools_get_the_confirm_dropdown_and_others_show_text()
    {
        var window = Host(NewViewModel());

        Assert.Equal(2, All<ComboBox>(window).Count(c => c.IsEffectivelyVisible));   // 寫入檔案、新增筆記
        Assert.Equal(3, All<TextBlock>(window).Count(t => t.IsEffectivelyVisible && t.Text == Strings.Tools_ConfirmNotNeeded));
        Assert.Equal(2, All<TextBlock>(window).Count(t => t.IsEffectivelyVisible && t.Text == Strings.Tools_ConfirmAlwaysAsk));

        var memory = All<TextBlock>(window).Single(t => t.IsEffectivelyVisible && t.Text == Strings.Tools_ConfirmAlwaysAsk
            && ToolTip.GetTip(t) as string == Strings.Tools_AlwaysAskMemoryTooltip);
        Assert.NotNull(memory);
        window.Close();
    }

    [AvaloniaFact]
    public void Model_warning_row_is_shown_when_the_workbench_model_has_tools_off()
    {
        var off = FakeModelCatalog.Model with { ToolsEnabled = false };
        var catalog = new FakeModelCatalog { Current = new AppSettings([FakeModelCatalog.Connection], [off], off.Id, off.Id, ThemePreference.System) };
        var window = Host(NewViewModel(catalog));

        Assert.Contains(All<Border>(window), b => b.Classes.Contains("warn-row") && b.IsEffectivelyVisible);
        Assert.True(HasVisibleText(window, string.Format(Strings.Tools_ModelToolsOff, off.DisplayName)));
        window.Close();
    }

    [AvaloniaFact]
    public void Warning_row_is_hidden_and_reset_is_disabled_on_defaults()
    {
        var window = Host(NewViewModel());

        Assert.DoesNotContain(All<Border>(window), b => b.Classes.Contains("warn-row") && b.IsEffectivelyVisible);
        Assert.False(All<Button>(window).Single(b => b.Content as string == Strings.Tools_ResetDefaults).IsEffectivelyEnabled);
        Assert.True(HasVisibleText(window, Strings.Tools_ResetBlocked));
        window.Close();
    }
}
