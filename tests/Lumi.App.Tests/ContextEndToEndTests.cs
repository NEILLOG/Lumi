using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Tools;
using Lumi.Infrastructure.Attachments;
using Lumi.Infrastructure.Persistence;
using Lumi.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.App.Tests;

/// <summary>
/// 4B 的整合測試：真的 SQLite、真的附件資料夾、真的檔案範圍與快照建構器、真的檔案工具，只有供應商是腳本化的。
/// 驗證「加入 → 送出 → 快照存檔 → 工具在範圍內讀取 → 移除後讀不到」這一整條路。
/// </summary>
public sealed class ContextEndToEndTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly string _project;
    private readonly SqliteConversationStore _db;
    private readonly FileAttachmentStore _attachments;
    private readonly FileToolOptions _fileOptions = new();

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    public ContextEndToEndTests()
    {
        _project = Path.Combine(_directory, "專案");
        Directory.CreateDirectory(_project);
        _db = new SqliteConversationStore(Options.Create(new PersistenceOptions { DatabasePath = Path.Combine(_directory, "lumi.db") }));
        _attachments = new FileAttachmentStore(Options.Create(new AttachmentOptions { Directory = Path.Combine(_directory, "attachments") }));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_directory, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private ConversationService Service(IChatProvider provider) => new(
        provider, _db, new FakeModelCatalog(),
        new ToolRegistry([new ReadFileTool(_fileOptions), new ListDirTool(_fileOptions), new SearchTool(_fileOptions)]),
        new ToolApprovalService(), new ToolPermissionMemory(), new ToolLoopOptions(), TimeProvider.System, NullLogger<ConversationService>.Instance,
        _db, new FileScopeFactory(), new ContextAttachmentBuilder(_fileOptions, _attachments, new HeuristicTokenEstimator()));

    private static async Task<List<ChatEvent>> Run(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events)
            list.Add(e);
        return list;
    }

    [Fact]
    public async Task AFolderWithTenThousandFiles_ProducesABoundedTree_AndAFastEstimate()
    {
        for (var d = 0; d < 100; d++)
        {
            Directory.CreateDirectory(Path.Combine(_project, $"dir{d:D3}"));
            for (var f = 0; f < 100; f++)
                File.WriteAllText(Path.Combine(_project, $"dir{d:D3}", $"file{f:D3}.txt"), "x");
        }
        var conversation = await _db.CreateAsync("大資料夾");
        await _db.AddAsync(conversation.Id, ContextKind.Folder, _project, "專案");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));

        var started = DateTime.UtcNow;
        await Run(Service(provider).SendAsync(conversation.Id, "這個專案有什麼"));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30));
        var folder = Assert.IsType<ContextBlock>(provider.Requests[0].Messages[0].Content[0]);
        var lines = folder.Content!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length <= _fileOptions.TreeMaxEntries + 2, $"檔案樹有 {lines.Length} 行");   // 項目上限＋根與「還有 N 個」兩行
        Assert.Contains("個項目沒有列出", folder.Content);
        Assert.InRange(new HeuristicTokenEstimator().Estimate(ContextBlockFormatter.ToPromptText(folder)), 1, 10_000);
    }

    [Fact]
    public async Task FullPath_AddFolderFileAndImage_Send_SnapshotsAreStored_AndNotRepeated()
    {
        Write("專案/src/app.cs", "class App {}\n");
        var notes = Write("筆記.md", "# 筆記\n內容");
        var picture = Path.Combine(_directory, "截圖.png");
        File.WriteAllBytes(picture, Png);
        var conversation = await _db.CreateAsync("c");
        await _db.AddAsync(conversation.Id, ContextKind.Folder, _project, "專案");
        await _db.AddAsync(conversation.Id, ContextKind.File, notes, "筆記.md");
        await _db.AddAsync(conversation.Id, ContextKind.Image, picture, "截圖.png");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("看到了"), SequenceChatProvider.Answer("好"));
        var service = Service(provider);

        await Run(service.SendAsync(conversation.Id, "幫我看看"));
        await Run(service.SendAsync(conversation.Id, "再問一個問題"));

        // 第一則使用者訊息：資料夾樹、檔案文字、圖片、使用者的字
        var stored = await _db.GetMessagesAsync(conversation.Id);
        var first = stored.First(m => m.Role == ChatRole.User);
        Assert.Equal([typeof(ContextBlock), typeof(ContextBlock), typeof(ImageBlock), typeof(TextBlock)], first.Content.Select(b => b.GetType()));
        Assert.Contains("app.cs", ((ContextBlock)first.Content[0]).Content);
        Assert.Equal("# 筆記\n內容", ((ContextBlock)first.Content[1]).Content);
        var image = (ImageBlock)first.Content[2];
        Assert.Equal(Png, await _attachments.ReadAsync(image.FileName));
        // 第二則：只有文字
        var second = stored.Where(m => m.Role == ChatRole.User).ElementAt(1);
        Assert.Equal([typeof(TextBlock)], second.Content.Select(b => b.GetType()));
        // 第二次請求仍然帶著第一則的快照（歷史只往後加）
        Assert.Contains(provider.Requests[1].Messages[0].Content, b => b is ContextBlock);
        Assert.All(await _db.GetAsync(conversation.Id), i => Assert.False(i.IsPending));
        // 重新開啟（重讀）後型別仍然正確，圖片引用被記錄
        Assert.Equal([image.FileName], await _db.GetReferencedFileNamesAsync());
    }

    [Fact]
    public async Task Tools_ReadInsideTheScope_AndAreRefusedOutsideIt_ThroughTheRealLoop()
    {
        Write("專案/src/app.cs", "第一行\n第二行 ❤️\n");
        var outside = Write("外面/secret.txt", "不該被讀到");
        var conversation = await _db.CreateAsync("c");
        await _db.AddAsync(conversation.Id, ContextKind.Folder, _project, "專案");
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(null, new ToolUseBlock("t1", "read_file", """{"path":"src/app.cs"}"""), new ToolUseBlock("t2", "read_file", System.Text.Json.JsonSerializer.Serialize(new { path = outside }))),
            SequenceChatProvider.Answer("好"));

        var events = await Run(Service(provider).SendAsync(conversation.Id, "讀兩個檔案"));

        var results = events.OfType<ToolCallCompleted>().Select(e => e.Result).ToList();
        Assert.False(results[0].IsError);
        Assert.Contains("第二行 ❤️", results[0].Content);
        Assert.True(results[1].IsError);
        Assert.Contains("不在可存取的範圍內", results[1].Content);
        Assert.DoesNotContain("不該被讀到", results[1].Content);
        // 有範圍時，請求帶著三個檔案工具
        Assert.Equal(["list_dir", "read_file", "search"], provider.Requests[0].Tools!.Select(t => t.Descriptor.Name));
    }

    [Fact]
    public async Task RemovingTheFolder_TakesTheToolsAway_ButTheSnapshotStaysInTheHistory()
    {
        Write("專案/a.txt", "內容");
        var conversation = await _db.CreateAsync("c");
        var item = await _db.AddAsync(conversation.Id, ContextKind.Folder, _project, "專案");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("一"), SequenceChatProvider.Answer("二"));
        var service = Service(provider);
        await Run(service.SendAsync(conversation.Id, "看"));

        await _db.RemoveAsync(conversation.Id, item.Id);
        await Run(service.SendAsync(conversation.Id, "還記得嗎"));

        Assert.Null(provider.Requests[1].Tools);   // 範圍沒了：不送檔案工具
        Assert.Contains(provider.Requests[1].Messages[0].Content, b => b is ContextBlock);   // 之前的快照還在歷史裡
    }

    [Fact]
    public async Task ReAddingAFile_AfterItChanged_EmbedsTheNewContent()
    {
        var file = Write("a.txt", "舊內容");
        var conversation = await _db.CreateAsync("c");
        await _db.AddAsync(conversation.Id, ContextKind.File, file, "a.txt");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("一"), SequenceChatProvider.Answer("二"));
        var service = Service(provider);
        await Run(service.SendAsync(conversation.Id, "第一次"));

        File.WriteAllText(file, "新內容");
        await _db.AddAsync(conversation.Id, ContextKind.File, file, "a.txt");
        await Run(service.SendAsync(conversation.Id, "第二次"));

        var users = (await _db.GetMessagesAsync(conversation.Id)).Where(m => m.Role == ChatRole.User).ToList();
        Assert.Equal("舊內容", ((ContextBlock)users[0].Content[0]).Content);   // 歷史裡的快照不會被改
        Assert.Equal("新內容", ((ContextBlock)users[1].Content[0]).Content);
    }

    [Fact]
    public async Task AFileThatVanishedBeforeSending_DoesNotBreakTheSend()
    {
        var file = Write("a.txt", "x");
        var conversation = await _db.CreateAsync("c");
        await _db.AddAsync(conversation.Id, ContextKind.File, file, "a.txt");
        File.Delete(file);
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));

        var events = await Run(Service(provider).SendAsync(conversation.Id, "看"));

        Assert.IsType<Completed>(events[^1]);
        var block = Assert.IsType<ContextBlock>(provider.Requests[0].Messages[0].Content[0]);
        Assert.False(block.Embedded);
        Assert.Contains("找不到", block.Note);
    }
}
