using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.Views;
using Lumi.Core.Speech;

namespace Lumi.App.Tests.Headless;

/// <summary>M6 6B：設定視窗「語音」頁的畫面——Menu 分類、欄位與按鈕的名稱、錯誤與警告的文字、測試結果的樣式。</summary>
public class SpeechPageTests
{
    private static bool Shown(Control c) => c.IsEffectivelyVisible;

    /// <summary>有名字的控制項在畫面上而且看得到（找不到也算沒有）。</summary>
    private static bool IsShowingNamed(Visual root, string name) =>
        root.GetVisualDescendants().OfType<Control>().Any(c => c.Name == name && c.IsEffectivelyVisible);

    private static bool HasVisibleText(Visual root, string text) =>
        root.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == text);

    [AvaloniaFact]
    public async Task TheSpeechCategory_IsTheLastMenuItem_WithItsName()
    {
        using var rig = await SettingsRig.CreateAsync("connections-list");
        var list = rig.Window.FindByName<ListBox>("CategoryList");

        Assert.Equal(7, list.ItemCount);
        var item = Assert.IsType<ListBoxItem>(list.Items[6]);
        Assert.Equal("CategorySpeech", item.Name);
        Assert.Equal("語音", AutomationProperties.GetName(item));
        Assert.Equal(["語音", "Whisper 語音辨識"], item.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray());
    }

    [AvaloniaFact]
    public async Task SelectingTheSpeechCategory_ShowsOnlyTheSpeechPage()
    {
        using var rig = await SettingsRig.CreateAsync("connections-list");

        rig.Window.FindByName<ListBox>("CategoryList").SelectedIndex = 6;
        rig.Settle();

        Assert.True(rig.Host.Settings.ShowSpeech);
        Assert.True(Shown(rig.Window.FindByName<StackPanel>("SpeechForm")));
        Assert.True(Shown(rig.Window.FindByName<SettingsPageHeader>("SpeechHeader")));
        Assert.False(IsShowingNamed(rig.Window, "PersonaForm"));
        Assert.False(IsShowingNamed(rig.Window, "ConnectionsPanel"));
    }

    [AvaloniaFact]
    public async Task TheFieldsAndButtons_HaveAccessibleNames()
    {
        using var rig = await SettingsRig.CreateAsync("speech");
        var window = rig.Window;

        foreach (var name in new[] { "SpeechEndpointBox", "SpeechLanguageBox", "SpeechTimeoutBox", "SpeechTokenBox" })
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(window.FindByName<TextBox>(name))), name);

        // 有文字的按鈕，名稱就是文字（快選語言、測試、還原、儲存）
        foreach (var name in new[] { "SpeechLangAutoButton", "SpeechLangZhButton", "SpeechLangEnButton", "SpeechLangJaButton", "SpeechTestButton", "SpeechRevertButton", "SpeechSaveButton" })
        {
            var button = window.FindByName<Button>(name);
            Assert.False(string.IsNullOrWhiteSpace(button.Content as string), name);
        }
        Assert.Equal(Strings.Speech_LangZh, window.FindByName<Button>("SpeechLangZhButton").Content);
        Assert.Equal(Strings.Speech_Save, window.FindByName<Button>("SpeechSaveButton").Content);
    }

    [AvaloniaFact]
    public async Task Buttons_FollowTheLevelRule_SaveIsTheOnlyPrimary()
    {
        using var rig = await SettingsRig.CreateAsync("speech-token-stored");
        var window = rig.Window;

        Assert.Contains("primary", window.FindByName<Button>("SpeechSaveButton").Classes);
        Assert.Contains("secondary", window.FindByName<Button>("SpeechTestButton").Classes);
        Assert.Contains("secondary", window.FindByName<Button>("SpeechRevertButton").Classes);
        Assert.Contains("secondary", window.FindByName<Button>("SpeechReplaceTokenButton").Classes);
        Assert.Contains("tertiary", window.FindByName<Button>("SpeechClearTokenButton").Classes);

        var primaries = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("primary") && Shown(b)).ToList();
        Assert.Single(primaries);
    }

    [AvaloniaFact]
    public async Task AnEmptyEndpoint_ShowsTheErrorAsText_AndTheTestReason()
    {
        using var rig = await SettingsRig.CreateAsync("speech");
        var window = rig.Window;

        Assert.True(HasVisibleText(window, Strings.Speech_EndpointRequired));
        Assert.True(Shown(window.FindByName<TextBlock>("SpeechTestBlockedText")));
        Assert.Equal(Strings.Speech_EndpointRequired, window.FindByName<TextBlock>("SpeechTestBlockedText").Text);
        Assert.False(window.FindByName<Button>("SpeechTestButton").IsEffectivelyEnabled);
        Assert.False(window.FindByName<Button>("SpeechSaveButton").IsEffectivelyEnabled);
        Assert.False(Shown(window.FindByName<TextBlock>("SpeechSaveBlockedText")));   // 沒有要儲存的變更，不說明
    }

    [AvaloniaFact]
    public async Task InvalidEndpoint_ShowsTheCoreReason_AndTheSaveReason()
    {
        using var rig = await SettingsRig.CreateAsync("speech");
        rig.Host.Speech.Endpoint = "http://speech.example.com";
        rig.Settle();

        var reason = SpeechEndpointRules.Validate("http://speech.example.com").Reason;
        Assert.True(HasVisibleText(rig.Window, reason!));
        Assert.True(Shown(rig.Window.FindByName<TextBlock>("SpeechSaveBlockedText")));
        Assert.Equal(reason, rig.Window.FindByName<TextBlock>("SpeechSaveBlockedText").Text);
        Assert.False(rig.Window.FindByName<Button>("SpeechSaveButton").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public async Task StoredToken_ShowsTheLastFour_AndTheReplaceAndClearButtons()
    {
        using var rig = await SettingsRig.CreateAsync("speech-token-stored");
        var window = rig.Window;

        Assert.True(HasVisibleText(window, string.Format(Strings.Speech_TokenSetHint, "5678")));
        Assert.True(Shown(window.FindByName<Button>("SpeechReplaceTokenButton")));
        Assert.True(Shown(window.FindByName<Button>("SpeechClearTokenButton")));
        Assert.False(Shown(window.FindByName<Grid>("SpeechTokenInputRow")));
        Assert.False(Shown(window.FindByName<TextBlock>("SpeechTokenHostChangedText")));
    }

    [AvaloniaFact]
    public async Task ChangingTheHost_ShowsTheWarning_AndTheTokenInput()
    {
        using var rig = await SettingsRig.CreateAsync("speech-token-stored");
        var window = rig.Window;

        rig.Host.Speech.Endpoint = "https://other.example.com/v1";
        rig.Settle();

        Assert.True(Shown(window.FindByName<TextBlock>("SpeechTokenHostChangedText")));
        Assert.Equal(Strings.Speech_TokenHostChanged, window.FindByName<TextBlock>("SpeechTokenHostChangedText").Text);
        Assert.False(Shown(window.FindByName<Grid>("SpeechTokenStatusRow")));
        Assert.True(Shown(window.FindByName<Grid>("SpeechTokenInputRow")));
        Assert.False(window.FindByName<Button>("SpeechTestButton").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public async Task TestSuccess_IsSuccessStyled_WithTheCheckIcon()
    {
        using var rig = await SettingsRig.CreateAsync("speech-test-ok");
        var ok = rig.Window.FindByName<Border>("SpeechTestOk");

        Assert.True(Shown(ok));
        Assert.Contains("ok", ok.Classes);
        Assert.Contains(Strings.Speech_TestOk, ok.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        Assert.Single(ok.GetVisualDescendants().OfType<Lumi.App.Controls.LumiIcon>());
        Assert.False(Shown(rig.Window.FindByName<Border>("SpeechTestFail")));
    }

    [AvaloniaFact]
    public async Task TestFailure_IsDangerStyled_WithAWarningIconAndTheReason()
    {
        using var rig = await SettingsRig.CreateAsync("speech-test-fail");
        var fail = rig.Window.FindByName<Border>("SpeechTestFail");

        Assert.True(Shown(fail));
        Assert.Contains("fail", fail.Classes);
        var texts = fail.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains(Strings.Speech_TestFailed, texts);
        Assert.Contains("Token 無效，或沒有 WHISPER_API 權限。", texts);
        Assert.Single(fail.GetVisualDescendants().OfType<Lumi.App.Controls.LumiIcon>());
        Assert.False(Shown(rig.Window.FindByName<Border>("SpeechTestOk")));
    }
}
