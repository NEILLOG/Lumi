using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Lumi.Core.Context;
using Lumi.Core.Memory;
using Lumi.Core.Models;
using Lumi.Core.Network;
using Lumi.Core.Prompts;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Chat;

/// <summary>
/// 對話流程：存使用者訊息 → 組請求 → 呼叫 <see cref="IChatProvider"/> → 轉發事件 → 存助理訊息；
/// 助理要呼叫工具時進入工具迴圈：確認權限、執行、存結果、再次送出，直到不再呼叫工具或達到次數上限。
/// 事件與 <see cref="IChatProvider"/> 的約定相同，另外一輪可以有多個 <see cref="Completed"/>（每次供應商呼叫一個）；
/// 取消時丟 <see cref="OperationCanceledException"/>。
/// </summary>
public sealed class ConversationService
{
    public const int TitleMaxLength = 30;

    private const string NoModelMessage = "還沒有可用的模型，請先到設定加入連線與模型。";
    private const string InterruptedMessage = "對話被中斷，沒有取得這個工具的結果。";
    private const string StoppedMessage = "使用者已停止。";
    private const string DeniedMessage = "使用者拒絕執行";
    private const string WrongModeMessage = "這個工具在目前模式不能使用。";
    private const string MemoryOffMessage = "長期記憶已在設定裡關閉，不能新增、修改或刪除記憶。";

    private readonly IChatProvider _provider;
    private readonly IConversationStore _store;
    private readonly IModelCatalog _catalog;
    private readonly IToolRegistry _registry;
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
        IMemoryStore? memoryStore = null)
    {
        _provider = provider;
        _store = store;
        _catalog = catalog;
        _registry = registry;
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

        // 模型設定的「使用工具」關掉時，請求完全不帶工具定義。
        // 其他情況一律帶全部工具，不依模式過濾：切換模式改變工具清單會讓整個快取失效（M5 的 Q1），模式在執行時檢查
        var availableTools = model.Model.ToolsEnabled ? _registry.GetAll() : [];

        // 小精靈的輸出上限較低；thinking 參數不因模式改變（改了訊息快取會失效，M5 的 Q4）
        int? maxOutputTokens = mode == ChatMode.Sprite ? Math.Min(model.Model.MaxOutputTokens, _promptOptions.SpriteMaxOutputTokens) : null;

        for (var iteration = 1; iteration <= _options.MaxIterations; iteration++)
        {
            // 每一輪重新取得對話目前的範圍（使用者加入的檔案與資料夾）。需要範圍的工具定義一律照送，不因為有沒有範圍而增減：
            // 工具清單變了，Anthropic 的 thinking 區塊（簽章綁定 tools）就失效、整個請求 400；沒有範圍時執行會回「沒有加入任何檔案或資料夾」
            var scope = _scopeFactory.Create(await _contextStore.GetAsync(conversationId, ct).ConfigureAwait(false));
            var memoryEnabled = _catalog.Current.Memory.Enabled;
            // 記憶工具也一律照送（同上：開關改變不能讓工具清單改變）；記憶關閉時執行會被擋下
            var tools = availableTools.ToList();
            var context = new ToolContext(conversationId, mode, _time, scope);

            var history = await _store.GetMessagesAsync(conversationId, ct).ConfigureAwait(false);
            var requestMessages = BuildRequestMessages(history);
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

            var request = new ChatRequest(model, requestMessages, tools.Count > 0 ? tools.Select(ToolDefinition.From).ToList() : null, system, turnSuffix, maxOutputTokens);
            Completed? completed = null;
            var failed = false;

            await foreach (var chatEvent in StreamOnceAsync(conversationId, request, model, ct).ConfigureAwait(false))
            {
                if (chatEvent is Completed c) completed = c;
                if (chatEvent is Failed) failed = true;
                yield return chatEvent;
            }

            var uses = completed?.Message.Content.OfType<ToolUseBlock>().ToList() ?? [];
            if (failed || completed is null || uses.Count == 0 || completed.Reason != StopReason.ToolUse)
                yield break;

            // ---- 依序處理每個工具呼叫（Q4：不平行，每個各自走權限確認）----
            var results = new List<ToolResultBlock>();
            var stopped = false;
            foreach (var use in uses)
            {
                var tool = tools.FirstOrDefault(t => t.Descriptor.Name == use.Name);
                if (tool is null)
                {
                    var unknown = new ToolResultBlock(use.Id, $"沒有這個工具：{use.Name}", true, use.Name, null, "沒有這個工具", 0, ToolApproval.NotRequired);
                    yield return new ToolCallStarted(use.Id, use.Name, use.Name, ToolPermission.ReadOnly, null, use.InputJson);
                    yield return new ToolCallCompleted(use.Id, unknown);
                    results.Add(unknown);
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

                // ---- 權限 ----
                var approval = ToolApproval.NotRequired;
                var denied = false;
                if (descriptor.Permission != ToolPermission.ReadOnly)
                {
                    if (_memory.IsAllowed(conversationId, descriptor.Permission))
                    {
                        approval = ToolApproval.AllowedForConversation;
                    }
                    else
                    {
                        var approvalRequest = new ToolApprovalRequest(use.Id, descriptor.Name, descriptor.DisplayName, descriptor.Permission, preview, use.InputJson);
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
                            case ApprovalDecision.AllowForConversation:
                                _memory.Allow(conversationId, descriptor.Permission);   // Destructive 不會被記住
                                approval = descriptor.Permission == ToolPermission.Destructive ? ToolApproval.AllowedOnce : ToolApproval.AllowedForConversation;
                                break;
                            case ApprovalDecision.AllowOnce:
                                approval = ToolApproval.AllowedOnce;
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
                    try
                    {
                        toolResult = await ToolExecutor.RunAsync(tool, use.InputJson, context, _options.MaxResultChars, _logger, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        stopped = true;
                        toolResult = ToolResult.Error(StoppedMessage, "已停止");
                    }

                    block = new ToolResultBlock(use.Id, toolResult.Content, toolResult.IsError, descriptor.DisplayName,
                        preview.Target, toolResult.Summary, ToolExecutor.ElapsedMilliseconds(started, _time), approval);
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

    private static ToolResultBlock StoppedResult(ToolUseBlock use, ToolDescriptor descriptor, ToolPreview preview, ToolApproval approval) =>
        new(use.Id, StoppedMessage, true, descriptor.DisplayName, preview.Target, "已停止", 0, approval);

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
