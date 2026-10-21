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

/// <summary>UD-2：「AI 連線」頁——分段切換、清單卡片＋詳細、進階收合、按鈕層級、錯誤文字、費用說明、&lt; 900 上下排。</summary>
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

        /// <summary>預設目錄已有一個 Anthropic 連線＋一個模型；再加一個 Ollama 連線與模型（工作台預設仍是原本那個，小精靈預設改成 Ollama 的）。</summary>
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

    // ---- 分段切換與頁首主要動作 ----

    [AvaloniaFact]
    public void TabControl_IsReplacedBySegmentedControl_WithTwoSegments()
    {
        using var rig = new Rig();
        Assert.Empty(rig.Window.GetVisualDescendants().OfType<TabControl>());

        var segmented = rig.Window.FindByName<Border>("TabSwitch");
        Assert.Contains("segmented", segmented.Classes);
        var connections = rig.Window.FindByName<RadioButton>("TabConnections");
        var models = rig.Window.FindByName<RadioButton>("TabModels");
        Assert.Equal("連線", connections.Content);
        Assert.Equal("模型設定", models.Content);

        // 選取＝Soft 底＋OnSoft 文字，圓角 10；沒選取的不是 Soft 底
        Assert.True(connections.IsChecked);
        Assert.False(models.IsChecked);
        var segment = (RadioButton r) => r.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Segment");
        Assert.Equal(new CornerRadius(10), segment(connections).CornerRadius);
        Assert.Equal(TokenColor(rig.Window, "LumiAccentSoftBrush"), ColorOf(segment(connections).Background));
        Assert.Equal(TokenColor(rig.Window, "LumiTextOnSoftBrush"), ColorOf(connections.Foreground));
        Assert.NotEqual(TokenColor(rig.Window, "LumiAccentSoftBrush"), (segment(models).Background as ISolidColorBrush)?.Color);
        Assert.Equal(TokenColor(rig.Window, "LumiTextMutedBrush"), ColorOf(models.Foreground));
    }

    [AvaloniaFact]
    public void Segments_FollowTabIndex_InBothDirections_AndSwapThePanels()
    {
        using var rig = new Rig();
        var connections = rig.Window.FindByName<RadioButton>("TabConnections");
        var models = rig.Window.FindByName<RadioButton>("TabModels");
        Assert.Equal(0, rig.Host.Settings.TabIndex);
        Assert.True(rig.Connections.IsEffectivelyVisible);
        Assert.False(rig.Models.IsEffectivelyVisible);

        models.IsChecked = true;   // 使用者點「模型設定」
        rig.Settle();
        Assert.Equal(1, rig.Host.Settings.TabIndex);
        Assert.False(connections.IsChecked);
        Assert.False(rig.Connections.IsEffectivelyVisible);
        Assert.True(rig.Models.IsEffectivelyVisible);

        rig.Host.Settings.TabIndex = 0;   // 程式導覽（例如「管理連線與模型」）
        rig.Settle();
        Assert.True(connections.IsChecked);
        Assert.False(models.IsChecked);
        Assert.True(rig.Connections.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task HeaderPrimaryAction_FollowsTheSegment()
    {
        using var rig = new Rig();
        var header = rig.Window.FindByName<SettingsPageHeader>("ConnectionsHeader");
        var newConnection = rig.Window.FindByName<Button>("NewConnectionButton");
        var newModel = rig.Window.FindByName<Button>("NewModelButton");

        Assert.Contains("primary", newConnection.Classes);
        Assert.Contains("primary", newModel.Classes);
        Assert.True(newConnection.IsEffectivelyVisible);
        Assert.False(newModel.IsEffectivelyVisible);
        Assert.Contains("新增連線", TextsIn(newConnection));
        Assert.Same(rig.Host.Connections.NewConnectionCommand, newConnection.Command);
        Assert.Single(header.GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("primary") && b.IsEffectivelyVisible);

        await rig.Host.Settings.NavigateAsync(new SettingsNavigation(SettingsSection.Models));
        rig.Settle();
        Assert.False(newConnection.IsEffectivelyVisible);
        Assert.True(newModel.IsEffectivelyVisible);
        Assert.Contains("新增模型設定", TextsIn(newModel));
        Assert.Same(rig.Host.Models.NewModelCommand, newModel.Command);
        Assert.Single(header.GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("primary") && b.IsEffectivelyVisible);

        // 在動作區（頁首）旁邊的主要動作標頭在標題的右邊
        Assert.True(newModel.TranslatePoint(default, header)!.Value.X > header.FindByName<TextBlock>("TitleText").TranslatePoint(default, header)!.Value.X);
    }

    [AvaloniaFact]
    public async Task HeaderPrimaryAction_StartsANewConnection_OrANewModel()
    {
        using var rig = new Rig();
        rig.Window.FindByName<Button>("NewConnectionButton").Command!.Execute(null);
        await SettingsHost.WaitUntil(() => rig.Host.Connections.HasEditor);
        Assert.True(rig.Host.Connections.IsNew);

        await rig.Host.Settings.NavigateAsync(new SettingsNavigation(SettingsSection.Models));
        rig.Host.Connections.DeleteCommand.Execute(null);   // 放棄還沒儲存的新連線
        rig.Window.FindByName<Button>("NewModelButton").Command!.Execute(null);
        await SettingsHost.WaitUntil(() => rig.Host.Models.HasEditor);
        Assert.True(rig.Host.Models.IsNew);
    }

    // ---- 清單卡片 ----

    [AvaloniaFact]
    public async Task ConnectionList_ShowsCards_WithInitialSubtitleAndStatusText()
    {
        using var rig = new Rig();
        await rig.AddOllamaAsync();
        rig.Host.Status.Set(rig.OllamaId, ConnectionTestResult.Ok(["llama3"]));
        var anthropicId = rig.Host.Catalog.Current.Connections.First(c => c.Format == ProviderFormat.Anthropic).Id;
        rig.Host.Status.Set(anthropicId, FakeConnectionTester.Failure(Lumi.Core.Chat.ChatFailureKind.Network, "連不上"));
        rig.Settle();

        var panel = rig.Connections;
        Assert.Equal("我的連線 · 2", panel.FindByName<TextBlock>("ListCaption").Text);
        var list = panel.FindByName<ListBox>("ConnectionList");
        Assert.Contains("cards", list.Classes);
        var cards = list.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("card")).ToList();
        Assert.Equal(2, cards.Count);

        var ollama = cards.Single(c => TextsIn(c).Contains("Ollama"));
        Assert.Equal(new Thickness(12), ollama.Padding);
        Assert.Equal(new CornerRadius(14), ollama.CornerRadius);
        Assert.Equal("O", ollama.GetVisualDescendants().OfType<ProviderMark>().Single().Text);   // 字首方塊（不是商標）
        Assert.Contains("OpenAI 相容 · 1 個模型", TextsIn(ollama));
        var status = ollama.GetVisualDescendants().OfType<StatusIndicator>().Single();
        Assert.Contains("連線正常", TextsIn(status));
        var title = ollama.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Ollama");
        Assert.Equal(14, title.FontSize);
        Assert.Equal(FontWeight.SemiBold, title.FontWeight);

        var anthropic = cards.Single(c => TextsIn(c).Contains("Anthropic"));
        Assert.Equal("A", anthropic.GetVisualDescendants().OfType<ProviderMark>().Single().Text);
        Assert.Contains("Anthropic 原生 · 1 個模型", TextsIn(anthropic));
        Assert.Contains("連線失敗", TextsIn(anthropic));   // 狀態有文字，不只靠顏色

        // 尚未測試：尚未確認
        rig.Host.Status.Remove(rig.OllamaId);
        rig.Settle();
        Assert.Contains("尚未確認", TextsIn(list));
    }

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

    [AvaloniaFact]
    public async Task SelectedConnectionCard_UsesSoftBackground_AndAccentBorder()
    {
        using var rig = new Rig();
        await rig.AddOllamaAsync();
        var list = rig.Connections.FindByName<ListBox>("ConnectionList");
        await rig.Host.Connections.SelectAsync(rig.Host.Connections.Items.Single(i => i.Id == rig.OllamaId));
        rig.Settle();

        var soft = TokenColor(rig.Window, "LumiAccentSoftBrush");
        var selected = list.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("card") && TextsIn(b).Contains("Ollama"));
        var other = list.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("card") && TextsIn(b).Contains("Anthropic"));
        Assert.Equal(soft, ColorOf(selected.Background));
        Assert.Equal(TokenColor(rig.Window, "LumiAccentBorderBrush"), ColorOf(selected.BorderBrush));
        Assert.NotEqual(soft, ColorOf(other.Background));
        Assert.Equal(TokenColor(rig.Window, "LumiTextOnSoftBrush"), ColorOf(selected.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Ollama").Foreground));
    }

    [AvaloniaFact]
    public void AddEntry_IsADashedTertiaryStyleButton_NotAPrimary()
    {
        using var rig = new Rig();
        var add = rig.Connections.FindByName<Button>("AddConnectionButton");
        Assert.Contains("dashed", add.Classes);
        Assert.DoesNotContain("primary", add.Classes);
        Assert.Same(rig.Host.Connections.NewConnectionCommand, add.Command);
        Assert.Contains("新增連線", TextsIn(add));
        var dash = add.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Rectangle>().Single();
        Assert.NotNull(dash.StrokeDashArray);   // 虛線框
        Assert.NotEmpty(dash.StrokeDashArray!);

        rig.Host.Settings.TabIndex = 1;
        rig.Settle();
        var addModel = rig.Models.FindByName<Button>("AddModelButton");
        Assert.Contains("dashed", addModel.Classes);
        Assert.DoesNotContain("primary", addModel.Classes);
        Assert.Same(rig.Host.Models.NewModelCommand, addModel.Command);
    }

    [AvaloniaFact]
    public async Task ModelList_ShowsCards_WithModelIdAndDefaultLabel_AndTheConnectionStatus()
    {
        using var rig = new Rig();
        await rig.AddOllamaAsync();
        rig.Host.Status.Set(rig.OllamaId, ConnectionTestResult.Ok(["llama3"]));
        rig.Host.Settings.TabIndex = 1;
        rig.Settle();

        var panel = rig.Models;
        Assert.Equal("我的模型設定 · 2", panel.FindByName<TextBlock>("ListCaption").Text);
        var cards = panel.FindByName<ListBox>("ModelList").GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("card")).ToList();
        Assert.Equal(2, cards.Count);

        var llama = cards.Single(c => TextsIn(c).Contains("Llama 3"));
        Assert.Contains("llama3 · 小精靈預設", TextsIn(llama));
        Assert.Equal("O", llama.GetVisualDescendants().OfType<ProviderMark>().Single().Text);
        Assert.Contains("連線正常", TextsIn(llama));

        var claude = cards.Single(c => TextsIn(c).Contains("Claude Opus 5.5"));
        Assert.Contains("claude-opus-5-5 · 工作台預設", TextsIn(claude));
        Assert.Contains("尚未確認", TextsIn(claude));

        // 測試連線的結果更新卡片的狀態，但不重建清單、不清掉表單
        await rig.Host.Models.SelectAsync(rig.Host.Models.Items.Single(i => i.Id == "llama-model"));
        var anthropicId = rig.Host.Catalog.Current.Connections.First(c => c.Format == ProviderFormat.Anthropic).Id;
        rig.Host.Status.Set(anthropicId, ConnectionTestResult.Ok(["claude-opus-5-5"]));
        rig.Settle();
        Assert.Contains("連線正常", TextsIn(cards.Single(c => TextsIn(c).Contains("Claude Opus 5.5"))));
        Assert.True(rig.Host.Models.HasEditor);
        Assert.Equal("llama-model", rig.Host.Models.SelectedItem?.Id);
    }

    // ---- 清單＋詳細：寬度 ----

    [AvaloniaFact]
    public void Wide_PutsTheListAt260_AndTheDetailInTheRest()
    {
        using var rig = new Rig(1040, 760);
        Assert.DoesNotContain("stacked", rig.Window.Classes);
        var layout = rig.Connections.FindByName<Grid>("Layout");
        Assert.Equal(260, layout.ColumnDefinitions[0].Width.Value);
        var master = rig.Connections.FindByName<DockPanel>("MasterHost");
        var detail = rig.Connections.FindByName<Panel>("DetailHost");
        Assert.Equal(0, Grid.GetColumn(master));
        Assert.Equal(1, Grid.GetColumn(detail));
        Assert.True(detail.Bounds.Left > master.Bounds.Right - 0.5);
        Assert.Equal(master.Bounds.Top, detail.Bounds.Top, 0.5);
        Assert.True(detail.Bounds.Width > master.Bounds.Width);
        Assert.Equal(layout.Bounds.Width, master.Bounds.Width + 16 + detail.Bounds.Width, 1.0);   // 詳細吃剩餘寬度
    }

    [AvaloniaFact]
    public async Task Below900_StacksTheListAboveTheDetail_ForBothSegments()
    {
        using var rig = new Rig(1040, 760);
        await rig.Host.Connections.SelectAsync(rig.Host.Connections.Items[0]);
        rig.Resize(800, 760);
        Assert.Contains("stacked", rig.Window.Classes);

        var master = rig.Connections.FindByName<DockPanel>("MasterHost");
        var detail = rig.Connections.FindByName<Panel>("DetailHost");
        Assert.Equal(0, Grid.GetRow(master));
        Assert.Equal(1, Grid.GetRow(detail));
        Assert.Equal(2, Grid.GetColumnSpan(master));
        Assert.Equal(2, Grid.GetColumnSpan(detail));
        Assert.True(detail.TranslatePoint(default, rig.Window)!.Value.Y >= master.TranslatePoint(default, rig.Window)!.Value.Y + master.Bounds.Height - 0.5);
        Assert.Equal(master.Bounds.Left, detail.Bounds.Left, 0.5);
        // 上下排時整頁捲動（並排時不捲動，清單與詳細各自捲動）
        Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, rig.Connections.FindByName<ScrollViewer>("PanelScroll").VerticalScrollBarVisibility);

        rig.Host.Settings.TabIndex = 1;
        rig.Settle();
        var modelMaster = rig.Models.FindByName<DockPanel>("MasterHost");
        var modelDetail = rig.Models.FindByName<Panel>("DetailHost");
        Assert.Equal(0, Grid.GetRow(modelMaster));
        Assert.Equal(1, Grid.GetRow(modelDetail));

        rig.Resize(1040, 760);
        Assert.DoesNotContain("stacked", rig.Window.Classes);
        Assert.Equal(1, Grid.GetColumn(rig.Models.FindByName<Panel>("DetailHost")));
        Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, rig.Models.FindByName<ScrollViewer>("PanelScroll").VerticalScrollBarVisibility);
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
        Assert.False(panel.FindByName<TextBlock>("DetailPlaceholder").IsEffectivelyVisible);
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
        rig.Host.Settings.TabIndex = 1;
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
    public async Task FocusApiKeyRequest_FocusesTheKeyBox_OnTheConnectionsSegment()
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

    // ---- 模型設定 ----

    [AvaloniaFact]
    public async Task ModelsPage_ShowsDefaultsAboveTheList_WithLabelsCaptionsAndTheCostNote()
    {
        using var rig = new Rig();
        rig.Host.Settings.TabIndex = 1;
        rig.Settle();
        var panel = rig.Models;

        var workbench = panel.FindByName<FormField>("WorkbenchDefaultField");
        var sprite = panel.FindByName<FormField>("SpriteDefaultField");
        Assert.Equal("工作台預設模型", workbench.Label);
        Assert.Equal("小精靈預設模型", sprite.Label);
        Assert.Same(rig.Host.Models.DefaultOptions, panel.FindByName<ComboBox>("WorkbenchDefaultBox").ItemsSource);
        Assert.Equal(Strings.Models_WorkbenchDefaultHint, workbench.Hint);
        var label = workbench.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_Label");
        var box = panel.FindByName<ComboBox>("WorkbenchDefaultBox");
        Assert.True(label.TranslatePoint(default, panel)!.Value.Y < box.TranslatePoint(default, panel)!.Value.Y);   // 標籤在上方
        Assert.Equal(11, workbench.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_Hint").FontSize);

        // 費用說明：預設模型下方的一行 Caption，重用 Workbench_CostNote；在清單之前
        var cost = panel.FindByName<TextBlock>("CostNote");
        Assert.True(cost.IsEffectivelyVisible);
        Assert.Equal(Strings.Workbench_CostNote, cost.Text);
        Assert.Equal(11, cost.FontSize);
        Assert.True(cost.TranslatePoint(default, panel)!.Value.Y > box.TranslatePoint(default, panel)!.Value.Y);
        Assert.True(cost.TranslatePoint(default, panel)!.Value.Y < panel.FindByName<TextBlock>("ListCaption").TranslatePoint(default, panel)!.Value.Y);
        await Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task ModelForm_HasTheFieldsWithTheirNames_AndTechnicalFieldsInACollapsedAdvancedSection()
    {
        using var rig = new Rig();
        rig.Host.Settings.TabIndex = 1;
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
        rig.Host.Settings.TabIndex = 1;
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
    public async Task NewModelButton_IsDisabledWithoutAConnection_AndTheReasonIsWrittenOut()
    {
        using var rig = new Rig();
        var connectionId = rig.Host.Catalog.Current.Connections[0].Id;
        await rig.Host.Catalog.DeleteConnectionAsync(connectionId);
        rig.Host.Settings.TabIndex = 1;
        rig.Settle();
        Assert.False(rig.Window.FindByName<Button>("NewModelButton").IsEffectivelyEnabled);
        Assert.False(rig.Models.FindByName<Button>("AddModelButton").IsEffectivelyEnabled);
        var hint = rig.Models.FindByName<TextBlock>("NeedConnectionHint");
        Assert.True(hint.IsEffectivelyVisible);
        Assert.Equal(Strings.Models_NeedConnection, hint.Text);
    }

    [AvaloniaFact]
    public async Task EditingAModel_KeepsTheEditorWhenAConnectionChangesElsewhere()
    {
        using var rig = new Rig();
        rig.Host.Settings.TabIndex = 1;
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
}
