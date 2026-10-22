using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Lumi.App.Controls;
using Lumi.App.Sprites;
using Lumi.App.Views;
using Xunit.Abstractions;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Headless;

/// <summary>
/// UE-3：每種按鈕樣式在 正常／Hover／Pressed／Disabled 四個狀態下，「實際生效」的底色、前景色、框線都是預期的 token。
/// Fluent 的 <c>Button:pointerover /template/ ContentPresenter#PART_ContentPresenter</c> 等規則設在模板內的 ContentPresenter，
/// 優先於我們設在 Button 本身（透過 TemplateBinding）的 Background／Foreground／BorderBrush，所以每個層級都要自己寫出狀態。
/// 讀取的是模板零件的屬性值（不是 Button 的屬性），並確認 Content 內的 TextBlock 與 LumiIcon 拿到同一個前景色。
/// </summary>
public class ButtonStateStyleTests
{
    private readonly ITestOutputHelper _output;

    public ButtonStateStyleTests(ITestOutputHelper output) => _output = output;

    internal enum State { Normal, Hover, Pressed, Disabled }

    /// <summary>預期的外觀。token 名稱（LumiXxxBrush）或 "Transparent"；null＝不檢查。</summary>
    internal sealed record Look(string? Bg, string? Fg, string? Border = null, double? Opacity = null);

    internal sealed record Layer(Look Normal, Look Hover, Look Pressed, Look Disabled)
    {
        public Look For(State state) => state switch
        {
            State.Normal => Normal,
            State.Hover => Hover,
            State.Pressed => Pressed,
            _ => Disabled,
        };
    }

    internal enum Kind { Button, Toggle, Radio }

    internal enum Parts { Presenter, Dashed, Segment }

    internal sealed record Case(string Name, string Host, Kind Kind, string Classes, Layer Layer, Parts Parts = Parts.Presenter, bool Checked = false);

    // ---- 各層級的預期外觀（plan/12 的「共用元件樣式」＋ UE-3 規格） ----

    internal static readonly Layer Primary = new(
        new Look("LumiPrimaryButtonBrush", "LumiPrimaryButtonTextBrush", "Transparent"),
        new Look("LumiPrimaryButtonHoverBrush", "LumiPrimaryButtonTextBrush", "Transparent"),
        new Look("LumiPrimaryButtonPressedBrush", "LumiPrimaryButtonTextBrush", "Transparent"),
        new Look("LumiSurfaceAltBrush", "LumiTextMutedBrush", "Transparent"));

    internal static readonly Layer Secondary = new(
        new Look("LumiSurfaceBrush", "LumiTextBrush", "LumiBorderStrongBrush"),
        new Look("LumiSurfaceAltBrush", "LumiTextBrush", "LumiBorderStrongBrush"),
        new Look("LumiAccentSoftBrush", "LumiTextBrush", "LumiBorderStrongBrush"),
        new Look("Transparent", "LumiTextMutedBrush", "LumiBorderBrush"));

    private static readonly Layer Tertiary = new(
        new Look("Transparent", "LumiTextMutedBrush", "Transparent"),
        new Look("LumiSurfaceAltBrush", "LumiTextMutedBrush", "Transparent"),
        new Look("LumiAccentSoftBrush", "LumiTextMutedBrush", "Transparent"),
        new Look("Transparent", "LumiTextMutedBrush", "Transparent", Opacity: 0.6));

    internal static readonly Layer Danger = new(
        new Look("LumiSurfaceBrush", "LumiDangerBrush", "LumiDangerBorderBrush"),
        new Look("LumiDangerBackgroundBrush", "LumiDangerBrush", "LumiDangerBorderBrush"),
        new Look("LumiDangerBackgroundBrush", "LumiDangerBrush", "LumiDangerBrush"),
        new Look("Transparent", "LumiTextMutedBrush", "LumiBorderBrush"));

    private static readonly Layer Send = new(
        new Look("LumiAccentBrush", "LumiOnAccentBrush", "Transparent"),
        new Look("LumiAccentHoverBrush", "LumiOnAccentBrush", "Transparent"),
        new Look("LumiAccentHoverBrush", "LumiOnAccentBrush", "Transparent"),
        new Look("LumiAccentSoftBrush", "LumiTextMutedBrush", "Transparent"));

    /// <summary>chrome：和 tertiary 一樣的無框按鈕，但平常用 Text 色。</summary>
    private static readonly Layer Chrome = new(
        new Look("Transparent", "LumiTextBrush", "Transparent"),
        new Look("LumiSurfaceAltBrush", "LumiTextBrush", "Transparent"),
        new Look("LumiAccentSoftBrush", "LumiTextBrush", "Transparent"),
        new Look("Transparent", "LumiTextMutedBrush", "Transparent", Opacity: 0.6));

    /// <summary>虛線「＋新增」入口：tertiary 風格；Hover／Pressed 是 Soft 底＋OnSoft 文字。底色是 Rectangle 的 Fill。</summary>
    private static readonly Layer Dashed = new(
        new Look("Transparent", "LumiTextMutedBrush"),
        new Look("LumiAccentSoftBrush", "LumiTextOnSoftBrush"),
        new Look("LumiAccentSoftBrush", "LumiTextOnSoftBrush"),
        new Look("Transparent", "LumiTextMutedBrush", Opacity: 0.6));

    /// <summary>分段切換裡未選取的那一段。</summary>
    private static readonly Layer Segment = new(
        new Look("Transparent", "LumiTextMutedBrush", "Transparent"),
        new Look("LumiSurfaceBrush", "LumiTextMutedBrush", "Transparent"),
        new Look("LumiAccentSoftBrush", "LumiTextMutedBrush", "Transparent"),
        new Look("Transparent", "LumiTextMutedBrush", "Transparent", Opacity: 0.6));

    /// <summary>分段切換裡選取的那一段：Soft 底＋OnSoft 文字，Hover／Pressed 不變。</summary>
    private static readonly Layer SegmentChecked = new(
        new Look("LumiAccentSoftBrush", "LumiTextOnSoftBrush", "Transparent"),
        new Look("LumiAccentSoftBrush", "LumiTextOnSoftBrush", "Transparent"),
        new Look("LumiAccentSoftBrush", "LumiTextOnSoftBrush", "Transparent"),
        new Look("LumiAccentSoftBrush", "LumiTextMutedBrush", "Transparent", Opacity: 0.6));

    /// <summary>ToggleButton.secondary 選取後：Soft 底＋Accent 邊框＋OnSoft 文字；停用時退回 secondary 的停用外觀。</summary>
    internal static readonly Layer SecondaryChecked = new(
        new Look("LumiAccentSoftBrush", "LumiTextOnSoftBrush", "LumiAccentBorderBrush"),
        new Look("LumiAccentSoftBrush", "LumiTextOnSoftBrush", "LumiAccentBorderBrush"),
        new Look("LumiAccentSoftBrush", "LumiTextOnSoftBrush", "LumiAccentBorderBrush"),
        new Look("Transparent", "LumiTextMutedBrush", "LumiBorderBrush"));

    /// <summary>文字連結（小精靈氣泡的「查看詳細」）：無底，Info 色，停用 Muted。</summary>
    private static readonly Layer Link = new(
        new Look("Transparent", "LumiInfoBrush", "Transparent"),
        new Look("Transparent", "LumiInfoBrush", "Transparent"),
        new Look("Transparent", "LumiInfoBrush", "Transparent"),
        new Look("Transparent", "LumiTextMutedBrush", "Transparent"));

    /// <summary>卡片標題整列是按鈕：Hover／Pressed／強制展開時的停用都不換底、不變灰。</summary>
    private static readonly Layer CardHeader = new(
        new Look("Transparent", "LumiTextBrush", "Transparent"),
        new Look("Transparent", "LumiTextBrush", "Transparent"),
        new Look("Transparent", "LumiTextBrush", "Transparent"),
        new Look("Transparent", "LumiTextBrush", "Transparent"));

    /// <summary>程式碼區塊上的「複製」：深色底上的 Code 文字色，Hover 才有底。</summary>
    private static readonly Layer CodeCopy = new(
        new Look("Transparent", "LumiCodeTextBrush", "Transparent"),
        new Look("LumiCodeBackgroundBrush", "LumiCodeTextBrush", "Transparent"),
        new Look("LumiCodeBackgroundBrush", "LumiCodeTextBrush", "Transparent"),
        new Look("Transparent", "LumiCodeTextBrush", "Transparent", Opacity: 0.6));

    /// <summary>標題列的「小精靈模式」：Soft 底；Hover 加 Accent 邊框；停用只降透明度（Opacity 0.55 在 Button 上）。</summary>
    private static readonly Layer SpriteMode = new(
        new Look("LumiAccentSoftBrush", "LumiTextOnSoftBrush", "Transparent"),
        new Look("LumiAccentSoftBrush", "LumiTextOnSoftBrush", "LumiAccentBrush"),
        new Look("LumiAccentSoftBrush", "LumiTextOnSoftBrush", null),
        new Look("LumiAccentSoftBrush", "LumiTextOnSoftBrush", null, Opacity: 0.55));

    private static readonly Case[] Cases =
    [
        // Themes/Controls.axaml 的四個層級（以及 Fluent 預設的按鈕不該漏出來）
        new("primary", "app", Kind.Button, "primary", Primary),
        new("secondary", "app", Kind.Button, "secondary", Secondary),
        new("tertiary", "app", Kind.Button, "tertiary", Tertiary),
        new("danger", "app", Kind.Button, "danger", Danger),
        new("dashed", "app", Kind.Button, "dashed", Dashed, Parts.Dashed),
        new("ToggleButton.secondary", "app", Kind.Toggle, "secondary", Secondary),
        new("ToggleButton.secondary:checked", "app", Kind.Toggle, "secondary", SecondaryChecked, Checked: true),
        new("segmented RadioButton", "app", Kind.Radio, "", Segment, Parts.Segment),
        new("segmented RadioButton:checked", "app", Kind.Radio, "", SegmentChecked, Parts.Segment, Checked: true),
        new("md-copy", "app", Kind.Button, "md-copy", CodeCopy),
        // Themes/ChatStyles.axaml
        new("chrome", "composer", Kind.Button, "chrome", Chrome),
        new("tertiary small (ChatStyles)", "composer", Kind.Button, "tertiary small", Tertiary),
        // 送出／停止圓鈕
        new("round send", "app", Kind.Button, "round send", Send),
        new("round stop", "app", Kind.Button, "round stop", Primary),
        // 各 View 內的 class
        new("tertiary composer-tool collapsible", "composer", Kind.Button, "tertiary composer-tool collapsible", Tertiary),
        new("tertiary chip-remove", "composer", Kind.Button, "tertiary chip-remove", Tertiary),
        new("secondary toolbar collapsible", "toolbar", Kind.Button, "secondary toolbar collapsible", Secondary),
        new("secondary toolbar capsule", "toolbar", Kind.Button, "secondary toolbar capsule", Secondary),
        new("tertiary toolbar iconOnly", "toolbar", Kind.Button, "tertiary toolbar iconOnly", Tertiary),
        new("tertiary small message-action", "conversation", Kind.Button, "tertiary small message-action", Tertiary),
        new("primary confirm", "toolcard", Kind.Button, "primary confirm", Primary),
        new("secondary confirm", "toolcard", Kind.Button, "secondary confirm", Secondary),
        new("danger confirm", "toolcard", Kind.Button, "danger confirm", Danger),
        new("cardheader", "toolcard", Kind.Button, "cardheader", CardHeader),
        new("groupheader", "toolrun", Kind.Button, "groupheader", CardHeader),
        new("bubble small primary", "bubble", Kind.Button, "small primary", Primary),
        new("bubble small secondary", "bubble", Kind.Button, "small secondary", Secondary),
        new("bubble small tertiary", "bubble", Kind.Button, "small tertiary", Tertiary),
        new("bubble small danger", "bubble", Kind.Button, "small danger", Danger),
        new("bubble link", "bubble", Kind.Button, "link", Link),
        new("spriteMode", "titlebar", Kind.Button, "spriteMode", SpriteMode),
    ];

    public static TheoryData<string, bool> Variants()
    {
        var data = new TheoryData<string, bool>();
        foreach (var c in Cases)
        {
            data.Add(c.Name, false);
            data.Add(c.Name, true);
        }
        return data;
    }

    // ---- 建立與讀取 ----

    private static UserControl? HostFor(string host) => host switch
    {
        "composer" => new ChatComposer(),
        "toolbar" => new ChatToolbar(),
        "conversation" => new ConversationView(),
        "toolcard" => new ToolCardView(),
        "toolrun" => new ToolRunView(),
        "bubble" => new SpriteBubbleView(),
        "titlebar" => new CustomTitleBar(),
        _ => null,
    };

    private static (Window Window, ContentControl Button) Build(Case c, bool dark)
    {
        ContentControl button = c.Kind switch
        {
            Kind.Toggle => new ToggleButton { IsChecked = c.Checked },
            Kind.Radio => new RadioButton { IsChecked = c.Checked, GroupName = "g" },
            _ => new Button(),
        };
        foreach (var cls in c.Classes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            button.Classes.Add(cls);
        Assert.True(Application.Current!.TryGetResource("LumiIconCheck", null, out var geometry));
        button.Content = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4,
            Children = { new TextBlock { Text = "按鈕" }, new LumiIcon { Data = (Geometry)geometry! } },
        };

        Control root = button;
        if (c.Kind == Kind.Radio)
            root = new Border { Classes = { "segmented" }, Child = button };

        var panel = new StackPanel { Margin = new Thickness(16), Children = { root } };
        var window = new Window { Width = 400, Height = 300, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        var host = HostFor(c.Host);
        if (host is null)
            window.Content = panel;
        else
        {
            host.Content = panel;   // 換掉 View 的內容，但 View 自己的 Styles 仍然作用在新內容上
            window.Content = host;
        }
        window.Show();
        window.UpdateLayout();
        return (window, button);
    }

    internal static void Apply(ContentControl button, State state)
    {
        button.IsEnabled = state != State.Disabled;
        var classes = (IPseudoClasses)button.Classes;
        classes.Set(":pointerover", state is State.Hover or State.Pressed);
        classes.Set(":pressed", state == State.Pressed);
    }

    private sealed record Actual(IBrush? Bg, IBrush? Fg, IBrush? Border, double Opacity, IBrush? TextFg, IBrush? IconFg, bool HasIcon = true);

    private static Actual Read(Case c, ContentControl button)
    {
        var text = button.GetVisualDescendants().OfType<TextBlock>().First();
        var icon = button.GetVisualDescendants().OfType<LumiIcon>().FirstOrDefault();
        var buttonOpacity = button.Opacity;
        switch (c.Parts)
        {
            case Parts.Dashed:
            {
                var rect = button.GetVisualDescendants().OfType<Rectangle>().Single(r => r.Name == "PART_Dash");
                var content = button.GetVisualDescendants().OfType<ContentPresenter>().Single(p => p.Name == "DashContent");
                return new Actual(rect.Fill, content.Foreground, null, buttonOpacity * content.Opacity, text.Foreground, icon?.Foreground, icon is not null);
            }
            case Parts.Segment:
            {
                var segment = button.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Segment");
                var content = button.GetVisualDescendants().OfType<ContentPresenter>().Single(p => p.Name == "SegmentContent");
                return new Actual(segment.Background, content.Foreground, segment.BorderBrush, buttonOpacity * segment.Opacity * content.Opacity, text.Foreground, icon?.Foreground, icon is not null);
            }
            default:
            {
                var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().Single(p => p.Name == "PART_ContentPresenter");
                return new Actual(presenter.Background, presenter.Foreground, presenter.BorderBrush, buttonOpacity * presenter.Opacity, text.Foreground, icon?.Foreground, icon is not null);
            }
        }
    }

    internal static string Describe(IBrush? brush) => brush switch
    {
        null => "null",
        ISolidColorBrush solid => solid.Color.ToString() + (solid.Opacity < 1 ? $"@{solid.Opacity:0.##}" : ""),
        _ => brush.GetType().Name,
    };

    internal static Color Resolve(Window window, string token)
    {
        if (token == "Transparent")
            return Colors.Transparent;
        Assert.True(Application.Current!.TryGetResource(token, window.ActualThemeVariant, out var value), $"找不到 token {token}");
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }

    internal static bool Matches(IBrush? actual, Color expected) =>
        actual is null ? expected.A == 0 : actual is ISolidColorBrush solid && solid.Color == expected && solid.Opacity == 1;

    /// <summary>檢查一個按鈕在四個狀態下的外觀，回傳所有不符的項目（空＝全部正確）。</summary>
    private static List<string> Check(Case c, bool dark)
    {
        var (window, button) = Build(c, dark);
        try
        {
            return CheckButton(c, window, button, dark);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>檢查已經顯示在 <paramref name="window"/> 裡的按鈕（例如 ConfirmDialog 的確認鈕）在四個狀態與鍵盤焦點下的外觀。</summary>
    internal static List<string> CheckButton(Case c, Window window, ContentControl button, bool dark)
    {
        var problems = new List<string>();
        {
            foreach (var state in Enum.GetValues<State>())
            {
                Apply(button, state);
                window.UpdateLayout();
                var expected = c.Layer.For(state);
                var actual = Read(c, button);

                void Compare(string what, string? token, IBrush? value)
                {
                    if (token is null)
                        return;
                    var color = Resolve(window, token);
                    if (!Matches(value, color))
                        problems.Add($"{c.Name} {(dark ? "dark" : "light")} {state}：{what} 應為 {token}({color}) 實際 {Describe(value)}");
                }

                Compare("Background", expected.Bg, actual.Bg);
                Compare("Foreground", expected.Fg, actual.Fg);
                Compare("BorderBrush", expected.Border, actual.Border);
                Compare("TextBlock.Foreground", expected.Fg, actual.TextFg);
                if (actual.HasIcon)
                    Compare("LumiIcon.Foreground", expected.Fg, actual.IconFg);
                if (expected.Opacity is { } opacity && Math.Abs(actual.Opacity - opacity) > 0.001)
                    problems.Add($"{c.Name} {(dark ? "dark" : "light")} {state}：Opacity 應為 {opacity} 實際 {actual.Opacity}");
                if (expected.Opacity is null && Math.Abs(actual.Opacity - 1) > 0.001)
                    problems.Add($"{c.Name} {(dark ? "dark" : "light")} {state}：Opacity 應為 1 實際 {actual.Opacity}");
            }
            problems.AddRange(CheckFocus(c, window, button, dark));
        }
        return problems;
    }

    /// <summary>
    /// 鍵盤焦點（Tab）：一般按鈕只有一個 Accent 2px 的焦點框（不是 Fluent 的黑白雙框）；
    /// dashed 與分段切換自己畫焦點（虛線框／邊框變 Accent），不能再疊一個焦點框。
    /// </summary>
    private static List<string> CheckFocus(Case c, Window window, ContentControl button, bool dark)
    {
        var problems = new List<string>();
        var prefix = $"{c.Name} {(dark ? "dark" : "light")} Focus：";
        Apply(button, State.Normal);
        button.Focus(NavigationMethod.Tab);
        window.UpdateLayout();
        Assert.True(((IPseudoClasses)button.Classes).Contains(":focus-visible"), prefix + "沒有進入 :focus-visible");

        var layer = AdornerLayer.GetAdornerLayer(button);
        var adorners = layer?.Children.Where(x => ReferenceEquals(AdornerLayer.GetAdornedElement(x), button)).ToList() ?? [];
        var accent = Resolve(window, "LumiAccentBrush");

        if (c.Parts == Parts.Presenter)
        {
            if (adorners.Count != 1)
                return [prefix + $"應有 1 個焦點框，實際 {adorners.Count}"];
            if (adorners[0] is not Border frame)
                return [prefix + $"焦點框應是 Border，實際 {adorners[0].GetType().Name}"];
            if (!Matches(frame.BorderBrush, accent))
                problems.Add(prefix + $"焦點框顏色應為 Accent({accent}) 實際 {Describe(frame.BorderBrush)}");
            if (frame.BorderThickness != new Thickness(2))
                problems.Add(prefix + $"焦點框粗細應為 2 實際 {frame.BorderThickness}");
            if (frame.Child is not null)
                problems.Add(prefix + "焦點框裡還有內框（雙框）");
            if (frame.CornerRadius.TopLeft <= 0)
                problems.Add(prefix + "焦點框應是圓角");
        }
        else
        {
            if (adorners.Count != 0)
                problems.Add(prefix + $"自己畫焦點的按鈕不該再有焦點框，實際 {adorners.Count} 個");
            var own = c.Parts == Parts.Dashed
                ? button.GetVisualDescendants().OfType<Rectangle>().Single(r => r.Name == "PART_Dash").Stroke
                : button.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Segment").BorderBrush;
            if (!Matches(own, accent))
                problems.Add(prefix + $"自己的焦點外觀應為 Accent({accent}) 實際 {Describe(own)}");
        }
        return problems;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void EveryButtonStyle_HasExplicitTokenLook_InEveryState(string name, bool dark)
    {
        var problems = Check(Cases.Single(c => c.Name == name), dark);
        foreach (var p in problems)
            _output.WriteLine(p);
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [AvaloniaFact]
    public void AdvancedExpanderHeader_DrawsItsOwnFocus_WithoutAnAdornerFrame()
    {
        var expander = new Expander { Classes = { "advanced" }, Header = "進階", Content = new TextBlock { Text = "內容" } };
        var window = new Window { Width = 300, Height = 200, Content = expander };
        window.Show();
        window.UpdateLayout();
        var header = expander.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "ExpanderHeader");
        header.Focus(NavigationMethod.Tab);
        window.UpdateLayout();

        var adorners = AdornerLayer.GetAdornerLayer(header)?.Children.Where(x => ReferenceEquals(AdornerLayer.GetAdornedElement(x), header)).ToList() ?? [];
        Assert.Empty(adorners);
        var border = header.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "HeaderBorder");
        Assert.True(Matches(border.BorderBrush, Resolve(window, "LumiAccentBrush")));
        window.Close();
    }
}
