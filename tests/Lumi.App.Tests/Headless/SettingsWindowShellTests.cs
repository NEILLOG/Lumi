using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;

namespace Lumi.App.Tests.Headless;

/// <summary>UD-1：設定視窗外殼——自訂標題列、Menu（分類、停用佔位、回到對話）、頁首元件。</summary>
public class SettingsWindowShellTests
{
    private sealed class RecordingModes : IAppModeCoordinator
    {
        public int OpenWorkbenchCalls { get; private set; }
        public Lumi.Core.Chat.ChatMode ActiveMode => Lumi.Core.Chat.ChatMode.Workbench;
        public bool IsSpriteTemporarilyHidden => false;
        public void Start() { }
        public bool EnterSpriteMode() => true;
        public void OpenWorkbench(string? scrollToCallId = null) => OpenWorkbenchCalls++;
        public void WorkbenchWillSend() { }
        public bool WorkbenchMayClose() => true;
        public void ShowSprite() { }
        public void HideSpriteTemporarily() { }
        public void Exit() { }
    }

    private static SettingsWindow Show(SettingsHost host)
    {
        var window = new SettingsWindow(host.Settings);
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static Color TokenColor(Window window, string key)
    {
        Assert.True(window.TryFindResource(key, window.ActualThemeVariant, out var value), $"找不到 token {key}");
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }

    private static Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static IEnumerable<string> TextsIn(Visual root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "");

    private static ContentPresenter PresenterOf(ListBoxItem item) =>
        item.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");

    // ---- 視窗與標題列 ----

    [AvaloniaFact]
    public void Window_UsesTheCustomTitleBar_WithoutTheSpriteButton()
    {
        using var host = new SettingsHost();
        var window = Show(host);

        Assert.True(window.ExtendClientAreaToDecorationsHint);
        Assert.True(window.Width >= 900);
        Assert.True(window.Height >= 640);
        Assert.Equal(720, window.MinWidth);

        var bar = window.FindByName<CustomTitleBar>("TitleBar");
        Assert.False(bar.ShowSpriteButton);
        Assert.Null(bar.DataContext);   // 不繼承設定視窗的 ViewModel：標題列不需要工作台的繫結
        Assert.False(bar.FindByName<Button>("SpriteModeButton").IsEffectivelyVisible);
        Assert.False(bar.FindByName<Border>("Divider").IsEffectivelyVisible);
        Assert.True(bar.FindByName<TextBlock>("Brand").IsEffectivelyVisible);
        Assert.Contains(Strings.TitleBar_Brand, TextsIn(bar));
        Assert.DoesNotContain(Strings.TitleBar_LogoMark, TextsIn(bar));   // 標題列只有軟體名稱，不放 logo
        window.Close();
    }

    [AvaloniaFact]
    public void TitleBar_ShowsTheSpriteButton_ByDefault_AndHidesItWhenAsked()
    {
        var bar = new CustomTitleBar { Platform = TitleBarPlatform.Windows };
        var host = new Window { Width = 600, Height = 100, Content = bar };
        host.Show();
        host.UpdateLayout();
        Assert.True(bar.ShowSpriteButton);
        Assert.True(bar.SpriteModeButton.IsEffectivelyVisible);
        Assert.True(bar.Divider.IsEffectivelyVisible);

        bar.ShowSpriteButton = false;
        host.UpdateLayout();
        Assert.False(bar.SpriteModeButton.IsEffectivelyVisible);
        Assert.False(bar.Divider.IsEffectivelyVisible);

        bar.ShowSpriteButton = true;
        host.UpdateLayout();
        Assert.True(bar.SpriteModeButton.IsEffectivelyVisible);
        host.Close();
    }

    // ---- Menu ----

    [AvaloniaFact]
    public void Menu_Is240Wide_OnTheChromeBackground_WithARightBorder()
    {
        using var host = new SettingsHost();
        var window = Show(host);

        var menu = window.FindByName<Border>("Menu");
        Assert.Equal(240, menu.Bounds.Width);
        Assert.Equal(new Thickness(0, 0, 1, 0), menu.BorderThickness);
        Assert.Equal(TokenColor(window, "LumiChromeBrush"), ColorOf(menu.Background));
        Assert.Equal(TokenColor(window, "LumiBorderBrush"), ColorOf(menu.BorderBrush));

        // 標題區：L 標誌、「Lumi 設定」、Caption
        var texts = TextsIn(menu).ToList();
        Assert.Contains(Strings.Settings_MenuTitle, texts);
        Assert.Contains("個人偏好與 AI 連線", texts);
        Assert.Contains(Strings.TitleBar_LogoMark, texts);
        // 底部：版本與金鑰說明
        Assert.StartsWith("Lumi ", window.FindByName<TextBlock>("MenuVersion").Text);
        Assert.Equal("金鑰只儲存在這台裝置", window.FindByName<TextBlock>("MenuKeysNote").Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Menu_ListsTheEightCategories_WithPlainNamesAndSubtitles()
    {
        using var host = new SettingsHost();
        var window = Show(host);

        var list = window.FindByName<ListBox>("CategoryList");
        Assert.Equal(8, list.ItemCount);
        var expected = new[]
        {
            ("CategoryConnections", "AI 連線", "供應商、金鑰與模型"),
            ("CategoryPersona", "人格與主動性", "回答風格與主動程度"),
            ("CategoryMemory", "記憶", "Lumi 記得的事"),
            ("CategoryTools", "工具", "能用的工具與確認方式"),
            ("CategoryGeneral", "外觀與操作", "主題、色票與明暗"),
            ("CategorySprite", "小精靈", "角色、尺寸與語氣"),
            ("CategorySpeech", "語音", "Whisper 語音辨識"),
            ("CategoryNetwork", "網路", "Proxy、瀏覽器"),
        };
        for (var i = 0; i < expected.Length; i++)
        {
            var item = Assert.IsType<ListBoxItem>(list.Items[i]);
            Assert.Equal(expected[i].Item1, item.Name);
            Assert.Equal([expected[i].Item2, expected[i].Item3], TextsIn(item).ToArray());
            Assert.Single(item.GetVisualDescendants().OfType<Lumi.App.Controls.LumiIcon>());   // 每個項目前面有圖示
            Assert.Equal(expected[i].Item2, Avalonia.Automation.AutomationProperties.GetName(item));
        }

        // 主項目 14／600；副標 Caption 11
        var first = (ListBoxItem)list.Items[0]!;
        var main = first.GetVisualDescendants().OfType<TextBlock>().First();
        var caption = first.GetVisualDescendants().OfType<TextBlock>().Last();
        Assert.Equal(14, main.FontSize);
        Assert.Equal(FontWeight.SemiBold, main.FontWeight);
        Assert.Equal(11, caption.FontSize);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SelectedMenuItem_UsesSoftBackground_AndOnSoftText_WithRadius10()
    {
        using var host = new SettingsHost();
        var window = Show(host);
        var list = window.FindByName<ListBox>("CategoryList");

        Assert.Equal(0, list.SelectedIndex);   // 預設是 AI 連線
        var connections = window.FindByName<ListBoxItem>("CategoryConnections");
        var general = window.FindByName<ListBoxItem>("CategoryGeneral");
        Assert.Equal(new CornerRadius(10), connections.CornerRadius);

        var soft = TokenColor(window, "LumiAccentSoftBrush");
        var onSoft = TokenColor(window, "LumiTextOnSoftBrush");
        Assert.Equal(soft, ColorOf(PresenterOf(connections).Background));
        Assert.Equal(onSoft, ColorOf(PresenterOf(connections).Foreground));
        Assert.Equal(onSoft, ColorOf(connections.GetVisualDescendants().OfType<TextBlock>().First().Foreground));
        Assert.NotEqual(soft, (PresenterOf(general).Background as ISolidColorBrush)?.Color);

        // 點另一個分類 → ViewModel 的 CategoryIndex 跟著變，選取樣式也移過去
        list.SelectedIndex = 4;
        window.UpdateLayout();
        Assert.Equal(4, host.Settings.CategoryIndex);
        Assert.True(host.Settings.ShowGeneral);
        Assert.Equal(soft, ColorOf(PresenterOf(general).Background));
        Assert.NotEqual(soft, (PresenterOf(connections).Background as ISolidColorBrush)?.Color);

        // ViewModel 導覽也會反映到 Menu
        await host.Settings.NavigateAsync(new SettingsNavigation(SettingsSection.Sprite));
        window.UpdateLayout();
        Assert.Equal(5, list.SelectedIndex);
        window.Close();
    }

    [AvaloniaFact]
    public void ComingSoonItems_AreDisabled_WithATooltipAndAVisibleCaption()
    {
        using var host = new SettingsHost();
        var window = Show(host);
        var list = window.FindByName<ListBox>("ComingSoonList");
        Assert.Equal(2, list.ItemCount);

        foreach (var (name, title) in new[] { ("CategoryHome", "設定首頁"), ("CategoryPrivacy", "資料與隱私") })
        {
            var item = window.FindByName<ListBoxItem>(name);
            Assert.False(item.IsEnabled);
            Assert.False(item.IsEffectivelyEnabled);
            Assert.Equal("即將推出", ToolTip.GetTip(item));
            Assert.True(ToolTip.GetShowOnDisabled(item));
            Assert.Equal([title, "即將推出"], TextsIn(item).ToArray());   // Caption 看得到，不只靠 Tooltip
            Assert.All(item.GetVisualDescendants().OfType<TextBlock>(), t => Assert.True(t.IsEffectivelyVisible));
            Assert.Single(item.GetVisualDescendants().OfType<Lumi.App.Controls.LumiIcon>());
        }

        // 停用項目不可選：點不到，選取與分類都不會變
        list.SelectedIndex = 0;
        window.UpdateLayout();
        Assert.Equal(0, host.Settings.CategoryIndex);
        window.Close();
    }

    // ---- 回到對話 ----

    [AvaloniaFact]
    public void BackToChat_IsATertiaryButtonWithAnArrowIcon_AndCallsReturnToWorkbench()
    {
        using var host = new SettingsHost();
        var window = Show(host);

        var button = window.FindByName<Button>("BackToChatButton");
        Assert.Contains("tertiary", button.Classes);
        Assert.Contains("回到對話", TextsIn(button));
        Assert.Single(button.GetVisualDescendants().OfType<Lumi.App.Controls.LumiIcon>());
        Assert.Equal("回到對話", Avalonia.Automation.AutomationProperties.GetName(button));
        Assert.NotNull(ToolTip.GetTip(button));
        Assert.Same(host.Settings.ReturnToChatCommand, button.Command);

        Assert.Equal(0, host.WindowService.ReturnToWorkbenchCalls);
        button.Command!.Execute(null);
        Assert.Equal(1, host.WindowService.ReturnToWorkbenchCalls);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ReturnToWorkbench_ClosesTheSettingsWindow_AndOpensTheWorkbench()
    {
        using var host = new SettingsHost();
        var modes = new RecordingModes();
        var service = new SettingsWindowService(() => new SettingsWindow(host.Settings), modes);

        var shown = service.ShowAsync(new SettingsNavigation(SettingsSection.Persona));
        Assert.False(shown.IsCompleted);

        Assert.True(await service.ReturnToWorkbench());
        await shown;   // 視窗關閉了
        Assert.Equal(1, modes.OpenWorkbenchCalls);
    }

    [AvaloniaFact]
    public async Task ReturnToWorkbench_WithUnsavedChanges_AsksFirst_AndStaysWhenCancelled()
    {
        using var host = new SettingsHost();
        var modes = new RecordingModes();
        var service = new SettingsWindowService(() => new SettingsWindow(host.Settings), modes);
        var shown = service.ShowAsync(new SettingsNavigation(SettingsSection.Persona));

        host.Persona.CustomPersona = "改到一半";
        Assert.True(host.Settings.HasUnsavedChanges);

        host.Confirm.Answer = false;
        Assert.False(await service.ReturnToWorkbench());
        Assert.Single(host.Confirm.Asked);
        Assert.Equal(0, modes.OpenWorkbenchCalls);
        Assert.False(shown.IsCompleted);   // 設定視窗還開著

        host.Confirm.Answer = true;
        Assert.True(await service.ReturnToWorkbench());
        await shown;
        Assert.Equal(1, modes.OpenWorkbenchCalls);
    }

    [AvaloniaFact]
    public async Task ReturnToWorkbench_WithoutAnOpenSettingsWindow_JustOpensTheWorkbench()
    {
        using var host = new SettingsHost();
        var modes = new RecordingModes();
        var service = new SettingsWindowService(() => new SettingsWindow(host.Settings), modes);

        Assert.True(await service.ReturnToWorkbench());
        Assert.Equal(1, modes.OpenWorkbenchCalls);
    }

    // ---- 頁首元件 ----

    [AvaloniaFact]
    public void PageHeader_ShowsEyebrowTitleDescription_AndTheActionsContent()
    {
        var action = new Button { Content = "新增" };
        var header = new SettingsPageHeader { Eyebrow = "AI 連線", Title = "供應商與模型", Description = "說明文字", Actions = action };
        var window = new Window { Width = 800, Height = 300, Content = header };
        window.Show();
        window.UpdateLayout();

        var eyebrow = header.FindByName<TextBlock>("EyebrowText");
        var title = header.FindByName<TextBlock>("TitleText");
        var description = header.FindByName<TextBlock>("DescriptionText");
        Assert.Equal("AI 連線", eyebrow.Text);
        Assert.Equal("供應商與模型", title.Text);
        Assert.Equal("說明文字", description.Text);
        Assert.Equal(28, title.FontSize);
        Assert.Equal(FontWeight.Bold, title.FontWeight);
        Assert.Equal(14, description.FontSize);
        Assert.Equal(TokenColor(window, "LumiTextMutedBrush"), ColorOf(description.Foreground));
        Assert.Equal(TokenColor(window, "LumiAccentTextBrush"), ColorOf(eyebrow.Foreground));

        var actions = header.FindByName<ContentPresenter>("ActionsHost");
        Assert.True(actions.IsEffectivelyVisible);
        Assert.Same(action, actions.Child);
        Assert.True(action.IsEffectivelyVisible);
        // 動作在標題的右邊
        Assert.True(action.TranslatePoint(default, header)!.Value.X > title.TranslatePoint(default, header)!.Value.X);
        window.Close();
    }

    [AvaloniaFact]
    public void PageHeader_WithoutEyebrowDescriptionOrActions_HidesThem()
    {
        var header = new SettingsPageHeader { Title = "只有標題" };
        var window = new Window { Width = 800, Height = 300, Content = header };
        window.Show();
        window.UpdateLayout();

        Assert.True(header.FindByName<TextBlock>("TitleText").IsEffectivelyVisible);
        Assert.False(header.FindByName<TextBlock>("EyebrowText").IsEffectivelyVisible);
        Assert.False(header.FindByName<TextBlock>("DescriptionText").IsEffectivelyVisible);
        Assert.False(header.FindByName<ContentPresenter>("ActionsHost").IsEffectivelyVisible);

        header.Description = "後來才有";
        window.UpdateLayout();
        Assert.True(header.FindByName<TextBlock>("DescriptionText").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task EachSettingsPage_StartsWithItsHeader()
    {
        using var host = new SettingsHost();
        var window = Show(host);

        var cases = new (SettingsSection Section, string Header, string Eyebrow, string Title, string Description)[]
        {
            (SettingsSection.Connections, "ConnectionsHeader", "AI 連線", "供應商與模型", "新增自己的 API 金鑰，並選擇每次對話預設使用的模型。"),
            (SettingsSection.Persona, "PersonaHeader", "人格與主動性", "回答風格", Strings.Settings_HeaderPersonaDescription),
            (SettingsSection.General, "GeneralHeader", "外觀與操作", "外觀與操作", "選擇主題色票與明暗。"),
            (SettingsSection.Sprite, "SpriteHeader", "小精靈", "小精靈", Strings.Settings_HeaderSpriteDescription),
        };
        foreach (var c in cases)
        {
            await host.Settings.NavigateAsync(new SettingsNavigation(c.Section));
            window.UpdateLayout();

            var shown = window.GetVisualDescendants().OfType<SettingsPageHeader>().Where(h => h.IsEffectivelyVisible).ToList();
            var header = Assert.Single(shown);
            Assert.Equal(c.Header, header.Name);
            Assert.Equal(c.Eyebrow, header.Eyebrow);
            Assert.Equal(c.Title, header.Title);
            Assert.Equal(c.Description, header.Description);
        }
        window.Close();
    }

    [AvaloniaFact]
    public async Task ExistingPageControls_KeepTheirNames()
    {
        using var host = new SettingsHost();
        var window = Show(host);

        await host.Settings.NavigateAsync(new SettingsNavigation(SettingsSection.Persona));
        window.UpdateLayout();
        foreach (var name in new[] { "PersonaStyleBox", "PersonaProactivityBox", "PersonaCustomBox", "PersonaSaveButton", "PersonaResetButton" })
            Assert.NotNull(window.GetVisualDescendants().OfType<Control>().SingleOrDefault(c => c.Name == name));

        await host.Settings.NavigateAsync(new SettingsNavigation(SettingsSection.General));
        window.UpdateLayout();
        Assert.True(window.FindByName<ListBox>("PaletteBox").IsEffectivelyVisible);

        await host.Settings.NavigateAsync(new SettingsNavigation(SettingsSection.Sprite));
        window.UpdateLayout();
        foreach (var name in new[] { "SpriteList", "SpritePanel", "SpritePreviewHost", "SpriteSizeBox", "SpriteVoiceBox", "SpriteStateList" })
            Assert.True(window.FindByName<Control>(name).IsEffectivelyVisible, name);
        window.Close();
    }
}
