using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.Views;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Headless;

/// <summary>
/// UE-2：設定視窗的可存取性掃描、Tab 順序與鍵盤操作（RWD原則 第 7 節、plan/12 驗收標準第 5、7 條）。
/// 每個分類頁（AI 連線的「連線」「模型設定」兩個分段、人格與主動性、外觀與操作、小精靈）在寬（1040）與 stacked（800）兩種寬度各掃一次。
/// </summary>
public class SettingsAccessibilityTests
{
    private const double Wide = 1040;
    private const double Stacked = 800;

    public static TheoryData<string, double> PageWidths()
    {
        var data = new TheoryData<string, double>();
        foreach (var page in SettingsRig.Pages)
        {
            data.Add(page, Wide);
            data.Add(page, Stacked);
        }
        return data;
    }

    // ---- 共用 ----

    private static bool HasVisibleText(Visual control) =>
        control.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text));

    private static string Describe(Control c) => $"{c.GetType().Name}#{c.Name}";

    private static bool InsideScrollBar(Visual v) => v.GetVisualAncestors().OfType<ScrollBar>().Any();

    private static bool HasName(Control c) =>
        !string.IsNullOrWhiteSpace(AutomationProperties.GetName(c)) || AutomationProperties.GetLabeledBy(c) is not null;

    private static bool IsTabStop(InputElement e) =>
        e.Focusable && KeyboardNavigation.GetIsTabStop(e) && e.IsEffectivelyEnabled && e.IsEffectivelyVisible;

    /// <summary>從視窗開頭一路按 Tab 會依序經過的控制項（回到起點或走完為止）。</summary>
    private static List<InputElement> TabChain(Window window)
    {
        var chain = new List<InputElement>();
        IInputElement current = window;
        for (var i = 0; i < 120; i++)
        {
            if (KeyboardNavigationHandler.GetNext(current, NavigationDirection.Next) is not InputElement next || chain.Contains(next))
                break;
            chain.Add(next);
            current = next;
        }
        return chain;
    }

    /// <summary>Tab 鏈上一個控制項的簡稱：有名字用名字，清單項目用「item:清單名」，其他按鈕用文字。</summary>
    private static string Label(InputElement e)
    {
        if (e is Control { Name: { Length: > 0 } name })
            return name;
        if (e is ListBoxItem item && item.FindAncestorOfType<ListBox>() is { } list)
            return "item:" + list.Name;
        if (e is ContentControl { Content: string text })
            return text;
        return e.GetType().Name;
    }

    // ---- 1. 可存取性掃描 ----

    [AvaloniaTheory]
    [MemberData(nameof(PageWidths))]
    public async Task EveryVisibleButtonWithoutText_HasAnAccessibleNameAndATooltip(string page, double width)
    {
        using var rig = await SettingsRig.CreateAsync(page, width);
        var buttons = rig.Window.GetVisualDescendants().OfType<ContentControl>()
            .Where(c => c is Button or ToggleButton && c is not RepeatButton && c.IsEffectivelyVisible && !InsideScrollBar(c))
            .ToList();
        Assert.NotEmpty(buttons);

        foreach (var button in buttons)
        {
            if (HasVisibleText(button))
                continue;   // 有文字的按鈕，文字就是名稱
            // 沒有文字（純圖示）：可存取名稱＋Tooltip 都要有
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)), $"{page}@{width}：{Describe(button)} 缺少 AutomationProperties.Name");
            Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(button) as string), $"{page}@{width}：{Describe(button)} 缺少 ToolTip.Tip");
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(PageWidths))]
    public async Task EveryVisibleInput_HasAnAccessibleName(string page, double width)
    {
        using var rig = await SettingsRig.CreateAsync(page, width);
        var inputs = rig.Window.GetVisualDescendants().OfType<Control>()
            .Where(c => c is TextBox or ComboBox or NumericUpDown or CheckBox && c.IsEffectivelyVisible)
            .ToList();

        foreach (var input in inputs)
        {
            // CheckBox 的文字就是名稱；其他輸入元件要有 AutomationProperties.Name（或 LabeledBy）。
            // 可編輯的 ComboBox 與 NumericUpDown 範本裡真正拿到焦點的 TextBox 也算（名稱由外層轉給它）。
            var named = HasName(input) || (input is CheckBox && HasVisibleText(input));
            Assert.True(named, $"{page}@{width}：{Describe(input)} 沒有可存取的名稱");
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(PageWidths))]
    public async Task EveryTabStop_HasANameOrVisibleText_AndCardListItemsAreNamed(string page, double width)
    {
        using var rig = await SettingsRig.CreateAsync(page, width);
        foreach (var stop in TabChain(rig.Window).OfType<Control>())
        {
            var named = HasName(stop) || (stop is not TextBox && HasVisibleText(stop));
            Assert.True(named, $"{page}@{width}：Tab 停靠點 {Describe(stop)} 沒有可存取的名稱");
        }

        // 可點擊的卡片（ListBox.cards 的每一項）一律有 AutomationProperties.Name，不靠內部文字拼湊
        var cards = rig.Window.GetVisualDescendants().OfType<ListBoxItem>()
            .Where(i => i.IsEffectivelyVisible && i.FindAncestorOfType<ListBox>() is { } list && list.Classes.Contains("cards"))
            .ToList();
        foreach (var card in cards)
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(card)), $"{page}@{width}：卡片 {Describe(card)} 缺少 AutomationProperties.Name");

        // 清單本身也有名稱（螢幕閱讀器念「清單」時要知道是哪一個）
        foreach (var list in rig.Window.GetVisualDescendants().OfType<ListBox>().Where(l => l.IsEffectivelyVisible))
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(list)), $"{page}@{width}：清單 {Describe(list)} 缺少 AutomationProperties.Name");
    }

    [AvaloniaFact]
    public async Task ConnectionAndModelCards_AreNamedWithTheirStatus()
    {
        using var rig = await SettingsRig.CreateAsync("connections-list");
        var connection = rig.Window.FindByName<ListBox>("ConnectionList").GetVisualDescendants().OfType<ListBoxItem>().First();
        var item = rig.Host.Connections.Items[0];
        Assert.Equal($"{item.Name}，{item.Subtitle}，{item.StatusText}", AutomationProperties.GetName(connection));

        rig.Host.Settings.TabIndex = 1;
        rig.Settle();
        var model = rig.Window.FindByName<ListBox>("ModelList").GetVisualDescendants().OfType<ListBoxItem>().First();
        var modelItem = rig.Host.Models.Items[0];
        Assert.Equal($"{modelItem.DisplayName}，{modelItem.CardSubtitle}，{modelItem.StatusText}", AutomationProperties.GetName(model));
    }

    [AvaloniaFact]
    public async Task TheInnerTextBoxes_OfEditableComboBoxAndNumericUpDown_AreNamedLikeTheirOwner()
    {
        using var rig = await SettingsRig.CreateAsync("models-advanced");
        var panel = rig.Window.FindByName<ModelsPanel>("ModelsPanel");
        foreach (var name in new[] { "ModelIdBox", "MaxOutputBox", "ContextWindowBox" })
        {
            var owner = panel.FindByName<Control>(name);
            var inner = owner.GetVisualDescendants().OfType<TextBox>().Single();
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(owner)), name);
            Assert.Equal(AutomationProperties.GetName(owner), AutomationProperties.GetName(inner));
        }
    }

    [AvaloniaTheory]
    [InlineData(Wide)]
    [InlineData(Stacked)]
    public async Task TheDisabledMenuItems_HaveAVisibleExplanation_AndAreNotOnTheTabChain(double width)
    {
        using var rig = await SettingsRig.CreateAsync("connections-list", width);
        var chain = TabChain(rig.Window);
        foreach (var name in new[] { "CategoryHome", "CategoryPrivacy" })
        {
            var item = rig.Window.FindByName<ListBoxItem>(name);
            Assert.False(item.IsEffectivelyEnabled);
            Assert.True(item.IsEffectivelyVisible);
            // 說明是看得到的文字（Caption），不只靠 Tooltip
            Assert.Contains(item.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == Strings.Settings_ComingSoon);
            Assert.Equal(Strings.Settings_ComingSoon, ToolTip.GetTip(item));
            Assert.DoesNotContain(item, chain);
            Assert.False(IsTabStop(item));
        }
        Assert.DoesNotContain(chain, e => e is ListBoxItem item && item.FindAncestorOfType<ListBox>() is { Name: "ComingSoonList" });
    }

    [AvaloniaFact]
    public async Task ADisabledSaveButton_HasAVisibleReason()
    {
        using var rig = await SettingsRig.CreateAsync("connections-errors");
        var panel = rig.Window.FindByName<ConnectionsPanel>("ConnectionsPanel");
        var save = panel.FindByName<Button>("SaveButton");
        Assert.False(save.IsEffectivelyEnabled);
        Assert.True(panel.FindByName<TextBlock>("FieldErrorsText").IsEffectivelyVisible);
        Assert.Equal(Strings.Form_FixErrors, panel.FindByName<TextBlock>("FieldErrorsText").Text);
        Assert.DoesNotContain(save, TabChain(rig.Window));
    }

    // ---- 2. Tab 順序 ----

    /// <summary>各畫面狀態在 Tab 鏈上必須依序出現的控制項（簡稱見 <see cref="Label"/>）；中間可以夾其他停靠點。</summary>
    private static readonly Dictionary<string, string[]> ExpectedOrder = new()
    {
        ["connections-list"] = ["BackToChatButton", "CategoryConnections", "NewConnectionButton", "TabConnections", "item:ConnectionList", "AddConnectionButton"],
        ["connections-form"] = ["BackToChatButton", "CategoryConnections", "NewConnectionButton", "TabConnections", "item:ConnectionList", "AddConnectionButton",
            "NameBox", "KeyBox", "RevealKeyButton", "ExpanderHeader", "DeleteButton", "TestButton", "SaveButton"],
        ["connections-advanced"] = ["BackToChatButton", "CategoryConnections", "NewConnectionButton", "TabConnections", "item:ConnectionList", "AddConnectionButton",
            "NameBox", "KeyBox", "RevealKeyButton", "ExpanderHeader", "FormatBox", "BaseUrlBox", "DeleteButton", "TestButton", "SaveButton"],
        ["connections-key-stored"] = ["BackToChatButton", "CategoryConnections", "NewConnectionButton", "TabConnections", "item:ConnectionList", "AddConnectionButton",
            "NameBox", "更換", "清除", "ExpanderHeader", "DeleteButton", "TestButton", "SaveButton"],
        ["models-list"] = ["BackToChatButton", "CategoryConnections", "NewModelButton", "TabModels", "WorkbenchDefaultBox", "SpriteDefaultBox", "item:ModelList", "AddModelButton"],
        ["models-form"] = ["BackToChatButton", "CategoryConnections", "NewModelButton", "TabModels", "WorkbenchDefaultBox", "SpriteDefaultBox", "item:ModelList", "AddModelButton",
            "ConnectionBox", "PART_EditableTextBox", "RefreshModelsButton", "DisplayNameBox", "ShowThinkingCheckBox", "ToolsEnabledCheckBox", "ExpanderHeader", "DeleteButton", "SaveButton"],
        ["models-advanced"] = ["BackToChatButton", "CategoryConnections", "NewModelButton", "TabModels", "WorkbenchDefaultBox", "SpriteDefaultBox", "item:ModelList", "AddModelButton",
            "ConnectionBox", "PART_EditableTextBox", "RefreshModelsButton", "DisplayNameBox", "ShowThinkingCheckBox", "ToolsEnabledCheckBox", "ExpanderHeader", "PART_TextBox", "DeleteButton", "SaveButton"],
        ["persona"] = ["BackToChatButton", "CategoryPersona", "PersonaStyleBox", "PersonaProactivityBox", "PersonaCustomBox", "PersonaResetButton"],
        ["persona-dirty"] = ["BackToChatButton", "CategoryPersona", "PersonaStyleBox", "PersonaProactivityBox", "PersonaCustomBox", "PersonaResetButton", "PersonaSaveButton"],
        ["general"] = ["BackToChatButton", "CategoryGeneral", "item:PaletteBox", "ThemeBox"],
        ["sprite"] = ["BackToChatButton", "CategorySprite", "item:SpriteList", "SpriteSizeBox", "SpriteVoiceBox", "item:SpriteStateList"],
        ["sprite-issues"] = ["BackToChatButton", "CategorySprite", "item:SpriteList", "SpriteReloadButton", "SpriteOpenFolderButton", "SpriteSizeBox", "SpriteVoiceBox", "item:SpriteStateList"],
        // 沒有儲存的變更時「還原」「儲存」停用（不在 Tab 鏈上）；停用的原因寫在旁邊的 Caption
        ["speech"] = ["BackToChatButton", "CategorySpeech", "SpeechEndpointBox", "SpeechLanguageBox", "SpeechLangAutoButton", "SpeechLangZhButton",
            "SpeechTimeoutBox", "SpeechTokenBox", "SpeechRevealTokenButton"],
        ["speech-token-stored"] = ["BackToChatButton", "CategorySpeech", "SpeechEndpointBox", "SpeechLanguageBox", "SpeechTimeoutBox",
            "SpeechReplaceTokenButton", "SpeechClearTokenButton", "SpeechTestButton"],
    };

    public static TheoryData<string, double> OrderedPageWidths()
    {
        var data = new TheoryData<string, double>();
        foreach (var page in ExpectedOrder.Keys)
        {
            data.Add(page, Wide);
            data.Add(page, Stacked);
        }
        return data;
    }

    [AvaloniaTheory]
    [MemberData(nameof(OrderedPageWidths))]
    public async Task TabOrder_FollowsTheVisualOrder_TitleBarThenMenuThenHeaderThenFieldsThenButtons(string page, double width)
    {
        using var rig = await SettingsRig.CreateAsync(page, width);
        var window = rig.Window;
        var chain = TabChain(window);
        var labels = chain.Select(Label).ToList();

        // 期望的相對順序（子序列）：每一個都要在、且依序
        var last = -1;
        foreach (var expected in ExpectedOrder[page])
        {
            var index = labels.FindIndex(last + 1, l => l == expected);
            Assert.True(index > last, $"{page}@{width}：「{expected}」應排在 Tab 鏈的第 {last + 1} 個之後。實際：{string.Join(" > ", labels)}");
            last = index;
        }

        // 標題列沒有 Tab 停靠點（小精靈按鈕在設定視窗隱藏），而且在視覺樹裡排在 Menu 與內容之前
        var titleBar = window.FindByName<CustomTitleBar>("TitleBar");
        Assert.DoesNotContain(titleBar.GetVisualDescendants().OfType<InputElement>(), IsTabStop);
        Assert.DoesNotContain(chain, e => e.FindAncestorOfType<CustomTitleBar>() is not null);
        Assert.True(titleBar.IsEffectivelyVisible);

        // Menu：第一個停靠點是「回到對話」，第二個是目前選取的分類（ListBox 單一停靠點）；其他分類、停用項目都不在 Tab 鏈上
        Assert.Equal("BackToChatButton", labels[0]);
        var menuStops = chain.Where(e => IsInMenu(e, window)).ToList();
        Assert.Equal(["BackToChatButton", $"Category{CategoryName(rig.Host.Settings.CategoryIndex)}"], menuStops.Select(Label).ToArray());
        Assert.Equal(2, chain.FindIndex(e => !IsInMenu(e, window)));   // 第三個停靠點已在內容區：Tab 離開 Menu 後進入頁面，不是逐項走四個分類
        Assert.Single(chain, e => e is ListBoxItem item && item.FindAncestorOfType<ListBox>() is { Name: "CategoryList" });

        // 視覺樹的順序也一致（沒有靠 TabIndex 硬調）
        var visual = window.GetVisualDescendants().OfType<InputElement>().Where(IsTabStop).ToList();
        Assert.True(visual.FindIndex(e => e is Control { Name: "BackToChatButton" }) < visual.FindIndex(e => IsInContent(e, window)));
        Assert.All(chain, e => Assert.True(e.IsEffectivelyEnabled && e.IsEffectivelyVisible));
    }

    [AvaloniaTheory]
    [InlineData("connections-form", "NewConnectionButton", "NameBox", "DeleteButton")]
    [InlineData("connections-advanced", "NewConnectionButton", "BaseUrlBox", "SaveButton")]
    [InlineData("models-form", "NewModelButton", "DisplayNameBox", "SaveButton")]
    [InlineData("persona-dirty", "PersonaStyleBox", "PersonaCustomBox", "PersonaSaveButton")]
    public async Task HeaderActionsComeBeforeTheFields_AndSaveAndDeleteComeAfter(string page, string first, string field, string buttons)
    {
        foreach (var width in new[] { Wide, Stacked })
        {
            using var rig = await SettingsRig.CreateAsync(page, width);
            var labels = TabChain(rig.Window).Select(Label).ToList();
            var iFirst = labels.IndexOf(first);
            var iField = labels.IndexOf(field);
            var iButtons = labels.IndexOf(buttons);
            Assert.True(iFirst >= 0 && iField >= 0 && iButtons >= 0, string.Join(" > ", labels));
            Assert.True(iFirst < iField, $"{page}@{width}：{first} 要在 {field} 之前");
            Assert.True(iField < iButtons, $"{page}@{width}：{buttons} 要在 {field} 之後");
        }
    }

    [AvaloniaFact]
    public async Task TheSegmentedSwitch_IsASingleTabStop_OnTheCheckedSegment()
    {
        using var rig = await SettingsRig.CreateAsync("connections-list");
        var settings = rig.Host.Settings;
        var chain = TabChain(rig.Window);
        Assert.Single(chain, e => e is RadioButton);
        Assert.Equal("TabConnections", Label(chain.Single(e => e is RadioButton)));

        settings.TabIndex = 1;
        rig.Settle();
        chain = TabChain(rig.Window);
        Assert.Single(chain, e => e is RadioButton);
        Assert.Equal("TabModels", Label(chain.Single(e => e is RadioButton)));
        // 頁首的主要動作仍在分段切換之前
        var labels = chain.Select(Label).ToList();
        Assert.True(labels.IndexOf("NewModelButton") < labels.IndexOf("TabModels"));
    }

    [AvaloniaFact]
    public async Task ShiftTab_WalksTheSameChainBackwards()
    {
        using var rig = await SettingsRig.CreateAsync("connections-form");
        var forward = TabChain(rig.Window);
        var backward = new List<InputElement>();
        IInputElement current = forward[0];
        // 從第一個停靠點往前，要繞到最後一個（循環）
        current = KeyboardNavigationHandler.GetNext(current, NavigationDirection.Previous)!;
        for (var i = 0; i < forward.Count && !backward.Contains((InputElement)current); i++)
        {
            backward.Add((InputElement)current);
            current = KeyboardNavigationHandler.GetNext(current, NavigationDirection.Previous)!;
        }
        Assert.Equal(forward.Count, backward.Count);
        Assert.Equal(forward.AsEnumerable().Reverse().Select(Label), backward.Select(Label));
    }

    private static string CategoryName(int index) => index switch { 0 => "Connections", 1 => "Persona", 2 => "Memory", 3 => "General", 4 => "Sprite", _ => "Speech" };

    private static bool IsInMenu(InputElement e, Window window) => e.GetVisualAncestors().Contains(window.FindByName<Border>("Menu"));

    private static bool IsInContent(InputElement e, Window window) => !IsInMenu(e, window) && e.FindAncestorOfType<CustomTitleBar>() is null;

    // ---- 3. 鍵盤操作 ----

    [AvaloniaFact]
    public async Task TheCategoryMenu_ChangesCategoryWithArrowKeys_AndSkipsTheDisabledItems()
    {
        using var rig = await SettingsRig.CreateAsync("connections-list");
        var settings = rig.Host.Settings;
        rig.Window.FindByName<ListBoxItem>("CategoryConnections").Focus();

        PressKey(rig, Key.Down);
        Assert.Equal(1, settings.CategoryIndex);
        Assert.Equal("CategoryPersona", FocusedName(rig));
        PressKey(rig, Key.Down);
        Assert.Equal(2, settings.CategoryIndex);   // 人格與主動性之後是「記憶」
        Assert.Equal("CategoryMemory", FocusedName(rig));
        PressKey(rig, Key.Down);
        PressKey(rig, Key.Down);
        Assert.Equal(4, settings.CategoryIndex);
        PressKey(rig, Key.Down);   // 小精靈之後是「語音」（最後一個可用的分類）
        Assert.Equal(5, settings.CategoryIndex);
        Assert.Equal("CategorySpeech", FocusedName(rig));
        PressKey(rig, Key.Down);   // 最後一個之後不會跑進停用的「設定首頁」「資料與隱私」
        Assert.Equal(5, settings.CategoryIndex);
        Assert.Equal("CategorySpeech", FocusedName(rig));
        PressKey(rig, Key.Up);
        Assert.Equal(4, settings.CategoryIndex);
        Assert.Equal("CategorySprite", FocusedName(rig));
        PressKey(rig, Key.Up);
        Assert.Equal(3, settings.CategoryIndex);
        Assert.True(settings.ShowGeneral);
    }

    [AvaloniaFact]
    public async Task TheSegments_SwitchWithArrowKeys_AndWrapAround()
    {
        using var rig = await SettingsRig.CreateAsync("connections-list");
        var settings = rig.Host.Settings;
        var connections = rig.Window.FindByName<RadioButton>("TabConnections");
        var models = rig.Window.FindByName<RadioButton>("TabModels");
        connections.Focus();

        PressKey(rig, Key.Right);
        Assert.Equal(1, settings.TabIndex);
        Assert.True(models.IsChecked);
        Assert.False(connections.IsChecked);
        Assert.True(models.IsFocused);
        Assert.True(rig.Window.FindByName<ModelsPanel>("ModelsPanel").IsEffectivelyVisible);

        PressKey(rig, Key.Right);   // 到頭接回第一段（單選群組的慣例）
        Assert.Equal(0, settings.TabIndex);
        Assert.True(connections.IsFocused);
        PressKey(rig, Key.Left);
        Assert.Equal(1, settings.TabIndex);
        PressKey(rig, Key.Down);
        Assert.Equal(0, settings.TabIndex);
        PressKey(rig, Key.Up);
        Assert.Equal(1, settings.TabIndex);

        // 換段之後，Tab 再進來停在新選的那一段
        Assert.Equal("TabModels", Label(TabChain(rig.Window).Single(e => e is RadioButton)));
    }

    [AvaloniaFact]
    public async Task Escape_DoesNotCloseTheSettingsWindow_AndNoButtonIsCancelOrDefaultExceptPrimary()
    {
        using var rig = await SettingsRig.CreateAsync("connections-form");
        var name = rig.Window.FindByName<TextBox>("NameBox");
        name.Focus();

        PressKey(rig, Key.Escape);
        Assert.True(rig.Window.IsVisible);   // Esc 維持現狀：不關閉設定視窗

        // 沒有任何按鈕被設成 IsCancel；若有按鈕是 IsDefault，只能是 primary（儲存），不能是刪除之類
        var all = new List<Button>();
        foreach (var page in new[] { "connections-form", "models-form", "persona-dirty", "sprite" })
        {
            using var other = await SettingsRig.CreateAsync(page);
            all.AddRange(other.Window.GetVisualDescendants().OfType<Button>().Where(b => b is not RepeatButton));
        }
        Assert.NotEmpty(all);
        Assert.DoesNotContain(all, b => b.IsCancel);
        Assert.All(all.Where(b => b.IsDefault), b =>
        {
            Assert.Contains("primary", b.Classes);
            Assert.EndsWith("SaveButton", b.Name);
        });
    }

    [AvaloniaTheory]
    [InlineData("connections-form", "NameBox")]
    [InlineData("connections-advanced", "BaseUrlBox")]
    [InlineData("models-form", "DisplayNameBox")]
    public async Task Enter_InAFormField_DoesNotTriggerDeleteOrAnyAction(string page, string field)
    {
        using var rig = await SettingsRig.CreateAsync(page);
        var box = rig.Window.FindByName<TextBox>(field);
        box.Focus();
        var connectionsBefore = rig.Host.Connections.Items.Count;
        var modelsBefore = rig.Host.Models.Items.Count;
        var testerCallsBefore = rig.Host.Tester.Calls.Count;   // 選取模型時會自動取一次可用模型清單，那不是 Enter 觸發的

        PressKey(rig, Key.Enter);

        Assert.Empty(rig.Host.Confirm.Asked);                 // 刪除會先跳確認：沒有跳 = 沒有觸發刪除
        Assert.Equal(testerCallsBefore, rig.Host.Tester.Calls.Count);   // 也沒有觸發「測試連線」
        Assert.Equal(connectionsBefore, rig.Host.Connections.Items.Count);
        Assert.Equal(modelsBefore, rig.Host.Models.Items.Count);
        Assert.True(rig.Window.IsVisible);
    }

    private static void PressKey(SettingsRig rig, Key key)
    {
        rig.Window.KeyPress(key, RawInputModifiers.None, PhysicalKeyFor(key), null);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        rig.Window.UpdateLayout();
    }

    private static PhysicalKey PhysicalKeyFor(Key key) => key switch
    {
        Key.Down => PhysicalKey.ArrowDown,
        Key.Up => PhysicalKey.ArrowUp,
        Key.Left => PhysicalKey.ArrowLeft,
        Key.Right => PhysicalKey.ArrowRight,
        Key.Enter => PhysicalKey.Enter,
        Key.Escape => PhysicalKey.Escape,
        _ => PhysicalKey.None,
    };

    private static string? FocusedName(SettingsRig rig) => (rig.Window.FocusManager?.GetFocusedElement() as Control)?.Name;
}
