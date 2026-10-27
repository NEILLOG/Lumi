using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Lumi.Core.Browser;
using Lumi.Core.Context;
using Lumi.Core.Memory;
using Lumi.Core.Models;
using Lumi.Core.Network;
using Lumi.Core.Prompts;
using Lumi.Core.Tools;
using Lumi.Core.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Chat;

/// <summary>
/// 對話流程：存使用者訊息 → 組請求 → 呼叫 <see cref="IChatProvider"/> → 轉發事件 → 存助理訊息；
/// 助理要呼叫工具時進入工具迴圈：確認權限、執行、存結果、再次送出，直到不再呼叫工具或達到次數上限。
/// 事件與 <see cref="IChatProvider"/> 的約定相同，另外一輪可以有多個 <see cref="Completed"/>（每次供應商呼叫一個）；
/// 取消時丟 <see cref="OperationCanceledException"/>。
/// 工具清單在一則對話內固定（M15 D1）：第一次送出時記下工具定義的快照，之後這則對話一律送快照。
/// </summary>
public sealed class ConversationService
{
    public const int TitleMaxLength = 30;

    /// <summary>工具進度文字的上限（M19 D12）。</summary>
    public const int ProgressMaxChars = 100;

    /// <summary>估算超過上下文上限的這個比例就開始整理（M22）：先做第一層（清掉舊的工具結果）。</summary>
    public const double CompactAtRatio = 0.8;

    /// <summary>第一層之後重新估算仍超過這個比例，再做第二層（LLM 摘要）。</summary>
    public const double SummaryAtRatio = 0.7;

    /// <summary>摘要那次呼叫的輸出上限。</summary>
    public const int SummaryMaxOutputTokens = 4000;

    private const string ContextTooLongMessage = "這則對話太長，整理後仍超過模型能處理的長度。請開新對話繼續。";
    private const string EmptySummaryMessage = "整理對話時沒有取得摘要內容。";
    private const string NoModelMessage = "還沒有可用的模型，請先到設定加入連線與模型。";
    private const string InterruptedMessage = "對話被中斷，沒有取得這個工具的結果。";
    private const string StoppedMessage = "使用者已停止。";
    private const string DeniedMessage = "使用者拒絕執行";
    private const string WrongModeMessage = "這個工具在目前模式不能使用。";
    private const string MemoryOffMessage = "長期記憶已在設定裡關閉，不能新增、修改或刪除記憶。";
    private const string UnavailableMessage = "這個工具目前無法使用。";

    private readonly IChatProvider _provider;
    private readonly IConversationStore _store;
    private readonly IModelCatalog _catalog;
    private readonly IToolApprovalService _approvals;
    private readonly ToolPermissionMemory _memory;
    private readonly ToolLoopOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ConversationService> _logger;
    private readonly IConversationContextStore _contextStore;
    private readonly IFileScopeFactory _scopeFactory;
    private readonly IContextAttachmentBuilder _attachmentBuilder;
    private readonly IPromptComposer _composer;
    private readonly PromptOptions _promptOptions;
    private readonly INetworkStatus _network;
    private readonly IMemoryStore? _memoryStore;
    private readonly IToolCatalog _toolCatalog;
    private readonly IAttachmentStore? _attachments;
    private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.Ordinal);

    /// <summary>不帶任何工具的最小組合（測試與不需要工具的場合）。</summary>
    public ConversationService(IChatProvider provider, IConversationStore store, IModelCatalog catalog)
        : this(provider, store, catalog, new ToolRegistry([]), new DenyAllToolApprovalService(), new ToolPermissionMemory(),
            new ToolLoopOptions(), TimeProvider.System, NullLogger<ConversationService>.Instance)
    {
    }

    public ConversationService(
        IChatProvider provider,
        IConversationStore store,
        IModelCatalog catalog,
        IToolRegistry registry,
        IToolApprovalService approvals,
        ToolPermissionMemory memory,
        ToolLoopOptions options,
        TimeProvider time,
        ILogger<ConversationService> logger,
        IConversationContextStore? contextStore = null,
        IFileScopeFactory? scopeFactory = null,
        IContextAttachmentBuilder? attachmentBuilder = null,
        IPromptComposer? composer = null,
        PromptOptions? promptOptions = null,
        INetworkStatus? network = null,
        IMemoryStore? memoryStore = null,
        IToolCatalog? toolCatalog = null,
        IAttachmentStore? attachments = null)
    {
        _provider = provider;
        _store = store;
        _catalog = catalog;
        _approvals = approvals;
        _memory = memory;
        _options = options;
        _time = time;
        _logger = logger;
        _contextStore = contextStore ?? new NullConversationContextStore();
        _scopeFactory = scopeFactory ?? new EmptyFileScopeFactory();
        _attachmentBuilder = attachmentBuilder ?? new NullContextAttachmentBuilder();
        _composer = composer ?? new NullPromptComposer();
        _promptOptions = promptOptions ?? new PromptOptions();
        _network = network ?? new AlwaysOnlineNetworkStatus();
        _memoryStore = memoryStore;
        _toolCatalog = toolCatalog ?? new ToolCatalog(registry, catalog);
        _attachments = attachments;
    }

    /// <summary>先把使用者訊息存檔，再串流助理回應。第一則訊息會順便設定對話標題。</summary>
    public async IAsyncEnumerable<ChatEvent> SendAsync(
        string conversationId, string userText, [EnumeratorCancellation] CancellationToken ct = default, ChatMode mode = ChatMode.Workbench)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userText);
        Begin(conversationId);
        try
        {
            var existing = await _store.GetMessagesAsync(conversationId, ct).ConfigureAwait(false);

            // 還沒送出的上下文項目：把「現在」的內容拍成快照，和使用者的文字一起存成同一則訊息，之後不再重複送（M4 的 Q1）
            var pending = (await _contextStore.GetAsync(conversationId, ct).ConfigureAwait(false)).Where(i => i.IsPending).ToList();
            var attachments = pending.Count == 0
                ? ContextAttachments.None
                : await _attachmentBuilder.BuildAsync(pending, ct).ConfigureAwait(false);

            List<ContentBlock> content = [.. attachments.Blocks, new TextBlock(userText)];
            var userMessage = new ChatMessage(ChatMessage.NewId(), ChatRole.User, content, DateTimeOffset.UtcNow);
            await _store.AppendMessageAsync(conversationId, userMessage, ct).ConfigureAwait(false);
            if (attachments.ItemIds.Count > 0)
            {
                // 訊息已經存好：標記不受取消影響，否則下次會把同樣的內容再帶一次
                await _contextStore.MarkEmbeddedAsync(conversationId, attachments.ItemIds, userMessage.Id, CancellationToken.None).ConfigureAwait(false);
            }
            if (existing.Count == 0)
                await _store.RenameAsync(conversationId, MakeTitle(userText), ct).ConfigureAwait(false);

            await foreach (var chatEvent in RunTurnAsync(conversationId, mode, ct).ConfigureAwait(false))
                yield return chatEvent;
        }
        finally
        {
            End(conversationId);
        }
    }

    /// <summary>
    /// 不新增使用者訊息，用目前的歷史再要一次回應（重試、存好金鑰後重送、工具迴圈中途失敗後接著跑）。
    /// 最後一則必須是使用者訊息或工具結果。
    /// </summary>
    public async IAsyncEnumerable<ChatEvent> RetryAsync(
        string conversationId, [EnumeratorCancellation] CancellationToken ct = default, ChatMode mode = ChatMode.Workbench)
    {
        Begin(conversationId);
        try
        {
            await foreach (var chatEvent in RunTurnAsync(conversationId, mode, ct).ConfigureAwait(false))
                yield return chatEvent;
        }
        finally
        {
            End(conversationId);
        }
    }

    /// <summary>
    /// 重新產生：移除最後一輪的助理與工具訊息（從尾端一路移除到最後一則使用者訊息，包含「已停止」的），
    /// 用同一段歷史再要一次回應，新回應取代舊的。只有從尾端移除，前面的訊息沒有任何改變（agent.md 第 7 節）。
    /// 最後一則是使用者訊息時丟 <see cref="InvalidOperationException"/>。
    /// </summary>
    public async IAsyncEnumerable<ChatEvent> RegenerateAsync(
        string conversationId, [EnumeratorCancellation] CancellationToken ct = default, ChatMode mode = ChatMode.Workbench)
    {
        Begin(conversationId);
        try
        {
            var history = await _store.GetMessagesAsync(conversationId, ct).ConfigureAwait(false);
            if (history.Count == 0 || history[^1].Role == ChatRole.User)
                throw new InvalidOperationException("最後一則必須是助理訊息才能重新產生。");

            // 沒有可用的模型時，不要先把舊的回答刪掉
            if (ResolveModel() is null)
            {
                yield return new Failed(new ChatFailure(ChatFailureKind.ModelNotConfigured, NoModelMessage, Retryable: false));
                yield break;
            }

            var toRemove = history.Reverse().TakeWhile(m => m.Role != ChatRole.User).Count();
            for (var i = 0; i < toRemove; i++)
                await _store.RemoveLastMessageAsync(conversationId, CancellationToken.None).ConfigureAwait(false);

            await foreach (var chatEvent in RunTurnAsync(conversationId, mode, ct).ConfigureAwait(false))
                yield return chatEvent;
        }
        finally
        {
            End(conversationId);
        }
    }

    private async IAsyncEnumerable<ChatEvent> RunTurnAsync(
        string conversationId, ChatMode mode, [EnumeratorCancellation] CancellationToken ct)
    {
        var model = ResolveModel();
        if (model is null)
        {
            yield return new Failed(new ChatFailure(ChatFailureKind.ModelNotConfigured, NoModelMessage, Retryable: false));
            yield break;
        }

        // 模型設定的「使用工具」關掉時，請求完全不帶工具定義，也不記快照。
        // 其他情況送的是這則對話的工具快照（M15 D1），不依模式過濾：切換模式改變工具清單會讓整個快取失效（M5 的 Q1），模式在執行時檢查
        var offered = model.Model.ToolsEnabled ? await ResolveToolSnapshotAsync(conversationId, ct).ConfigureAwait(false) : [];

        // 小精靈的輸出上限較低；thinking 參數不因模式改變（改了訊息快取會失效，M5 的 Q4）
        int? maxOutputTokens = mode == ChatMode.Sprite ? Math.Min(model.Model.MaxOutputTokens, _promptOptions.SpriteMaxOutputTokens) : null;

        var contextRetried = false;   // 超過上限後已經整理並重送過一次（成功拿到回應後重設）

        for (var iteration = 1; iteration <= _options.MaxIterations; iteration++)
        {
            // 每一輪重新取得對話目前的範圍（使用者加入的檔案與資料夾）。需要範圍的工具定義一律照送，不因為有沒有範圍而增減：
            // 工具清單變了，Anthropic 的 thinking 區塊（簽章綁定 tools）就失效、整個請求 400；沒有範圍時執行會回「沒有加入任何檔案或資料夾」
            var scope = _scopeFactory.Create(await _contextStore.GetAsync(conversationId, ct).ConfigureAwait(false));
            var memoryEnabled = _catalog.Current.Memory.Enabled;
            // 記憶工具也一律照送（同上：開關改變不能讓工具清單改變）；記憶關閉時執行會被擋下
            var context = new ToolContext(conversationId, mode, _time, scope);

            var history = await _store.GetMessagesAsync(conversationId, ct).ConfigureAwait(false);

            // ---- 上下文整理（M22）：估算超過上限的 80% 先清舊工具結果，仍超過 70% 再做 LLM 摘要。只影響送出的內容，存檔不變 ----
            var compaction = await _store.GetCompactionStateAsync(conversationId, ct).ConfigureAwait(false);
            if (model.Model.ContextWindow > 0)
            {
                var window = model.Model.ContextWindow;
                var overhead = ContextEstimator.EstimateText(await _store.GetSystemPromptAsync(conversationId, ct).ConfigureAwait(false))
                               + (offered.Count > 0 ? ContextEstimator.EstimateText(ToolSnapshotJson.Serialize(offered)) : 0);
                var estimate = ContextEstimator.Estimate(CompactionApplier.Apply(history, compaction), compaction, overhead);
                if (estimate > window * CompactAtRatio)
                {
                    var boundary = CompactionApplier.ToolResultsBoundary(history);
                    if (boundary > (compaction.ToolResultsUptoSeq ?? 0))
                    {
                        await foreach (var compactionEvent in CompactToolResultsAsync(conversationId, boundary, model, ct).ConfigureAwait(false))
                            yield return compactionEvent;
                        compaction = await _store.GetCompactionStateAsync(conversationId, ct).ConfigureAwait(false);
                        estimate = ContextEstimator.Estimate(CompactionApplier.Apply(history, compaction), compaction, overhead);
                    }

                    if (estimate > window * SummaryAtRatio)
                    {
                        var outcome = new CompactionOutcome();
                        await foreach (var compactionEvent in CompactSummaryAsync(conversationId, model, history, compaction, outcome, ct).ConfigureAwait(false))
                            yield return compactionEvent;
                        if (outcome.Completed)
                            compaction = await _store.GetCompactionStateAsync(conversationId, ct).ConfigureAwait(false);
                    }
                }
            }

            var requestMessages = BuildRequestMessages(CompactionApplier.Apply(history, compaction));
            if (requestMessages.Count == 0 || requestMessages[^1].Role is not (ChatRole.User or ChatRole.Tool))
                throw new InvalidOperationException("最後一則訊息必須是使用者訊息或工具結果才能要求回應。");

            // 每次呼叫供應商前重組：兩次請求之間設定或記憶可能被改了（同一輪模型剛存的記憶，下一次呼叫就帶上）
            var memory = await LoadMemoryAsync(ct).ConfigureAwait(false);
            var prompt = _composer.Compose(new PromptContext(mode, _catalog.Current.Persona, memory));

            // system 在一則對話內固定：第一次送出時記下來，之後都送它。記憶、人設、記憶開關在中途改變時，
            // 不改 system，而是把最新的完整內容附在這一則訊息後面（SystemPromptUpdate）——thinking 簽章綁定 system，改了會 400
            var system = await _store.GetSystemPromptAsync(conversationId, CancellationToken.None).ConfigureAwait(false);
            if (system is null && prompt.System is not null)
            {
                await _store.SetSystemPromptAsync(conversationId, prompt.System, CancellationToken.None).ConfigureAwait(false);
                system = prompt.System;
            }
            system ??= prompt.System;

            var turnSuffix = prompt.TurnSuffix;
            var earlier = requestMessages.Take(requestMessages.Count - 1).Select(m => m.TurnSuffix);
            var announced = SystemPromptUpdate.LastIn(earlier) ?? system;   // 模型目前看到的最新版本
            if (prompt.System is not null && prompt.System != announced)
                turnSuffix = turnSuffix is null ? SystemPromptUpdate.Wrap(prompt.System) : turnSuffix + "\n\n" + SystemPromptUpdate.Wrap(prompt.System);

            // 這一次的指示跟著這一則（請求的最後一則）存起來，之後每次請求都原樣帶上：歷史只往後加，thinking 簽章才不會失效。
            // 它還是最後一則，所以重試或重新產生時可以直接改成這次的內容
            var last = requestMessages[^1];
            if (last.TurnSuffix != turnSuffix)
            {
                await _store.SetTurnSuffixAsync(conversationId, last.Id, turnSuffix, CancellationToken.None).ConfigureAwait(false);
                requestMessages = [.. requestMessages.Take(requestMessages.Count - 1), last with { TurnSuffix = turnSuffix }];
            }

            var request = new ChatRequest(model, requestMessages, offered.Count > 0 ? offered : null, system, turnSuffix, maxOutputTokens);
            Completed? completed = null;
            var failed = false;

            Failed? tooLong = null;

            await foreach (var chatEvent in StreamOnceAsync(conversationId, request, model, ct).ConfigureAwait(false))
            {
                // 超過上限：先不轉發，下面整理後重送一次（還是失敗才回報）
                if (chatEvent is Failed { Failure.Kind: ChatFailureKind.ContextTooLong } overLimit && !contextRetried)
                {
                    tooLong = overLimit;
                    continue;
                }
                if (chatEvent is Completed c) completed = c;
                if (chatEvent is Failed) failed = true;
                // 整理後重送仍然超過上限：請使用者開新對話
                yield return chatEvent is Failed { Failure.Kind: ChatFailureKind.ContextTooLong } again
                    ? new Failed(again.Failure with { Message = ContextTooLongMessage })
                    : chatEvent;
            }

            if (tooLong is not null)
            {
                contextRetried = true;
                var outcome = new CompactionOutcome();
                var latest = await _store.GetCompactionStateAsync(conversationId, ct).ConfigureAwait(false);
                await foreach (var compactionEvent in CompactSummaryAsync(conversationId, model, history, latest, outcome, ct).ConfigureAwait(false))
                    yield return compactionEvent;
                if (outcome.Completed)
                {
                    iteration--;   // 不算一次迴圈，用整理後的歷史再送一次
                    continue;
                }
                yield return new Failed(tooLong.Failure with { Message = ContextTooLongMessage });
                yield break;
            }

            if (completed is not null)
                contextRetried = false;

            var uses = completed?.Message.Content.OfType<ToolUseBlock>().ToList() ?? [];
            if (failed || completed is null || uses.Count == 0 || completed.Reason != StopReason.ToolUse)
                yield break;

            // ---- 依序處理每個工具呼叫（Q4：不平行，每個各自走權限確認）----
            var results = new List<ToolResultBlock>();
            var stopped = false;
            foreach (var use in uses)
            {
                // 快照裡沒有的名稱：模型憑空叫的，照舊回「沒有這個工具」
                if (!offered.Any(d => d.Name == use.Name))
                {
                    var unknown = new ToolResultBlock(use.Id, $"沒有這個工具：{use.Name}", true, use.Name, null, "沒有這個工具", 0, ToolApproval.NotRequired);
                    yield return new ToolCallStarted(use.Id, use.Name, use.Name, ToolPermission.ReadOnly, null, use.InputJson);
                    yield return new ToolCallCompleted(use.Id, unknown);
                    results.Add(unknown);
                    continue;
                }

                // 在快照裡、但現在找不到（程式更新後拿掉了）：回錯誤，不執行（M15 D4）
                var tool = _toolCatalog.Find(use.Name);
                if (tool is null)
                {
                    var unavailable = new ToolResultBlock(use.Id, UnavailableMessage, true, use.Name, null, "無法使用", 0, ToolApproval.NotRequired);
                    yield return new ToolCallStarted(use.Id, use.Name, use.Name, ToolPermission.ReadOnly, null, use.InputJson);
                    yield return new ToolCallCompleted(use.Id, unavailable);
                    results.Add(unavailable);
                    continue;
                }

                var descriptor = tool.Descriptor;
                var preview = tool.Preview(use.InputJson, context);

                if (!stopped && descriptor.RequiresMemory && !memoryEnabled)
                {
                    // 定義有送（為了簽章），但使用者把長期記憶關掉了：不詢問權限、不執行
                    var memoryOff = new ToolResultBlock(use.Id, MemoryOffMessage, true, descriptor.DisplayName, preview.Target, "長期記憶已關閉", 0, ToolApproval.NotRequired);
                    yield return new ToolCallStarted(use.Id, descriptor.Name, descriptor.DisplayName, descriptor.Permission, preview.Target, use.InputJson);
                    yield return new ToolCallCompleted(use.Id, memoryOff);
                    results.Add(memoryOff);
                    continue;
                }

                if (!stopped && !descriptor.Modes.Allows(mode))
                {
                    // 定義有送（為了快取），但這個模式不能執行：不詢問權限、不執行
                    var wrongMode = new ToolResultBlock(use.Id, WrongModeMessage, true, descriptor.DisplayName, preview.Target, "目前模式不能用", 0, ToolApproval.NotRequired);
                    yield return new ToolCallStarted(use.Id, descriptor.Name, descriptor.DisplayName, descriptor.Permission, preview.Target, use.InputJson);
                    yield return new ToolCallCompleted(use.Id, wrongMode);
                    results.Add(wrongMode);
                    continue;
                }

                if (stopped)
                {
                    // 前一個呼叫被使用者停止：後面還沒開始的也補「已停止」，否則 tool_use 缺結果，下次送出會被 API 拒絕
                    results.Add(StoppedResult(use, descriptor, preview, ToolApproval.NotRequired));
                    continue;
                }

                yield return new ToolCallStarted(use.Id, descriptor.Name, descriptor.DisplayName, descriptor.Permission, preview.Target, use.InputJson);

                // ---- 網址來源（M17 D4）：沒出現過的網站與搜尋引擎一定詢問，不看「永遠允許」與「本次對話都允許」----
                var urlCheck = UrlCheck.Known;
                if (preview.Url is { } url)
                {
                    var provenance = UrlProvenance.From(history, results);
                    urlCheck = NetworkUrlPolicy.Check(url, provenance, origin => _memory.IsSiteApproved(conversationId, origin));
                }
                var mustAsk = urlCheck != UrlCheck.Known;

                // ---- 權限 ----
                var approval = ToolApproval.NotRequired;
                var denied = false;
                if (descriptor.Permission != ToolPermission.ReadOnly || mustAsk)
                {
                    if (!mustAsk && IsAlwaysAllowed(descriptor))
                    {
                        // 設定頁的「永遠允許」：不詢問，也不記在對話裡（設定立即生效，M15 D5）
                        approval = ToolApproval.AllowedAlways;
                    }
                    else if (!mustAsk && _memory.IsAllowed(conversationId, descriptor))
                    {
                        approval = ToolApproval.AllowedForConversation;
                    }
                    else
                    {
                        var approvalRequest = new ToolApprovalRequest(use.Id, descriptor.Name, descriptor.DisplayName, descriptor.Permission, preview, use.InputJson)
                        {
                            Warnings = mustAsk ? [NetworkUrlPolicy.Warning(urlCheck, preview.Url!)] : [],
                            AllowRemember = urlCheck != UrlCheck.SearchEngine,
                        };
                        yield return new ApprovalRequired(approvalRequest);

                        ApprovalDecision? decision = null;
                        try
                        {
                            decision = await _approvals.RequestAsync(approvalRequest, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            stopped = true;
                        }

                        if (stopped)
                        {
                            var cancelledResult = StoppedResult(use, descriptor, preview, ToolApproval.NotRequired);
                            results.Add(cancelledResult);
                            yield return new ToolCallCompleted(use.Id, cancelledResult);
                            continue;
                        }

                        switch (decision)
                        {
                            case ApprovalDecision.AllowForConversation when urlCheck == UrlCheck.SearchEngine:
                                // 搜尋引擎網址不能「本次對話都允許」：當成允許一次，什麼都不記
                                approval = ToolApproval.AllowedOnce;
                                break;
                            case ApprovalDecision.AllowForConversation:
                                _memory.Allow(conversationId, descriptor);   // Destructive 不會被記住；MCP 工具依名稱記住（M16 D13）
                                approval = descriptor.Permission == ToolPermission.Destructive ? ToolApproval.AllowedOnce : ToolApproval.AllowedForConversation;
                                if (urlCheck == UrlCheck.UnknownSite)
                                    _memory.ApproveSite(conversationId, WebOrigin.Of(preview.Url!));
                                break;
                            case ApprovalDecision.AllowOnce:
                                approval = ToolApproval.AllowedOnce;
                                if (urlCheck == UrlCheck.UnknownSite)
                                    _memory.ApproveSite(conversationId, WebOrigin.Of(preview.Url!));
                                break;
                            default:
                                approval = ToolApproval.Denied;
                                denied = true;
                                break;
                        }
                    }
                }

                // ---- 執行 ----
                ToolResultBlock block;
                if (denied)
                {
                    block = new ToolResultBlock(use.Id, DeniedMessage, true, descriptor.DisplayName, preview.Target, "你已拒絕", 0, ToolApproval.Denied);
                }
                else
                {
                    var started = _time.GetTimestamp();
                    ToolResult toolResult;

                    // 工具執行中轉發進度（M19 D12）。等待工具時不能在 try 裡 yield，所以迴圈放在 try 外面；
                    // 工具結束後不再發剩下的進度。取消、逾時、例外仍由下面的 ToolExecutor 與 catch 處理
                    var progress = new ToolProgressSink();
                    using var toolCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var running = ToolExecutor.RunAsync(tool, use.InputJson, context with { Progress = progress }, _options.MaxResultChars, _logger, toolCts.Token);
                    try
                    {
                        var ready = progress.WaitToReadAsync(ct).AsTask();
                        while (!running.IsCompleted && !ct.IsCancellationRequested)
                        {
                            await Task.WhenAny(running, ready).ConfigureAwait(false);
                            if (running.IsCompleted)
                                break;
                            while (progress.TryRead(out var text))
                            {
                                if (NormalizeProgress(text) is { } shown)
                                    yield return new ToolCallProgress(use.Id, shown);
                            }
                            ready = progress.WaitToReadAsync(ct).AsTask();
                        }
                    }
                    finally
                    {
                        progress.Complete();
                        // 迭代器在工具執行中被釋放（呼叫端放棄列舉）時，不讓工具繼續跑下去
                        if (!running.IsCompleted)
                            toolCts.Cancel();
                    }

                    try
                    {
                        toolResult = await running.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        stopped = true;
                        toolResult = ToolResult.Error(StoppedMessage, "已停止");
                    }

                    var images = await SaveToolImagesAsync(toolResult).ConfigureAwait(false);
                    block = new ToolResultBlock(use.Id, toolResult.Content, toolResult.IsError, descriptor.DisplayName,
                        preview.Target, toolResult.Summary, ToolExecutor.ElapsedMilliseconds(started, _time), approval, images);
                }

                results.Add(block);
                yield return new ToolCallCompleted(use.Id, block);
            }

            // 先存結果再決定要不要往下：每個 tool_use 都必須有結果，存檔不受取消影響
            await _store.AppendMessageAsync(conversationId,
                new ChatMessage(ChatMessage.NewId(), ChatRole.Tool, results, _time.GetUtcNow()), CancellationToken.None).ConfigureAwait(false);

            if (stopped)
                throw new OperationCanceledException(ct);
        }

        yield return new Failed(new ChatFailure(ChatFailureKind.ToolLoopLimit,
            $"工具呼叫已達到 {_options.MaxIterations} 次上限，先停下來了。已完成的步驟都保留，可以按「重試」接著做。", Retryable: true));
    }

    /// <summary>手動整理（M22）：直接做第二層（LLM 摘要），不看門檻。沒有東西可整理時不發任何事件。</summary>
    public async IAsyncEnumerable<ChatEvent> CompactAsync(string conversationId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Begin(conversationId);
        try
        {
            var model = ResolveModel();
            if (model is null)
            {
                yield return new CompactionFailed(CompactionKind.Summary, new ChatFailure(ChatFailureKind.ModelNotConfigured, NoModelMessage, Retryable: false));
                yield break;
            }

            var history = await _store.GetMessagesAsync(conversationId, ct).ConfigureAwait(false);
            var state = await _store.GetCompactionStateAsync(conversationId, ct).ConfigureAwait(false);
            await foreach (var compactionEvent in CompactSummaryAsync(conversationId, model, history, state, new CompactionOutcome(), ct).ConfigureAwait(false))
                yield return compactionEvent;
        }
        finally
        {
            End(conversationId);
        }
    }

    private sealed class CompactionOutcome
    {
        public bool Completed { get; set; }
    }

    /// <summary>第一層：不呼叫模型，只記下邊界。</summary>
    private async IAsyncEnumerable<ChatEvent> CompactToolResultsAsync(
        string conversationId, int boundary, ResolvedModel model, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new CompactionStarted(CompactionKind.ToolResults);
        await _store.AddCompactionAsync(conversationId,
            new CompactionRecord(CompactionRecord.NewId(), CompactionKind.ToolResults, boundary, null, model.Model.ModelId, _time.GetUtcNow()),
            CancellationToken.None).ConfigureAwait(false);
        yield return new CompactionCompleted(CompactionKind.ToolResults, boundary);
    }

    /// <summary>
    /// 第二層：用同一個模型（不帶工具、不要求 thinking、輸出上限 <see cref="SummaryMaxOutputTokens"/>）把邊界之前的內容整理成摘要並存檔。
    /// 邊界沒有比上次往前（沒有新的東西可整理）時什麼都不做。失敗不存任何東西，發 <see cref="CompactionFailed"/>。
    /// 摘要那次呼叫的用量以 <see cref="UsageEvent"/> 轉發，並存進整理紀錄與 <see cref="CompactionCompleted.Usage"/>。
    /// </summary>
    private async IAsyncEnumerable<ChatEvent> CompactSummaryAsync(
        string conversationId, ResolvedModel model, IReadOnlyList<ChatMessage> history, CompactionState state,
        CompactionOutcome outcome, [EnumeratorCancellation] CancellationToken ct)
    {
        var boundary = CompactionApplier.SummaryBoundary(history);
        if (boundary <= (state.SummaryUptoSeq ?? 0))
            yield break;

        yield return new CompactionStarted(CompactionKind.Summary);

        var input = CompactionApplier.BuildSummaryInput(history, state, boundary);
        var summaryModel = new ResolvedModel(model.Model with { ShowThinking = false }, model.Connection);
        var request = new ChatRequest(summaryModel, input, null, null, null, Math.Min(SummaryMaxOutputTokens, model.Model.MaxOutputTokens));

        Completed? completed = null;
        ChatFailure? failure = null;
        TokenUsage? lastUsage = null;
        await foreach (var chatEvent in _provider.StreamAsync(request, ct).ConfigureAwait(false))
        {
            switch (chatEvent)
            {
                case UsageEvent usage:
                    lastUsage = usage.Tokens;
                    yield return usage;
                    break;
                case Completed c:
                    completed = c;
                    break;
                case Failed f:
                    failure = DecorateFailure(f.Failure, model);
                    break;
                // 文字與思考不轉發：摘要不是一則助理回應，不能出現在畫面上
            }
        }

        var summary = completed is null
            ? ""
            : string.Concat(completed.Message.Content.OfType<TextBlock>().Select(t => t.Text)).Trim();
        if (failure is null && summary.Length == 0)
            failure = new ChatFailure(ChatFailureKind.Unknown, EmptySummaryMessage, Retryable: true);
        if (failure is not null)
        {
            _logger.LogWarning("對話整理失敗：{Kind}", failure.Kind);
            yield return new CompactionFailed(CompactionKind.Summary, failure);
            yield break;
        }

        var usageToStore = completed!.Message.Usage ?? lastUsage;
        await _store.AddCompactionAsync(conversationId,
            new CompactionRecord(CompactionRecord.NewId(), CompactionKind.Summary, boundary, summary, model.Model.ModelId, _time.GetUtcNow(), usageToStore),
            CancellationToken.None).ConfigureAwait(false);
        outcome.Completed = true;
        yield return new CompactionCompleted(CompactionKind.Summary, boundary, usageToStore);
    }

    /// <summary>
    /// 把工具回傳的圖片存進附件資料夾（M18 D12）：最多前 <see cref="BrowserLimits.MaxScreenshotImages"/> 張、超過
    /// <see cref="BrowserLimits.MaxScreenshotBytes"/> 的略過、存檔失敗的略過；沒有附件儲存時整批丟棄。沒有可記的圖片回 null。
    /// </summary>
    private async Task<IReadOnlyList<ImageBlock>?> SaveToolImagesAsync(ToolResult toolResult)
    {
        if (_attachments is null || toolResult.Images.Count == 0)
            return null;

        var saved = new List<ImageBlock>();
        foreach (var image in toolResult.Images.Take(BrowserLimits.MaxScreenshotImages))
        {
            if (image.Data.LongLength > BrowserLimits.MaxScreenshotBytes)
                continue;
            try
            {
                var fileName = await _attachments.SaveAsync(image.Data, image.MediaType, CancellationToken.None).ConfigureAwait(false);
                saved.Add(new ImageBlock(fileName, image.MediaType, image.DisplayName));
            }
            catch (Exception ex)
            {
                // 不記內容；少一張圖不該讓整個工具結果失敗
                _logger.LogWarning(ex, "工具結果的圖片存檔失敗，已略過這一張。");
            }
        }
        return saved.Count == 0 ? null : saved;
    }

    /// <summary>長期記憶（第 5 層）：記憶關閉或沒有記憶儲存時為 null；只取有效的記憶。</summary>
    private async Task<MemoryPromptContext?> LoadMemoryAsync(CancellationToken ct)
    {
        if (!_catalog.Current.Memory.Enabled || _memoryStore is null)
            return null;
        var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        return new MemoryPromptContext(await _memoryStore.ListActiveAsync(today, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// 兩種模式都用同一個預設模型（<see cref="AppSettings.WorkbenchModelId"/>）：小精靈不另外選模型，
    /// 設定檔裡舊的 <see cref="AppSettings.SpriteModelId"/> 不再使用。
    /// </summary>
    private ResolvedModel? ResolveModel() => _catalog.Resolve(_catalog.Current.WorkbenchModelId);

    private ChatFailure DecorateFailure(ChatFailure failure, ResolvedModel model) =>
        ChatFailureText.Decorate(failure, _network.HasUsableNetworkInterface(), ChatFailureText.IsLocalTarget(model.Connection.BaseUrl));

    /// <summary>
    /// 這則對話的工具定義：有快照就原樣使用；沒有就記下來。已經有助理訊息的舊對話補上「全部」工具（和過去送的一樣），
    /// 還沒有助理訊息的新對話補上「已啟用」的工具（M15 D1、D3）。
    /// </summary>
    private async Task<IReadOnlyList<ToolDefinition>> ResolveToolSnapshotAsync(string conversationId, CancellationToken ct)
    {
        var saved = ToolSnapshotJson.Deserialize(await _store.GetToolSnapshotAsync(conversationId, ct).ConfigureAwait(false));
        if (saved is not null)
            return saved;

        var history = await _store.GetMessagesAsync(conversationId, ct).ConfigureAwait(false);
        var tools = history.Any(m => m.Role == ChatRole.Assistant)
            ? _toolCatalog.GetAll()
            : await _toolCatalog.GetEnabledForNewConversationAsync(ct).ConfigureAwait(false);
        var definitions = tools.Select(ToolDefinition.From).ToList();
        await _store.SetToolSnapshotAsync(conversationId, ToolSnapshotJson.Serialize(definitions), CancellationToken.None).ConfigureAwait(false);
        return definitions;
    }

    /// <summary>設定頁的「永遠允許」：名單裡有，而且這個工具允許設定（Write／Network 且 <see cref="ToolDescriptor.CanAlwaysAllow"/>）。</summary>
    private bool IsAlwaysAllowed(ToolDescriptor descriptor) =>
        _catalog.Current.Tools.AlwaysAllow.Contains(descriptor.Name, StringComparer.Ordinal) && IToolCatalog.CanAlwaysAllow(descriptor);

    private static ToolResultBlock StoppedResult(ToolUseBlock use, ToolDescriptor descriptor, ToolPreview preview, ToolApproval approval) =>
        new(use.Id, StoppedMessage, true, descriptor.DisplayName, preview.Target, "已停止", 0, approval);

    /// <summary>進度文字：空白的略過（null），超過 <see cref="ProgressMaxChars"/> 字截斷。</summary>
    private static string? NormalizeProgress(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (text.Length <= ProgressMaxChars)
            return text;
        // 不把代理對（emoji）切成兩半
        var cut = char.IsHighSurrogate(text[ProgressMaxChars - 1]) ? ProgressMaxChars - 1 : ProgressMaxChars;
        return text[..cut];
    }

    /// <summary>
    /// 工具的進度通道（M19 D12）。不用 <see cref="Progress{T}"/>（它會抓同步內容、回到呼叫端的執行緒）；
    /// 寫入是有界佇列，滿了丟掉最舊的，工具不會因為畫面來不及而被卡住。
    /// </summary>
    private sealed class ToolProgressSink : IProgress<string>
    {
        private readonly Channel<string> _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        public void Report(string value) => _channel.Writer.TryWrite(value);

        public ValueTask<bool> WaitToReadAsync(CancellationToken ct) => _channel.Reader.WaitToReadAsync(ct);

        public bool TryRead(out string value) => _channel.Reader.TryRead(out value!);

        public void Complete() => _channel.Writer.TryComplete();
    }

    /// <summary>呼叫一次供應商：轉發事件、完成時存助理訊息、取消時把已收到的文字存成「已停止」。</summary>
    private async IAsyncEnumerable<ChatEvent> StreamOnceAsync(
        string conversationId, ChatRequest request, ResolvedModel model, [EnumeratorCancellation] CancellationToken ct)
    {
        var modelId = model.Model.ModelId;
        var receivedText = new StringBuilder();

        await using var enumerator = _provider.StreamAsync(request, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            bool hasNext;
            try
            {
                hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 已收到的文字存成「已停止」；未完成的思考內容沒有 signature、沒完成的工具呼叫不完整，都不保留。
                // 存檔不受取消影響。
                if (receivedText.Length > 0)
                {
                    var stopped = new ChatMessage(
                        ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock(receivedText.ToString())],
                        DateTimeOffset.UtcNow, MessageStatus.Stopped, modelId);
                    await _store.AppendMessageAsync(conversationId, stopped, CancellationToken.None).ConfigureAwait(false);
                }
                throw;
            }

            if (!hasNext)
                yield break;

            var chatEvent = enumerator.Current;
            switch (chatEvent)
            {
                case TextDelta delta:
                    receivedText.Append(delta.Text);
                    break;
                case Completed completed:
                    // 先存檔再通知，UI 收到 Completed 時資料庫已經是最新狀態
                    await _store.AppendMessageAsync(conversationId, completed.Message, CancellationToken.None).ConfigureAwait(false);
                    break;
                // Failed：不存（Q15）；失敗文字的補充（離線、RetryAfter）在這裡一處套用，錯誤列與小精靈氣泡共用
            }

            yield return chatEvent is Failed failed ? new Failed(DecorateFailure(failed.Failure, model)) : chatEvent;
        }
    }

    /// <summary>
    /// 把存檔的歷史轉成要送出的訊息：只送 Complete／Stopped、略過沒有內容的訊息、
    /// 連續的使用者（或助理）訊息合併成一則（API 要求交替）。只影響送出的請求，不改存檔。
    /// 工具歷史保持成對：每個 <see cref="ToolUseBlock"/> 後面緊接一則工具結果訊息，缺結果的（例如程式當掉）
    /// 補一個錯誤結果，沒有對應呼叫的孤兒結果丟掉——否則兩家 API 都會回 400。
    /// </summary>
    public static IReadOnlyList<ChatMessage> BuildRequestMessages(IReadOnlyList<ChatMessage> history)
    {
        var messages = history
            .Where(m => m.Status is MessageStatus.Complete or MessageStatus.Stopped && HasContent(m))
            .ToList();
        var result = new List<ChatMessage>();

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];

            // 工具結果只在「前面的助理訊息有 tool_use」時有意義，會跟著那則助理訊息一起處理；單獨出現的是孤兒
            if (message.Role == ChatRole.Tool)
                continue;

            AddMerged(result, message);

            var uses = message.Role == ChatRole.Assistant ? message.Content.OfType<ToolUseBlock>().ToList() : [];
            if (uses.Count == 0)
                continue;

            var found = new List<ToolResultBlock>();
            var next = i + 1;
            while (next < messages.Count && messages[next].Role == ChatRole.Tool)
            {
                found.AddRange(messages[next].Content.OfType<ToolResultBlock>());
                next++;
            }

            var ordered = uses
                .Select(use => found.FirstOrDefault(r => r.ToolUseId == use.Id)
                               ?? new ToolResultBlock(use.Id, InterruptedMessage, true, use.Name, null, "沒有結果", 0, ToolApproval.NotRequired))
                .Cast<ContentBlock>()
                .ToList();
            var source = i + 1 < messages.Count && messages[i + 1].Role == ChatRole.Tool ? messages[i + 1] : null;
            result.Add(new ChatMessage(source?.Id ?? ChatMessage.NewId(), ChatRole.Tool, ordered, source?.CreatedAt ?? message.CreatedAt, TurnSuffix: source?.TurnSuffix));
            i = next - 1;
        }

        // API 要求第一則是使用者訊息
        while (result.Count > 0 && result[0].Role != ChatRole.User)
            result.RemoveAt(0);

        return result;
    }

    private static void AddMerged(List<ChatMessage> result, ChatMessage message)
    {
        if (result.Count > 0 && result[^1].Role == message.Role && message.Role != ChatRole.Tool)
        {
            var previous = result[^1];
            // 合併後的指示以最後一則為準：中間的使用者訊息之間沒有（存檔的）助理訊息，沒有 thinking 區塊綁在它們後面
            result[^1] = previous with { Content = [.. previous.Content, .. message.Content], TurnSuffix = message.TurnSuffix };
        }
        else
        {
            result.Add(message);
        }
    }

    public static string MakeTitle(string userText)
    {
        var singleLine = string.Join(' ', userText.Split((char[])['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        var elements = System.Globalization.StringInfo.ParseCombiningCharacters(singleLine);
        // 以字形叢集為單位截斷，避免把 emoji 切壞
        return elements.Length <= TitleMaxLength
            ? singleLine
            : singleLine[..elements[TitleMaxLength]] + "…";
    }

    private static bool HasContent(ChatMessage message) => message.Content.Any(block => block switch
    {
        TextBlock t => t.Text.Length > 0,
        _ => true,
    });

    private void Begin(string conversationId)
    {
        if (!_active.TryAdd(conversationId, 0))
            throw new InvalidOperationException("這則對話已經有回應在進行中。");
    }

    private void End(string conversationId) => _active.TryRemove(conversationId, out _);
}
