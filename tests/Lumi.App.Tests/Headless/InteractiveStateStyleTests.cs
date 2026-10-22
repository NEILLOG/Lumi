using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.App.Controls;
using Lumi.App.Tests.TestSupport;
using Lumi.App.Views;
using Xunit.Abstractions;
using State = Lumi.App.Tests.Headless.ButtonStateStyleTests.State;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Headless;

/// <summary>
/// UE-4：清單項目、選單項目、下拉、核取方塊、單選鈕、進階區段標題、捲軸、浮出層外框，在 正常／Hover／Pressed／Disabled／Selected／Focus × 淺深色下，
/// 「實際生效」的 Background／Foreground／BorderBrush（模板零件上的值，不是控制項本身的屬性）都是預期的 token。
/// 寫法同 <see cref="ButtonStateStyleTests"/>：Fluent 把這些狀態設在模板內的零件，會蓋掉我們設在控制項上的顏色。
/// </summary>
public class InteractiveStateStyleTests
{
    private readonly ITestOutputHelper _output;

    public InteractiveStateStyleTests(ITestOutputHelper output) => _output = output;

    public static TheoryData<bool> Variants() => new() { false, true };

    // ---- 共用 ----

    private static ThemeVariant VariantOf(bool dark) => dark ? ThemeVariant.Dark : ThemeVariant.Light;

    /// <summary>用偽類別模擬狀態（和 UE-3 相同）；<paramref name="selected"/> 同時設 :selected（清單項目、選單項目的 Hover 也是 :selected）。</summary>
    private static void SetState(Control control, State state, bool? selected = null)
    {
        control.IsEnabled = state != State.Disabled;
        var classes = (IPseudoClasses)control.Classes;
        classes.Set(":pointerover", state is State.Hover or State.Pressed);
        classes.Set(":pressed", state == State.Pressed);
        if (selected is { } value)
            classes.Set(":selected", value);
    }

    private sealed class Problems
    {
        private readonly Window _window;
        public List<string> Items { get; } = [];

        public Problems(Window window) => _window = window;

        public void Is(string context, string what, IBrush? actual, string token)
        {
            var expected = ButtonStateStyleTests.Resolve(_window, token);
            if (!ButtonStateStyleTests.Matches(actual, expected))
                Items.Add($"{context}：{what} 應為 {token}({expected}) 實際 {ButtonStateStyleTests.Describe(actual)}");
        }

        public void Opacity(string context, double actual, double expected)
        {
            if (Math.Abs(actual - expected) > 0.001)
                Items.Add($"{context}：Opacity 應為 {expected} 實際 {actual}");
        }
    }

    private void Report(List<string> problems)
    {
        foreach (var problem in problems)
            _output.WriteLine(problem);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Take(40)));
    }

    private static T Part<T>(Visual root, string name) where T : Control =>
        root.GetSelfAndVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static Window Host(Control content, bool dark, double width = 400, double height = 300)
    {
        var window = new Window
        {
            Width = width, Height = height, RequestedThemeVariant = VariantOf(dark),
            Content = new StackPanel { Margin = new Thickness(16), Children = { content } },
        };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    /// <summary>鍵盤焦點（Tab）：只有一個 Accent 2px 的圓角框（不是 Fluent 的黑白雙框）。</summary>
    private static List<string> CheckAccentFocusFrame(string name, Window window, Control target)
    {
        var prefix = $"{name} Focus：";
        SetState(target, State.Normal);
        target.Focus(NavigationMethod.Tab);
        window.UpdateLayout();
        Assert.True(((IPseudoClasses)target.Classes).Contains(":focus-visible"), prefix + "沒有進入 :focus-visible");
        var adorners = AdornerLayer.GetAdornerLayer(target)?.Children.Where(x => ReferenceEquals(AdornerLayer.GetAdornedElement(x), target)).ToList() ?? [];
        if (adorners.Count != 1)
            return [prefix + $"應有 1 個焦點框，實際 {adorners.Count}"];
        var problems = new Problems(window);
        if (adorners[0] is not Border frame)
            return [prefix + $"焦點框應是 Border，實際 {adorners[0].GetType().Name}"];
        problems.Is(name + " Focus", "焦點框顏色", frame.BorderBrush, "LumiAccentBrush");
        if (frame.BorderThickness != new Thickness(2))
            problems.Items.Add(prefix + $"焦點框粗細應為 2 實際 {frame.BorderThickness}");
        if (frame.Child is not null)
            problems.Items.Add(prefix + "焦點框裡還有內框（雙框）");
        if (frame.CornerRadius.TopLeft <= 0)
            problems.Items.Add(prefix + "焦點框應是圓角");
        return problems.Items;
    }

    // ===== ListBoxItem =====

    private sealed record ListLook(string? PresenterBg, string PresenterFg, string TextFg);

    /// <summary>一般清單項目（歷史抽屜、設定 Menu、ModelPicker、小精靈狀態預覽）。</summary>
    private static ListLook ExpectedListLook(State state, bool selected) => (selected, state) switch
    {
        (false, State.Normal) => new("Transparent", "LumiTextBrush", "LumiTextBrush"),
        (false, State.Hover or State.Pressed) => new("LumiSurfaceAltBrush", "LumiTextBrush", "LumiTextBrush"),
        (false, State.Disabled) => new("Transparent", "LumiTextMutedBrush", "LumiTextMutedBrush"),
        (true, State.Disabled) => new("LumiAccentSoftBrush", "LumiTextMutedBrush", "LumiTextMutedBrush"),
        _ => new("LumiAccentSoftBrush", "LumiTextOnSoftBrush", "LumiTextOnSoftBrush"),
    };

    /// <summary>卡片清單項目（ListBox.cards／palette）：ListBoxItem 本身不畫底色（避免蓋住卡片圓角），視覺在 Border.card 上。</summary>
    private static ListLook ExpectedCardItemLook(State state, bool selected) =>
        ExpectedListLook(state, selected) with { PresenterBg = "Transparent" };

    /// <param name="selectedDisabled">是否檢查「選取中又停用」：實際畫面不會出現（停用的項目不能被選取），設定 Menu／歷史抽屜的區域樣式也沒有定義它，所以只在全域樣式的合成項目上檢查。</param>
    private static void CheckListItem(Problems problems, string context, ListBoxItem item, bool card, Window window, bool selectedDisabled = false)
    {
        var originalEnabled = item.IsEnabled;
        var originalSelected = ((IPseudoClasses)item.Classes).Contains(":selected");
        foreach (var selected in new[] { false, true })
        foreach (var state in Enum.GetValues<State>())
        {
            if (selected && state == State.Disabled && !selectedDisabled)
                continue;
            SetState(item, state, selected);
            window.UpdateLayout();
            var expected = card ? ExpectedCardItemLook(state, selected) : ExpectedListLook(state, selected);
            var presenter = item.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            var ctx = $"{context} {(selected ? "selected " : "")}{state}";
            problems.Is(ctx, "ContentPresenter.Background", presenter.Background, expected.PresenterBg!);
            problems.Is(ctx, "ContentPresenter.Foreground", presenter.Foreground, expected.PresenterFg);
            // 沒有自己指定前景的文字（Caption 等有自己的 Muted）才會繼承項目的前景
            var text = item.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => !t.GetBaseValue(TextBlock.ForegroundProperty).HasValue);
            if (text is not null && !card)
                problems.Is(ctx, "TextBlock.Foreground", text.Foreground, expected.TextFg);
        }
        SetState(item, originalEnabled ? State.Normal : State.Disabled, originalSelected);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void ListBoxItem_PlainAndSelected_HaveTokenLooks_InEveryState(bool dark)
    {
        var plain = new ListBoxItem { Content = "項目" };
        var list = new ListBox { Items = { plain } };
        var window = Host(list, dark);
        var problems = new Problems(window);
        CheckListItem(problems, "ListBoxItem", plain, card: false, window, selectedDisabled: true);
        problems.Items.AddRange(CheckAccentFocusFrame("ListBoxItem", window, plain));
        window.Close();
        Report(problems.Items);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public async Task ListBoxItem_FromTheRealWindows_HaveTokenLooks_InEveryState(bool dark)
    {
        using var surfaces = await AppSurfaces.OpenAsync();
        var problems = new List<string>();
        var seen = new Dictionary<string, int>();
        foreach (var (name, window) in surfaces.Windows)
        {
            window.RequestedThemeVariant = VariantOf(dark);
            window.UpdateLayout();
            var p = new Problems(window);
            foreach (var item in window.GetVisualDescendants().OfType<ListBoxItem>())
            {
                var list = item.GetVisualAncestors().OfType<ListBox>().First();
                var kind = list.Classes.Contains("cards") ? "cards" : list.Name ?? string.Join('.', list.Classes.Where(c => !c.StartsWith(':')));
                var card = list.Classes.Contains("cards");
                seen[kind] = seen.GetValueOrDefault(kind) + 1;
                CheckListItem(p, $"{name} {kind}#{item.Name} {(dark ? "dark" : "light")}", item, card, window);
                if (card)
                    CheckCardVisual(p, $"{name} {kind} {(dark ? "dark" : "light")}", item, window);
            }
            problems.AddRange(p.Items);
        }

        _output.WriteLine("掃描的清單：" + string.Join("、", seen.Select(kv => $"{kv.Key}×{kv.Value}")));
        // 歷史抽屜、設定 Menu、卡片清單（連線、模型、小精靈、色票）、ModelPicker、小精靈狀態預覽都掃到了
        Assert.Contains("history-list", seen.Keys);
        Assert.Contains("cards", seen.Keys);
        Assert.Contains("EntryList", seen.Keys);
        Assert.Contains("CategoryList", seen.Keys);
        Assert.Contains("SpriteStateList", seen.Keys);
        Report(problems);
    }

    /// <summary>卡片清單：未選取＝Surface 底；選取＝Soft 底＋Accent 邊框；Hover 才有陰影。</summary>
    private static void CheckCardVisual(Problems problems, string context, ListBoxItem item, Window window)
    {
        var card = item.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("card"));
        if (card is null)
            return;
        var originalSelected = ((IPseudoClasses)item.Classes).Contains(":selected");
        SetState(item, State.Normal, false);
        window.UpdateLayout();
        problems.Is(context + " normal", "Border.card.Background", card.Background, "LumiSurfaceBrush");
        problems.Is(context + " normal", "Border.card.BorderBrush", card.BorderBrush, "LumiBorderBrush");
        SetState(item, State.Hover, false);
        window.UpdateLayout();
        problems.Is(context + " hover", "Border.card.Background", card.Background, "LumiSurfaceBrush");
        if (card.BoxShadow.Count == 0)
            problems.Items.Add(context + " hover：可點擊的卡片 Hover 應有陰影");
        SetState(item, State.Normal, true);
        window.UpdateLayout();
        problems.Is(context + " selected", "Border.card.Background", card.Background, "LumiAccentSoftBrush");
        problems.Is(context + " selected", "Border.card.BorderBrush", card.BorderBrush, "LumiAccentBorderBrush");
        SetState(item, State.Pressed, true);
        window.UpdateLayout();
        problems.Is(context + " selected pressed", "Border.card.Background", card.Background, "LumiAccentSoftBrush");
        SetState(item, State.Normal, originalSelected);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public async Task ListBoxItemFocus_IsAnAccentFrame_ExceptCardsWhichDrawTheirOwnBorder(bool dark)
    {
        using var surfaces = await AppSurfaces.OpenAsync();
        var problems = new List<string>();
        var checkedLists = new HashSet<string>();
        foreach (var (name, window) in surfaces.Windows)
        {
            window.RequestedThemeVariant = VariantOf(dark);
            window.UpdateLayout();
            // 每種清單各挑第一個可聚焦的項目
            foreach (var group in window.GetVisualDescendants().OfType<ListBoxItem>().Where(i => i.IsEffectivelyVisible && i.IsEffectivelyEnabled)
                         .GroupBy(i => i.GetVisualAncestors().OfType<ListBox>().First()))
            {
                var list = group.Key;
                var item = group.First();
                var key = $"{name}/{list.Name ?? string.Join('.', list.Classes.Where(c => !c.StartsWith(':')))}";
                checkedLists.Add(key);
                if (list.Classes.Contains("cards"))
                {
                    // 卡片自己畫焦點（Border.card 邊框變 Accent），不能再疊一個焦點框
                    item.Focus(NavigationMethod.Tab);
                    window.UpdateLayout();
                    var adorners = AdornerLayer.GetAdornerLayer(item)?.Children.Where(x => ReferenceEquals(AdornerLayer.GetAdornedElement(x), item)).ToList() ?? [];
                    if (adorners.Count != 0)
                        problems.Add($"{key} Focus：卡片自己畫焦點，不該再有焦點框（實際 {adorners.Count}）");
                    var card = item.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("card"));
                    var p = new Problems(window);
                    p.Is(key + " Focus", "Border.card.BorderBrush", card.BorderBrush, "LumiAccentBrush");
                    problems.AddRange(p.Items);
                }
                else
                {
                    problems.AddRange(CheckAccentFocusFrame(key, window, item));
                }
            }
        }
        _output.WriteLine("焦點檢查的清單：" + string.Join("、", checkedLists));
        Assert.True(checkedLists.Count >= 8, $"只檢查了 {checkedLists.Count} 個清單");
        Report(problems);
    }

    // ===== MenuItem／Flyout 外框 =====

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public async Task MenuItem_HasTokenLooks_InEveryState(bool dark)
    {
        using var surfaces = await AppSurfaces.OpenAsync(includeSettings: false);
        var window = surfaces.Windows.Single(w => w.Name == "flyouts").Window;
        window.RequestedThemeVariant = VariantOf(dark);
        window.UpdateLayout();
        var items = window.GetVisualDescendants().OfType<MenuItem>().ToList();
        Assert.True(items.Count >= 3);
        var p = new Problems(window);
        foreach (var item in items)
        {
            var originalEnabled = item.IsEnabled;
            Assert.Equal(new CornerRadius(10), item.CornerRadius);
            foreach (var state in Enum.GetValues<State>())
            {
                // 停用的那一項只檢查停用；其他項檢查 正常／Hover（:selected）／Pressed
                if (!originalEnabled && state != State.Disabled)
                    continue;
                SetState(item, state, selected: state is State.Hover or State.Pressed);
                window.UpdateLayout();
                var layoutRoot = Part<Border>(item, "PART_LayoutRoot");
                var header = Part<ContentPresenter>(item, "PART_HeaderPresenter");
                var text = header.GetVisualDescendants().OfType<TextBlock>().First();
                var (bg, fg) = state switch
                {
                    State.Normal => ("LumiSurfaceBrush", "LumiTextBrush"),
                    State.Hover => ("LumiSurfaceAltBrush", "LumiTextBrush"),
                    State.Pressed => ("LumiAccentSoftBrush", "LumiTextOnSoftBrush"),
                    _ => ("LumiSurfaceBrush", "LumiTextMutedBrush"),
                };
                var ctx = $"MenuItem「{item.Header}」 {(dark ? "dark" : "light")} {state}";
                p.Is(ctx, "PART_LayoutRoot.Background", layoutRoot.Background, bg);
                p.Is(ctx, "PART_HeaderPresenter.Foreground", header.Foreground, fg);
                p.Is(ctx, "TextBlock.Foreground", text.Foreground, fg);
                if (layoutRoot.CornerRadius != new CornerRadius(10))
                    p.Items.Add($"{ctx}：LayoutRoot 圓角應為 10 實際 {layoutRoot.CornerRadius}");
            }
            SetState(item, originalEnabled ? State.Normal : State.Disabled, selected: false);
        }
        var enabled = items.First(i => i.IsEnabled);
        p.Items.AddRange(CheckAccentFocusFrame("MenuItem", window, enabled));
        Report(p.Items);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public async Task FlyoutAndMenuFlyoutPresenters_AreSurfaceWithBorder_Radius12(bool dark)
    {
        using var surfaces = await AppSurfaces.OpenAsync(includeSettings: false);
        var window = surfaces.Windows.Single(w => w.Name == "flyouts").Window;
        window.RequestedThemeVariant = VariantOf(dark);
        window.UpdateLayout();
        var p = new Problems(window);
        foreach (var presenter in new Control[]
                 {
                     window.GetVisualDescendants().OfType<FlyoutPresenter>().Single(),
                     window.GetVisualDescendants().OfType<MenuFlyoutPresenter>().Single(),
                 })
        {
            var ctx = $"{presenter.GetType().Name} {(dark ? "dark" : "light")}";
            var root = Part<Border>(presenter, "LayoutRoot");
            p.Is(ctx, "LayoutRoot.Background", root.Background, "LumiSurfaceBrush");
            p.Is(ctx, "LayoutRoot.BorderBrush", root.BorderBrush, "LumiBorderBrush");
            p.Is(ctx, "Foreground", ((TemplatedControl)presenter).Foreground, "LumiTextBrush");
            if (root.CornerRadius != new CornerRadius(12))
                p.Items.Add($"{ctx}：圓角應為 12 實際 {root.CornerRadius}");
            if (root.BorderThickness != new Thickness(1))
                p.Items.Add($"{ctx}：框線應為 1 實際 {root.BorderThickness}");
            // 內容文字繼承 Text 色（不是 Fluent 的純黑）
            var firstText = presenter.GetVisualDescendants().OfType<TextBlock>().First(t => !string.IsNullOrWhiteSpace(t.Text) && !t.GetBaseValue(TextBlock.ForegroundProperty).HasValue);
            p.Is(ctx, "內容 TextBlock.Foreground", firstText.Foreground, "LumiTextBrush");
        }
        Report(p.Items);
    }

    // ===== CheckBox =====

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void CheckBox_HasTokenLooks_InEveryState(bool dark)
    {
        var p = new List<string>();
        foreach (var isChecked in new[] { false, true })
        {
            var box = new CheckBox { Content = "選項", IsChecked = isChecked };
            var window = Host(box, dark);
            var problems = new Problems(window);
            foreach (var state in Enum.GetValues<State>())
            {
                SetState(box, state);
                window.UpdateLayout();
                var rect = Part<Border>(box, "NormalRectangle");
                var glyph = Part<Path>(box, "CheckGlyph");
                var label = Part<ContentPresenter>(box, "PART_ContentPresenter");
                var text = label.GetVisualDescendants().OfType<TextBlock>().First();
                var ctx = $"CheckBox{(isChecked ? " checked" : "")} {(dark ? "dark" : "light")} {state}";
                var (bg, border) = (isChecked, state) switch
                {
                    (false, State.Normal) => ("LumiSurfaceBrush", "LumiBorderStrongBrush"),
                    (false, State.Hover) => ("LumiSurfaceAltBrush", "LumiAccentBrush"),
                    (false, State.Pressed) => ("LumiAccentSoftBrush", "LumiAccentBrush"),
                    (false, _) => ("LumiSurfaceAltBrush", "LumiBorderBrush"),
                    (true, State.Normal) => ("LumiAccentBrush", "LumiAccentBrush"),
                    (true, State.Hover or State.Pressed) => ("LumiAccentHoverBrush", "LumiAccentHoverBrush"),
                    _ => ("LumiBorderStrongBrush", "LumiBorderStrongBrush"),
                };
                problems.Is(ctx, "NormalRectangle.Background", rect.Background, bg);
                problems.Is(ctx, "NormalRectangle.BorderBrush", rect.BorderBrush, border);
                var labelToken = state == State.Disabled ? "LumiTextMutedBrush" : "LumiTextBrush";
                problems.Is(ctx, "文字 Foreground", label.Foreground, labelToken);
                problems.Is(ctx, "TextBlock.Foreground", text.Foreground, labelToken);
                if (isChecked)
                    problems.Is(ctx, "CheckGlyph.Fill", glyph.Fill, state == State.Disabled ? "LumiSurfaceBrush" : "LumiOnAccentBrush");
            }
            problems.Items.AddRange(CheckAccentFocusFrame($"CheckBox{(isChecked ? " checked" : "")}", window, box));
            window.Close();
            p.AddRange(problems.Items);
        }
        Report(p);
    }

    // ===== RadioButton（非分段切換） =====

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void RadioButton_HasTokenLooks_InEveryState(bool dark)
    {
        var p = new List<string>();
        foreach (var isChecked in new[] { false, true })
        {
            var radio = new RadioButton { Content = "選項", IsChecked = isChecked, GroupName = "g" + isChecked };
            var window = Host(radio, dark);
            var problems = new Problems(window);
            foreach (var state in Enum.GetValues<State>())
            {
                SetState(radio, state);
                window.UpdateLayout();
                var outer = Part<Ellipse>(radio, "OuterEllipse");
                var checkOuter = Part<Ellipse>(radio, "CheckOuterEllipse");
                var dot = Part<Ellipse>(radio, "CheckGlyph");
                var label = Part<ContentPresenter>(radio, "PART_ContentPresenter");
                var ctx = $"RadioButton{(isChecked ? " checked" : "")} {(dark ? "dark" : "light")} {state}";
                if (!isChecked)
                {
                    var (fill, stroke) = state switch
                    {
                        State.Normal => ("LumiSurfaceBrush", "LumiBorderStrongBrush"),
                        State.Hover => ("LumiSurfaceAltBrush", "LumiAccentBrush"),
                        State.Pressed => ("LumiAccentSoftBrush", "LumiAccentBrush"),
                        _ => ("LumiSurfaceAltBrush", "LumiBorderBrush"),
                    };
                    problems.Is(ctx, "OuterEllipse.Fill", outer.Fill, fill);
                    problems.Is(ctx, "OuterEllipse.Stroke", outer.Stroke, stroke);
                    problems.Opacity(ctx, checkOuter.Opacity, 0);
                }
                else
                {
                    var (ringFill, ringStroke, dotFill) = state switch
                    {
                        State.Normal => ("LumiSurfaceBrush", "LumiAccentBrush", "LumiAccentBrush"),
                        State.Hover or State.Pressed => ("LumiSurfaceBrush", "LumiAccentHoverBrush", "LumiAccentHoverBrush"),
                        _ => ("LumiSurfaceAltBrush", "LumiBorderStrongBrush", "LumiBorderStrongBrush"),
                    };
                    problems.Is(ctx, "CheckOuterEllipse.Fill", checkOuter.Fill, ringFill);
                    problems.Is(ctx, "CheckOuterEllipse.Stroke", checkOuter.Stroke, ringStroke);
                    problems.Is(ctx, "CheckGlyph.Fill", dot.Fill, dotFill);
                    problems.Opacity(ctx, outer.Opacity, 0);
                }
                problems.Is(ctx, "文字 Foreground", label.Foreground, state == State.Disabled ? "LumiTextMutedBrush" : "LumiTextBrush");
            }
            problems.Items.AddRange(CheckAccentFocusFrame($"RadioButton{(isChecked ? " checked" : "")}", window, radio));
            window.Close();
            p.AddRange(problems.Items);
        }
        Report(p);
    }

    [AvaloniaFact]
    public async Task TheRealCheckBoxes_UseTheGlobalCheckBoxStyle()
    {
        using var surfaces = await AppSurfaces.OpenAsync();
        var names = surfaces.Windows.SelectMany(w => w.Window.GetVisualDescendants().OfType<CheckBox>()).Select(c => c.Name).Distinct().ToList();
        // 模型設定的「顯示思考過程」「啟用工具」、小精靈頁的語氣開關
        Assert.Contains("ShowThinkingCheckBox", names);
        Assert.Contains("ToolsEnabledCheckBox", names);
        Assert.Contains("SpriteVoiceBox", names);
        foreach (var (name, window) in surfaces.Windows)
        foreach (var box in window.GetVisualDescendants().OfType<CheckBox>().Where(c => c.IsEffectivelyVisible))
        {
            var rect = Part<Border>(box, "NormalRectangle");
            var p = new Problems(window);
            p.Is($"{name} {box.Name}", "NormalRectangle.BorderBrush（未勾選或已勾選的 Accent）", rect.BorderBrush,
                box.IsChecked == true ? "LumiAccentBrush" : "LumiBorderStrongBrush");
            Report(p.Items);
        }
    }

    // ===== ComboBox／ComboBoxItem =====

    private static ComboBox NewComboBox() => new() { Items = { "甲", "乙", "丙" }, SelectedIndex = 1, Width = 200 };

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void ComboBox_HasTokenLooks_InEveryState(bool dark)
    {
        var combo = NewComboBox();
        var window = Host(combo, dark);
        var p = new Problems(window);
        foreach (var state in Enum.GetValues<State>())
        {
            SetState(combo, state);
            window.UpdateLayout();
            var background = Part<Border>(combo, "Background");
            var content = Part<ContentControl>(combo, "ContentPresenter");
            var glyph = Part<LumiIcon>(combo, "DropDownGlyph");
            var glow = Part<Border>(combo, "FocusGlow");
            var ctx = $"ComboBox {(dark ? "dark" : "light")} {state}";
            var (bg, border, fg) = state switch
            {
                State.Normal => ("LumiSurfaceBrush", "LumiBorderStrongBrush", "LumiTextBrush"),
                State.Hover or State.Pressed => ("LumiSurfaceBrush", "LumiAccentBorderBrush", "LumiTextBrush"),
                _ => ("LumiSurfaceAltBrush", "LumiBorderBrush", "LumiTextMutedBrush"),
            };
            p.Is(ctx, "Background.Background", background.Background, bg);
            p.Is(ctx, "Background.BorderBrush", background.BorderBrush, border);
            p.Is(ctx, "ContentPresenter.Foreground", content.Foreground, fg);
            p.Is(ctx, "DropDownGlyph.Foreground", glyph.Foreground, "LumiTextMutedBrush");
            Assert.False(glow.IsVisible, ctx + "：沒有焦點時不該有光暈");
        }

        // 鍵盤焦點：Accent 邊框＋Soft 光暈，不再疊 Fluent 的焦點框
        SetState(combo, State.Normal);
        combo.Focus(NavigationMethod.Tab);
        window.UpdateLayout();
        var ctxFocus = $"ComboBox {(dark ? "dark" : "light")} Focus";
        p.Is(ctxFocus, "Background.BorderBrush", Part<Border>(combo, "Background").BorderBrush, "LumiAccentBrush");
        p.Is(ctxFocus, "Background.Background", Part<Border>(combo, "Background").Background, "LumiSurfaceBrush");
        p.Is(ctxFocus, "FocusGlow.BorderBrush", Part<Border>(combo, "FocusGlow").BorderBrush, "LumiAccentSoftBrush");
        Assert.True(Part<Border>(combo, "FocusGlow").IsVisible);
        p.Is(ctxFocus, "ContentPresenter.Foreground", Part<ContentControl>(combo, "ContentPresenter").Foreground, "LumiTextBrush");
        var adorners = AdornerLayer.GetAdornerLayer(combo)?.Children.Where(x => ReferenceEquals(AdornerLayer.GetAdornedElement(x), combo)).ToList() ?? [];
        Assert.True(adorners.Count == 0, ctxFocus + $"：自己畫焦點，不該再有焦點框（{adorners.Count}）");

        // 展開下拉清單：邊框維持 Accent
        combo.IsDropDownOpen = true;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        p.Is($"ComboBox {(dark ? "dark" : "light")} DropDownOpen", "Background.BorderBrush", Part<Border>(combo, "Background").BorderBrush, "LumiAccentBrush");
        var popup = (Border)Part<Popup>(combo, "PART_Popup").Child!;
        p.Is("ComboBox 下拉清單外框", "PopupBorder.Background", popup.Background, "LumiSurfaceBrush");
        p.Is("ComboBox 下拉清單外框", "PopupBorder.BorderBrush", popup.BorderBrush, "LumiBorderBrush");
        combo.IsDropDownOpen = false;
        window.Close();
        Report(p.Items);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void ComboBoxItem_HasTokenLooks_InEveryState(bool dark)
    {
        var combo = NewComboBox();
        var window = Host(combo, dark);
        combo.IsDropDownOpen = true;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var p = new Problems(window);
        var items = Enumerable.Range(0, 3).Select(i => (ComboBoxItem)combo.ContainerFromIndex(i)!).ToList();
        Assert.All(items, Assert.NotNull);
        foreach (var (item, index) in items.Select((item, i) => (item, i)))
        {
            var selected = index == 1;
            foreach (var state in Enum.GetValues<State>())
            {
                SetState(item, state, selected);
                window.UpdateLayout();
                var presenter = item.GetVisualDescendants().OfType<ContentPresenter>().First(c => c.Name == "PART_ContentPresenter");
                var ctx = $"ComboBoxItem{(selected ? " selected" : "")} {(dark ? "dark" : "light")} {state}";
                var look = ExpectedListLook(state, selected);
                p.Is(ctx, "ContentPresenter.Background", presenter.Background, look.PresenterBg!);
                p.Is(ctx, "ContentPresenter.Foreground", presenter.Foreground, look.PresenterFg);
                var text = presenter.GetVisualDescendants().OfType<TextBlock>().First();
                p.Is(ctx, "TextBlock.Foreground", text.Foreground, look.TextFg);
                if (presenter.CornerRadius != new CornerRadius(10))
                    p.Items.Add($"{ctx}：圓角應為 10 實際 {presenter.CornerRadius}");
            }
            SetState(item, State.Normal, selected);
        }
        p.Items.AddRange(CheckAccentFocusFrame("ComboBoxItem", window, items[0]));
        combo.IsDropDownOpen = false;
        window.Close();
        Report(p.Items);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public async Task TheRealComboBoxes_KeepTheTokenLook_InNormalAndDisabled(bool dark)
    {
        using var surfaces = await AppSurfaces.OpenAsync();
        var found = 0;
        var problems = new List<string>();
        foreach (var (name, window) in surfaces.Windows)
        {
            window.RequestedThemeVariant = VariantOf(dark);
            window.UpdateLayout();
            var p = new Problems(window);
            foreach (var combo in window.GetVisualDescendants().OfType<ComboBox>().Where(c => c.IsEffectivelyVisible))
            {
                found++;
                var originalEnabled = combo.IsEnabled;
                foreach (var state in new[] { State.Normal, State.Hover, State.Disabled })
                {
                    SetState(combo, state);
                    window.UpdateLayout();
                    var bg = Part<Border>(combo, "Background");
                    var ctx = $"{name} {combo.Name} {(dark ? "dark" : "light")} {state}";
                    p.Is(ctx, "Background.Background", bg.Background, state == State.Disabled ? "LumiSurfaceAltBrush" : "LumiSurfaceBrush");
                    p.Is(ctx, "Background.BorderBrush", bg.BorderBrush, state switch { State.Normal => "LumiBorderStrongBrush", State.Hover => "LumiAccentBorderBrush", _ => "LumiBorderBrush" });
                }
                SetState(combo, originalEnabled ? State.Normal : State.Disabled);
            }
            problems.AddRange(p.Items);
        }
        Assert.True(found >= 6, $"只掃到 {found} 個 ComboBox");
        Report(problems);
    }

    // ===== NumericUpDown =====

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void NumericUpDown_HasTokenLooks_InEveryState(bool dark)
    {
        var nud = new NumericUpDown { Value = 3, Minimum = 0, Maximum = 10, Width = 160 };
        var window = Host(nud, dark);
        var p = new Problems(window);
        var spinner = Part<ButtonSpinner>(nud, "PART_Spinner");
        var chrome = spinner.GetVisualDescendants().OfType<Border>().First(b => b.BorderThickness == new Thickness(1));
        var number = Part<TextPresenter>(nud, "PART_TextPresenter");

        foreach (var state in Enum.GetValues<State>())
        {
            SetState(nud, state);
            window.UpdateLayout();
            var ctx = $"NumericUpDown {(dark ? "dark" : "light")} {state}";
            var (bg, border, fg) = state switch
            {
                State.Normal => ("LumiSurfaceBrush", "LumiBorderStrongBrush", "LumiTextBrush"),
                State.Hover or State.Pressed => ("LumiSurfaceBrush", "LumiAccentBorderBrush", "LumiTextBrush"),
                _ => ("LumiSurfaceAltBrush", "LumiBorderBrush", "LumiTextMutedBrush"),
            };
            p.Is(ctx, "外框 Background", chrome.Background, bg);
            p.Is(ctx, "外框 BorderBrush", chrome.BorderBrush, border);
            p.Is(ctx, "數字 Foreground", number.Foreground, fg);
        }

        SetState(nud, State.Normal);
        ((IPseudoClasses)nud.Classes).Set(":focus-within", true);
        window.UpdateLayout();
        p.Is($"NumericUpDown {(dark ? "dark" : "light")} Focus", "外框 BorderBrush", chrome.BorderBrush, "LumiAccentBrush");
        ((IPseudoClasses)nud.Classes).Set(":focus-within", false);

        // 內層 TextBox 沒有自己的框與底（外框代表整個控制項）
        var inner = Part<TextBox>(nud, "PART_TextBox");
        Assert.Equal(new Thickness(0), inner.BorderThickness);
        Assert.Equal(0, inner.MinHeight);

        // 上下箭頭按鈕
        foreach (var button in spinner.GetVisualDescendants().OfType<RepeatButton>())
        {
            foreach (var state in Enum.GetValues<State>())
            {
                SetState(button, state);
                window.UpdateLayout();
                var presenter = Part<ContentPresenter>(button, "PART_ContentPresenter");
                var icon = button.GetVisualDescendants().OfType<PathIcon>().Single();
                var ctx = $"NumericUpDown {button.Name} {(dark ? "dark" : "light")} {state}";
                var (bg, fg) = state switch
                {
                    State.Normal => ("Transparent", "LumiTextMutedBrush"),
                    State.Hover => ("LumiSurfaceAltBrush", "LumiTextBrush"),
                    State.Pressed => ("LumiAccentSoftBrush", "LumiTextBrush"),
                    _ => ("Transparent", "LumiTextMutedBrush"),
                };
                p.Is(ctx, "ContentPresenter.Background", presenter.Background, bg);
                p.Is(ctx, "ContentPresenter.BorderBrush", presenter.BorderBrush, "LumiBorderBrush");
                p.Is(ctx, "ContentPresenter.Foreground", presenter.Foreground, fg);
                p.Is(ctx, "PathIcon.Foreground", icon.Foreground, fg);
            }
            SetState(button, State.Normal);
        }
        window.Close();
        Report(p.Items);
    }

    // ===== Expander（進階區段標題、思考過程） =====

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void AdvancedExpanderHeader_HasTokenLooks_InEveryState(bool dark)
    {
        var expander = new Expander { Classes = { "advanced" }, Header = "進階", Content = new TextBlock { Text = "內容" }, IsExpanded = true };
        var window = Host(expander, dark);
        var p = new Problems(window);
        var header = Part<ToggleButton>(expander, "ExpanderHeader");
        foreach (var state in Enum.GetValues<State>())
        {
            SetState(header, state);
            window.UpdateLayout();
            var border = Part<Border>(header, "HeaderBorder");
            var content = Part<ContentPresenter>(header, "HeaderContent");
            var ctx = $"Expander.advanced {(dark ? "dark" : "light")} {state}";
            var fg = state is State.Hover or State.Pressed ? "LumiTextBrush" : "LumiTextMutedBrush";
            p.Is(ctx, "HeaderBorder.Background", border.Background, "Transparent");
            p.Is(ctx, "HeaderBorder.BorderBrush", border.BorderBrush, "Transparent");
            p.Is(ctx, "HeaderContent.Foreground", content.Foreground, fg);
            p.Is(ctx, "標題文字 Foreground", content.GetVisualDescendants().OfType<TextBlock>().First().Foreground, fg);
        }
        SetState(header, State.Normal);
        // 內容區沒有 Fluent 的灰底與框線
        p.Is($"Expander.advanced {(dark ? "dark" : "light")}", "Expander.Background", expander.Background, "Transparent");
        var content2 = Part<Border>(expander, "ExpanderContent");
        Assert.Null(content2.Background);
        header.Focus(NavigationMethod.Tab);
        window.UpdateLayout();
        p.Is($"Expander.advanced {(dark ? "dark" : "light")} Focus", "HeaderBorder.BorderBrush", Part<Border>(header, "HeaderBorder").BorderBrush, "LumiAccentBrush");
        window.Close();
        Report(p.Items);
    }

    [AvaloniaFact]
    public async Task EveryExpanderInTheApp_IsAnAdvancedExpander()
    {
        using var surfaces = await AppSurfaces.OpenAsync();
        var count = 0;
        foreach (var (name, window) in surfaces.Windows)
        foreach (var expander in window.GetVisualDescendants().OfType<Expander>())
        {
            count++;
            Assert.True(expander.Classes.Contains("advanced"), $"{name}：Expander#{expander.Name} 沒有 advanced（會是 Fluent 預設的灰底外觀）");
        }
        Assert.True(count >= 3, $"只掃到 {count} 個 Expander");
    }

    // ===== ScrollBar =====

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void ScrollBar_ThumbIsBorderStrong_TrackIsTransparent(bool dark)
    {
        var bar = new ScrollBar
        {
            Orientation = Avalonia.Layout.Orientation.Vertical, Maximum = 100, ViewportSize = 20, Height = 120,
            AllowAutoHide = false, Visibility = ScrollBarVisibility.Visible,
        };
        var window = Host(bar, dark);
        var p = new Problems(window);
        var thumb = bar.GetVisualDescendants().OfType<Thumb>().Single();
        var track = Part<Rectangle>(bar, "TrackRect");
        foreach (var state in new[] { State.Normal, State.Hover, State.Pressed })
        {
            var classes = (IPseudoClasses)thumb.Classes;
            classes.Set(":pointerover", state != State.Normal);
            classes.Set(":pressed", state == State.Pressed);
            window.UpdateLayout();
            var ctx = $"ScrollBar {(dark ? "dark" : "light")} {state}";
            p.Is(ctx, "Thumb.Background", thumb.Background, state == State.Normal ? "LumiBorderStrongBrush" : "LumiTextMutedBrush");
            p.Is(ctx, "TrackRect.Fill", track.Fill, "Transparent");
        }
        window.Close();
        Report(p.Items);
    }

}
