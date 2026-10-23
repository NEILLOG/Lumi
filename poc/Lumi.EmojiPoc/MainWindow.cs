using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Lumi.App.Controls;

namespace Lumi.EmojiPoc;

/// <summary>
/// 9E 驗證頁：每組驗證字串 × 三種情境（一般內文、粗體 Run、等寬字型）並排。
/// 目標是在 Windows 上一眼看出國旗是圖案還是兩個字母。判定流程見 README.md。
/// </summary>
public sealed class MainWindow : Window
{
    public MainWindow()
    {
        Title = "Lumi emoji 驗證頁（9E）";
        Width = 980;
        Height = 760;

        var monoFamily = new FontFamily(OperatingSystem.IsWindows() ? "Consolas" : "Menlo");

        var root = new StackPanel { Spacing = 16, Margin = new Thickness(16) };
        root.Children.Add(new TextBlock
        {
            Text = $"作業系統：{RuntimeInformation.OSDescription}\n" +
                   $"目前 emoji 字型：{EmojiText.EmojiFont.Name}\n" +
                   "國旗（🇹🇼🇯🇵🇺🇸🇬🇧）若顯示為兩個字母（例如 TW），記錄於 README.md 並進入「評估 Twemoji COLR」。",
            TextWrapping = TextWrapping.Wrap,
        });

        foreach (var set in EmojiSamples.Sets)
            root.Children.Add(BuildSet(set, monoFamily));

        Content = new ScrollViewer { Content = root };
    }

    private static Control BuildSet(EmojiSamples.Set set, FontFamily monoFamily)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = set.Name,
            FontWeight = FontWeight.Bold,
            FontSize = 16,
        });

        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("*,*,*"),
            ColumnSpacing = 12,
        };

        // 一般內文：EmojiTextBlock（與工作台訊息列表相同的路徑）
        var plain = new EmojiTextBlock { Text = set.Text, FontSize = 20, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(plain, 0);
        grid.Children.Add(plain);

        // 粗體：Markdown **粗體** 的 Run，經 EmojiText.AddText 並套用樣式
        var bold = new TextBlock { FontSize = 20, TextWrapping = TextWrapping.Wrap };
        bold.Inlines = new InlineCollection();
        EmojiText.AddText(bold.Inlines, set.Text, run => run.FontWeight = FontWeight.Bold);
        Grid.SetColumn(bold, 1);
        grid.Children.Add(bold);

        // 等寬：程式碼區塊風格
        var mono = new TextBlock { FontSize = 20, FontFamily = monoFamily, TextWrapping = TextWrapping.Wrap };
        mono.Inlines = new InlineCollection();
        EmojiText.AddText(mono.Inlines, set.Text);
        Grid.SetColumn(mono, 2);
        grid.Children.Add(mono);

        panel.Children.Add(grid);
        panel.Children.Add(new Border { Height = 1, Background = Brushes.LightGray });
        return panel;
    }
}
