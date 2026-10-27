using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.App.Controls;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.App.Views.Responsive;
using Lumi.Core.Models;

namespace Lumi.App.Tests.Headless;

/// <summary>UD-2、M22 22D：「AI 連線」頁——連線與模型合成一份清單、清單卡片＋詳細、進階收合、按鈕層級、錯誤文字、費用說明、&lt; 900 上下排。</summary>
public class ConnectionsPageTests
{
    private sealed class Rig : IDisposable
    {
        public SettingsHost Host { get; } = new(new AvaloniaUiDispatcher());
        public SettingsWindow Window { get; }
        public string OllamaId { get; } = "ollama";

        public Rig(double width = 1040, double height = 760)
        {
            Window = new SettingsWindow(Host.Settings) { Width = width, Height = height };
            Window.Show();
            Window.UpdateLayout();
        }

        /// <summary>預設目錄已有一個 Anthropic 連線＋一個模型；再加一個 Ollama 連線與模型（預設仍是原本那個；舊的小精靈模型設定指向 Ollama 的，畫面不再顯示）。</summary>
        public async Task AddOllamaAsync()
        {
            await Host.Catalog.SaveConnectionAsync(new ConnectionSettings(OllamaId, "Ollama", ProviderFormat.OpenAICompatible, "http://localhost:11434/v1"), null);
            await Host.Catalog.SaveModelAsync(new ModelSettings("llama-model", OllamaId, "llama3", "Llama 3", 4096, 128_000, false));
            await Host.Catalog.SetSpriteModelAsync("llama-model");
            Settle();
        }

        /// <summary>執行 UI 執行緒上排隊的工作（目錄、狀態變動的清單更新）並重新排版。</summary>
        public void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }

        public AiConnectionsView Page => Window.FindByName<AiConnectionsView>("AiPage");
        public ListBox List => Window.FindByName<ListBox>("AiList");
        public ConnectionsPanel Connections => Window.FindByName<ConnectionsPanel>("ConnectionsPanel");
        public ModelsPanel Models => Window.FindByName<ModelsPanel>("ModelsPanel");

        public void Resize(double width, double height)
        {
            // Headless 的 Window.Width／Height 在 Show 之後不會觸發 Bounds 變更，要直接要求平台視窗改尺寸（等同使用者拖曳邊框）
            Window.Width = width;
            Window.Height = height;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Window.UpdateLayout();
        }

        public void Dispose()
        {
            Window.Close();
            Host.Dispose();
        }
    }

    private static Color TokenColor(Window window, string key)
    {
        Assert.True(window.TryFindResource(key, window.ActualThemeVariant, out var value), $"找不到 token {key}");
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }

    private static Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static IEnumerable<string> TextsIn(Visual root) => root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "");

    private static ListBoxItem CardOf(ListBox list, int index)
    {
        list.UpdateLayout();
        return list.GetVisualDescendants().OfType<ListBoxItem>().ElementAt(index);
    }

    /// <summary>收合的 Expander 內容可能根本不在視覺樹裡：找不到或看不見都算「沒顯示」。</summary>
    private static bool IsShown(Visual root, string name) =>
        root.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name)?.IsEffectivelyVisible == true;

    private static string RequirementOf(FormField field) =>
        field.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_Requirement").Text ?? "";

    private static TextBlock ErrorTextOf(FormField field) =>
        field.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_Error");

    private static IEnumerable<Border> CardsIn(ListBox list) =>
        list.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("card"));

    private static ListBoxItem RowOf(ListBox list, AiListItem item) =>
        list.GetVisualDescendants().OfType<ListBoxItem>().Single(i => ReferenceEquals(i.DataContext, item));

    private static AiConnectionItem ConnectionRow(Rig rig, string id) => rig.Host.Settings.AiSettings.Items.OfType<AiConnectionItem>().Single(c => c.Id == id);

    // ---- 頁首與預設模型 ----

    [AvaloniaFact]
    public void TheSegmentedSwitch_IsGone_AndTheHeaderHasOneNewConnectionPrimary()
    {
        using var rig = new Rig();
        Assert.Empty(rig.Window.GetVisualDescendants().OfType<TabControl>());
        Assert.DoesNotContain(rig.Window.FindByName<Grid>("ConnectionsPage").GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("segmented"));
        Assert.DoesNotContain(rig.Window.GetVisualDescendants().OfType<Control>(), c => c.Name is "TabSwitch" or "TabConnections" or "TabModels" or "NewModelButton");

        var header = rig.Window.FindByName<SettingsPageHeader>("ConnectionsHeader");
        var newConnection = rig.Window.FindByName<Button>("NewConnectionButton");
        Assert.Contains("primary", newConnection.Classes);
        Assert.True(newConnection.IsEffectivelyVisible);
        Assert.Contains("新增連線", TextsIn(newConnection));
        Assert.Same(rig.Host.Settings.AiSettings.NewConnectionCommand, newConnection.Command);
        Assert.Single(header.GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("primary") && b.IsEffectivelyVisible);
        // 頁首說明沿用現有的字串
        Assert.Equal(Strings.Settings_HeaderConnectionsTitle, header.FindByName<TextBlock>("TitleText").Text);
        // 主要動作在標題的右邊
        Assert.True(newConnection.TranslatePoint(default, header)!.Value.X > header.FindByName<TextBlock>("TitleText").TranslatePoint(default, header)!.Value.X);
    }

    [AvaloniaFact]
    public async Task HeaderPrimaryAction_StartsANewConnection_EvenFromTheModelForm()
    {
        using var rig = new Rig();
        await rig.Host.Models.SelectAsync(rig.Host.Models.Items[0]);
        rig.Settle();
        Assert.True(rig.Models.FindByName<Border>("DetailCard").IsEffectivelyVisible);

        rig.Window.FindByName<Button>("NewConnectionButton").Command!.Execute(null);
        await SettingsHost.WaitUntil(() => rig.Host.Connections.HasEditor);
        rig.Settle();

        Assert.True(rig.Host.Connections.IsNew);
        Assert.True(rig.Connections.FindByName<Border>("DetailCard").IsEffectivelyVisible);
        Assert.False(rig.Models.FindByName<Border>("DetailCard").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task TheDefaultModelBox_SitsBelowTheHeader_AboveTheList_WithTheCostNote()
    {
        using var rig = new Rig();
        await rig.AddOllamaAsync();
        var page = rig.Page;

        var field = page.FindByName<FormField>("WorkbenchDefaultField");
        Assert.Equal("預設模型", field.Label);
        Assert.Null(page.FindControl<FormField>("SpriteDefaultField"));   // 小精靈不另外選模型
        var box = page.FindByName<ComboBox>("WorkbenchDefaultBox");
        Assert.Same(rig.Host.Models.DefaultOptions, box.ItemsSource);
        Assert.Equal(Strings.Models_WorkbenchDefaultHint, field.Hint);
        var label = field.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_Label");
        Assert.True(label.TranslatePoint(default, page)!.Value.Y < box.TranslatePoint(default, page)!.Value.Y);   // 標籤在上方

        // 費用說明：預設模型下方的一行 Caption；整段在清單之前、頁首之後
        var cost = page.FindByName<TextBlock>("CostNote");
        Assert.Equal(Strings.Workbench_CostNote, cost.Text);
        Assert.Equal(11, cost.FontSize);
        Assert.True(cost.TranslatePoint(default, page)!.Value.Y > box.TranslatePoint(default, page)!.Value.Y);
        Assert.True(cost.TranslatePoint(default, page)!.Value.Y < page.FindByName<TextBlock>("ListCaption").TranslatePoint(default, page)!.Value.Y);
        var header = rig.Window.FindByName<SettingsPageHeader>("ConnectionsHeader");
        Assert.True(box.TranslatePoint(default, rig.Window)!.Value.Y > header.TranslatePoint(default, rig.Window)!.Value.Y + header.Bounds.Height - 0.5);

        // 改了立即生效
        var llama = rig.Host.Models.DefaultOptions.Single(o => o.Id == "llama-model");
        box.SelectedItem = llama;
        rig.Settle();
        await SettingsHost.WaitUntil(() => rig.Host.Catalog.Current.WorkbenchModelId == "llama-model");
        rig.Settle();
        // 清單上的「預設」標籤跟著移動
        var tagged = rig.Host.Settings.AiSettings.Items.OfType<AiModelItem>().Single(m => m.HasTag);
        Assert.Equal("llama-model", tagged.Id);
    }

    // ---- 清單：連線 → 模型 → 新增模型 ----

    [AvaloniaFact]
    public async Task List_ShowsConnectionsWithTheirModelsAndAnAddRow_InOrder()
    {
        using var rig = new Rig();
        await rig.AddOllamaAsync();
        rig.Host.Status.Set(rig.OllamaId, ConnectionTestResult.Ok(["llama3"]));
        var anthropicId = rig.Host.Catalog.Current.Connections.First(c => c.Format == ProviderFormat.Anthropic).Id;
        rig.Host.Status.Set(anthropicId, FakeConnectionTester.Failure(Lumi.Core.Chat.ChatFailureKind.Network, "連不上"));
        rig.Settle();

        Assert.Equal("連線與模型", rig.Page.FindByName<TextBlock>("ListCaption").Text);
        Assert.Contains("cards", rig.List.Classes);
        Assert.False(rig.Page.FindByName<TextBlock>("AiEmptyText").IsEffectivelyVisible);
        var items = rig.Host.Settings.AiSettings.Items;
        var rows = rig.List.GetVisualDescendants().OfType<ListBoxItem>().ToList();
        Assert.Equal(items.Count, rows.Count);
        Assert.Equal(items, rows.Select(r => (AiListItem)r.DataContext!));   // 畫面順序＝清單順序

        // 連線：字首方塊（不是商標）、名稱、狀態點加文字
        var ollama = CardsIn(rig.List).Single(c => c.DataContext is AiConnectionItem { Id: "ollama" });
        Assert.Equal(new Thickness(12), ollama.Padding);
        Assert.Equal("O", ollama.GetVisualDescendants().OfType<ProviderMark>().Single().Text);
        var title = ollama.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Ollama");
        Assert.Equal(14, title.FontSize);
        Assert.Equal(FontWeight.SemiBold, title.FontWeight);
        Assert.Contains("連線正常", TextsIn(ollama.GetVisualDescendants().OfType<StatusIndicator>().Single()));
        var anthropic = CardsIn(rig.List).Single(c => c.DataContext is AiConnectionItem { Id: var id } && id == anthropicId);
        Assert.Contains("連線失敗", TextsIn(anthropic));   // 狀態有文字，不只靠顏色

        // 模型：縮排在連線底下，只有名稱（預設的才有「預設」標籤）
        var llama = CardsIn(rig.List).Single(c => c.DataContext is AiModelItem { Id: "llama-model" });
        Assert.Equal(24, llama.Margin.Left);
        Assert.Contains("Llama 3", TextsIn(llama));
        Assert.DoesNotContain("預設", TextsIn(llama));
        var claude = CardsIn(rig.List).Single(c => c.DataContext is AiModelItem { HasTag: true });
        Assert.Contains("預設", TextsIn(claude));
        Assert.Contains(claude.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("badge"));

        // 連線狀態更新只換文字，不重建清單、不清掉表單
        await rig.Host.Models.SelectAsync(rig.Host.Models.Items.Single(i => i.Id == "llama-model"));
        rig.Host.Status.Remove(rig.OllamaId);
        rig.Settle();
        Assert.Contains("尚未確認", TextsIn(CardsIn(rig.List).Single(c => c.DataContext is AiConnectionItem { Id: "ollama" })));
        Assert.True(rig.Host.Models.HasEditor);
        Assert.Equal("llama-model", rig.Host.Models.SelectedItem?.Id);
        Assert.Equal("llama-model", Assert.IsType<AiModelItem>(rig.Host.Settings.AiSettings.SelectedItem).Id);
    }

    [AvaloniaFact]
    public async Task EachConnection_EndsWithADashedAddModelRow()
    {
        using var rig = new Rig();
        await rig.AddOllamaAsync();
        var addRows = rig.List.GetVisualDescendants().OfType<ListBoxItem>().Where(r => r.DataContext is AiAddModelItem).ToList();
        Assert.Equal(rig.Host.Connections.Items.Count, addRows.Count);

        foreach (var row in addRows)
        {
            Assert.Contains("新增模型", TextsIn(row));
            var dash = row.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Rectangle>().Single();
            Assert.NotNull(dash.StrokeDashArray);   // 虛線框
            Assert.NotEmpty(dash.StrokeDashArray!);
            Assert.DoesNotContain(row.GetVisualDescendants().OfType<Button>(), _ => true);   // 它是清單的一列，不是另一個 Tab 停靠點
        }
        // 每個連線的最後一列
        var items = rig.Host.Settings.AiSettings.Items;
        for (var i = 0; i < items.Count; i++)
            if (items[i] is AiAddModelItem)
                Assert.True(items[i + 1] is AiConnectionItem or AiImageGenerationItem);   // 最後一個連線的下一列是固定的「圖片生成」
    }

    [AvaloniaFact]
    public async Task SelectedRows_UseSoftBackgroundAndAccentBorder_ForConnectionsAndModels()
    {
        using var rig = new Rig();
        await rig.AddOllamaAsync();
        var soft = TokenColor(rig.Window, "LumiAccentSoftBrush");
        var accentBorder = TokenColor(rig.Window, "LumiAccentBorderBrush");

        await rig.Host.Settings.AiSettings.SelectAsync(ConnectionRow(rig, rig.OllamaId));
        rig.Settle();
        var selected = CardsIn(rig.List).Single(b => b.DataContext is AiConnectionItem { Id: "ollama" });
        var other = CardsIn(rig.List).First(b => b.DataContext is AiConnectionItem { Id: not "ollama" });
        Assert.Equal(soft, ColorOf(selected.Background));
        Assert.Equal(accentBorder, ColorOf(selected.BorderBrush));
        Assert.NotEqual(soft, ColorOf(other.Background));
        Assert.Equal(TokenColor(rig.Window, "LumiTextOnSoftBrush"), ColorOf(selected.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Ollama").Foreground));

        await rig.Host.Settings.AiSettings.SelectAsync(rig.Host.Settings.AiSettings.Items.OfType<AiModelItem>().Single(m => m.Id == "llama-model"));
        rig.Settle();
        var model = CardsIn(rig.List).Single(b => b.DataContext is AiModelItem { Id: "llama-model" });
        Assert.Equal(soft, ColorOf(model.Background));
        Assert.Equal(accentBorder, ColorOf(model.BorderBrush));
        Assert.NotEqual(soft, ColorOf(CardsIn(rig.List).Single(b => b.DataContext is AiConnectionItem { Id: "ollama" }).Background));   // 連線那列不再是選取
    }

    [AvaloniaFact]
    public async Task ClickingRows_SwapsTheDetail_BetweenConnectionForm_ModelForm_AndPlaceholder()
    {
        using var rig = new Rig();
        await rig.AddOllamaAsync();
        var placeholder = rig.Window.FindByName<TextBlock>("DetailPlaceholder");
        var connectionCard = rig.Connections.FindByName<Border>("DetailCard");
        var modelCard = rig.Models.FindByName<Border>("DetailCard");
        Assert.True(placeholder.IsEffectivelyVisible);
        Assert.Equal(Strings.Conn_Select, placeholder.Text);
        Assert.False(connectionCard.IsEffectivelyVisible);
        Assert.False(modelCard.IsEffectivelyVisible);

        rig.List.SelectedItem = ConnectionRow(rig, rig.OllamaId);   // 使用者點連線
        rig.Settle();
        Assert.False(placeholder.IsEffectivelyVisible);
        Assert.True(connectionCard.IsEffectivelyVisible);
        Assert.False(modelCard.IsEffectivelyVisible);
        Assert.Equal("Ollama", rig.Connections.FindByName<TextBlock>("HeaderTitleText").Text);

        rig.List.SelectedItem = rig.Host.Settings.AiSettings.Items.OfType<AiModelItem>().Single(m => m.Id == "llama-model");   // 再點模型
        rig.Settle();
        Assert.False(connectionCard.IsEffectivelyVisible);
        Assert.True(modelCard.IsEffectivelyVisible);
        Assert.Equal("Llama 3", rig.Models.FindByName<TextBlock>("HeaderTitleText").Text);
        Assert.Equal("Ollama", rig.Models.FindByName<TextBlock>("HeaderProviderText").Text);
    }

    [AvaloniaFact]
    public async Task ClickingAnAddModelRow_OpensANewModelOnThatConnection()
    {
        using var rig = new Rig();
        await rig.AddOllamaAsync();
        var add = rig.Host.Settings.AiSettings.Items.OfType<AiAddModelItem>().Single(a => a.ConnectionId == rig.OllamaId);

        rig.List.SelectedItem = add;
        rig.Settle();

        Assert.True(rig.Host.Models.IsNew);
        Assert.True(rig.Models.FindByName<Border>("DetailCard").IsEffectivelyVisible);
        Assert.Equal(rig.OllamaId, Assert.IsType<ConnectionOption>(rig.Models.FindByName<ComboBox>("ConnectionBox").SelectedItem).Id);   // 預先選好這個連線
        Assert.Equal(add, rig.List.SelectedItem);
    }

    [AvaloniaFact]
    public async Task SwitchingRows_WithUnsavedChanges_AsksFirst_AndDecliningRestoresTheSelection()
    {
        using var rig = new Rig();
        await rig.AddOllamaAsync();
        var ollama = ConnectionRow(rig, rig.OllamaId);
        rig.List.SelectedItem = ollama;
        rig.Settle();
        rig.Host.Connections.Name = "改到一半";

        rig.Host.Confirm.Answer = false;
        rig.List.SelectedItem = rig.Host.Settings.AiSettings.Items.OfType<AiModelItem>().Single(m => m.Id == "llama-model");
        rig.Settle();
        Assert.Single(rig.Host.Confirm.Asked);
        Assert.Equal(ollama, rig.List.SelectedItem);   // 清單回到原本的選取
        Assert.True(rig.Host.Connections.HasEditor);
        Assert.Equal("改到一半", rig.Host.Connections.Name);

        rig.Host.Confirm.Answer = true;
        rig.List.SelectedItem = rig.Host.Settings.AiSettings.Items.OfType<AiModelItem>().Single(m => m.Id == "llama-model");
        rig.Settle();
        Assert.Equal(2, rig.Host.Confirm.Asked.Count);
        Assert.True(rig.Host.Models.HasEditor);
        Assert.False(rig.Host.Connections.HasEditor);
    }

    [AvaloniaFact]
    public async Task SavingANewConnection_SelectsItsRow_AndShowsTheAddModelHint()
    {
        using var rig = new Rig();
        rig.Window.FindByName<Button>("NewConnectionButton").Command!.Execute(null);
        await SettingsHost.WaitUntil(() => rig.Host.Connections.HasEditor);
        rig.Host.Connections.Name = "公司 AI Gateway";
        rig.Host.Connections.BaseUrl = "https://gateway.example.com/v1";
        await rig.Host.Connections.SaveCommand.ExecuteAsync(null);
        rig.Settle();

        var selected = Assert.IsType<AiConnectionItem>(rig.List.SelectedItem);
        Assert.Equal("公司 AI Gateway", selected.Connection.Name);
        Assert.Contains(Strings.AiSettings_ConnectionSavedHint, TextsIn(rig.Connections));   // 既有的說明列樣式
        var notice = rig.Connections.GetVisualDescendants().OfType<Border>().First(b => b.IsEffectivelyVisible && b.Child is TextBlock { Text: var t } && t == Strings.AiSettings_ConnectionSavedHint);
        Assert.Equal(TokenColor(rig.Window, "LumiAccentSoftBrush"), ColorOf(notice.Background));
    }

    [AvaloniaFact]
    public async Task WithoutAnyConnection_TheListShowsTheEmptyExplanation_AndTheHeaderStillOffersNewConnection()
    {
        using var rig = new Rig();
        foreach (var connection in rig.Host.Catalog.Current.Connections.ToList())
            await rig.Host.Catalog.DeleteConnectionAsync(connection.Id);
        rig.Settle();

        var empty = rig.Page.FindByName<TextBlock>("AiEmptyText");
        Assert.True(empty.IsEffectivelyVisible);
        Assert.Equal(Strings.AiSettings_Empty, empty.Text);
        Assert.False(rig.List.IsEffectivelyVisible);
        Assert.True(rig.Window.FindByName<Button>("NewConnectionButton").IsEffectivelyEnabled);
    }

    // ---- 清單卡片 ----

    [AvaloniaFact]
    public async Task StatusIndicator_UsesTheFixedSemanticDots_AndAlwaysHasText()
    {
        using var rig = new Rig();
        var host = new StackPanel();
        var unknown = new StatusIndicator { Status = ConnectionStatus.Unknown };
        var ok = new StatusIndicator { Status = ConnectionStatus.Ok };
        var failed = new StatusIndicator { Status = ConnectionStatus.Failed, IsBadge = true };
        host.Children.Add(unknown);
        host.Children.Add(ok);
        host.Children.Add(failed);
        var window = new Window { Width = 300, Height = 200, Content = host };
        window.Show();
        window.UpdateLayout();

        Color Dot(StatusIndicator s) => ColorOf(s.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single().Fill);
        Assert.Equal(TokenColor(window, "LumiWarningDotBrush"), Dot(unknown));
        Assert.Equal(TokenColor(window, "LumiSuccessDotBrush"), Dot(ok));
        Assert.Equal(TokenColor(window, "LumiDangerDotBrush"), Dot(failed));
        Assert.Equal(["尚未確認"], TextsIn(unknown));
        Assert.Equal(["連線正常"], TextsIn(ok));
        Assert.Equal(["連線失敗"], TextsIn(failed));

        // 徽章模式：Border.badge（Soft 底＋文字）；預設是清單用的純點加文字
        Assert.DoesNotContain(unknown.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("badge"));
        var badge = failed.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("badge"));
        Assert.Equal(TokenColor(window, "LumiAccentSoftBrush"), ColorOf(badge.Background));
        window.Close();
        await Task.CompletedTask;
    }

    // ---- 清單＋詳細：寬度與整頁捲動 ----

    [AvaloniaFact]
    public void Wide_PutsTheListAt260_AndTheDetailInTheRest()
    {
        using var rig = new Rig(1040, 760);
        Assert.DoesNotContain("stacked", rig.Window.Classes);
        var layout = rig.Page.FindByName<Grid>("Layout");
        Assert.Equal(260, layout.ColumnDefinitions[0].Width.Value);
        var master = rig.Page.FindByName<StackPanel>("MasterHost");
        var detail = rig.Page.FindByName<Panel>("DetailHost");
        Assert.Equal(0, Grid.GetColumn(master));
        Assert.Equal(1, Grid.GetColumn(detail));
        Assert.True(detail.Bounds.Left > master.Bounds.Right - 0.5);
        Assert.Equal(master.Bounds.Top, detail.Bounds.Top, 0.5);
        Assert.True(detail.Bounds.Width > master.Bounds.Width);
        Assert.Equal(layout.Bounds.Width, master.Bounds.Width + 16 + detail.Bounds.Width, 1.0);   // 詳細吃剩餘寬度
    }

    [AvaloniaFact]
    public async Task Below900_StacksTheListAboveTheDetail()
    {
        using var rig = new Rig(1040, 760);
        await rig.Host.Connections.SelectAsync(rig.Host.Connections.Items[0]);
        rig.Resize(800, 760);
        Assert.Contains("stacked", rig.Window.Classes);

        var master = rig.Page.FindByName<StackPanel>("MasterHost");
        var detail = rig.Page.FindByName<Panel>("DetailHost");
        Assert.Equal(0, Grid.GetRow(master));
        Assert.Equal(1, Grid.GetRow(detail));
        Assert.Equal(2, Grid.GetColumnSpan(master));
        Assert.Equal(2, Grid.GetColumnSpan(detail));
        Assert.True(detail.TranslatePoint(default, rig.Window)!.Value.Y >= master.TranslatePoint(default, rig.Window)!.Value.Y + master.Bounds.Height - 0.5);
        Assert.Equal(master.Bounds.Left, detail.Bounds.Left, 0.5);

        rig.Resize(1040, 760);
        Assert.DoesNotContain("stacked", rig.Window.Classes);
        Assert.Equal(1, Grid.GetColumn(rig.Page.FindByName<Panel>("DetailHost")));
    }

    [AvaloniaTheory]
    [InlineData(1040.0)]
    [InlineData(800.0)]
    public async Task TheDetailHasNoInnerScroller_AndTheWholePageScrollsWithTheButtonsFollowingTheForm(double width)
    {
        using var rig = new Rig(width, 480);   // 矮視窗：表單一定比視窗高
        await rig.Host.Connections.SelectAsync(rig.Host.Connections.Items[0]);
        rig.Settle();
        var scroll = rig.Page.FindByName<ScrollViewer>("PageScroll");
        var card = rig.Connections.FindByName<Border>("DetailCard");

        // 詳細卡片裡沒有自己的捲動區（文字框範本內的不算）
        Assert.DoesNotContain(card.GetVisualDescendants().OfType<ScrollViewer>(), v => v.FindAncestorOfType<TextBox>() is null);
        Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
        // 卡片高度＝內容高度（Padding 24＋邊框 1，上下各一）
        var inner = Assert.IsAssignableFrom<Control>(card.Child);
        Assert.Equal(inner.Bounds.Height + 2 * 24 + 2, card.Bounds.Height, 1.0);

        // 整頁比視窗高，所以整頁一起捲；底部按鈕列跟著表單走（不固定在視窗底）
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height + 1);
        var save = rig.Connections.FindByName<Button>("SaveButton");
        double BottomInViewport() => save.TranslatePoint(new Point(0, save.Bounds.Height), scroll)!.Value.Y;
        Assert.True(BottomInViewport() > scroll.Viewport.Height);   // 還沒捲：儲存鈕在可視範圍下面
        scroll.ScrollToEnd();
        rig.Settle();
        Assert.True(BottomInViewport() <= scroll.Viewport.Height + 1);   // 捲到底看得到
        Assert.True(scroll.Offset.Y > 0);
    }

    [AvaloniaFact]
    public async Task TheModelFormIsAlsoFullHeight_WithoutAnInnerScroller()
    {
        using var rig = new Rig(1040, 480);
        await rig.Host.Models.SelectAsync(rig.Host.Models.Items[0]);
        rig.Models.FindByName<Expander>("AdvancedExpander").IsExpanded = true;
        rig.Settle();
        var card = rig.Models.FindByName<Border>("DetailCard");

        Assert.DoesNotContain(card.GetVisualDescendants().OfType<ScrollViewer>(), v => v.FindAncestorOfType<TextBox>() is null && v.FindAncestorOfType<NumericUpDown>() is null);
        var inner = Assert.IsAssignableFrom<Control>(card.Child);
        Assert.Equal(inner.Bounds.Height + 2 * 24 + 2, card.Bounds.Height, 1.0);
        Assert.True(rig.Page.FindByName<ScrollViewer>("PageScroll").Extent.Height > rig.Page.FindByName<ScrollViewer>("PageScroll").Viewport.Height);
    }

    [AvaloniaFact]
    public async Task AListTallerThanTheWindow_ScrollsWithThePage_NotOnItsOwn()
    {
        using var rig = new Rig(1040, 480);
        await rig.AddOllamaAsync();
        for (var i = 0; i < 6; i++)
            await rig.Host.Catalog.SaveModelAsync(new ModelSettings($"extra-{i}", rig.OllamaId, $"m{i}", $"額外 {i}", 4096, 128_000, false));
        rig.Settle();

        var scroll = rig.Page.FindByName<ScrollViewer>("PageScroll");
        var innerList = rig.List.GetVisualDescendants().OfType<ScrollViewer>().First();
        Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, innerList.VerticalScrollBarVisibility);
        Assert.True(rig.List.Bounds.Height > scroll.Viewport.Height);   // 清單比視窗高
        Assert.True(scroll.Extent.Height >= rig.List.Bounds.Height);    // 由整頁的捲動區容納
    }

    [AvaloniaFact]
    public void StackedClass_HasHysteresis_AndNeverAppearsOnTheWorkbench()
    {
        using var rig = new Rig(1040, 760);
        rig.Resize(899, 760);
        Assert.Contains("stacked", rig.Window.Classes);
        rig.Resize(905, 760);   // 已經是上下排：要到 908 才回到左右排
        Assert.Contains("stacked", rig.Window.Classes);
        rig.Resize(908, 760);
        Assert.DoesNotContain("stacked", rig.Window.Classes);
        rig.Resize(895, 760);   // 左右排：低於 900 才切
        Assert.Contains("stacked", rig.Window.Classes);
        Assert.Single(rig.Window.Classes, c => c is "wide" or "compact" or "narrow");   // 寬度三段照常

        // 只對設定視窗啟用：預設的 Track 不會加 stacked
        var plain = new Window { Width = 1040, Height = 700 };
        ResponsiveClasses.Track(plain);
        plain.Show();
        plain.Width = 800;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        plain.UpdateLayout();
        Assert.DoesNotContain("stacked", plain.Classes);
        plain.Close();
    }

    // ---- 詳細區：標頭、欄位、進階 ----

    [AvaloniaFact]
    public async Task ConnectionDetail_IsOneContainerCard_WithHeaderBadgeAndNoCardsAroundTheFields()
    {
        using var rig = new Rig();
        await rig.AddOllamaAsync();
        rig.Host.Status.Set(rig.OllamaId, ConnectionTestResult.Ok(["llama3"]));
        await rig.Host.Connections.SelectAsync(rig.Host.Connections.Items.Single(i => i.Id == rig.OllamaId));
        rig.Settle();

        var panel = rig.Connections;
        var card = panel.FindByName<Border>("DetailCard");
        Assert.Contains("card", card.Classes);
        Assert.True(card.IsEffectivelyVisible);
        Assert.False(rig.Window.FindByName<TextBlock>("DetailPlaceholder").IsEffectivelyVisible);
        // 容器卡片裡不再有卡片（欄位與表單不包卡片）
        Assert.DoesNotContain(card.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("card"));

        Assert.Equal("Ollama", panel.FindByName<TextBlock>("HeaderTitleText").Text);
        Assert.Equal("OpenAI 相容", panel.FindByName<TextBlock>("HeaderProviderText").Text);
        Assert.Equal(20, panel.FindByName<TextBlock>("HeaderTitleText").FontSize);
        Assert.Equal(11, panel.FindByName<TextBlock>("HeaderProviderText").FontSize);
        var badge = panel.FindByName<StatusIndicator>("StatusBadge");
        Assert.True(badge.IsBadge);
        Assert.Equal(["連線正常"], TextsIn(badge));
        Assert.Contains(badge.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("badge"));

        // 標頭徽章跟著測試連線的結果
        rig.Host.Status.Set(rig.OllamaId, FakeConnectionTester.Failure(Lumi.Core.Chat.ChatFailureKind.Network, "連不上"));
        rig.Settle();
        Assert.Equal(["連線失敗"], TextsIn(badge));
    }

    [AvaloniaFact]
    public async Task ConnectionForm_HasLabelsAboveTheInputs_RequirementTags_AndCaptions()
    {
        using var rig = new Rig();
        await rig.Host.Connections.SelectAsync(rig.Host.Connections.Items[0]);
        rig.Settle();
        var panel = rig.Connections;

        var name = panel.FindByName<FormField>("NameField");
        Assert.Equal("名稱", name.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_Label").Text);
        Assert.Equal("必填", RequirementOf(name));
        var nameBox = panel.FindByName<TextBox>("NameBox");
        Assert.True(name.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_Label").TranslatePoint(default, panel)!.Value.Y
                    < nameBox.TranslatePoint(default, panel)!.Value.Y);   // 標籤在輸入框上方
        var hint = name.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_Hint");
        Assert.Equal(Strings.Conn_NameHint, hint.Text);
        Assert.Equal(11, hint.FontSize);   // 說明是 Caption，在輸入框下方
        Assert.True(hint.TranslatePoint(default, panel)!.Value.Y > nameBox.TranslatePoint(default, panel)!.Value.Y);

        // 金鑰：KeyBox 保留（缺金鑰流程用它取得焦點）
        Assert.NotNull(panel.FindByName<TextBox>("KeyBox"));
        Assert.Contains("API 金鑰", TextsIn(panel));
    }

    [AvaloniaFact]
    public async Task AdvancedSection_IsCollapsedByDefault_HoldingFormatAndBaseUrl()
    {
        using var rig = new Rig();
        await rig.Host.Connections.SelectAsync(rig.Host.Connections.Items[0]);
        rig.Settle();
        var panel = rig.Connections;

        var expander = panel.FindByName<Expander>("AdvancedExpander");
        Assert.False(expander.IsExpanded);
        Assert.Equal("進階", expander.Header);
        Assert.False(IsShown(panel, "FormatField"));   // 技術欄位預設收起
        Assert.False(IsShown(panel, "BaseUrlField"));
        Assert.True(panel.FindByName<FormField>("NameField").IsEffectivelyVisible);

        expander.IsExpanded = true;
        rig.Settle();
        Assert.True(panel.FindByName<FormField>("FormatField").IsEffectivelyVisible);
        Assert.True(panel.FindByName<FormField>("BaseUrlField").IsEffectivelyVisible);
        Assert.Equal(Strings.Conn_Format, panel.FindByName<FormField>("FormatField").Label);
        Assert.Equal(Strings.Conn_BaseUrl, panel.FindByName<FormField>("BaseUrlField").Label);
    }

    [AvaloniaFact]
    public async Task AdvancedSection_OpensItself_WhenItHoldsAnError()
    {
        using var rig = new Rig();
        await rig.Host.Connections.SelectAsync(rig.Host.Connections.Items[0]);
        rig.Settle();
        var expander = rig.Connections.FindByName<Expander>("AdvancedExpander");
        Assert.False(expander.IsExpanded);

        rig.Host.Connections.BaseUrl = "不是網址";
        rig.Settle();
        Assert.True(expander.IsExpanded);   // 錯誤不能藏在收合的區段裡
        var field = rig.Connections.FindByName<FormField>("BaseUrlField");
        Assert.True(ErrorTextOf(field).IsEffectivelyVisible);
        Assert.Equal(Strings.Conn_UrlInvalid, ErrorTextOf(field).Text);
        Assert.Contains("error", rig.Connections.FindByName<TextBox>("BaseUrlBox").Classes);
    }

    [AvaloniaFact]
    public async Task ConnectionErrors_ShowAsTextAndADangerBorder_AndExplainWhySaveIsDisabled()
    {
        using var rig = new Rig();
        await rig.Host.Connections.SelectAsync(rig.Host.Connections.Items[0]);
        rig.Settle();
        var panel = rig.Connections;
        var nameField = panel.FindByName<FormField>("NameField");
        var nameBox = panel.FindByName<TextBox>("NameBox");
        Assert.False(ErrorTextOf(nameField).IsEffectivelyVisible);
        Assert.DoesNotContain("error", nameBox.Classes);
        Assert.False(panel.FindByName<TextBlock>("FieldErrorsText").IsEffectivelyVisible);
        Assert.True(panel.FindByName<Button>("SaveButton").IsEffectivelyEnabled);

        rig.Host.Connections.Name = "  ";
        rig.Settle();
        Assert.True(ErrorTextOf(nameField).IsEffectivelyVisible);
        Assert.Equal(Strings.Conn_NameRequired, ErrorTextOf(nameField).Text);   // 一定有文字
        Assert.Equal(TokenColor(rig.Window, "LumiDangerBrush"), ColorOf(ErrorTextOf(nameField).Foreground));
        Assert.Contains("error", nameBox.Classes);
        var border = nameBox.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_BorderElement");
        Assert.Equal(TokenColor(rig.Window, "LumiDangerBrush"), ColorOf(border.BorderBrush));

        // 儲存停用，原因用文字寫在按鈕列上方
        Assert.False(panel.FindByName<Button>("SaveButton").IsEffectivelyEnabled);
        var why = panel.FindByName<TextBlock>("FieldErrorsText");
        Assert.True(why.IsEffectivelyVisible);
        Assert.Equal(Strings.Form_FixErrors, why.Text);

        rig.Host.Connections.Name = "好了";
        rig.Settle();
        Assert.False(ErrorTextOf(nameField).IsEffectivelyVisible);
        Assert.True(panel.FindByName<Button>("SaveButton").IsEffectivelyEnabled);
        Assert.False(why.IsEffectivelyVisible);
    }

    // ---- 按鈕層級 ----

    [AvaloniaFact]
    public async Task ConnectionActions_HaveOneButtonPerLevel_AndOnlyOnePrimary()
    {
        using var rig = new Rig();
        await rig.Host.Connections.SelectAsync(rig.Host.Connections.Items[0]);
        rig.Settle();
        var panel = rig.Connections;
        var delete = panel.FindByName<Button>("DeleteButton");
        var test = panel.FindByName<Button>("TestButton");
        var save = panel.FindByName<Button>("SaveButton");

        Assert.Contains("danger", delete.Classes);
        Assert.Contains("secondary", test.Classes);
        Assert.Contains("primary", save.Classes);
        Assert.Equal("刪除", delete.Content);
        Assert.Equal("測試連線", test.Content);
        Assert.Equal("儲存", save.Content);
        Assert.Same(rig.Host.Connections.DeleteCommand, delete.Command);
        Assert.Same(rig.Host.Connections.TestCommand, test.Command);
        Assert.Same(rig.Host.Connections.SaveCommand, save.Command);

        // 刪除在左，測試連線與儲存在右；上方有分隔線
        var card = panel.FindByName<Border>("DetailCard");
        Assert.True(delete.TranslatePoint(default, card)!.Value.X < test.TranslatePoint(default, card)!.Value.X);
        Assert.True(test.TranslatePoint(default, card)!.Value.X < save.TranslatePoint(default, card)!.Value.X);
        var divider = card.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("divider"));
        Assert.True(divider.TranslatePoint(default, card)!.Value.Y < save.TranslatePoint(default, card)!.Value.Y);

        // 詳細區（動作區）只有一個 Primary；頁首另一個動作區也只有一個
        var primaries = card.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("primary") && b.IsEffectivelyVisible).ToList();
        Assert.Same(save, Assert.Single(primaries));
    }

    [AvaloniaFact]
    public async Task ModelActions_HaveDangerDeleteAndPrimarySave_AndNoTestButton()
    {
        using var rig = new Rig();
        await rig.Host.Models.SelectAsync(rig.Host.Models.Items[0]);
        rig.Settle();
        var panel = rig.Models;
        var delete = panel.FindByName<Button>("DeleteButton");
        var save = panel.FindByName<Button>("SaveButton");
        Assert.Contains("danger", delete.Classes);
        Assert.Contains("primary", save.Classes);
        Assert.Same(rig.Host.Models.DeleteCommand, delete.Command);
        Assert.Same(rig.Host.Models.SaveCommand, save.Command);
        Assert.DoesNotContain(panel.GetVisualDescendants().OfType<Button>(), b => b.Name == "TestButton");

        var card = panel.FindByName<Border>("DetailCard");
        Assert.Same(save, Assert.Single(card.GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("primary") && b.IsEffectivelyVisible));
        Assert.DoesNotContain(card.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("card"));
    }

    [AvaloniaFact]
    public async Task SaveButton_IsEnabledWhenTheFormIsValid_AndFollowsTheForm()
    {
        using var rig = new Rig();
        await rig.Host.Connections.SelectAsync(rig.Host.Connections.Items[0]);
        rig.Settle();
        var save = rig.Connections.FindByName<Button>("SaveButton");
        Assert.True(save.IsEffectivelyEnabled);
        rig.Host.Connections.BaseUrl = "";
        Assert.False(save.IsEffectivelyEnabled);
        rig.Host.Connections.BaseUrl = "https://api.example.com";
        Assert.True(save.IsEffectivelyEnabled);
    }

    // ---- 測試連線 ----

    [AvaloniaFact]
    public async Task TestConnection_ShowsTheResultAsTextWithAnIcon_AndKeepsTheForm()
    {
        using var rig = new Rig();
        await rig.Host.Connections.SelectAsync(rig.Host.Connections.Items[0]);
        rig.Settle();
        var panel = rig.Connections;
        Assert.False(panel.FindByName<Border>("TestResultOk").IsEffectivelyVisible);
        Assert.False(panel.FindByName<Border>("TestResultFail").IsEffectivelyVisible);

        rig.Host.Tester.Result = FakeConnectionTester.Failure(Lumi.Core.Chat.ChatFailureKind.InvalidApiKey, "金鑰無效，請重新輸入。");
        await rig.Host.Connections.TestCommand.ExecuteAsync(null);
        rig.Settle();
        var fail = panel.FindByName<Border>("TestResultFail");
        Assert.True(fail.IsEffectivelyVisible);
        Assert.Contains("金鑰無效，請重新輸入。", TextsIn(fail));   // 失敗訊息一定有文字
        Assert.Contains("連線失敗", TextsIn(fail));
        Assert.False(panel.FindByName<Border>("TestResultOk").IsEffectivelyVisible);
        Assert.True(rig.Host.Connections.HasEditor);   // 測試連線不會讓表單消失
        Assert.Equal(["連線失敗"], TextsIn(panel.FindByName<StatusIndicator>("StatusBadge")));

        rig.Host.Tester.Result = ConnectionTestResult.Ok(["a", "b", "c"]);
        await rig.Host.Connections.TestCommand.ExecuteAsync(null);
        rig.Settle();
        Assert.True(panel.FindByName<Border>("TestResultOk").IsEffectivelyVisible);
        Assert.Contains("成功 · 可用模型 3 個", TextsIn(panel.FindByName<Border>("TestResultOk")));
        Assert.False(panel.FindByName<Border>("TestResultFail").IsEffectivelyVisible);
        Assert.Equal(["連線正常"], TextsIn(panel.FindByName<StatusIndicator>("StatusBadge")));
        Assert.True(rig.Host.Connections.HasEditor);
    }

    [AvaloniaFact]
    public async Task WhileTesting_TheTestButtonBecomesTextAndACancelButton()
    {
        using var rig = new Rig();
        await rig.Host.Connections.SelectAsync(rig.Host.Connections.Items[0]);
        rig.Host.Tester.Gate = new TaskCompletionSource();
        var running = rig.Host.Connections.TestCommand.ExecuteAsync(null);
        rig.Settle();
        var panel = rig.Connections;
        Assert.False(panel.FindByName<Button>("TestButton").IsEffectivelyVisible);
        Assert.True(panel.FindByName<TextBlock>("TestingText").IsEffectivelyVisible);
        Assert.True(panel.FindByName<Button>("CancelTestButton").IsEffectivelyVisible);

        rig.Host.Tester.Gate.SetResult();
        await running;
        rig.Settle();
        Assert.True(panel.FindByName<Button>("TestButton").IsEffectivelyVisible);
        Assert.False(panel.FindByName<TextBlock>("TestingText").IsEffectivelyVisible);
    }

    // ---- 金鑰（沿用）----

    [AvaloniaFact]
    public async Task ApiKey_StatusReplaceClear_AndTheKeyBoxStillWork()
    {
        using var rig = new Rig();
        await rig.Host.Catalog.SaveConnectionAsync(new ConnectionSettings("withkey", "有金鑰", ProviderFormat.OpenAICompatible, "https://x.example.com/v1"),
            new Lumi.Core.Secrets.SecretValue("sk-abcdef-1234"));
        rig.Settle();
        await rig.Host.Connections.SelectAsync(rig.Host.Connections.Items.Single(i => i.Id == "withkey"));
        rig.Settle();
        var panel = rig.Connections;

        Assert.Contains("已設定 · 結尾 1234", TextsIn(panel));
        Assert.False(panel.FindByName<TextBox>("KeyBox").IsEffectivelyVisible);   // 已存的金鑰不讀回來顯示
        var buttons = panel.FindByName<FormField>("ApiKeyField").GetVisualDescendants().OfType<Button>().ToList();
        Assert.Same(rig.Host.Connections.ReplaceKeyCommand, buttons.Single(b => (string?)b.Content == "更換").Command);
        Assert.Same(rig.Host.Connections.ClearKeyCommand, buttons.Single(b => (string?)b.Content == "清除").Command);

        rig.Host.Connections.ReplaceKeyCommand.Execute(null);
        rig.Settle();
        var keyBox = panel.FindByName<TextBox>("KeyBox");
        Assert.True(keyBox.IsEffectivelyVisible);
        Assert.Equal('•', keyBox.PasswordChar);
        var reveal = panel.FindByName<Avalonia.Controls.Primitives.ToggleButton>("RevealKeyButton");
        Assert.Equal("顯示", reveal.Content);
        reveal.IsChecked = true;   // 顯示
        Assert.True(rig.Host.Connections.RevealKey);
        Assert.True(keyBox.RevealPassword);
        Assert.Contains(buttons, b => (string?)b.Content == "取消更換");
    }

    [AvaloniaFact]
    public async Task FocusApiKeyRequest_SelectsTheConnection_AndFocusesTheKeyBox()
    {
        using var rig = new Rig();
        await rig.Host.Settings.NavigateAsync(new SettingsNavigation(SettingsSection.Connections,
            rig.Host.Catalog.Current.Connections[0].Id, FocusApiKey: true, Notice: "缺金鑰"));
        rig.Settle();
        Dispatcher.UIThread.RunJobs();
        var keyBox = rig.Connections.FindByName<TextBox>("KeyBox");
        Assert.True(keyBox.IsEffectivelyVisible);
        Assert.Contains("缺金鑰", TextsIn(rig.Connections));
        Assert.True(keyBox.IsFocused);
    }

    // ---- 模型表單 ----

    [AvaloniaFact]
    public async Task ModelForm_HasTheFieldsWithTheirNames_AndTechnicalFieldsInACollapsedAdvancedSection()
    {
        using var rig = new Rig();
        await rig.Host.Models.SelectAsync(rig.Host.Models.Items[0]);
        rig.Settle();
        var panel = rig.Models;

        Assert.Equal("Claude Opus 5.5", panel.FindByName<TextBlock>("HeaderTitleText").Text);
        Assert.Equal("Anthropic", panel.FindByName<TextBlock>("HeaderProviderText").Text);
        // 開啟模型時會順便取得可用模型清單，等於測過連線了
        Assert.Equal(["連線正常"], TextsIn(panel.FindByName<StatusIndicator>("StatusBadge")));

        Assert.Equal("使用的連線", panel.FindByName<FormField>("ConnectionField").Label);
        Assert.Equal("模型 ID", panel.FindByName<FormField>("ModelIdField").Label);
        Assert.Equal("顯示名稱", panel.FindByName<FormField>("DisplayNameField").Label);
        Assert.True(panel.FindByName<ComboBox>("ModelIdBox").IsEditable);
        Assert.Same(rig.Host.Models.RefreshModelListCommand, panel.FindByName<Button>("RefreshModelsButton").Command);
        Assert.True(panel.FindByName<CheckBox>("ShowThinkingCheckBox").IsEffectivelyVisible);
        Assert.True(panel.FindByName<CheckBox>("ToolsEnabledCheckBox").IsEffectivelyVisible);

        var expander = panel.FindByName<Expander>("AdvancedExpander");
        Assert.False(expander.IsExpanded);
        Assert.False(IsShown(panel, "MaxOutputField"));
        Assert.False(IsShown(panel, "ContextWindowField"));
        expander.IsExpanded = true;
        rig.Settle();
        Assert.True(panel.FindByName<FormField>("MaxOutputField").IsEffectivelyVisible);
        Assert.True(panel.FindByName<FormField>("ContextWindowField").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task ModelErrors_ShowAsText_AndTheAdvancedSectionOpensForTechnicalErrors()
    {
        using var rig = new Rig();
        await rig.Host.Models.SelectAsync(rig.Host.Models.Items[0]);
        rig.Settle();
        var panel = rig.Models;
        rig.Host.Models.DisplayName = "";
        rig.Settle();
        var displayField = panel.FindByName<FormField>("DisplayNameField");
        Assert.Equal(Strings.Models_NameRequired, ErrorTextOf(displayField).Text);
        Assert.True(ErrorTextOf(displayField).IsEffectivelyVisible);
        Assert.Contains("error", panel.FindByName<TextBox>("DisplayNameBox").Classes);
        Assert.False(panel.FindByName<Button>("SaveButton").IsEffectivelyEnabled);
        Assert.Equal(Strings.Form_FixErrors, panel.FindByName<TextBlock>("FieldErrorsText").Text);
        Assert.True(panel.FindByName<TextBlock>("FieldErrorsText").IsEffectivelyVisible);
        rig.Host.Models.ModelId = "";
        rig.Settle();
        Assert.Equal(Strings.Models_IdRequired, ErrorTextOf(panel.FindByName<FormField>("ModelIdField")).Text);
        Assert.Contains("error", panel.FindByName<ComboBox>("ModelIdBox").Classes);

        var expander = panel.FindByName<Expander>("AdvancedExpander");
        Assert.False(expander.IsExpanded);
        rig.Host.Models.MaxOutputTokens = 0;
        rig.Settle();
        Assert.True(expander.IsExpanded);
        Assert.Equal(Strings.Models_NumberInvalid, ErrorTextOf(panel.FindByName<FormField>("MaxOutputField")).Text);
        Assert.True(ErrorTextOf(panel.FindByName<FormField>("MaxOutputField")).IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task EditingAModel_KeepsTheEditorWhenAConnectionChangesElsewhere()
    {
        using var rig = new Rig();
        await rig.Host.Models.SelectAsync(rig.Host.Models.Items[0]);
        rig.Settle();
        var connectionId = rig.Host.Models.SelectedConnection!.Id;

        // 在連線分段改名並儲存：目錄變動會重建模型分段的連線下拉，編輯中的模型不能因此失去所選的連線
        await rig.Host.Catalog.SaveConnectionAsync(rig.Host.Catalog.Current.Connections.First(c => c.Id == connectionId) with { Name = "改名後" }, null);
        rig.Settle();
        Assert.True(rig.Host.Models.HasEditor);
        Assert.Equal(connectionId, rig.Host.Models.SelectedConnection?.Id);
        Assert.Equal("改名後", rig.Host.Models.SelectedConnection?.Name);
        Assert.True(rig.Host.Models.CanSave);
    }
    [AvaloniaFact]
    public async Task ImageGeneration_IsTheLastRow_AndItsPanelShowsWhenSelected()
    {
        using var rig = new Rig();
        var image = rig.Host.Settings.AiSettings.Items.OfType<AiImageGenerationItem>().Single();
        Assert.Same(image, rig.Host.Settings.AiSettings.Items[^1]);
        var row = RowOf(rig.List, image);
        Assert.NotNull(row.GetVisualDescendants().OfType<TextBlock>().SingleOrDefault(t => t.Text == Strings.ImageGen_NotConfigured));

        await rig.Host.Settings.AiSettings.SelectAsync(image);
        rig.Settle();

        var panel = rig.Window.FindByName<ImageGenerationPanel>("ImageGenerationPanel");
        Assert.True(panel.FindByName<Border>("ImageDetailCard").IsEffectivelyVisible);
        Assert.False(rig.Connections.FindByName<Border>("DetailCard").IsEffectivelyVisible);
        Assert.False(rig.Models.FindByName<Border>("DetailCard").IsEffectivelyVisible);
        Assert.False(rig.Window.FindByName<TextBlock>("DetailPlaceholder").IsEffectivelyVisible);
        Assert.NotNull(panel.FindByName<Button>("ImageSaveButton"));
        Assert.False(panel.FindByName<Button>("ImageCancelButton").IsEnabled);   // 沒有未儲存的變更：取消不能按
    }
}
