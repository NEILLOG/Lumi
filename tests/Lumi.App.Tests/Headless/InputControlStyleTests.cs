using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Lumi.App.Controls;

namespace Lumi.App.Tests.Headless;

/// <summary>UA-8：TextBox／ComboBox 在淺色與深色主題都吃 token（不是 Fluent 的純黑底），Focus／錯誤／停用狀態。</summary>
public class InputControlStyleTests
{
    private static ThemeVariant Variant(bool dark) => dark ? ThemeVariant.Dark : ThemeVariant.Light;

    private static Window Host(ThemeVariant variant, params Control[] children)
    {
        var panel = new StackPanel { Spacing = 16, Margin = new Thickness(16) };
        foreach (var child in children)
            panel.Children.Add(child);
        var window = new Window { Width = 400, Height = 500, Content = panel, RequestedThemeVariant = variant };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static Color Token(Window window, string name)
    {
        // 色票在 ThemeDictionaries 裡，要指定目前視窗的明暗才查得到
        Assert.True(Application.Current!.TryGetResource(name, window.ActualThemeVariant, out var value), $"找不到 token {name}");
        return value switch
        {
            ISolidColorBrush brush => brush.Color,
            Color color => color,
            _ => throw new InvalidOperationException($"{name} 不是顏色"),
        };
    }

    private static Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static T Part<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(x => x.Name == name);

    private static bool IsOpaqueBlack(Color c) => c.A == 255 && c.R == 0 && c.G == 0 && c.B == 0;

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextBox_UsesTokens(bool dark)
    {
        var box = new TextBox { Text = "你好", Watermark = "提示" };
        var window = Host(Variant(dark), box);

        var surface = Token(window, "LumiSurfaceBrush");
        Assert.False(IsOpaqueBlack(surface));
        var border = Part<Border>(box, "PART_BorderElement");
        Assert.Equal(surface, ColorOf(border.Background));
        Assert.Equal(Token(window, "LumiBorderStrongBrush"), ColorOf(border.BorderBrush));
        Assert.Equal(Token(window, "LumiTextBrush"), ColorOf(box.Foreground));
        Assert.Equal(new Thickness(1), border.BorderThickness);
        Assert.Equal(new CornerRadius(10), border.CornerRadius);
        Assert.False(Part<Border>(box, "PART_FocusGlow").IsVisible);

        box.Text = "";
        window.UpdateLayout();
        var watermark = Part<TextBlock>(box, "PART_Watermark");
        Assert.True(watermark.IsVisible);
        Assert.Equal(Token(window, "LumiTextMutedBrush"), ColorOf(watermark.Foreground));
    }

    [AvaloniaFact]
    public void TextBox_HeightIs40_AndMultiLineCanBeLarger()
    {
        var single = new TextBox { Text = "x" };
        var multi = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 96 };
        var window = Host(ThemeVariant.Light, single, multi);
        Assert.Equal(40, single.Bounds.Height);
        Assert.Equal(96, multi.MinHeight);
        Assert.Equal(96, multi.Bounds.Height);
        Assert.Equal(40, new Lazy<double>(() => (double)window.FindResource("LumiControlHeight")!).Value);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextBox_Focus_ShowsAccentBorderAndSoftGlow(bool dark)
    {
        var box = new TextBox { Text = "x" };
        var other = new TextBox();
        var window = Host(Variant(dark), box, other);

        box.Focus();
        window.UpdateLayout();
        Assert.True(box.IsFocused);

        var border = Part<Border>(box, "PART_BorderElement");
        Assert.Equal(Token(window, "LumiAccentBrush"), ColorOf(border.BorderBrush));
        Assert.Equal(Token(window, "LumiSurfaceBrush"), ColorOf(border.Background));
        Assert.Equal(new Thickness(1), border.BorderThickness);
        var glow = Part<Border>(box, "PART_FocusGlow");
        Assert.True(glow.IsVisible);
        Assert.Equal(Token(window, "LumiAccentSoftBrush"), ColorOf(glow.BorderBrush));
        Assert.Equal(new Thickness(3), glow.BorderThickness);
        // 光暈在外圈，不佔版面
        Assert.Equal(40, box.Bounds.Height);

        other.Focus();
        window.UpdateLayout();
        Assert.False(Part<Border>(box, "PART_FocusGlow").IsVisible);
        Assert.Equal(Token(window, "LumiBorderStrongBrush"), ColorOf(border.BorderBrush));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextBox_Hover_StillUsesTokenBackground(bool dark)
    {
        var box = new TextBox { Text = "x" };
        var window = Host(Variant(dark), box);
        var center = box.TranslatePoint(new Point(box.Bounds.Width / 2, box.Bounds.Height / 2), window)!.Value;
        window.MouseMove(center);
        window.UpdateLayout();
        Assert.True(box.IsPointerOver);
        var border = Part<Border>(box, "PART_BorderElement");
        Assert.Equal(Token(window, "LumiSurfaceBrush"), ColorOf(border.Background));
        Assert.False(IsOpaqueBlack(ColorOf(border.Background)));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextBox_ErrorClass_UsesDangerBorder(bool dark)
    {
        var box = new TextBox { Text = "x", Classes = { "error" } };
        var window = Host(Variant(dark), box);
        var border = Part<Border>(box, "PART_BorderElement");
        Assert.Equal(Token(window, "LumiDangerBrush"), ColorOf(border.BorderBrush));

        box.Focus();
        window.UpdateLayout();
        Assert.Equal(Token(window, "LumiDangerBrush"), ColorOf(border.BorderBrush));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextBox_Disabled_UsesSurfaceAltAndMutedText(bool dark)
    {
        var box = new TextBox { Text = "x", IsEnabled = false };
        var window = Host(Variant(dark), box);
        var border = Part<Border>(box, "PART_BorderElement");
        Assert.Equal(Token(window, "LumiSurfaceAltBrush"), ColorOf(border.Background));
        Assert.Equal(Token(window, "LumiTextMutedBrush"), ColorOf(box.Foreground));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComboBox_UsesTokens_AndChevronIcon(bool dark)
    {
        var combo = new ComboBox { ItemsSource = new[] { "甲", "乙" }, SelectedIndex = 0, Width = 240 };
        var window = Host(Variant(dark), combo);

        var background = Part<Border>(combo, "Background");
        Assert.Equal(Token(window, "LumiSurfaceBrush"), ColorOf(background.Background));
        Assert.Equal(Token(window, "LumiBorderStrongBrush"), ColorOf(background.BorderBrush));
        Assert.Equal(new CornerRadius(10), background.CornerRadius);
        Assert.Equal(new Thickness(1), background.BorderThickness);
        Assert.Equal(40, combo.Bounds.Height);
        Assert.Equal(Token(window, "LumiTextBrush"), ColorOf(Part<ContentControl>(combo, "ContentPresenter").Foreground));

        var glyph = Part<LumiIcon>(combo, "DropDownGlyph");
        Assert.Equal(Token(window, "LumiTextMutedBrush"), ColorOf(glyph.Foreground));
        Assert.True(Application.Current!.TryGetResource("LumiIconChevronDown", null, out var chevron));
        Assert.Same(chevron, glyph.Data);
        Assert.False(Part<Border>(combo, "FocusGlow").IsVisible);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComboBox_Focus_ShowsAccentBorderAndSoftGlow(bool dark)
    {
        var combo = new ComboBox { ItemsSource = new[] { "甲", "乙" }, SelectedIndex = 0, Width = 240 };
        var window = Host(Variant(dark), combo);
        combo.Focus();
        window.UpdateLayout();

        Assert.Equal(Token(window, "LumiAccentBrush"), ColorOf(Part<Border>(combo, "Background").BorderBrush));
        var glow = Part<Border>(combo, "FocusGlow");
        Assert.True(glow.IsVisible);
        Assert.Equal(Token(window, "LumiAccentSoftBrush"), ColorOf(glow.BorderBrush));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComboBox_Disabled_UsesSurfaceAlt(bool dark)
    {
        var combo = new ComboBox { ItemsSource = new[] { "甲" }, SelectedIndex = 0, IsEnabled = false, Width = 240 };
        var window = Host(Variant(dark), combo);
        Assert.Equal(Token(window, "LumiSurfaceAltBrush"), ColorOf(Part<Border>(combo, "Background").Background));
        Assert.Equal(Token(window, "LumiTextMutedBrush"), ColorOf(Part<ContentControl>(combo, "ContentPresenter").Foreground));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComboBox_Editable_InnerTextBoxHasNoOwnChrome(bool dark)
    {
        var combo = new ComboBox { ItemsSource = new[] { "甲" }, IsEditable = true, Text = "甲", Width = 240 };
        var window = Host(Variant(dark), combo);
        var inner = Part<TextBox>(combo, "PART_EditableTextBox");
        var innerBorder = Part<Border>(inner, "PART_BorderElement");
        Assert.Equal(new Thickness(0), innerBorder.BorderThickness);
        Assert.Equal(Colors.Transparent, ColorOf(innerBorder.Background));
        Assert.Equal(40, combo.Bounds.Height);

        inner.Focus();
        window.UpdateLayout();
        Assert.False(Part<Border>(inner, "PART_FocusGlow").IsVisible);
        Assert.True(Part<Border>(combo, "FocusGlow").IsVisible);
        Assert.Equal(Token(window, "LumiAccentBrush"), ColorOf(Part<Border>(combo, "Background").BorderBrush));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComboBoxItem_SelectedUsesSoftAndOnSoft_OthersAreTransparent(bool dark)
    {
        var selected = new ComboBoxItem { Content = "甲", IsSelected = true };
        var plain = new ComboBoxItem { Content = "乙" };
        var window = Host(Variant(dark), selected, plain);

        var selectedPresenter = Part<ContentPresenter>(selected, "PART_ContentPresenter");
        Assert.Equal(Token(window, "LumiAccentSoftBrush"), ColorOf(selectedPresenter.Background));
        Assert.Equal(Token(window, "LumiTextOnSoftBrush"), ColorOf(selectedPresenter.Foreground));
        Assert.Equal(new CornerRadius(10), selectedPresenter.CornerRadius);

        var plainPresenter = Part<ContentPresenter>(plain, "PART_ContentPresenter");
        Assert.True(plainPresenter.Background is null || ColorOf(plainPresenter.Background).A == 0);
        Assert.Equal(Token(window, "LumiTextBrush"), ColorOf(plainPresenter.Foreground));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComboBox_OpenPopup_UsesTokensForListAndSelectedItem(bool dark)
    {
        var combo = new ComboBox { ItemsSource = new[] { "甲", "乙" }, SelectedIndex = 1, Width = 240 };
        var window = Host(Variant(dark), combo);
        combo.IsDropDownOpen = true;
        window.UpdateLayout();

        // 下拉清單在 Popup 裡，不在 ComboBox 本身的視覺樹
        var popupBorder = Assert.IsType<Border>(Part<Popup>(combo, "PART_Popup").Child);
        Assert.Equal("PopupBorder", popupBorder.Name);
        Assert.Equal(Token(window, "LumiSurfaceBrush"), ColorOf(popupBorder.Background));
        Assert.Equal(Token(window, "LumiBorderBrush"), ColorOf(popupBorder.BorderBrush));

        var items = popupBorder.GetVisualDescendants().OfType<ComboBoxItem>().ToList();
        Assert.Equal(2, items.Count);
        var selectedPresenter = Part<ContentPresenter>(items[1], "PART_ContentPresenter");
        Assert.Equal(Token(window, "LumiAccentSoftBrush"), ColorOf(selectedPresenter.Background));
        Assert.Equal(Token(window, "LumiTextOnSoftBrush"), ColorOf(selectedPresenter.Foreground));
    }
}
