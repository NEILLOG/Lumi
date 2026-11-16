using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.ContextNotes;

/// <summary>
/// 研究本的正式服務（計畫 4.2、4.5～4.7）：建立、整理（書記）、使用者操作、版本、外部修改、圖片。
/// 每本研究本有三樣東西（都放在 <see cref="_runtimes"/> 裡）：<c>RunGate</c>（同時只跑一次整理）、
/// <c>WriteLock</c>（只在讀目前版本與存新版本的短暫期間持有，呼叫書記時不持有）、排隊旗標。
/// </summary>
public sealed class ContextNoteService(
    IContextNoteStore store,
    IContextNoteFiles files,
    IContextNoteScribe scribe,
    IConversationStore conversations,
    IAttachmentStore attachments,
    TimeProvider time,
    ILogger<ContextNoteService>? logger = null) : IContextNoteService
{
    private const string FallbackTitle = "研究本";

    /// <summary>單獨一行的圖片：<c>![說明](目標)</c>。</summary>
    private static readonly Regex ImageLine = new(@"^\s*!\[([^\]]*)\]\(([^)]*)\)\s*$", RegexOptions.CultureInvariant);

    /// <summary>書記引用對話裡的圖：<c>圖N</c>。</summary>
    private static readonly Regex ImageToken = new(@"^圖[0-9]+$", RegexOptions.CultureInvariant);

    private readonly ConcurrentDictionary<string, NoteRuntime> _runtimes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NoteRuntime> _byConversation = new(StringComparer.Ordinal);
    private readonly ILogger<ContextNoteService> _logger = logger ?? NullLogger<ContextNoteService>.Instance;

    public event EventHandler<ContextNoteChangedArgs>? NoteChanged;
    public event EventHandler<ContextNoteStatus>? StatusChanged;

    private enum RunMode { Auto, Manual, First }

    /// <summary>每本研究本的執行期狀態（不存資料庫）。</summary>
    private sealed class NoteRuntime(string noteId)
    {
        private readonly object _gate = new();
        private bool? _autoOrganize;
        private ContextNoteStatus? _status;
        private int _pending, _firstPending, _editing;

        public string NoteId { get; } = noteId;

        /// <summary>同時只跑一次整理。</summary>
        public SemaphoreSlim RunGate { get; } = new(1, 1);

        /// <summary>只在讀目前版本與存新版本的短暫期間持有；呼叫書記的期間不持有。</summary>
        public SemaphoreSlim WriteLock { get; } = new(1, 1);

        /// <summary>自動整理的開關（還沒讀過時是 null）。</summary>
        public bool? AutoOrganize
        {
            get { lock (_gate) return _autoOrganize; }
            set { lock (_gate) _autoOrganize = value; }
        }

        public ContextNoteStatus? Status
        {
            get { lock (_gate) return _status; }
        }

        /// <returns>狀態真的變了回 true。</returns>
        public bool ChangeStatus(ContextNoteStatus status)
        {
            lock (_gate)
            {
                if (_status == status)
                    return false;
                _status = status;
                return true;
            }
        }

        /// <summary>使用者正在編輯（書記不跑）。</summary>
        public bool Editing
        {
            get => Volatile.Read(ref _editing) == 1;
            set => Volatile.Write(ref _editing, value ? 1 : 0);
        }

        public void RequestAuto() => Volatile.Write(ref _pending, 1);

        public void RequestFirst() => Volatile.Write(ref _firstPending, 1);

        public bool TakeAuto() => Interlocked.Exchange(ref _pending, 0) == 1;

        public bool TakeFirst() => Interlocked.Exchange(ref _firstPending, 0) == 1;

        public bool HasWork => Volatile.Read(ref _pending) == 1 || Volatile.Read(ref _firstPending) == 1;
    }

    // ---- 讀取 ----

    public async Task<ContextNoteView?> GetViewAsync(string conversationId, CancellationToken ct = default)
    {
        var note = await store.GetByConversationAsync(conversationId, ct).ConfigureAwait(false);
        if (note is null)
            return null;
        Remember(note);
        var current = await store.GetRevisionAsync(note.Id, note.CurrentVersion, ct).ConfigureAwait(false);
        if (current is null)
            return null;

        // 上一版：CurrentVersion - 1 找不到就往前找最近的一版
        ContextNoteRevision? previous = null;
        for (var version = note.CurrentVersion - 1; version >= 1 && previous is null; version--)
            previous = await store.GetRevisionAsync(note.Id, version, ct).ConfigureAwait(false);
        return new ContextNoteView(note, current, previous);
    }

    public ContextNoteStatus GetStatus(string noteId)
    {
        if (_runtimes.TryGetValue(noteId, out var rt) && rt.Status is { } known)
            return known;
        // 沒有讀過這本研究本（GetViewAsync 會記下自動整理的開關）時當成 Idle，不同步等資料庫
        var auto = rt?.AutoOrganize;
        return new ContextNoteStatus(noteId, auto is false ? ScribeState.Paused : ScribeState.Idle);
    }

    // ---- 建立 ----

    public async Task<ContextNote> StartAsync(string conversationId, string goal, ContextNoteStartMode mode, CancellationToken ct = default)
    {
        var trimmed = ValidateGoal(goal);
        if (await store.GetByConversationAsync(conversationId, ct).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("這則對話已經有研究本。");

        var now = time.GetUtcNow();
        var noteId = ContextNote.NewId();
        var content = ContextNoteDocument.Empty.ToMarkdown();

        // 空白開始：從目前最後一則訊息之後才整理；從對話開始：進度是 0，背景整理整則對話
        var progress = new ContextNoteProgress(0, null);
        if (mode == ContextNoteStartMode.Blank)
        {
            var messages = await conversations.GetMessagesAsync(conversationId, ct).ConfigureAwait(false);
            if (messages.Count > 0)
                progress = new ContextNoteProgress(messages[^1].Seq, messages[^1].Id);
        }

        var note = new ContextNote(
            noteId, conversationId, trimmed, AutoOrganize: true, CurrentVersion: 1,
            progress.UptoSeq, progress.UptoMessageId, GoalHint: null, now, now);
        var first = new ContextNoteRevision(
            ContextNoteRevision.NewId(), noteId, 1,
            mode == ContextNoteStartMode.Blank ? ContextNoteRevisionKind.StartBlank : ContextNoteRevisionKind.StartConversation,
            trimmed, content, [], TriggerExcerpt: null, RestoredCount: 0, now);
        await store.CreateAsync(note, first, ct).ConfigureAwait(false);
        Remember(note);
        await WriteFileAsync(conversationId, noteId, trimmed, content, ct).ConfigureAwait(false);
        NoteChanged?.Invoke(this, new ContextNoteChangedArgs(conversationId, noteId, 1));

        if (mode == ContextNoteStartMode.FromConversation)
            RequestFirst(Runtime(noteId));
        return note;
    }

    // ---- 整理（背景） ----

    public void NotifyTurnFinished(string conversationId)
    {
        try
        {
            // 已經知道這則對話的研究本：同步排隊（合併的旗標，不等待）
            if (_byConversation.TryGetValue(conversationId, out var known) && known.AutoOrganize is { } auto)
            {
                if (auto)
                    RequestAuto(known);
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var note = await store.GetByConversationAsync(conversationId).ConfigureAwait(false);
                    if (note is null || !note.AutoOrganize)
                        return;
                    Remember(note);
                    RequestAuto(Runtime(note.Id));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 只記錄錯誤，不寫對話與研究本的內容
                    _logger.LogWarning(ex, "通知研究本整理時發生錯誤。");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "通知研究本整理時發生錯誤。");
        }
    }

    public async Task OrganizeNowAsync(string noteId, CancellationToken ct = default)
    {
        var rt = Runtime(noteId);
        await rt.RunGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RunOnceAsync(rt, RunMode.Manual).ConfigureAwait(false);
        }
        finally
        {
            rt.RunGate.Release();
            if (rt.HasWork)
                StartWorkerIfIdle(rt);
        }
    }

    /// <summary>測試用：等到這本研究本的背景整理全部結束（沒有在跑、也沒有排隊的）。</summary>
    public async Task WhenIdleAsync(string noteId)
    {
        var rt = Runtime(noteId);
        while (rt.HasWork || rt.RunGate.CurrentCount == 0)
        {
            await rt.RunGate.WaitAsync().ConfigureAwait(false);
            rt.RunGate.Release();
            // 剛才放開前來的排隊請求，這裡接手（否則沒有人會去跑）
            if (rt.HasWork)
                StartWorkerIfIdle(rt);
            await Task.Yield();
        }
    }

    private void RequestAuto(NoteRuntime rt)
    {
        rt.RequestAuto();
        StartWorkerIfIdle(rt);
    }

    private void RequestFirst(NoteRuntime rt)
    {
        rt.RequestFirst();
        StartWorkerIfIdle(rt);
    }

    /// <summary>拿得到整理的閘門才開背景工作；拿不到就是已經有人在跑，它放開前會再檢查排隊的旗標。</summary>
    private void StartWorkerIfIdle(NoteRuntime rt)
    {
        if (!rt.RunGate.Wait(0))
            return;
        _ = Task.Run(() => WorkerAsync(rt));
    }

    private async Task WorkerAsync(NoteRuntime rt)
    {
        try
        {
            while (true)
            {
                if (rt.TakeFirst())
                    await RunOnceAsync(rt, RunMode.First).ConfigureAwait(false);
                else if (rt.TakeAuto())
                    await RunOnceAsync(rt, RunMode.Auto).ConfigureAwait(false);
                else
                    break;
            }
        }
        finally
        {
            rt.RunGate.Release();
        }

        // 放開之後才檢查：放開前來的排隊請求，這裡會接手
        if (rt.HasWork)
            StartWorkerIfIdle(rt);
    }

    private async Task RunOnceAsync(NoteRuntime rt, RunMode mode)
    {
        try
        {
            await RunCoreAsync(rt, mode).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 只記錄例外，不寫對話與研究本的內容
            _logger.LogWarning(ex, "整理研究本時發生錯誤。");
            SetStatus(rt, ScribeState.Failed);
        }
    }

    /// <summary>一次整理（計畫 4.6 的流程 1～10）。呼叫方已經拿到 <c>RunGate</c>。</summary>
    private async Task RunCoreAsync(NoteRuntime rt, RunMode mode)
    {
        // 1：先比對外部修改
        await SyncExternalAsync(rt.NoteId).ConfigureAwait(false);

        var note = await store.GetAsync(rt.NoteId).ConfigureAwait(false);
        if (note is null)
            return;
        Remember(note);
        if (mode == RunMode.Auto && !note.AutoOrganize)
        {
            FinishRun(rt, 0);
            return;
        }

        // 2：使用者正在編輯，等結束後會再排隊
        if (rt.Editing)
        {
            SetStatus(rt, ScribeState.WaitingForUser);
            return;
        }

        // 3：要整理哪些訊息
        var messages = await conversations.GetMessagesAsync(note.ConversationId).ConfigureAwait(false);
        IReadOnlyList<ChatMessage> pending;
        TurnText turn;
        if (mode == RunMode.First)
        {
            if (messages.Count == 0)
            {
                FinishRun(rt, 0);
                return;
            }
            pending = messages;
            turn = ContextNoteTurnFormatter.Format(messages, ContextNoteLimits.StartMaxChars);
        }
        else
        {
            pending = ContextNoteTurnFormatter.SelectPending(messages, note.ScribedUptoSeq, note.ScribedUptoMessageId);
            if (pending.Count == 0)
            {
                FinishRun(rt, 0);
                return;
            }
            turn = ContextNoteTurnFormatter.Format(pending);
        }
        var last = pending[^1];
        var progress = new ContextNoteProgress(last.Seq, last.Id);

        // 4：遮蔽受保護的內容，呼叫書記（呼叫期間不持有 WriteLock）
        var revision = await store.GetRevisionAsync(note.Id, note.CurrentVersion).ConfigureAwait(false);
        if (revision is null)
            return;
        var previousDoc = ContextNoteDocument.Parse(revision.Content);
        var masked = ContextNoteProtection.Mask(previousDoc, revision.ProtectedBlocks);
        var startVersion = note.CurrentVersion;

        SetStatus(rt, ScribeState.Running);
        var result = await scribe.RunAsync(new ScribeRequest(note.ConversationId, note.Goal, masked, turn)).ConfigureAwait(false);

        // 6：呼叫期間使用者編輯、改了目標或做了其他操作 → 丟掉結果，重新排隊
        if (await IsStaleAsync(rt, note.Id, startVersion).ConfigureAwait(false))
        {
            Requeue(rt, mode);
            return;
        }

        // 5：呼叫失敗、沒有模型：進度不前進
        if (result.NoModel)
        {
            SetStatus(rt, ScribeState.NoModel);
            return;
        }
        if (result.Failure is not null)
        {
            SetStatus(rt, ScribeState.Failed);
            return;
        }

        // 回答格式不對：和檢查不過一樣，不前進
        var output = result.Output;
        if (output is null)
        {
            SetStatus(rt, ScribeState.Rejected);
            return;
        }

        string? content = null;
        var restored = 0;
        if (!output.NoChange)
        {
            // 8：檢查（不過就丟掉這一版，進度不前進）
            var reject = ContextNoteValidator.Check(ContextNoteDocument.Parse(masked.Markdown), output.Note!, guardChanges: mode == RunMode.Auto);
            if (reject != NoteRejectReason.None)
            {
                _logger.LogInformation("書記的結果沒有通過檢查（{Reason}）。", reject);
                SetStatus(rt, ScribeState.Rejected);
                return;
            }

            // 9：收回受保護的內容、處理圖片、正規化
            var unmasked = ContextNoteProtection.Unmask(ContextNoteDocument.Parse(output.Note!), masked.Entries);
            var withImages = await ProcessImagesAsync(note.Id, unmasked.Document, previousDoc, turn.Images).ConfigureAwait(false);
            content = ContextNoteDocument.Parse(withImages.ToMarkdown()).ToMarkdown();
            restored = unmasked.RestoredCount;
        }

        // 內容和目前版本一樣，或 <no_change/>：不產生版本，只前進進度（與目標提示）
        var noNewVersion = content is null || content == revision.Content;
        var kind = mode switch
        {
            RunMode.First => ContextNoteRevisionKind.ScribeFirst,
            RunMode.Manual => ContextNoteRevisionKind.ScribeManual,
            _ => ContextNoteRevisionKind.ScribeAuto,
        };

        // 10：存檔（再確認一次沒有被改過）
        ContextNoteChangedArgs? changed = null;
        var stale = false;
        await rt.WriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var fresh = await store.GetAsync(note.Id).ConfigureAwait(false);
            stale = rt.Editing || fresh is null || fresh.CurrentVersion != startVersion;
            if (!stale && fresh is not null)
            {
                if (noNewVersion)
                {
                    await store.SetProgressAsync(note.Id, progress).ConfigureAwait(false);
                    if (output.GoalHint is not null)
                        await store.SetGoalHintAsync(note.Id, output.GoalHint).ConfigureAwait(false);
                }
                else
                {
                    var protectedBlocks = ContextNoteProtection.Prune(ContextNoteDocument.Parse(content!), revision.ProtectedBlocks);
                    var saved = await SaveVersionAsync(
                            fresh, kind, fresh.Goal, content!, protectedBlocks, turn.LastUserExcerpt, restored,
                            progress, output.GoalHint, setGoalHint: output.GoalHint is not null)
                        .ConfigureAwait(false);
                    changed = Args(fresh, saved);
                }
            }
        }
        finally
        {
            rt.WriteLock.Release();
        }

        if (stale)
        {
            Requeue(rt, mode);
            return;
        }
        if (changed is not null)
            NoteChanged?.Invoke(this, changed);
        FinishRun(rt, restored);
    }

    private async Task<bool> IsStaleAsync(NoteRuntime rt, string noteId, int startVersion)
    {
        await rt.WriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var fresh = await store.GetAsync(noteId).ConfigureAwait(false);
            return rt.Editing || fresh is null || fresh.CurrentVersion != startVersion;
        }
        finally
        {
            rt.WriteLock.Release();
        }
    }

    private void Requeue(NoteRuntime rt, RunMode mode)
    {
        if (mode == RunMode.First)
            RequestFirst(rt);
        else
            RequestAuto(rt);
    }

    /// <summary>圖片（4.7）：單獨一行的 <c>![說明](圖N)</c> 複製成 <c>images/檔名</c>；其他圖片路徑只留舊版已有的那一行。</summary>
    private async Task<ContextNoteDocument> ProcessImagesAsync(
        string noteId, ContextNoteDocument document, ContextNoteDocument previous, IReadOnlyList<TurnImage> images)
    {
        var previousLines = previous.AllBlocks()
            .SelectMany(x => x.Block.Text.Split('\n'))
            .Select(line => line.Trim())
            .ToHashSet(StringComparer.Ordinal);

        var copied = 0;
        var sections = new List<NoteSection>();
        foreach (var section in document.Sections)
        {
            var blocks = new List<NoteBlock>();
            foreach (var block in section.Blocks)
            {
                var lines = new List<string>();
                foreach (var line in block.Text.Split('\n'))
                {
                    var match = ImageLine.Match(line);
                    if (!match.Success)
                    {
                        lines.Add(line);
                        continue;
                    }

                    var alt = match.Groups[1].Value;
                    var target = match.Groups[2].Value.Trim();
                    if (ImageToken.IsMatch(target))
                    {
                        if (copied >= ContextNoteLimits.MaxImagesPerRun)
                            continue;
                        var path = await CopyImageAsync(noteId, target, images).ConfigureAwait(false);
                        if (path is null)
                            continue;
                        copied++;
                        lines.Add($"![{alt}]({path})");
                    }
                    else if (previousLines.Contains(line.Trim()))
                    {
                        lines.Add(line);
                    }
                    // 其他路徑（舊版沒有這一行）整行拿掉
                }

                var text = string.Join('\n', lines);
                if (text.Trim().Length > 0)
                    blocks.Add(new NoteBlock(text));
            }
            sections.Add(new NoteSection(section.Name, blocks));
        }
        return new ContextNoteDocument(sections);
    }

    /// <returns>存好的相對路徑；找不到、讀不到、存不了都回 null。</returns>
    private async Task<string?> CopyImageAsync(string noteId, string token, IReadOnlyList<TurnImage> images)
    {
        var image = images.FirstOrDefault(i => string.Equals(i.Token, token, StringComparison.Ordinal));
        if (image is null)
            return null;
        try
        {
            byte[]? data;
            if (image.AttachmentFileName is { } fileName)
                data = await attachments.ReadAsync(fileName).ConfigureAwait(false);
            else if (image.FilePath is { } path && File.Exists(path))
                data = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            else
                data = null;
            if (data is null)
                return null;
            return await files.SaveImageAsync(noteId, data, image.MediaType).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "複製研究本的圖片失敗。");
            return null;
        }
    }

    // ---- 使用者的操作 ----

    public void BeginUserEdit(string noteId)
    {
        var rt = Runtime(noteId);
        rt.Editing = true;
        SetStatus(rt, ScribeState.WaitingForUser);
    }

    public async Task EndUserEditAsync(string noteId, string? newContent, CancellationToken ct = default)
    {
        var rt = Runtime(noteId);
        ContextNote? loaded = null;
        await EditAsync(noteId, async () =>
        {
            var (note, revision) = await LoadCurrentAsync(noteId, ct).ConfigureAwait(false);
            loaded = note;
            // 內容超過上限就丟錯（還在編輯中，狀態不變）
            var content = newContent is null ? null : NormalizeUserContent(newContent);
            rt.Editing = false;
            if (content is null || content == revision.Content)
                return null;

            var protectedBlocks = ContextNoteProtection.AfterUserEdit(
                ContextNoteDocument.Parse(revision.Content), revision.ProtectedBlocks, ContextNoteDocument.Parse(content));
            var saved = await SaveVersionAsync(note, ContextNoteRevisionKind.UserEdit, note.Goal, content, protectedBlocks, null, 0, ct: ct)
                .ConfigureAwait(false);
            return Args(note, saved);
        }, ct).ConfigureAwait(false);

        // 叫醒背景工作：自動整理開著才整理（有新內容或還有沒整理的訊息，整理本身會判斷）
        if (loaded is null)
            return;
        Remember(loaded);
        if (loaded.AutoOrganize)
            RequestAuto(rt);
        else if (rt.Status?.State is ScribeState.WaitingForUser)
            FinishRun(rt, 0);
    }

    public Task SetGoalAsync(string noteId, string goal, CancellationToken ct = default)
    {
        var trimmed = ValidateGoal(goal);
        return EditAsync(noteId, async () =>
        {
            var (note, revision) = await LoadCurrentAsync(noteId, ct).ConfigureAwait(false);
            var saved = await SaveVersionAsync(
                    note, ContextNoteRevisionKind.UserGoal, trimmed, revision.Content, revision.ProtectedBlocks, null, 0,
                    setGoalHint: true, goalHint: null, ct: ct)
                .ConfigureAwait(false);
            return Args(note, saved);
        }, ct);
    }

    public async Task SetAutoOrganizeAsync(string noteId, bool autoOrganize, CancellationToken ct = default)
    {
        await store.SetAutoOrganizeAsync(noteId, autoOrganize, ct).ConfigureAwait(false);
        var rt = Runtime(noteId);
        rt.AutoOrganize = autoOrganize;
        if (await store.GetAsync(noteId, ct).ConfigureAwait(false) is { } note)
            _byConversation[note.ConversationId] = rt;

        // 只在沒有在跑、沒有等你編輯的時候換狀態
        var last = rt.Status;
        if (last is null || last.State is ScribeState.Idle or ScribeState.Paused)
            SetStatus(rt, autoOrganize ? ScribeState.Idle : ScribeState.Paused, last?.RestoredCount ?? 0);
    }

    public Task MarkDecidedAsync(string noteId, string blockText, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(blockText);
        return EditAsync(noteId, async () =>
        {
            var (note, revision) = await LoadCurrentAsync(noteId, ct).ConfigureAwait(false);
            var document = ContextNoteDocument.Parse(revision.Content);
            var source = document.Sections.FirstOrDefault(s => s.Blocks.Any(b => b.Text == blockText))
                         ?? throw new ArgumentException("找不到這個區塊。", nameof(blockText));

            // 不在「已決定」就搬到最後面；已經在就只加保護
            var moved = source.Blocks.First(b => b.Text == blockText);
            var sections = document.Sections.Select(s =>
            {
                if (s.Name == source.Name && s.Name != ContextNoteDocument.Decided)
                    return new NoteSection(s.Name, s.Blocks.Where(b => b.Text != blockText).ToArray());
                if (s.Name == ContextNoteDocument.Decided && source.Name != ContextNoteDocument.Decided)
                    return new NoteSection(s.Name, s.Blocks.Append(moved).ToArray());
                return s;
            });
            var content = ContextNoteDocument.Parse(new ContextNoteDocument(sections).ToMarkdown()).ToMarkdown();
            var protectedBlocks = ContextNoteProtection.Prune(
                ContextNoteDocument.Parse(content),
                revision.ProtectedBlocks.Append(blockText).Distinct(StringComparer.Ordinal).ToArray());

            var saved = await SaveVersionAsync(note, ContextNoteRevisionKind.UserAction, note.Goal, content, protectedBlocks, null, 0, ct: ct)
                .ConfigureAwait(false);
            return Args(note, saved);
        }, ct);
    }

    public Task SetProtectedAsync(string noteId, string blockText, bool isProtected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(blockText);
        return EditAsync(noteId, async () =>
        {
            var (note, revision) = await LoadCurrentAsync(noteId, ct).ConfigureAwait(false);
            var list = revision.ProtectedBlocks.Where(x => x != blockText).ToList();
            if (isProtected)
                list.Add(blockText);
            var protectedBlocks = ContextNoteProtection.Prune(ContextNoteDocument.Parse(revision.Content), list);

            // 內容不變也要存一版（畫面的操作紀錄）
            var saved = await SaveVersionAsync(note, ContextNoteRevisionKind.UserAction, note.Goal, revision.Content, protectedBlocks, null, 0, ct: ct)
                .ConfigureAwait(false);
            return Args(note, saved);
        }, ct);
    }

    public Task RevertAsync(string noteId, int version, CancellationToken ct = default) =>
        EditAsync(noteId, async () =>
        {
            var (note, _) = await LoadCurrentAsync(noteId, ct).ConfigureAwait(false);
            var target = await store.GetRevisionAsync(noteId, version, ct).ConfigureAwait(false)
                         ?? throw new ArgumentException("找不到這一版。", nameof(version));
            var saved = await SaveVersionAsync(
                    note, ContextNoteRevisionKind.Revert, target.Goal, target.Content, target.ProtectedBlocks,
                    version.ToString(CultureInfo.InvariantCulture), 0, ct: ct)
                .ConfigureAwait(false);
            return Args(note, saved);
        }, ct);

    public Task DismissGoalHintAsync(string noteId, CancellationToken ct = default) =>
        EditAsync(noteId, async () =>
        {
            var note = await store.GetAsync(noteId, ct).ConfigureAwait(false)
                       ?? throw new InvalidOperationException("找不到研究本。");
            await store.SetGoalHintAsync(noteId, null, ct).ConfigureAwait(false);
            return new ContextNoteChangedArgs(note.ConversationId, noteId, note.CurrentVersion);
        }, ct);

    public Task<string> AddImageAsync(string noteId, ReadOnlyMemory<byte> data, string mediaType, CancellationToken ct = default) =>
        files.SaveImageAsync(noteId, data, mediaType, ct);

    /// <summary>比對 note.md 與目前版本（4.2）：內容不同就存一版「外部修改」；檔案不見了就用目前版本重寫。標題行不比。</summary>
    public Task SyncExternalAsync(string noteId, CancellationToken ct = default) =>
        EditAsync(noteId, async () =>
        {
            var note = await store.GetAsync(noteId, ct).ConfigureAwait(false);
            if (note is null)
                return null;
            var revision = await store.GetRevisionAsync(noteId, note.CurrentVersion, ct).ConfigureAwait(false);
            if (revision is null)
                return null;

            var file = await files.ReadNoteAsync(noteId, ct).ConfigureAwait(false);
            if (file is null)
            {
                await WriteFileAsync(note.ConversationId, noteId, note.Goal, revision.Content, ct).ConfigureAwait(false);
                return null;
            }

            var (goal, content) = ContextNoteFileFormat.Parse(file, note.Goal);
            if (goal == note.Goal && content == revision.Content)
                return null;

            var protectedBlocks = ContextNoteProtection.AfterUserEdit(
                ContextNoteDocument.Parse(revision.Content), revision.ProtectedBlocks, ContextNoteDocument.Parse(content));
            var saved = await SaveVersionAsync(note, ContextNoteRevisionKind.External, goal, content, protectedBlocks, null, 0, ct: ct)
                .ConfigureAwait(false);
            return Args(note, saved);
        }, ct);

    // ---- 共用 ----

    private NoteRuntime Runtime(string noteId) => _runtimes.GetOrAdd(noteId, id => new NoteRuntime(id));

    /// <summary>記下研究本的開關與對話對應（讓 <see cref="NotifyTurnFinished"/> 能同步排隊）。</summary>
    private void Remember(ContextNote note)
    {
        var rt = Runtime(note.Id);
        rt.AutoOrganize = note.AutoOrganize;
        _byConversation[note.ConversationId] = rt;
    }

    private void SetStatus(NoteRuntime rt, ScribeState state, int restored = 0) =>
        SetStatus(rt, new ContextNoteStatus(rt.NoteId, state, restored));

    private void SetStatus(NoteRuntime rt, ContextNoteStatus status)
    {
        if (rt.ChangeStatus(status))
            StatusChanged?.Invoke(this, status);
    }

    /// <summary>整理結束：正在編輯就是等你編輯；否則關閉自動整理是 Paused，開著是 Idle。</summary>
    private void FinishRun(NoteRuntime rt, int restored)
    {
        var state = rt.Editing ? ScribeState.WaitingForUser
            : rt.AutoOrganize is false ? ScribeState.Paused
            : ScribeState.Idle;
        SetStatus(rt, state, restored);
    }

    /// <summary>在 <c>WriteLock</c> 裡執行一個操作，結束後（鎖已經放開）才發 <see cref="NoteChanged"/>。</summary>
    private async Task EditAsync(string noteId, Func<Task<ContextNoteChangedArgs?>> body, CancellationToken ct)
    {
        var rt = Runtime(noteId);
        ContextNoteChangedArgs? changed;
        await rt.WriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            changed = await body().ConfigureAwait(false);
        }
        finally
        {
            rt.WriteLock.Release();
        }
        if (changed is not null)
            NoteChanged?.Invoke(this, changed);
    }

    private async Task<(ContextNote Note, ContextNoteRevision Revision)> LoadCurrentAsync(string noteId, CancellationToken ct)
    {
        var note = await store.GetAsync(noteId, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("找不到研究本。");
        var revision = await store.GetRevisionAsync(noteId, note.CurrentVersion, ct).ConfigureAwait(false)
                       ?? throw new InvalidOperationException("研究本的目前版本不見了。");
        return (note, revision);
    }

    /// <summary>存一版並重寫 note.md。<paramref name="note"/> 必須是鎖裡讀到的最新值。</summary>
    private async Task<ContextNoteRevision> SaveVersionAsync(
        ContextNote note, ContextNoteRevisionKind kind, string goal, string content, IReadOnlyList<string> protectedBlocks,
        string? excerpt, int restored, ContextNoteProgress? progress = null, string? goalHint = null, bool setGoalHint = false,
        CancellationToken ct = default)
    {
        var revision = new ContextNoteRevision(
            ContextNoteRevision.NewId(), note.Id, note.CurrentVersion + 1, kind, goal, content, protectedBlocks,
            excerpt, restored, time.GetUtcNow());
        await store.AddRevisionAsync(revision, progress, setGoalHint, goalHint, ct).ConfigureAwait(false);
        await WriteFileAsync(note.ConversationId, note.Id, goal, content, ct).ConfigureAwait(false);
        return revision;
    }

    private async Task WriteFileAsync(string conversationId, string noteId, string goal, string content, CancellationToken ct = default)
    {
        var title = await TitleAsync(conversationId, ct).ConfigureAwait(false);
        await files.WriteNoteAsync(noteId, ContextNoteFileFormat.Compose(title, goal, content), ct).ConfigureAwait(false);
    }

    /// <summary>對話標題（note.md 第一行）；取不到用備用名稱。</summary>
    private async Task<string> TitleAsync(string conversationId, CancellationToken ct = default)
    {
        var title = (await conversations.GetConversationAsync(conversationId, ct).ConfigureAwait(false))?.Title;
        return string.IsNullOrWhiteSpace(title) ? FallbackTitle : title;
    }

    private static ContextNoteChangedArgs Args(ContextNote note, ContextNoteRevision revision) =>
        new(note.ConversationId, note.Id, revision.Version);

    private static string ValidateGoal(string? goal)
    {
        var trimmed = (goal ?? "").Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("目標不能是空的。", nameof(goal));
        if (trimmed.Length > ContextNoteLimits.GoalMaxLength)
            throw new ArgumentException($"目標不能超過 {ContextNoteLimits.GoalMaxLength} 字。", nameof(goal));
        return trimmed;
    }

    private static string NormalizeUserContent(string text)
    {
        if (text.Length > ContextNoteLimits.NoteMaxChars)
            throw new ArgumentException($"研究本不能超過 {ContextNoteLimits.NoteMaxChars} 字。", nameof(text));
        return ContextNoteDocument.Parse(text).ToMarkdown();
    }
}
