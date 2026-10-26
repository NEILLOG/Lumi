using System.Xml.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.App.Controls;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Models;
using Lumi.Sprites.Orb;
using Lumi.Sprites.Assets;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Headless;

/// <summary>UD-3：「外觀與操作」「人格與主動性」「小精靈」三頁——色票卡片、明暗下拉、區段表單、按鈕層級、角色卡片、警告區、&lt; 900 上下排。</summary>
public class AppearancePersonaSpritePagesTests
{
    private const int GeneralIndex = (int)SettingsCategory.General;
    private const int PersonaIndex = (int)SettingsCategory.Persona;
    private const int SpriteIndex = (int)SettingsCategory.Sprite;

    private sealed class Rig : IDisposable
    {
        public SettingsHost Host { get; }
        public SettingsWindow Window { get; }

        public Rig(int category, double width = 1040, double height = 760, SettingsHost? host = null, Action<SettingsHost>? beforeShow = null)
        {
            Host = host ?? new SettingsHost(new AvaloniaUiDispatcher());
            beforeShow?.Invoke(Host);
            Window = new SettingsWindow(Host.Settings) { Width = width, Height = height };
            Window.Show();
            Host.Settings.CategoryIndex = category;
            Settle();
        }

        public void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }

        public void Resize(double width, double height)
        {
            Window.Width = width;
            Window.Height = height;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Window.UpdateLayout();
        }

        public ListBox PaletteBox => Window.FindByName<ListBox>("PaletteBox");

        public void Click(Control control)
        {
            Window.UpdateLayout();
            var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), Window)!.Value;
            Window.MouseDown(center, MouseButton.Left);
            Window.MouseUp(center, MouseButton.Left);
            Settle();
        }

        public void Dispose()
        {
            Window.Close();
            Host.Dispose();
        }
    }

    private static IEnumerable<string> TextsIn(Visual root) => root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "");

    private static ListBoxItem ItemOf(ListBox list, int index) =>
        Assert.IsType<ListBoxItem>(list.ContainerFromIndex(index));

    private static ListBoxItem PaletteItem(Rig rig, string id) =>
        rig.PaletteBox.GetVisualDescendants().OfType<ListBoxItem>().Single(i => ((PaletteOption)i.DataContext!).Id == id);

    private static bool BadgeShown(ListBoxItem item) =>
        item.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("active-badge")).IsEffectivelyVisible;

    private static Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    /// <summary>直接讀 Themes/Palettes/{id}.axaml 的 Light 變體色碼（測試的對照來源）。</summary>
    private static Color PaletteFileColor(string id, string key)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lumi.sln")))
            dir = dir.Parent;
        var path = Path.Combine(dir!.FullName, "src", "Lumi.App", "Themes", "Palettes", id + ".axaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var doc = XDocument.Load(path);
        var light = doc.Descendants().First(e => e.Name.LocalName == "ResourceDictionary" && (string?)e.Attribute(x + "Key") == "Light");
        var color = light.Elements().First(e => e.Name.LocalName == "Color" && (string?)e.Attribute(x + "Key") == key);
        return Color.Parse(color.Value.Trim());
    }

    private static IReadOnlyList<Border> Swatches(ListBoxItem item) =>
        item.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("swatch")).ToList();

    private static readonly string[] BannedSymbols = ["✕", "⚠", "↑", "■", "×", "▾"];

    // ===== 外觀與操作 =====

    [AvaloniaFact]
    public void Palette_ShowsFiveSelectableCards_WithNameDescriptionAndFourSwatches()
    {
        using var rig = new Rig(GeneralIndex);

        Assert.Equal(5, rig.PaletteBox.ItemCount);
        Assert.Contains("cards", rig.PaletteBox.Classes);
        Assert.IsType<WrapPanel>(rig.PaletteBox.ItemsPanelRoot);
        foreach (var option in PaletteOption.All)
        {
            var item = PaletteItem(rig, option.Id);
            var texts = TextsIn(item).ToList();
            Assert.Contains(option.Text, texts);
            Assert.Contains(option.Description, texts);
            Assert.Equal(4, Swatches(item).Count);
            // 一個色票一張卡片，卡片裡不再有卡片
            var cards = item.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("card")).ToList();
            Assert.Single(cards);
        }
        Assert.Contains("柔和、生活化、低刺激", TextsIn(PaletteItem(rig, "sage")));
        Assert.Contains("清爽、友善、識別度高", TextsIn(PaletteItem(rig, "teal")));
        Assert.Contains("穩定、熟悉、生產力工具感", TextsIn(PaletteItem(rig, "mist-blue")));
        Assert.Contains("現代、鮮明、AI 科技感", TextsIn(PaletteItem(rig, "indigo")));
        Assert.Contains("溫暖、人文感", TextsIn(PaletteItem(rig, "terracotta")));
    }

    [AvaloniaTheory]
    [InlineData("sage")]
    [InlineData("teal")]
    [InlineData("mist-blue")]
    [InlineData("indigo")]
    [InlineData("terracotta")]
    public void Palette_SwatchColors_MatchThePaletteFile_NotTheCurrentTheme(string id)
    {
        using var rig = new Rig(GeneralIndex);
        var swatches = Swatches(PaletteItem(rig, id));

        Assert.Equal(PaletteFileColor(id, "LumiAccentColor"), ColorOf(swatches[0].Background));
        Assert.Equal(PaletteFileColor(id, "LumiAccentHoverColor"), ColorOf(swatches[1].Background));
        Assert.Equal(PaletteFileColor(id, "LumiAccentSoftColor"), ColorOf(swatches[2].Background));
        Assert.Equal(PaletteFileColor(id, "LumiTextColor"), ColorOf(swatches[3].Background));

        // 換成深色之後色塊不變（預覽的是色票自己的淺色變體）
        rig.Window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        rig.Settle();
        Assert.Equal(PaletteFileColor(id, "LumiAccentColor"), ColorOf(Swatches(PaletteItem(rig, id))[0].Background));
    }

    [AvaloniaFact]
    public void PalettePreview_ReadsThePaletteFiles_AndEachPaletteHasItsOwnAccent()
    {
        foreach (var id in ThemePalettes.All)
        {
            var colors = PalettePreview.For(id);
            Assert.Equal(PaletteFileColor(id, "LumiAccentColor"), colors.Accent);
            Assert.Equal(PaletteFileColor(id, "LumiAccentHoverColor"), colors.Hover);
            Assert.Equal(PaletteFileColor(id, "LumiAccentSoftColor"), colors.Soft);
            Assert.Equal(PaletteFileColor(id, "LumiTextColor"), colors.Text);
            Assert.Same(PalettePreview.For(id), PalettePreview.For(id));   // 快取
        }
        Assert.Equal(5, ThemePalettes.All.Select(id => PalettePreview.For(id).Accent).Distinct().Count());
        Assert.Equal(PalettePreview.For("sage"), PalettePreview.For("不存在"));   // 未知 ID 讀成預設
    }

    [AvaloniaFact]
    public void Palette_SelectedCard_HasSoftBackground_AccentBorder_AndAVisibleActiveBadge()
    {
        using var rig = new Rig(GeneralIndex);

        foreach (var option in PaletteOption.All)
            Assert.Equal(option.Id == "sage", BadgeShown(PaletteItem(rig, option.Id)));

        var sage = PaletteItem(rig, "sage");
        var card = sage.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("card"));
        Assert.True(rig.Window.TryFindResource("LumiAccentSoftBrush", rig.Window.ActualThemeVariant, out var soft));
        Assert.True(rig.Window.TryFindResource("LumiAccentBorderBrush", rig.Window.ActualThemeVariant, out var border));
        Assert.Equal(ColorOf((IBrush)soft!), ColorOf(card.Background));
        Assert.Equal(ColorOf((IBrush)border!), ColorOf(card.BorderBrush));
        // 狀態是文字，不只靠顏色
        Assert.Contains(Strings.Common_Active, TextsIn(sage));
        var other = PaletteItem(rig, "teal").GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("card"));
        Assert.NotEqual(ColorOf(card.Background), ColorOf(other.Background));
    }

    [AvaloniaFact]
    public async Task Palette_ClickingACard_AppliesItImmediately_AndMovesTheBadge()
    {
        using var rig = new Rig(GeneralIndex);

        rig.Click(PaletteItem(rig, "teal"));

        Assert.Equal("teal", rig.Host.General.SelectedPalette.Id);
        await SettingsHost.WaitUntil(() => rig.Host.Catalog.Current.ThemeId == "teal");   // SetThemeIdAsync 已寫入設定
        rig.Settle();
        Assert.True(BadgeShown(PaletteItem(rig, "teal")));
        Assert.False(BadgeShown(PaletteItem(rig, "sage")));
    }

    [AvaloniaFact]
    public async Task Palette_FollowsTheSettingChangedElsewhere()
    {
        using var rig = new Rig(GeneralIndex);

        await rig.Host.Catalog.SetThemeIdAsync("indigo");
        rig.Settle();

        Assert.Equal("indigo", ((PaletteOption)rig.PaletteBox.SelectedItem!).Id);
        Assert.True(BadgeShown(PaletteItem(rig, "indigo")));
    }

    [AvaloniaFact]
    public void Palette_CardsHaveAccessibleNames_WithThePaletteNameAndWhetherInUse()
    {
        using var rig = new Rig(GeneralIndex);

        foreach (var option in PaletteOption.All)
        {
            var name = AutomationProperties.GetName(PaletteItem(rig, option.Id));
            Assert.Contains(option.Text, name);
            if (option.Id == "sage")
                Assert.Contains(Strings.Common_Active, name);
            else
                Assert.DoesNotContain(Strings.Common_Active, name);
        }

        rig.Click(PaletteItem(rig, "terracotta"));
        Assert.Contains(Strings.Common_Active, AutomationProperties.GetName(PaletteItem(rig, "terracotta")));
        Assert.DoesNotContain(Strings.Common_Active, AutomationProperties.GetName(PaletteItem(rig, "sage")));
        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(rig.PaletteBox)));
    }

    [AvaloniaFact]
    public void Palette_CanBeOperatedWithTheKeyboard()
    {
        using var rig = new Rig(GeneralIndex);
        var sage = PaletteItem(rig, "sage");
        Assert.True(sage.Focusable);
        Assert.True(sage.IsTabStop || rig.PaletteBox.IsTabStop || rig.PaletteBox.Focusable);

        sage.Focus();
        rig.Settle();
        Assert.True(sage.IsFocused);

        rig.Window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        rig.Settle();
        Assert.Equal("teal", ((PaletteOption)rig.PaletteBox.SelectedItem!).Id);

        rig.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        rig.Settle();
        Assert.Equal("terracotta", ((PaletteOption)rig.PaletteBox.SelectedItem!).Id);   // 往下一列（WrapPanel 換行，1040 寬每列三張）

        rig.Window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        rig.Settle();
        Assert.Equal("teal", ((PaletteOption)rig.PaletteBox.SelectedItem!).Id);

        // 空白鍵與 Enter 選取目前聚焦的卡片
        var indigo = PaletteItem(rig, "indigo");
        indigo.Focus();
        rig.Window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        rig.Settle();
        Assert.Equal("indigo", ((PaletteOption)rig.PaletteBox.SelectedItem!).Id);

        var mist = PaletteItem(rig, "mist-blue");
        mist.Focus();
        rig.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        rig.Settle();
        Assert.Equal("mist-blue", ((PaletteOption)rig.PaletteBox.SelectedItem!).Id);
    }

    [AvaloniaFact]
    public void Palette_CardsWrap_WhenTheWindowIsNarrow()
    {
        using var rig = new Rig(GeneralIndex, 1040, 760);
        var wideRows = Enumerable.Range(0, 5).Select(i => Math.Round(ItemOf(rig.PaletteBox, i).Bounds.Top)).Distinct().Count();

        rig.Resize(720, 760);
        var narrowRows = Enumerable.Range(0, 5).Select(i => Math.Round(ItemOf(rig.PaletteBox, i).Bounds.Top)).Distinct().Count();

        Assert.True(wideRows >= 2);
        Assert.True(narrowRows >= wideRows);
        Assert.True(rig.PaletteBox.GetVisualDescendants().OfType<ScrollViewer>().All(s => s.Extent.Width <= s.Viewport.Width + 0.5));   // 沒有水平捲動
    }

    [AvaloniaFact]
    public async Task ThemeDropdown_HasLabelAboveIt_AndTheCaption_AndAppliesTheChoice()
    {
        using var rig = new Rig(GeneralIndex);
        var box = rig.Window.FindByName<ComboBox>("ThemeBox");

        Assert.Equal(["跟隨系統", "淺色", "深色"], box.Items.Cast<ThemeOption>().Select(o => o.Text));
        var field = box.FindAncestorOfType<FormField>()!;
        Assert.Equal(Strings.General_ThemeMode, field.Label);
        Assert.Equal("跟隨系統會在系統切換深淺色時一起切換。", field.Hint);
        var label = field.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == field.Label);
        Assert.True(label.TranslatePoint(default, rig.Window)!.Value.Y < box.TranslatePoint(default, rig.Window)!.Value.Y);   // 標籤在輸入框上方
        Assert.Contains(field.Hint, TextsIn(field));
        Assert.Contains(field.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == field.Hint && t.FontSize == 11);

        box.SelectedItem = ThemeOption.All.Single(o => o.Preference == ThemePreference.Dark);
        await SettingsHost.WaitUntil(() => rig.Host.Catalog.Current.Theme == ThemePreference.Dark);
    }

    [AvaloniaFact]
    public void GeneralPage_UsesSectionTitles_ADivider_AndNoCardAroundTheForm()
    {
        using var rig = new Rig(GeneralIndex);
        var sections = rig.Window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Classes.Contains("section-title")).ToList();

        // 第三段是「筆記資料夾」（M10 10B），第四段是「進階」（M20），都在明暗之後
        Assert.Equal([Strings.General_Palette, Strings.General_Theme, Strings.NotesFolder_Title, Strings.Advanced_Title], sections.Select(t => t.Text));
        Assert.All(sections, t => Assert.Equal(20, t.FontSize));
        Assert.All(sections, t => Assert.Equal(FontWeight.Bold, t.FontWeight));
        // 第一條分隔線在色票與明暗之間（之後還有「筆記資料夾」前的分隔線）
        var divider = rig.Window.GetVisualDescendants().OfType<Border>().First(b => b.IsEffectivelyVisible && b.Classes.Contains("divider") && IsInsideGeneralPage(rig, b));
        Assert.True(sections[0].TranslatePoint(default, rig.Window)!.Value.Y < divider.TranslatePoint(default, rig.Window)!.Value.Y);
        Assert.True(divider.TranslatePoint(default, rig.Window)!.Value.Y < sections[1].TranslatePoint(default, rig.Window)!.Value.Y);

        // 卡片只有五張色票卡片，沒有把整份表單或下拉選單包進卡片
        var themeBox = rig.Window.FindByName<ComboBox>("ThemeBox");
        Assert.Null(themeBox.FindAncestorOfType<ListBoxItem>());
        Assert.DoesNotContain(themeBox.GetVisualAncestors().OfType<Border>(), b => b.Classes.Contains("card"));
        Assert.Equal(5, rig.Window.GetVisualDescendants().OfType<Border>().Count(b => b.IsEffectivelyVisible && b.Classes.Contains("card")));
    }

    private static bool IsInsideGeneralPage(Rig rig, Visual visual) =>
        visual.FindAncestorOfType<ScrollViewer>() is { } sv && sv.GetVisualDescendants().OfType<SettingsPageHeader>().Any(h => h.Name == "GeneralHeader");

    [AvaloniaFact]
    public void GeneralPage_ShowsTheErrorText_WhenSavingFails()
    {
        using var rig = new Rig(GeneralIndex);
        var error = rig.Window.FindByName<TextBlock>("GeneralError");
        Assert.False(error.IsEffectivelyVisible);

        rig.Host.General.Error = Strings.Settings_SaveFailed;
        rig.Settle();

        Assert.True(error.IsEffectivelyVisible);
        Assert.Equal(Strings.Settings_SaveFailed, error.Text);
    }

    // ===== 人格與主動性 =====

    [AvaloniaFact]
    public void PersonaPage_HasThreeSections_WithFormFields_AndDividers_AndNoCards()
    {
        using var rig = new Rig(PersonaIndex);
        var form = rig.Window.FindByName<StackPanel>("PersonaForm");

        var titles = form.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("section-title")).Select(t => t.Text).ToList();
        Assert.Equal([Strings.Persona_SectionStyle, Strings.Persona_SectionProactivity, Strings.Persona_Custom], titles);

        var fields = form.GetVisualDescendants().OfType<FormField>().ToList();
        Assert.Equal([Strings.Persona_Style, Strings.Persona_Proactivity, Strings.Persona_CustomLabel], fields.Select(f => f.Label));
        Assert.Equal(FieldRequirement.Optional, fields[2].Requirement);
        Assert.Equal(host(rig).Persona.SelectedStyleHint, fields[0].Hint);
        Assert.Equal(host(rig).Persona.SelectedProactivityHint, fields[1].Hint);
        Assert.Contains(Strings.Persona_ProactivityNote, TextsIn(form));
        Assert.Contains(Strings.Persona_CustomNote, TextsIn(form));

        // 欄位標籤在輸入框上方
        foreach (var (field, name) in new[] { (fields[0], "PersonaStyleBox"), (fields[1], "PersonaProactivityBox"), (fields[2], "PersonaCustomBox") })
        {
            var label = field.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == field.Label);
            var input = field.GetVisualDescendants().OfType<Control>().Single(c => c.Name == name);
            Assert.True(label.TranslatePoint(default, rig.Window)!.Value.Y < input.TranslatePoint(default, rig.Window)!.Value.Y, name);
        }

        Assert.Equal(3, form.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("divider")));
        Assert.DoesNotContain(form.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("card"));   // 不用卡片包表單
        Assert.Contains(Strings.Field_Optional, TextsIn(fields[2]));
    }

    private static SettingsHost host(Rig rig) => rig.Host;

    [AvaloniaFact]
    public void PersonaPage_CounterAndErrorText_AreKept()
    {
        using var rig = new Rig(PersonaIndex);
        var custom = rig.Window.FindByName<TextBox>("PersonaCustomBox");
        var counter = rig.Window.FindByName<TextBlock>("PersonaCounter");
        Assert.Equal(rig.Host.Persona.CustomCounter, counter.Text);
        Assert.Equal(11, counter.FontSize);

        custom.Text = new string('字', PersonaSettings.CustomPersonaMaxLength + 1);
        rig.Settle();

        Assert.Equal(rig.Host.Persona.CustomCounter, counter.Text);
        var field = custom.FindAncestorOfType<FormField>()!;
        Assert.Equal(rig.Host.Persona.CustomError, field.Error);
        Assert.Contains(rig.Host.Persona.CustomError!, TextsIn(field));   // 錯誤有文字
        Assert.Contains("error", custom.Classes);                        // 同時有 Danger 邊框
        Assert.False(rig.Window.FindByName<Button>("PersonaSaveButton").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void PersonaPage_ButtonRow_ResetIsSecondary_SaveIsThePrimary_AndADividerSeparatesIt()
    {
        using var rig = new Rig(PersonaIndex);
        var reset = rig.Window.FindByName<Button>("PersonaResetButton");
        var save = rig.Window.FindByName<Button>("PersonaSaveButton");
        var row = rig.Window.FindByName<Grid>("PersonaActions");

        Assert.Contains("secondary", reset.Classes);
        Assert.Contains("primary", save.Classes);
        Assert.Single(row.GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("primary"));
        Assert.Equal(Strings.Persona_Reset, reset.Content);
        Assert.Equal(Strings.Persona_Save, save.Content);
        Assert.True(reset.TranslatePoint(default, rig.Window)!.Value.X < save.TranslatePoint(default, rig.Window)!.Value.X);   // 重設在左、儲存在右
        // 這一頁（可見的按鈕）只有一個 Primary
        var page = row.FindAncestorOfType<ScrollViewer>()!;
        Assert.Single(page.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && b.Classes.Contains("primary"));

        // 動作列上方有分隔線
        var dividers = rig.Window.FindByName<StackPanel>("PersonaForm").GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("divider")).ToList();
        var lastDivider = dividers.OrderBy(d => d.TranslatePoint(default, rig.Window)!.Value.Y).Last();
        Assert.True(lastDivider.TranslatePoint(default, rig.Window)!.Value.Y < row.TranslatePoint(default, rig.Window)!.Value.Y);
    }

    [AvaloniaFact]
    public void PersonaPage_Message_IsStillShown()
    {
        using var rig = new Rig(PersonaIndex);
        var message = rig.Window.FindByName<TextBlock>("PersonaMessage");
        Assert.False(message.IsEffectivelyVisible);

        rig.Host.Persona.Message = Strings.Persona_Saved;
        rig.Settle();

        Assert.True(message.IsEffectivelyVisible);
        Assert.Equal(Strings.Persona_Saved, message.Text);
    }

    // ===== 小精靈 =====

    private static async Task<SettingsHost> HostWithCorgi(CorgiPack pack, bool broken, bool warning = false)
    {
        if (broken)
            pack.AddBroken();
        var library = new SpriteLibrary([new OrbSpriteFactory()], new AssetSpriteOptions { Directory = pack.Root });
        await library.ReloadAsync();
        var host = new SettingsHost(library, new FakeFolderOpener());
        if (warning)
            host.Sprite.Issues.Add(new SpriteIssueItem("corgi：內容貼邊（測試用警告）", IsFatal: false));
        return host;
    }

    [AvaloniaFact]
    public async Task SpritePage_ListsTheCharactersAsSelectableCards_WithKindTag_AndActiveBadge()
    {
        using var pack = new CorgiPack();
        using var rig = new Rig(SpriteIndex, host: await HostWithCorgi(pack, broken: false));
        var list = rig.Window.FindByName<ListBox>("SpriteList");

        Assert.Contains("cards", list.Classes);
        Assert.Equal(2, list.ItemCount);
        var orb = ItemOf(list, 0);
        var corgi = ItemOf(list, 1);
        var orbOption = (SpriteOption)orb.DataContext!;
        var corgiOption = (SpriteOption)corgi.DataContext!;
        Assert.Contains(orbOption.Name, TextsIn(orb));
        Assert.Contains(orbOption.Credit, TextsIn(orb));
        Assert.Contains(Strings.SpriteSettings_Kind_Code, TextsIn(orb));
        Assert.Contains(Strings.SpriteSettings_Kind_Asset, TextsIn(corgi));
        Assert.Single(orb.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("card"));   // 巢狀一層
        Assert.Single(corgi.GetVisualDescendants().OfType<Image>());                                          // 素材型有縮圖
        Assert.Equal(corgiOption.Name, AutomationProperties.GetName(corgi));

        // 選取的卡片顯示「使用中」文字徽章
        Assert.True(BadgeShown(orb));
        Assert.False(BadgeShown(corgi));
        rig.Click(corgi);
        Assert.Equal(corgiOption.Id, rig.Host.Sprite.SelectedSprite.Id);
        Assert.True(BadgeShown(corgi));
        Assert.False(BadgeShown(orb));
    }

    [AvaloniaFact]
    public async Task SpritePage_FolderButtons_AreSecondary_AndThereIsNoPrimary()
    {
        using var pack = new CorgiPack();
        using var rig = new Rig(SpriteIndex, host: await HostWithCorgi(pack, broken: false));

        var reload = rig.Window.FindByName<Button>("SpriteReloadButton");
        var open = rig.Window.FindByName<Button>("SpriteOpenFolderButton");
        Assert.Contains("secondary", reload.Classes);
        Assert.Contains("secondary", open.Classes);
        Assert.True(reload.IsEffectivelyVisible);
        Assert.True(open.IsEffectivelyVisible);
        var page = reload.FindAncestorOfType<ScrollViewer>()!;
        Assert.DoesNotContain(page.GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("primary"));
        // 兩個按鈕在角色清單下方
        var list = rig.Window.FindByName<ListBox>("SpriteList");
        Assert.True(reload.TranslatePoint(default, rig.Window)!.Value.Y > list.TranslatePoint(default, rig.Window)!.Value.Y + list.Bounds.Height - 0.5);
        Assert.True(rig.Window.FindByName<TextBlock>("SpriteFolderPath").IsEffectivelyVisible);
        Assert.Equal(pack.Root, rig.Window.FindByName<TextBlock>("SpriteFolderPath").Text);
    }

    [AvaloniaFact]
    public async Task SpritePage_IssuesUseTheAlertIcon_AndTheTextLabel_NotSymbols()
    {
        using var pack = new CorgiPack();
        using var rig = new Rig(SpriteIndex, host: await HostWithCorgi(pack, broken: true, warning: true));
        var panel = rig.Window.FindByName<StackPanel>("SpriteIssuesPanel");
        var list = rig.Window.FindByName<ItemsControl>("SpriteIssueList");

        Assert.True(panel.IsEffectivelyVisible);
        Assert.Equal(2, list.ItemCount);
        Assert.True(rig.Window.TryFindResource("LumiIconAlertTriangle", out var triangle));
        var rows = list.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("issue")).ToList();
        Assert.Equal(2, rows.Count);
        var fatal = Assert.Single(rows, r => r.Classes.Contains("fatal"));
        var warn = Assert.Single(rows, r => r.Classes.Contains("warn"));
        foreach (var row in rows)
        {
            var icon = Assert.Single(row.GetVisualDescendants().OfType<LumiIcon>());
            Assert.Same(triangle, icon.Data);
            Assert.DoesNotContain(TextsIn(row), t => BannedSymbols.Any(t.Contains));
        }
        Assert.Equal(Strings.SpriteSettings_Issue_Fatal, TextsIn(fatal).First());
        Assert.Contains(rig.Host.Sprite.Issues.First(i => i.IsFatal).Text, TextsIn(fatal));
        Assert.Equal(Strings.SpriteSettings_Issue_Warning, TextsIn(warn).First());
        Assert.Contains("內容貼邊", string.Concat(TextsIn(warn)));

        // 失敗＝Danger 淡底，警告＝Warning 淡底
        Assert.True(rig.Window.TryFindResource("LumiDangerBackgroundBrush", rig.Window.ActualThemeVariant, out var danger));
        Assert.True(rig.Window.TryFindResource("LumiWarningBackgroundBrush", rig.Window.ActualThemeVariant, out var warning));
        Assert.Equal(ColorOf((IBrush)danger!), ColorOf(fatal.Background));
        Assert.Equal(ColorOf((IBrush)warning!), ColorOf(warn.Background));
        Assert.Contains(Strings.SpriteSettings_IssuesNote, TextsIn(panel));
    }

    [AvaloniaFact]
    public async Task SpritePage_SizeVoiceAndStates_AreFormFieldsAndSections()
    {
        using var pack = new CorgiPack();
        using var rig = new Rig(SpriteIndex, host: await HostWithCorgi(pack, broken: false));

        var size = rig.Window.FindByName<ComboBox>("SpriteSizeBox");
        var sizeField = size.FindAncestorOfType<FormField>()!;
        Assert.Equal(Strings.SpriteSettings_Size, sizeField.Label);
        Assert.Equal(Strings.SpriteSettings_SizeNote, sizeField.Hint);
        var voice = rig.Window.FindByName<CheckBox>("SpriteVoiceBox");
        Assert.Equal(Strings.SpriteSettings_Voice, voice.FindAncestorOfType<FormField>()!.Label);
        Assert.Equal(10, rig.Window.FindByName<ListBox>("SpriteStateList").ItemCount);

        var titles = rig.Window.FindByName<FractionWidthLimiter>("SpriteDetail").GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Classes.Contains("section-title")).Select(t => t.Text).ToList();
        Assert.Equal([Strings.SpriteSettings_Character, Strings.SpriteSettings_SectionLook, Strings.SpriteSettings_Preview], titles);
        // 預覽以外沒有把欄位包成卡片：卡片只有角色清單的卡片（2 張）
        Assert.Equal(2, rig.Window.FindByName<FractionWidthLimiter>("SpriteDetail").GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("card")));
        // 11px Caption 一律用 muted
        Assert.Contains(rig.Window.FindByName<FractionWidthLimiter>("SpriteDetail").GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == Strings.SpriteSettings_PreviewNote && t.Classes.Contains("muted") && t.FontSize == 11);
    }

    [AvaloniaFact]
    public async Task SpritePage_Wide_PutsThePreviewOnTheLeft240_AndStackedPutsItOnTop()
    {
        using var pack = new CorgiPack();
        using var rig = new Rig(SpriteIndex, 1040, 760, await HostWithCorgi(pack, broken: false));
        var card = rig.Window.FindByName<Border>("SpritePreviewCard");
        var list = rig.Window.FindByName<ListBox>("SpriteList");
        var host = rig.Window.FindByName<Border>("SpritePreviewHost");

        Assert.DoesNotContain("stacked", rig.Window.Classes);
        Assert.Equal(240, card.Bounds.Width);
        Assert.Equal(240, card.Bounds.Height);
        Assert.Contains("card", card.Classes);
        Assert.Equal(14, card.CornerRadius.TopLeft);
        Assert.Same(card, host.Parent);
        Assert.True(card.TranslatePoint(default, rig.Window)!.Value.X + card.Bounds.Width <= list.TranslatePoint(default, rig.Window)!.Value.X + 0.5);   // 預覽在左
        Assert.True(list.TranslatePoint(default, rig.Window)!.Value.Y < card.TranslatePoint(default, rig.Window)!.Value.Y + card.Bounds.Height);

        rig.Resize(800, 760);
        Assert.Contains("stacked", rig.Window.Classes);
        Assert.Equal(240, card.Bounds.Width);
        var cardBottom = card.TranslatePoint(default, rig.Window)!.Value.Y + card.Bounds.Height;
        Assert.True(list.TranslatePoint(default, rig.Window)!.Value.Y >= cardBottom - 0.5);   // 預覽在上方，清單在其下
        Assert.Equal(card.TranslatePoint(default, rig.Window)!.Value.X, list.TranslatePoint(default, rig.Window)!.Value.X, 1.0);

        rig.Resize(1040, 760);
        Assert.DoesNotContain("stacked", rig.Window.Classes);
        Assert.True(card.TranslatePoint(default, rig.Window)!.Value.X + card.Bounds.Width <= list.TranslatePoint(default, rig.Window)!.Value.X + 0.5);
    }

    // ===== 三頁共通：沒有 Unicode 符號按鈕、無文字按鈕要有名稱、間距尺度 =====

    [AvaloniaTheory]
    [InlineData(GeneralIndex)]
    [InlineData(PersonaIndex)]
    [InlineData(SpriteIndex)]
    public async Task ThePage_HasNoSymbolButtons_AndEveryTextlessButtonHasAnAccessibleName(int category)
    {
        using var pack = new CorgiPack();
        using var rig = new Rig(category, host: await HostWithCorgi(pack, broken: true, warning: true));
        var header = rig.Window.GetVisualDescendants().OfType<SettingsPageHeader>().Single(h => h.IsEffectivelyVisible);
        var page = header.FindAncestorOfType<ScrollViewer>()!;

        var buttons = page.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
        foreach (var button in buttons)
        {
            var textInside = button.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Concat([button.Content as string ?? ""]).ToList();
            Assert.DoesNotContain(textInside, t => BannedSymbols.Any(t.Contains));
            if (textInside.All(string.IsNullOrWhiteSpace))
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)), $"{button.Name} 沒有可存取名稱");
        }
        // 整頁文字也不再用 ✕⚠ 等符號當圖示
        Assert.DoesNotContain(TextsIn(page), t => BannedSymbols.Any(t.Contains));
        // 這三頁最多一個 Primary
        Assert.True(buttons.Count(b => b.Classes.Contains("primary")) <= 1);
    }

    [AvaloniaTheory]
    [InlineData(GeneralIndex)]
    [InlineData(PersonaIndex)]
    [InlineData(SpriteIndex)]
    public async Task ThePage_CaptionsAreMuted_AndUseTheCaptionSize(int category)
    {
        using var pack = new CorgiPack();
        using var rig = new Rig(category, host: await HostWithCorgi(pack, broken: false));
        var header = rig.Window.GetVisualDescendants().OfType<SettingsPageHeader>().Single(h => h.IsEffectivelyVisible);
        var page = header.FindAncestorOfType<ScrollViewer>()!;

        var muted = page.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Classes.Contains("muted")).ToList();
        Assert.NotEmpty(muted);
        Assert.All(muted, t => Assert.Equal(11, t.FontSize));
    }
}
