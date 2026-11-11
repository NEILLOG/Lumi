using Lumi.Core.Context;
using Lumi.Core.Images;
using Lumi.Core.Notes;
using Lumi.Core.Tools;
using Lumi.Tools.Browser;
using Lumi.Tools.Commands;
using Lumi.Tools.Images;
using Lumi.Tools.Memory;
using Lumi.Tools.Notes;
using Lumi.Tools.Reminders;
using Lumi.Tools.Skills;
using Lumi.Tools.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumi.Tools;

public static class ToolsServiceCollectionExtensions
{
    public static IServiceCollection AddLumiTools(this IServiceCollection services, Action<ToolsOptions>? configure = null)
    {
        var options = new ToolsOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton(options.Files);

        // 檔案範圍：對話目前加入的檔案與資料夾（取代 Core 預設的空範圍）
        services.Replace(ServiceDescriptor.Singleton<IFileScopeFactory, FileScopeFactory>());
        // 送出時把待送出的項目拍成快照（檔案文字、資料夾檔案樹、圖片）
        services.Replace(ServiceDescriptor.Singleton<IContextAttachmentBuilder, ContextAttachmentBuilder>());
        // 給 chip 顯示狀態用：是檔案、資料夾還是圖片，有沒有問題
        services.Replace(ServiceDescriptor.Singleton<IContextInspector, ContextInspector>());

        services.AddLumiTool<GetCurrentTimeTool>();
        services.AddLumiTool<CalculateTool>();
        services.AddLumiTool<AppHelpTool>();

        // 技能（M29）：ISkillStore 由 Core 預設註冊（App 換成 FileSkillStore）；read_skill 只在對話快照含它時 system 才列清單
        services.AddLumiTool<ReadSkillTool>();
        services.AddLumiTool<SaveSkillTool>();
        services.AddLumiTool<RunCommandTool>();   // 執行指令（M31）：預設關閉；ICommandRunner 在 Platform、ICommandGuard 在 Infrastructure 註冊
        services.AddLumiTool<ListDirTool>();
        services.AddLumiTool<ReadFileTool>();
        services.AddLumiTool<SearchTool>();
        services.AddLumiTool<ReadDocumentTool>();
        services.AddLumiTool<ShowDetailsTool>();
        services.AddLumiTool<WriteFileTool>();
        services.AddLumiTool<OverwriteFileTool>();
        services.AddLumiTool<CopyFileTool>();
        services.AddLumiTool<MoveFileTool>();
        services.AddLumiTool<CreateFolderTool>();

        // 筆記與對話搜尋（M10 10B）：資料夾由 INoteFolderProvider 提供（設定實作取代 Core 的預設）
        services.AddSingleton(new NoteOptions());
        services.AddSingleton<INoteStore, NoteStore>();
        services.TryAddSingleton<INoteFolderProvider, DefaultNoteFolderProvider>();
        services.AddLumiTool<AddNoteTool>();
        services.AddLumiTool<ReadNotesTool>();
        services.AddLumiTool<ConversationSearchTool>();
        services.AddLumiTool<ReadConversationTool>();

        // 提醒與排程（M10 10E）：IReminderService 與 ReminderOptions 由 Lumi.Core 註冊
        services.AddLumiTool<CreateReminderTool>();
        services.AddLumiTool<CreateScheduledPromptTool>();   // 排程跑 prompt（M24）：IModelCatalog 由組裝根提供
        services.AddLumiTool<StartTimerTool>();
        services.AddLumiTool<ListRemindersTool>();
        services.AddLumiTool<CancelReminderTool>();
        services.AddLumiTool<SetReminderEnabledTool>();

        // 長期記憶（M14 14C）：IMemoryStore 與 MemoryOptions 由組裝根與 Lumi.Core 註冊；記憶關閉時定義不送（RequiresMemory）
        services.AddLumiTool<MemorySaveTool>();
        services.AddLumiTool<MemoryUpdateTool>();
        services.AddLumiTool<MemoryDeleteTool>();

        // 網路工具（M17）：IWebClient、IWebContentExtractor、IFeedReader、INetworkDiagnostics 由 Lumi.Web 的 AddLumiWeb 註冊
        services.AddLumiTool<WebFetchTool>();
        services.AddLumiTool<WebLinksTool>();
        services.AddLumiTool<RssReadTool>();
        services.AddLumiTool<HttpRequestTool>();
        services.AddLumiTool<HttpSendTool>();
        services.AddLumiTool<DownloadFileTool>();
        services.AddLumiTool<NetCheckTool>();
        services.AddLumiTool<WebCrawlTool>();   // 爬取網站（M19）：HTTP 或 Lumi 的瀏覽器，進度顯示在卡片上

        // 圖片生成與編輯（22F）：IImageGenerator 由 Lumi.Providers 註冊；圖片存到「圖片／Lumi」
        services.TryAddSingleton<IImageGenerator, NullImageGenerator>();
        services.TryAddSingleton<IAttachmentStore, NullAttachmentStore>();
        services.AddSingleton(_ => new GeneratedImageStore(GeneratedImageStore.DefaultFolder));
        services.AddLumiTool<GenerateImageTool>();
        services.AddLumiTool<EditImageTool>();

        // Outlook 工具：IOutlookClient 由 Lumi.Outlook 的 AddLumiOutlook 在 Windows 註冊（其他平台用 Core 的 NullOutlookClient，整組不顯示）
        services.TryAddSingleton<Lumi.Core.Outlook.IOutlookClient, Lumi.Core.Outlook.NullOutlookClient>();
        services.AddLumiTool<Lumi.Tools.Outlook.OutlookMailSearchTool>();
        services.AddLumiTool<Lumi.Tools.Outlook.OutlookMailReadTool>();
        services.AddLumiTool<Lumi.Tools.Outlook.OutlookCalendarListTool>();
        services.AddLumiTool<Lumi.Tools.Outlook.OutlookMailSaveAttachmentTool>();
        services.AddLumiTool<Lumi.Tools.Outlook.OutlookMailDraftTool>();
        services.AddLumiTool<Lumi.Tools.Outlook.OutlookCalendarSaveTool>();
        services.AddLumiTool<Lumi.Tools.Outlook.OutlookMeetingDraftTool>();
        services.AddLumiTool<Lumi.Tools.Outlook.OutlookMailSendToSelfTool>();
        services.AddLumiTool<Lumi.Tools.Outlook.OutlookMailSendTool>();

        // 瀏覽器工具（M18）：IBrowserSessions 由 Lumi.Browser 的 AddLumiBrowser 註冊（沒註冊時用 Core 的 NullBrowserSessions）
        services.AddLumiTool<BrowserOpenTool>();
        services.AddLumiTool<BrowserSnapshotTool>();
        services.AddLumiTool<BrowserExtractTool>();
        services.AddLumiTool<BrowserScreenshotTool>();
        services.AddLumiTool<BrowserScrollTool>();
        services.AddLumiTool<BrowserCloseTool>();
        services.AddLumiTool<BrowserClickTool>();
        services.AddLumiTool<BrowserTypeTool>();
        services.AddLumiTool<BrowserSelectTool>();
        services.AddLumiTool<BrowserPressTool>();
        services.AddLumiTool<BrowserBackTool>();
        services.AddLumiTool<BrowserUploadTool>();
        services.AddLumiTool<BrowserDownloadTool>();
        services.AddLumiTool<BrowserWaitForUserTool>();
        return services;
    }
}
