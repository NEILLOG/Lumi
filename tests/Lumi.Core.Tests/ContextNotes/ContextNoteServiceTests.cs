using System.Collections.Concurrent;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.ContextNotes;
using Lumi.Core.Tests.TestSupport;

namespace Lumi.Core.Tests.ContextNotes;

/// <summary>研究本服務：建立、整理、版本、進度、排隊（計畫 4.6、6.6 包 35E）。</summary>
public sealed class ContextNoteServiceTests
{
    [Fact]
    public async Task StartBlank_CreatesFirstVersion_AndProgressIsTheLastMessage()
    {
        var rig = new ContextNoteRig();
        var conversation = await rig.ConversationAsync();
        await rig.AddUserAsync(conversation, "問題一");
        await rig.AddAssistantAsync(conversation, "回答一");

        var note = await rig.StartAsync(conversation, mode: ContextNoteStartMode.Blank);
        await rig.IdleAsync(note.Id);

        Assert.Equal(2, note.ScribedUptoSeq);
        var first = await rig.RevisionAsync(note.Id, 1);
        Assert.Equal(ContextNoteRevisionKind.StartBlank, first.Kind);
        Assert.Equal(ContextNoteDocument.Empty.ToMarkdown(), first.Content);
        Assert.Empty(rig.Scribe.Requests);
        Assert.Contains("**目標**：測試目標", (await rig.Files.ReadNoteAsync(note.Id))!);
    }

    [Fact]
    public async Task StartFromConversation_ScribesTheWholeConversation_AsFirstVersion()
    {
        var rig = new ContextNoteRig { Scribe = { Respond = (_, _) => Task.FromResult(FakeScribe.Answer(ContextNoteRig.Doc(("問題拆解", "第一版整理")))) } };
        var conversation = await rig.ConversationAsync();
        await rig.AddUserAsync(conversation, "問題一");
        await rig.AddAssistantAsync(conversation, "回答一");
        var last = (await rig.Conversations.GetMessagesAsync(conversation)).Last();

        var note = await rig.StartAsync(conversation, mode: ContextNoteStartMode.FromConversation);
        await rig.IdleAsync(note.Id);

        var request = Assert.Single(rig.Scribe.Requests);
        Assert.Contains("問題一", request.Turn.Text);
        Assert.Contains("回答一", request.Turn.Text);
        var current = await rig.CurrentAsync(note.Id);
        Assert.Equal(2, current.Version);
        Assert.Equal(ContextNoteRevisionKind.ScribeFirst, current.Kind);
        Assert.Contains("第一版整理", current.Content);
        Assert.Equal(ContextNoteRevisionKind.StartConversation, (await rig.RevisionAsync(note.Id, 1)).Kind);
        var stored = await rig.NoteAsync(note.Id);
        Assert.Equal(last.Seq, stored.ScribedUptoSeq);
        Assert.Equal(last.Id, stored.ScribedUptoMessageId);
    }

    [Fact]
    public async Task Start_RejectsEmptyOrLongGoal_AndASecondNoteForTheSameConversation()
    {
        var rig = new ContextNoteRig();
        var conversation = await rig.ConversationAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => rig.Service.StartAsync(conversation, "   ", ContextNoteStartMode.Blank));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Service.StartAsync(conversation, new string('目', 201), ContextNoteStartMode.Blank));
        await rig.StartAsync(conversation);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service.StartAsync(conversation, "再一本", ContextNoteStartMode.Blank));
    }

    [Fact]
    public async Task EveryTurn_CreatesANewVersion_AndAdvancesProgress_SendingOnlyTheNewMessages()
    {
        var rig = new ContextNoteRig { Scribe = { Respond = (n, _) => Task.FromResult(FakeScribe.Answer(ContextNoteRig.Doc(("問題拆解", $"整理第{n}輪")))) } };
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);

        await rig.AddUserAsync(conversation, "問題一");
        await rig.AddAssistantAsync(conversation, "回答一");
        rig.Service.NotifyTurnFinished(conversation);
        await rig.IdleAsync(note.Id);

        var afterFirst = await rig.CurrentAsync(note.Id);
        Assert.Equal(2, afterFirst.Version);
        Assert.Equal(ContextNoteRevisionKind.ScribeAuto, afterFirst.Kind);
        Assert.Equal("問題一", afterFirst.TriggerExcerpt);
        Assert.Contains("整理第1輪", afterFirst.Content);

        await rig.AddUserAsync(conversation, "問題二");
        await rig.AddAssistantAsync(conversation, "回答二");
        rig.Service.NotifyTurnFinished(conversation);
        await rig.IdleAsync(note.Id);

        var afterSecond = await rig.CurrentAsync(note.Id);
        Assert.Equal(3, afterSecond.Version);
        Assert.Contains("整理第2輪", afterSecond.Content);
        var second = rig.Scribe.Requests[1];
        Assert.Contains("問題二", second.Turn.Text);
        Assert.DoesNotContain("問題一", second.Turn.Text);
        Assert.Equal((await rig.Conversations.GetMessagesAsync(conversation)).Last().Seq, (await rig.NoteAsync(note.Id)).ScribedUptoSeq);
    }

    [Fact]
    public async Task NoChange_CreatesNoVersion_ButAdvancesProgress()
    {
        var rig = new ContextNoteRig();
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);
        await rig.AddUserAsync(conversation, "問題一");
        await rig.AddAssistantAsync(conversation, "回答一");

        rig.Service.NotifyTurnFinished(conversation);
        await rig.IdleAsync(note.Id);

        Assert.Equal(1, (await rig.NoteAsync(note.Id)).CurrentVersion);
        Assert.Equal(2, (await rig.NoteAsync(note.Id)).ScribedUptoSeq);
        Assert.Equal(ScribeState.Idle, rig.Service.GetStatus(note.Id).State);
    }

    [Fact]
    public async Task SameContentAsCurrent_CreatesNoVersion_ButAdvancesProgress()
    {
        var rig = new ContextNoteRig { Scribe = { Respond = (_, _) => Task.FromResult(FakeScribe.Answer(ContextNoteDocument.Empty.ToMarkdown())) } };
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);
        await rig.AddUserAsync(conversation, "問題一");

        rig.Service.NotifyTurnFinished(conversation);
        await rig.IdleAsync(note.Id);

        Assert.Equal(1, (await rig.NoteAsync(note.Id)).CurrentVersion);
        Assert.Equal(1, (await rig.NoteAsync(note.Id)).ScribedUptoSeq);
    }

    [Fact]
    public async Task CheckFails_CreatesNoVersion_DoesNotAdvance_AndIsRejected()
    {
        var rig = new ContextNoteRig { Scribe = { Respond = (_, _) => Task.FromResult(FakeScribe.Answer("## 問題拆解\n只有一段")) } };
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);
        await rig.AddUserAsync(conversation, "問題一");

        rig.Service.NotifyTurnFinished(conversation);
        await rig.IdleAsync(note.Id);

        var stored = await rig.NoteAsync(note.Id);
        Assert.Equal(1, stored.CurrentVersion);
        Assert.Equal(0, stored.ScribedUptoSeq);
        Assert.Equal(ScribeState.Rejected, rig.Service.GetStatus(note.Id).State);
    }

    [Fact]
    public async Task AnswerWithoutNoteOrNoChange_IsRejected()
    {
        var rig = new ContextNoteRig { Scribe = { Respond = (_, _) => Task.FromResult(new ScribeResult(null, null)) } };
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);
        await rig.AddUserAsync(conversation, "問題一");

        rig.Service.NotifyTurnFinished(conversation);
        await rig.IdleAsync(note.Id);

        Assert.Equal(0, (await rig.NoteAsync(note.Id)).ScribedUptoSeq);
        Assert.Equal(ScribeState.Rejected, rig.Service.GetStatus(note.Id).State);
    }

    [Fact]
    public async Task ProviderFailure_DoesNotAdvance_AndTheNextRunSendsBothBatches()
    {
        var rig = new ContextNoteRig
        {
            Scribe = { Respond = (n, _) => Task.FromResult(n == 1 ? FakeScribe.Failure() : FakeScribe.NoChange()) },
        };
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);
        await rig.AddUserAsync(conversation, "第一批的問題");
        await rig.AddAssistantAsync(conversation, "第一批的回答");

        rig.Service.NotifyTurnFinished(conversation);
        await rig.IdleAsync(note.Id);

        Assert.Equal(ScribeState.Failed, rig.Service.GetStatus(note.Id).State);
        Assert.Equal(0, (await rig.NoteAsync(note.Id)).ScribedUptoSeq);

        await rig.AddUserAsync(conversation, "第二批的問題");
        rig.Service.NotifyTurnFinished(conversation);
        await rig.IdleAsync(note.Id);

        var retry = rig.Scribe.Requests[1];
        Assert.Contains("第一批的問題", retry.Turn.Text);
        Assert.Contains("第二批的問題", retry.Turn.Text);
        Assert.Equal(3, (await rig.NoteAsync(note.Id)).ScribedUptoSeq);
    }

    [Fact]
    public async Task NoModel_SetsStatusNoModel_AndDoesNotAdvance()
    {
        var rig = new ContextNoteRig { Scribe = { Respond = (_, _) => Task.FromResult(new ScribeResult(null, null, NoModel: true)) } };
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);
        await rig.AddUserAsync(conversation, "問題一");

        rig.Service.NotifyTurnFinished(conversation);
        await rig.IdleAsync(note.Id);

        Assert.Equal(ScribeState.NoModel, rig.Service.GetStatus(note.Id).State);
        Assert.Equal(0, (await rig.NoteAsync(note.Id)).ScribedUptoSeq);
    }

    [Fact]
    public async Task ProtectedBlock_DeletedByTheScribe_IsPutBackAndCounted()
    {
        var rig = new ContextNoteRig
        {
            Scribe = { Respond = (n, _) => Task.FromResult(n == 1
                ? FakeScribe.Answer(ContextNoteRig.Doc(("問題拆解", "第一段"), ("已決定", "決定A")))
                : FakeScribe.Answer(ContextNoteRig.Doc(("問題拆解", "第一段改寫")))) },
        };
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);
        await rig.AddUserAsync(conversation, "問題一");
        rig.Service.NotifyTurnFinished(conversation);
        await rig.IdleAsync(note.Id);

        await rig.Service.SetProtectedAsync(note.Id, "決定A", true);
        await rig.AddUserAsync(conversation, "問題二");
        rig.Service.NotifyTurnFinished(conversation);
        await rig.IdleAsync(note.Id);

        var request = rig.Scribe.Requests[1];
        Assert.Contains("[[保護:1]]", request.Masked.Markdown);
        Assert.DoesNotContain("決定A", request.Masked.Markdown);
        Assert.Equal("決定A", Assert.Single(request.Masked.Entries).Text);

        var current = await rig.CurrentAsync(note.Id);
        Assert.Equal(1, current.RestoredCount);
        Assert.Contains("決定A", current.ProtectedBlocks);
        Assert.Contains("決定A", current.Content);
        Assert.Equal(1, rig.Service.GetStatus(note.Id).RestoredCount);
    }

    [Fact]
    public async Task AutoOrganizeOff_NotifyDoesNotCallTheScribe()
    {
        var rig = new ContextNoteRig();
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);
        await rig.Service.SetAutoOrganizeAsync(note.Id, false);
        await rig.AddUserAsync(conversation, "問題一");

        rig.Service.NotifyTurnFinished(conversation);
        await rig.IdleAsync(note.Id);

        Assert.Empty(rig.Scribe.Requests);
        Assert.Equal(ScribeState.Paused, rig.Service.GetStatus(note.Id).State);
    }

    [Fact]
    public async Task OrganizeNow_RunsEvenWhenAutoOrganizeIsOff()
    {
        var rig = new ContextNoteRig { Scribe = { Respond = (_, _) => Task.FromResult(FakeScribe.Answer(ContextNoteRig.Doc(("問題拆解", "手動整理")))) } };
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);
        await rig.Service.SetAutoOrganizeAsync(note.Id, false);
        await rig.AddUserAsync(conversation, "問題一");

        await rig.Service.OrganizeNowAsync(note.Id).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(rig.Scribe.Requests);
        var current = await rig.CurrentAsync(note.Id);
        Assert.Equal(ContextNoteRevisionKind.ScribeManual, current.Kind);
        Assert.Contains("手動整理", current.Content);
        Assert.Equal(ScribeState.Paused, rig.Service.GetStatus(note.Id).State);
    }

    [Fact]
    public async Task ThreeNotifications_CallTheScribeAtMostTwice()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = new ContextNoteRig
        {
            Scribe = { Respond = async (n, _) => { if (n == 1) await gate.Task; return FakeScribe.NoChange(); } },
        };
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);
        await rig.AddUserAsync(conversation, "問題一");

        rig.Service.NotifyTurnFinished(conversation);
        rig.Service.NotifyTurnFinished(conversation);
        rig.Service.NotifyTurnFinished(conversation);
        gate.TrySetResult();
        await rig.IdleAsync(note.Id);

        Assert.InRange(rig.Scribe.Requests.Count, 1, 2);
    }

    [Fact]
    public async Task Regenerated_TailMessages_OrganizeTheNewRound()
    {
        var rig = new ContextNoteRig { Scribe = { Respond = (_, _) => Task.FromResult(FakeScribe.NoChange()) } };
        var conversation = await rig.ConversationAsync();
        await rig.AddUserAsync(conversation, "問題一");
        await rig.AddAssistantAsync(conversation, "舊回覆");
        var note = await rig.StartAsync(conversation, mode: ContextNoteStartMode.Blank);

        // 重新產生最後一則回覆：舊的被拿掉，接上新的回覆與下一輪
        await rig.Conversations.RemoveLastMessageAsync(conversation);
        await rig.AddAssistantAsync(conversation, "新回覆");
        await rig.AddUserAsync(conversation, "問題二");
        rig.Service.NotifyTurnFinished(conversation);
        await rig.IdleAsync(note.Id);

        var request = Assert.Single(rig.Scribe.Requests);
        Assert.Contains("新回覆", request.Turn.Text);
        Assert.Contains("問題二", request.Turn.Text);
        Assert.DoesNotContain("舊回覆", request.Turn.Text);
    }

    [Fact]
    public async Task GoalHint_IsStored_AndClearedBySetGoal()
    {
        var rig = new ContextNoteRig
        {
            Scribe = { Respond = (_, _) => Task.FromResult(FakeScribe.Answer(ContextNoteRig.Doc(("問題拆解", "一段")), "要不要把目標改成更具體的？")) },
        };
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);
        await rig.AddUserAsync(conversation, "問題一");

        rig.Service.NotifyTurnFinished(conversation);
        await rig.IdleAsync(note.Id);
        Assert.Equal("要不要把目標改成更具體的？", (await rig.NoteAsync(note.Id)).GoalHint);

        await rig.Service.SetGoalAsync(note.Id, "新的目標");
        var stored = await rig.NoteAsync(note.Id);
        Assert.Null(stored.GoalHint);
        Assert.Equal("新的目標", stored.Goal);
        Assert.Equal(ContextNoteRevisionKind.UserGoal, (await rig.CurrentAsync(note.Id)).Kind);
    }

    [Fact]
    public async Task DismissGoalHint_ClearsTheHint_AndAnnouncesTheChange()
    {
        var rig = new ContextNoteRig
        {
            Scribe = { Respond = (_, _) => Task.FromResult(FakeScribe.Answer(ContextNoteRig.Doc(("問題拆解", "一段")), "提示")) },
        };
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);
        await rig.AddUserAsync(conversation, "問題一");
        rig.Service.NotifyTurnFinished(conversation);
        await rig.IdleAsync(note.Id);
        var before = rig.Changes.Count;

        await rig.Service.DismissGoalHintAsync(note.Id);

        Assert.Null((await rig.NoteAsync(note.Id)).GoalHint);
        Assert.True(rig.Changes.Count > before);
    }

    [Fact]
    public async Task MoreThanTwoHundredVersions_KeepsTheFirstAndTheLatest()
    {
        var rig = new ContextNoteRig();
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);

        for (var i = 1; i <= 201; i++)
            await rig.Service.EndUserEditAsync(note.Id, ContextNoteRig.Doc(("問題拆解", $"第{i}段")));
        await rig.IdleAsync(note.Id);

        var revisions = await rig.Store.ListRevisionsAsync(note.Id);
        Assert.Equal(ContextNoteLimits.MaxRevisions, revisions.Count);
        Assert.NotNull(await rig.Store.GetRevisionAsync(note.Id, 1));
        Assert.Equal(202, revisions[0].Version);
    }

    [Fact]
    public async Task GetView_ReturnsTheCurrentAndThePreviousVersion()
    {
        var rig = new ContextNoteRig();
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);
        await rig.Service.EndUserEditAsync(note.Id, ContextNoteRig.Doc(("問題拆解", "甲")));
        await rig.Service.EndUserEditAsync(note.Id, ContextNoteRig.Doc(("問題拆解", "乙")));
        await rig.IdleAsync(note.Id);

        var view = await rig.Service.GetViewAsync(conversation);

        Assert.NotNull(view);
        Assert.Equal(3, view.Current.Version);
        Assert.Equal(2, view.Previous!.Version);
        Assert.Null(await rig.Service.GetViewAsync(await rig.ConversationAsync()));
    }

    [Fact]
    public async Task StatusChanged_IsRaisedWhenTheStateChanges()
    {
        var rig = new ContextNoteRig { Scribe = { Respond = (_, _) => Task.FromResult(FakeScribe.Failure()) } };
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);
        await rig.AddUserAsync(conversation, "問題一");

        rig.Service.NotifyTurnFinished(conversation);
        await rig.IdleAsync(note.Id);

        var states = rig.Statuses.Select(s => s.State).ToList();
        Assert.Contains(ScribeState.Running, states);
        Assert.Equal(ScribeState.Failed, states[^1]);
    }
}

/// <summary>測試用的書記：<see cref="Respond"/> 依第幾次呼叫回答；沒有設定就回 <c>&lt;no_change/&gt;</c>。每次收到的請求都記下來。</summary>
internal sealed class FakeScribe : IContextNoteScribe
{
    private readonly object _lock = new();

    public List<ScribeRequest> Requests { get; } = [];

    /// <summary>第 n 次呼叫（從 1 起）的回答。</summary>
    public Func<int, ScribeRequest, Task<ScribeResult>>? Respond { get; set; }

    public Task<ScribeResult> RunAsync(ScribeRequest request, CancellationToken ct = default)
    {
        int n;
        lock (_lock)
        {
            Requests.Add(request);
            n = Requests.Count;
        }
        return Respond is null ? Task.FromResult(NoChange()) : Respond(n, request);
    }

    public static ScribeResult NoChange() => new(new ScribeOutput(true, null, null), null);

    public static ScribeResult Answer(string markdown, string? hint = null) => new(new ScribeOutput(false, markdown, hint), null);

    public static ScribeResult Failure() => new(null, new ChatFailure(ChatFailureKind.Network, "網路錯誤", Retryable: true));
}

/// <summary>測試用的時間：固定，可以改。</summary>
internal sealed class FixedClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>測試用的附件儲存：檔名對應位元組。</summary>
internal sealed class FakeAttachments : IAttachmentStore
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public Task<string> SaveAsync(ReadOnlyMemory<byte> data, string mediaType, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<byte[]?> ReadAsync(string fileName, CancellationToken ct = default) =>
        Task.FromResult(Files.TryGetValue(fileName, out var bytes) ? bytes : null);

    public Task<int> DeleteUnreferencedAsync(IReadOnlySet<string> referencedFileNames, CancellationToken ct = default) => Task.FromResult(0);
}

/// <summary>研究本服務的測試環境：對話、研究本儲存、檔案、書記、附件，全部用記憶體版本。</summary>
internal sealed class ContextNoteRig
{
    public InMemoryConversationStore Conversations { get; } = new();
    public InMemoryContextNoteStore Store { get; } = new();
    public InMemoryContextNoteFiles Files { get; } = new();
    public FakeScribe Scribe { get; } = new();
    public FakeAttachments Attachments { get; } = new();
    public FixedClock Clock { get; } = new();
    public ConcurrentQueue<ContextNoteChangedArgs> Changes { get; } = new();
    public ConcurrentQueue<ContextNoteStatus> Statuses { get; } = new();
    public ContextNoteService Service { get; }

    public ContextNoteRig()
    {
        Service = new ContextNoteService(Store, Files, Scribe, Conversations, Attachments, Clock);
        Service.NoteChanged += (_, e) => Changes.Enqueue(e);
        Service.StatusChanged += (_, s) => Statuses.Enqueue(s);
    }

    /// <summary>用一組區段與文字組出完整的研究本內容（7 個區段，照順序）。</summary>
    public static string Doc(params (string Section, string Text)[] blocks) =>
        new ContextNoteDocument(ContextNoteDocument.SectionNames.Select(name =>
            new NoteSection(name, blocks.Where(b => b.Section == name).Select(b => new NoteBlock(b.Text)).ToArray()))).ToMarkdown();

    public async Task<string> ConversationAsync() => (await Conversations.CreateAsync("測試對話")).Id;

    public Task AddUserAsync(string conversationId, string text, params ContentBlock[] extra) =>
        Conversations.AppendMessageAsync(conversationId, new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock(text), .. extra], DateTimeOffset.UtcNow));

    public Task AddAssistantAsync(string conversationId, string text) =>
        Conversations.AppendMessageAsync(conversationId, new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock(text)], DateTimeOffset.UtcNow));

    public Task<ContextNote> StartAsync(string conversationId, string goal = "測試目標", ContextNoteStartMode mode = ContextNoteStartMode.Blank) =>
        Service.StartAsync(conversationId, goal, mode);

    public async Task<ContextNote> NoteAsync(string noteId) => (await Store.GetAsync(noteId))!;

    public async Task<ContextNoteRevision> RevisionAsync(string noteId, int version) => (await Store.GetRevisionAsync(noteId, version))!;

    public async Task<ContextNoteRevision> CurrentAsync(string noteId) => await RevisionAsync(noteId, (await NoteAsync(noteId)).CurrentVersion);

    /// <summary>等背景整理全部結束（有逾時保護）。</summary>
    public Task IdleAsync(string noteId) => Service.WhenIdleAsync(noteId).WaitAsync(TimeSpan.FromSeconds(10));
}
