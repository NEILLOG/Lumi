using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Lumi.App.Services;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Lumi.Core.Speech;
using Lumi.Sprites.Assets;
using Lumi.Sprites.Orb;
using CommunityToolkit.Mvvm.Input;

namespace Lumi.App.Tests.TestSupport;

/// <summary>
/// 設定視窗的測試環境（可存取性、Tab 順序、文字對比共用）：<see cref="Pages"/> 列出每個分類頁的各種畫面狀態
/// （清單、表單、進階展開、錯誤、測試結果、載入問題…），<see cref="CreateAsync"/> 開出視窗並切到該狀態。
/// </summary>
internal sealed class SettingsRig : IDisposable
{
    /// <summary>每個分類頁要掃描的畫面狀態名稱。</summary>
    public static readonly string[] Pages =
    [
        "connections-list", "connections-form", "connections-advanced", "connections-errors", "connections-key-stored",
        "connections-test-ok", "connections-test-fail",
        "models-form", "models-advanced", "image-generation",
        "persona", "persona-dirty", "memory", "memory-new", "memory-editing", "general", "sprite", "sprite-issues",
        "speech", "speech-token-stored", "speech-test-ok", "speech-test-fail",
    ];

    private readonly CorgiPack? _pack;

    private SettingsRig(SettingsHost host, SettingsWindow window, CorgiPack? pack)
    {
        Host = host;
        Window = window;
        _pack = pack;
    }

    public SettingsHost Host { get; }

    public SettingsWindow Window { get; }

    public static async Task<SettingsRig> CreateAsync(string state, double width = 1040, double height = 760)
    {
        CorgiPack? pack = null;
        SettingsHost host;
        if (state == "sprite-issues")
        {
            pack = new CorgiPack();
            pack.AddBroken();
            var library = new SpriteLibrary([new OrbSpriteFactory()], new AssetSpriteOptions { Directory = pack.Root });
            await library.ReloadAsync();
            host = new SettingsHost(library, new FakeFolderOpener());
            host.Sprite.Issues.Add(new SpriteIssueItem("corgi：內容貼邊（測試用警告）", IsFatal: false));
        }
        else
        {
            host = new SettingsHost(new AvaloniaUiDispatcher());
        }

        var window = new SettingsWindow(host.Settings) { Width = width, Height = height };
        window.Show();
        var rig = new SettingsRig(host, window, pack);
        rig.Settle();
        await rig.ApplyStateAsync(state);
        return rig;
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

    private async Task ApplyStateAsync(string state)
    {
        var settings = Host.Settings;
        settings.CategoryIndex = (int)SettingsCategory.Connections;   // 預設落在「設定首頁」；除非狀態另外指定，其餘狀態都從 AI 連線開始
        switch (state)
        {
            case "connections-list":
                break;
            case "connections-form":
            case "connections-advanced":
            case "connections-errors":
            case "connections-key-stored":
            case "connections-test-ok":
            case "connections-test-fail":
                await ApplyConnectionStateAsync(state);
                break;
            case "models-form":
            case "models-advanced":
                await Host.Models.SelectAsync(Host.Models.Items[0]);
                Settle();
                if (state == "models-advanced")
                    ExpandAdvanced("ModelsPanel");
                break;
            case "image-generation":
                await Host.Catalog.SetImageGenerationAsync(new ImageGenerationSettings(Host.Catalog.Current.Connections[0].Id, "gpt-image-1"));   // 已設定：模型 ID 不空白
                await Host.Settings.AiSettings.SelectAsync(Host.Settings.AiSettings.Items.OfType<AiImageGenerationItem>().Single());
                break;
            case "persona":
                settings.CategoryIndex = 2;
                break;
            case "persona-dirty":
                settings.CategoryIndex = 2;
                Host.Persona.CustomPersona = "請用簡短的句子回答。";
                break;
            case "memory":
            case "memory-new":
            case "memory-editing":
                Host.MemoryStore.Seed("我是後端工程師", Lumi.Core.Memory.MemoryCategory.Profile, Lumi.Core.Memory.MemorySource.Explicit);
                Host.MemoryStore.Seed("偏好以繁體中文回覆", Lumi.Core.Memory.MemoryCategory.Preference, Lumi.Core.Memory.MemorySource.Suggested,
                    new DateOnly(2026, 12, 31));
                Host.MemoryStore.Seed("目前在做內部工具", Lumi.Core.Memory.MemoryCategory.Project, Lumi.Core.Memory.MemorySource.Manual,
                    new DateOnly(2026, 10, 1));   // 已過期（測試用的今天是 2026-10-19）
                Settle();
                settings.CategoryIndex = 3;
                if (state == "memory-new")
                    Host.Memory.NewCommand.Execute(null);
                if (state == "memory-editing")
                    Host.Memory.Groups[0].Items[0].EditCommand.Execute(null);
                break;
            case "tools":
                settings.CategoryIndex = 5;
                break;
            case "general":
                settings.CategoryIndex = 6;
                break;
            case "sprite":
            case "sprite-issues":
                settings.CategoryIndex = 7;
                break;
            case "speech":
                settings.CategoryIndex = 8;
                break;
            case "speech-token-stored":
                await Host.Catalog.SetSpeechAsync(new SpeechSettings("https://speech.example.com/v1", "zh", 120), new SecretValue("wh-token-5678"));
                settings.CategoryIndex = 8;
                break;
            case "speech-test-ok":
            case "speech-test-fail":
                settings.CategoryIndex = 8;
                Host.Speech.Endpoint = "https://speech.example.com/v1";
                Host.Speech.NewToken = "wh-token-5678";
                Settle();
                Host.SpeechTester.Failure = state == "speech-test-fail"
                    ? new SpeechFailure(SpeechFailureKind.InvalidToken, "Token 無效，或沒有 WHISPER_API 權限。", false)
                    : null;
                await ((IAsyncRelayCommand)Host.Speech.TestCommand).ExecuteAsync(null);
                break;
            default:
                throw new ArgumentException(state, nameof(state));
        }
        Settle();
    }

    private async Task ApplyConnectionStateAsync(string state)
    {
        var connections = Host.Connections;
        if (state == "connections-key-stored")
        {
            await Host.Catalog.SaveConnectionAsync(new ConnectionSettings("withkey", "有金鑰", ProviderFormat.OpenAICompatible, "https://x.example.com/v1"),
                new Lumi.Core.Secrets.SecretValue("sk-abcdef-1234"));
            Settle();
            await connections.SelectAsync(connections.Items.Single(i => i.Id == "withkey"));
        }
        else
        {
            await connections.SelectAsync(connections.Items[0]);
        }
        Settle();

        switch (state)
        {
            case "connections-advanced":
                connections.BaseUrl = "http://example.com/v1";   // 非本機的 http：顯示警告文字
                Settle();
                ExpandAdvanced("ConnectionsPanel");
                break;
            case "connections-errors":
                connections.Name = "";                           // 必填欄位空白：錯誤文字與「請先修正欄位」
                connections.BaseUrl = "不是網址";
                Settle();
                ExpandAdvanced("ConnectionsPanel");
                break;
            case "connections-test-ok":
                Host.Tester.Result = ConnectionTestResult.Ok(["model-a", "model-b"]);
                await ((IAsyncRelayCommand)connections.TestCommand).ExecuteAsync(null);
                break;
            case "connections-test-fail":
                Host.Tester.Result = FakeConnectionTester.Failure(ChatFailureKind.InvalidApiKey, "金鑰無效，請重新輸入。");
                await ((IAsyncRelayCommand)connections.TestCommand).ExecuteAsync(null);
                break;
        }
    }

    private void ExpandAdvanced(string panelName)
    {
        var panel = Window.FindByName<UserControl>(panelName);
        var expander = panel.FindByName<Expander>("AdvancedExpander");
        expander.IsExpanded = true;
        Settle();
    }

    /// <summary>把色票與明暗套到整個應用程式與這個視窗（立即生效）。測試結束要呼叫 <see cref="ResetTheme"/>。</summary>
    public void ApplyTheme(string paletteId, Avalonia.Styling.ThemeVariant variant)
    {
        ThemeService.ApplyPalette(Application.Current!.Resources.MergedDictionaries, paletteId);
        Window.RequestedThemeVariant = variant;
        Settle();
    }

    /// <summary>還原預設色票（其他測試都假設是 sage）。</summary>
    public static void ResetTheme() =>
        ThemeService.ApplyPalette(Application.Current!.Resources.MergedDictionaries, ThemePalettes.Default);

    public void Dispose()
    {
        Window.Close();
        Host.Dispose();
        _pack?.Dispose();
    }
}
