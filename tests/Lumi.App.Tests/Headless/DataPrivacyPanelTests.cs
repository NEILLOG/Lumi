using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Headless;

/// <summary>M20 20C：設定頁「資料與隱私」的畫面——位置列數與 ViewModel 相同、送出內容的區段、六個「怎麼控制」區段。</summary>
public class DataPrivacyPanelTests
{
    private static Window Host(DataPrivacyViewModel vm, double width = 1040)
    {
        var window = new Window { Width = width, Height = 900, Content = new DataPrivacyPanel { DataContext = vm } };
        // 設定視窗提供 muted 與 section-title 樣式；獨立視窗用同一份共用樣式
        window.Styles.Add(new StyleInclude(new Uri("avares://Lumi.App/")) { Source = new Uri("avares://Lumi.App/Themes/ChatStyles.axaml") });
        window.Classes.Add("wide");
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static DataPrivacyViewModel NewViewModel()
    {
        var root = Path.Combine(Path.GetTempPath(), "lumi-privacy-panel-" + Guid.NewGuid().ToString("N"));
        return new DataPrivacyViewModel(
            new FakeDataLocationProvider(
                new DataLocation("data", "資料", "desc", root, IsFile: false),
                new DataLocation("database", "資料庫", "desc", Path.Combine(root, "lumi.db"), IsFile: true),
                new DataLocation("secrets", "金鑰", "desc", null, IsFile: false)),
            new FakeFolderOpener(),
            new FakeClipboardService());
    }

    [AvaloniaFact]
    public void LocationList_HasOneRowPerLocation()
    {
        var vm = NewViewModel();
        var window = Host(vm);

        var list = window.FindByName<ItemsControl>("PrivacyLocationList");
        var rows = list.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("loc-row")).ToList();

        Assert.Equal(vm.Locations.Count, rows.Count);
        Assert.Equal(3, rows.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void SendSection_IsShown()
    {
        var window = Host(NewViewModel());

        var section = window.FindByName<StackPanel>("PrivacySendSection");
        Assert.True(section.IsEffectivelyVisible);
        Assert.Contains(section.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Strings.Privacy_SendKeys);
        window.Close();
    }

    [AvaloniaFact]
    public void ControlSection_HasSixEntries_FiveWithAButton()
    {
        var window = Host(NewViewModel());

        var section = window.FindByName<StackPanel>("PrivacyControlSection");
        Assert.Equal(6, section.Children.Count);

        var buttons = section.GetVisualDescendants().OfType<Button>().Where(b => b.Content as string == Strings.Privacy_GoSettings).ToList();
        Assert.Equal(5, buttons.Count);
        window.Close();
    }
}
