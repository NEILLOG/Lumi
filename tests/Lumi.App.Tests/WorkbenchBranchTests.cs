using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Branches;
using Lumi.Core.Chat;
using Lumi.Core.ContextNotes;
using Lumi.Core.Models;
using Lumi.Core.Usage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

/// <summary>
/// M36 36F：工作台的支線（開支線、問這段、帶回、放棄、回到上一層、上一層被鎖住、輸入框、研究本跟著主線、用量算在主線）。
/// 支線服務用 <see cref="FakeBranchService"/>，狀態用 <see cref="FakeBranchService.States"/> 設定。
/// </summary>
public class WorkbenchBranchTests
{
    private sealed record Rig(WorkbenchViewModel Vm, FakeChatSession Session, InMemoryConversationStore Store, string MainId,
        FakeBranchService Branches, FakeBranchMergePrompt Prompt, FakeConfirmService Confirm, FakeContextNoteService Notes,
        InMemoryContextNoteStore NoteStore, FakeContextNoteWindowService Window);

    /// <summary>建一個工作台（主線是 <see cref="Rig.MainId"/>，工作台開在 <paramref name="conversationId"/>，沒有就是主線）。</summary>
    private static async Task<Rig> CreateAsync(string? conversationId = null, InMemoryConversationStore? store = null, SpriteConversationService? sprite = null,
        bool withNote = true, InMemoryUsageStore? usage = null)
    {
        store ??= new InMemoryConversationStore();
        var main = await store.CreateAsync("主線");
        var session = new FakeChatSession { ConversationId = conversationId ?? main.Id };
        var branches = new FakeBranchService();
        var prompt = new FakeBranchMergePrompt();
        var confirm = new FakeConfirmService();
        var notes = new FakeContextNoteService();
        var noteStore = new InMemoryContextNoteStore();
        var window = new FakeContextNoteWindowService();
        var catalog = new FakeModelCatalog();
        var time = new FakeTimeProvider();
        var monitor = usage is null ? null : new BudgetMonitor(usage, store, catalog, time);
        var vm = new WorkbenchViewModel(null!, store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
            new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), time,
            NullLogger<WorkbenchViewModel>.Instance, session: session, attachments: new FakeAttachmentStore(),
            deletion: HistoryTestSupport.Deletion(store), confirm: confirm, spriteConversation: sprite,
            contextNotes: notes, contextNoteStore: noteStore, contextNoteWindow: window, contextNoteStart: new FakeContextNoteStartPrompt(),
            branches: branches, branchStore: new InMemoryBranchStore(), mergePrompt: prompt, usageStore: usage, budgetMonitor: monitor);
        if (withNote)
            await notes.StartAsync(main.Id, "既有的目標", ContextNoteStartMode.Blank);
        return new Rig(vm, session, store, main.Id, branches, prompt, confirm, notes, noteStore, window);
    }

    private static Task<ChatMessage> AddAsync(InMemoryConversationStore store, string conversationId, ChatRole role, string text, TokenUsage? usage = null)
    {
        var message = new ChatMessage(ChatMessage.NewId(), role, [new TextBlock(text)], DateTimeOffset.UtcNow, Usage: usage);
        return store.AppendMessageAsync(conversationId, message).ContinueWith(_ => message);
    }

    private static BranchPathNode Node(string id, string title, int depth = 0, BranchStatus? status = null) => new(id, title, depth, status);

    private static Branch Child(string id, string parentId, string rootId, int depth, string forkMessageId, BranchStatus status = BranchStatus.Active,
        string? summary = null) =>
        new(id, parentId, rootId, depth, forkMessageId, "分岔的摘錄", DateTimeOffset.UnixEpoch, null, status, summary, null,
            DateTimeOffset.UnixEpoch, status == BranchStatus.Active ? null : DateTimeOffset.UnixEpoch);

    /// <summary>把 <paramref name="branch"/> 設成第一層（或第二層）支線的狀態：路徑是主線到它自己。</summary>
    private static void SetBranchState(FakeBranchService branches, string mainId, string mainTitle, Conversation branch, string forkMessageId,
        BranchStatus status = BranchStatus.Active, Conversation? firstLevel = null)
    {
        var path = new List<BranchPathNode> { Node(mainId, mainTitle) };
        var parentId = mainId;
        var depth = 1;
        if (firstLevel is not null)
        {
            path.Add(Node(firstLevel.Id, firstLevel.Title, 1, BranchStatus.Active));
            parentId = firstLevel.Id;
            depth = 2;
        }
        path.Add(Node(branch.Id, branch.Title, depth, status));
        var current = Child(branch.Id, parentId, mainId, depth, forkMessageId, status);
        branches.States[branch.Id] = new BranchState(path, current, [], null);
    }

    // ---- 開支線與問這段 ----

    [Fact]
    public async Task Start_opens_branch_and_raises_entered()
    {
        var rig = await CreateAsync();
        var reply = await AddAsync(rig.Store, rig.MainId, ChatRole.Assistant, "回答");
        await rig.Vm.InitializeAsync();
        var branch = await rig.Store.CreateAsync("量化是什麼", ConversationKind.Branch);
        rig.Branches.StartResult = branch;
        SetBranchState(rig.Branches, rig.MainId, "主線", branch, reply.Id);
        var entered = 0;
        rig.Vm.BranchEntered += (_, _) => entered++;
        var message = Assert.Single(rig.Vm.Messages, m => m.IsAssistant);
        Assert.True(message.CanBranch);

        await rig.Vm.StartBranchCommand.ExecuteAsync(message);

        Assert.Contains($"Start:{rig.MainId}:{reply.Id}:null", rig.Branches.Calls);
        Assert.Equal(branch.Id, rig.Session.ConversationId);
        Assert.Equal(1, entered);
        Assert.True(rig.Vm.IsInBranch);
        Assert.True(rig.Vm.BranchBar.Show);
    }

    [Fact]
    public async Task Ask_prefills_question_draft()
    {
        var rig = await CreateAsync();
        var reply = await AddAsync(rig.Store, rig.MainId, ChatRole.Assistant, "回答");
        await rig.Vm.InitializeAsync();
        var branch = await rig.Store.CreateAsync("問這段", ConversationKind.Branch);
        rig.Branches.StartResult = branch;
        SetBranchState(rig.Branches, rig.MainId, "主線", branch, reply.Id);
        var message = Assert.Single(rig.Vm.Messages, m => m.IsAssistant);
        var quote = "一段很長的引用文字，超過四十個字的部分應該被截斷，用省略號結尾，這樣問題的草稿才不會太長、太難讀。";
        var selected = 0;
        rig.Vm.SelectInputRequested += (_, _) => selected++;

        await rig.Vm.AskAboutSelectionCommand.ExecuteAsync(new BranchQuoteRequest(message, quote));

        var shortened = quote[..BranchLimits.QuoteDraftChars] + "…";
        Assert.Equal(string.Format(Strings.Branch_QuestionDraftFormat, shortened), rig.Vm.InputText);
        Assert.Contains($"Start:{rig.MainId}:{reply.Id}:{quote}", rig.Branches.Calls);
        Assert.Equal(1, selected);
        Assert.Equal(branch.Id, rig.Session.ConversationId);
    }

    [Fact]
    public async Task Start_disabled_while_responding_and_at_depth_two()
    {
        var rig = await CreateAsync();
        var reply = await AddAsync(rig.Store, rig.MainId, ChatRole.Assistant, "回答");
        await rig.Vm.InitializeAsync();
        var message = Assert.Single(rig.Vm.Messages, m => m.IsAssistant);
        Assert.True(message.CanBranch);
        Assert.Equal(Strings.Branch_StartTip, message.BranchTip);

        rig.Vm.IsResponding = true;
        Assert.False(message.CanBranch);
        Assert.Equal(Strings.Branch_StartBusyTip, message.BranchTip);
        rig.Vm.IsResponding = false;

        // 第二層：工作台開在第二層的支線裡
        var first = await rig.Store.CreateAsync("第一層", ConversationKind.Branch);
        var second = await rig.Store.CreateAsync("第二層", ConversationKind.Branch);
        await AddAsync(rig.Store, second.Id, ChatRole.Assistant, "第二層的回答");
        SetBranchState(rig.Branches, rig.MainId, "主線", second, reply.Id, firstLevel: first);
        await rig.Vm.OpenConversationAsync(second);
        var deep = Assert.Single(rig.Vm.Messages, m => m.IsAssistant);
        Assert.False(deep.CanBranch);
        Assert.True(deep.ShowBranchAction);
        Assert.Equal(Strings.Branch_StartDepthTip, deep.BranchTip);
    }

    [Fact]
    public async Task Branch_action_hidden_in_sprite_conversation_finished_and_locked()
    {
        // 小精靈的專屬對話：不能開支線
        var store = new InMemoryConversationStore();
        var sprite = new SpriteConversationService(store);
        var spriteId = (await sprite.GetOrCreateAsync("與小精靈的對話")).Id;
        var spriteRig = await CreateAsync(spriteId, store, sprite);
        await AddAsync(store, spriteId, ChatRole.Assistant, "回答");
        await spriteRig.Vm.InitializeAsync();
        Assert.False(Assert.Single(spriteRig.Vm.Messages, m => m.IsAssistant).ShowBranchAction);

        // 已經帶回的支線：唯讀，動作隱藏
        var finished = await CreateAsync(store: store);
        var merged = await finished.Store.CreateAsync("已帶回", ConversationKind.Branch);
        await AddAsync(finished.Store, merged.Id, ChatRole.Assistant, "支線的回答");
        SetBranchState(finished.Branches, finished.MainId, "主線", merged, "fork", BranchStatus.Merged);
        await finished.Vm.OpenConversationAsync(merged);
        Assert.False(Assert.Single(finished.Vm.Messages, m => m.IsAssistant).ShowBranchAction);
        Assert.True(finished.Vm.IsBranchFinished);

        // 上一層被鎖住：動作隱藏
        var locked = await CreateAsync(store: store);
        var reply = await AddAsync(locked.Store, locked.MainId, ChatRole.Assistant, "回答");
        var active = await locked.Store.CreateAsync("進行中", ConversationKind.Branch);
        locked.Branches.States[locked.MainId] = new BranchState([Node(locked.MainId, "主線")], null,
            [Child(active.Id, locked.MainId, locked.MainId, 1, reply.Id)], Child(active.Id, locked.MainId, locked.MainId, 1, reply.Id));
        await locked.Vm.InitializeAsync();
        Assert.True(locked.Vm.IsBranchLocked);
        Assert.False(Assert.Single(locked.Vm.Messages, m => m.IsAssistant).ShowBranchAction);
    }

    // ---- 上一層被鎖住、結束時的輸入與送出 ----

    [Fact]
    public async Task Locked_parent_cannot_send_retry_regenerate_or_compact()
    {
        var rig = await CreateAsync();
        var reply = await AddAsync(rig.Store, rig.MainId, ChatRole.Assistant, "回答");
        var active = await rig.Store.CreateAsync("進行中", ConversationKind.Branch);
        rig.Branches.States[rig.MainId] = new BranchState([Node(rig.MainId, "主線")], null,
            [Child(active.Id, rig.MainId, rig.MainId, 1, reply.Id)], Child(active.Id, rig.MainId, rig.MainId, 1, reply.Id));
        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = "問題";
        rig.Vm.ErrorMessage = "出錯了";

        Assert.False(rig.Vm.SendCommand.CanExecute(null));
        Assert.False(rig.Vm.RetryCommand.CanExecute(null));
        Assert.False(rig.Vm.RegenerateCommand.CanExecute(null));
        Assert.False(rig.Vm.CanCompactAndContinue);
        Assert.Equal(Strings.Branch_LockedActionTip, rig.Vm.CompactTip);
        Assert.False(rig.Vm.InputEnabled);
        Assert.Equal(Strings.Branch_InputLocked, rig.Vm.ComposerPlaceholder);
    }

    [Fact]
    public async Task Finished_branch_is_read_only()
    {
        var rig = await CreateAsync();
        var branch = await rig.Store.CreateAsync("已放棄", ConversationKind.Branch);
        await AddAsync(rig.Store, branch.Id, ChatRole.User, "問");
        SetBranchState(rig.Branches, rig.MainId, "主線", branch, "fork", BranchStatus.Abandoned);
        await rig.Vm.OpenConversationAsync(branch);
        rig.Vm.InputText = "還想問";

        Assert.False(rig.Vm.SendCommand.CanExecute(null));
        Assert.False(rig.Vm.InputEnabled);
        Assert.Equal(Strings.Branch_InputFinished, rig.Vm.ComposerPlaceholder);
        Assert.Equal(Strings.Branch_CompactDisabledTip, rig.Vm.CompactTip);
    }

    [Fact]
    public async Task Composer_placeholder_by_state()
    {
        var rig = await CreateAsync();
        await rig.Vm.InitializeAsync();
        Assert.Equal(Strings.Workbench_InputPlaceholder, rig.Vm.ComposerPlaceholder);

        var branch = await rig.Store.CreateAsync("進行中", ConversationKind.Branch);
        SetBranchState(rig.Branches, rig.MainId, "主線", branch, "fork");
        await rig.Vm.OpenConversationAsync(branch);
        Assert.Equal(Strings.Branch_InputPlaceholder, rig.Vm.ComposerPlaceholder);
        Assert.True(rig.Vm.InputEnabled);
    }

    // ---- 帶回、放棄、回到上一層 ----

    [Fact]
    public async Task Merge_opens_result_conversation()
    {
        var rig = await CreateAsync();
        var branch = await rig.Store.CreateAsync("支線", ConversationKind.Branch);
        await AddAsync(rig.Store, branch.Id, ChatRole.User, "問");
        SetBranchState(rig.Branches, rig.MainId, "主線", branch, "fork");
        await rig.Vm.OpenConversationAsync(branch);
        var main = (await rig.Store.GetConversationAsync(rig.MainId))!;
        rig.Prompt.Result = new BranchMergeResult(main);

        await rig.Vm.BranchBar.MergeCommand.ExecuteAsync(null);

        var request = Assert.Single(rig.Prompt.Requests);
        var step = Assert.Single(request.Steps);
        Assert.Equal(branch.Id, step.ConversationId);
        Assert.True(step.TargetIsMain);
        Assert.Equal(1, step.MessageCount);
        Assert.Equal(rig.MainId, rig.Session.ConversationId);
    }

    [Fact]
    public async Task Merge_cancel_stays()
    {
        var rig = await CreateAsync();
        var branch = await rig.Store.CreateAsync("支線", ConversationKind.Branch);
        await AddAsync(rig.Store, branch.Id, ChatRole.User, "問");
        SetBranchState(rig.Branches, rig.MainId, "主線", branch, "fork");
        await rig.Vm.OpenConversationAsync(branch);
        rig.Prompt.Result = new BranchMergeResult(null);

        await rig.Vm.BranchBar.MergeCommand.ExecuteAsync(null);

        Assert.Single(rig.Prompt.Requests);
        Assert.Equal(branch.Id, rig.Session.ConversationId);
    }

    [Fact]
    public async Task MergeAll_sends_two_steps_deepest_first()
    {
        var rig = await CreateAsync();
        var first = await rig.Store.CreateAsync("第一層", ConversationKind.Branch);
        var second = await rig.Store.CreateAsync("第二層", ConversationKind.Branch);
        await AddAsync(rig.Store, first.Id, ChatRole.User, "一");
        await AddAsync(rig.Store, second.Id, ChatRole.User, "二");
        SetBranchState(rig.Branches, rig.MainId, "主線", second, "fork", firstLevel: first);
        await rig.Vm.OpenConversationAsync(second);
        rig.Prompt.Result = new BranchMergeResult(null);

        await rig.Vm.BranchBar.MergeAllCommand.ExecuteAsync(null);

        var request = Assert.Single(rig.Prompt.Requests);
        Assert.Equal(2, request.Steps.Count);
        Assert.Equal(second.Id, request.Steps[0].ConversationId);
        Assert.False(request.Steps[0].TargetIsMain);
        Assert.Equal("第一層", request.Steps[0].TargetTitle);
        Assert.Equal(first.Id, request.Steps[1].ConversationId);
        Assert.True(request.Steps[1].TargetIsMain);
        Assert.Equal("主線", request.Steps[1].TargetTitle);
        Assert.Equal(second.Id, rig.Session.ConversationId);   // 取消：留在原地
    }

    [Fact]
    public async Task Abandon_confirms_only_when_has_messages()
    {
        var rig = await CreateAsync();
        var empty = await rig.Store.CreateAsync("空的", ConversationKind.Branch);
        SetBranchState(rig.Branches, rig.MainId, "主線", empty, "fork");
        rig.Branches.Parents[empty.Id] = (await rig.Store.GetConversationAsync(rig.MainId))!;
        await rig.Vm.OpenConversationAsync(empty);

        await rig.Vm.BranchBar.AbandonCommand.ExecuteAsync(null);

        Assert.Empty(rig.Confirm.Asked);
        Assert.Contains($"Abandon:{empty.Id}", rig.Branches.Calls);
        Assert.Equal(rig.MainId, rig.Session.ConversationId);

        // 有訊息：先確認；不同意就留在原地
        var used = await rig.Store.CreateAsync("有內容", ConversationKind.Branch);
        await AddAsync(rig.Store, used.Id, ChatRole.User, "問");
        SetBranchState(rig.Branches, rig.MainId, "主線", used, "fork");
        rig.Branches.Parents[used.Id] = (await rig.Store.GetConversationAsync(rig.MainId))!;
        await rig.Vm.OpenConversationAsync(used);
        rig.Confirm.Answer = false;

        await rig.Vm.BranchBar.AbandonCommand.ExecuteAsync(null);

        var asked = Assert.Single(rig.Confirm.Asked);
        Assert.Equal(Strings.Branch_AbandonConfirm, asked.Message);
        Assert.DoesNotContain($"Abandon:{used.Id}", rig.Branches.Calls);
        Assert.Equal(used.Id, rig.Session.ConversationId);
    }

    [Fact]
    public async Task Back_and_return_to_branch()
    {
        var rig = await CreateAsync();
        var branch = await rig.Store.CreateAsync("已帶回", ConversationKind.Branch);
        await AddAsync(rig.Store, branch.Id, ChatRole.User, "問");
        SetBranchState(rig.Branches, rig.MainId, "主線", branch, "fork", BranchStatus.Merged);
        await rig.Vm.OpenConversationAsync(branch);

        await rig.Vm.BranchBar.BackCommand.ExecuteAsync(null);
        Assert.Equal(rig.MainId, rig.Session.ConversationId);

        // 上一層被鎖住：回到最深的進行中支線
        var active = await rig.Store.CreateAsync("進行中", ConversationKind.Branch);
        rig.Branches.States[rig.MainId] = new BranchState([Node(rig.MainId, "主線")], null,
            [Child(active.Id, rig.MainId, rig.MainId, 1, "fork")], Child(active.Id, rig.MainId, rig.MainId, 1, "fork"));
        rig.Branches.ActiveLeaves[rig.MainId] = active;
        await rig.Vm.OpenConversationAsync((await rig.Store.GetConversationAsync(rig.MainId))!);

        await rig.Vm.BranchBar.ReturnToBranchCommand.ExecuteAsync(null);

        Assert.Contains($"FindActiveLeaf:{rig.MainId}", rig.Branches.Calls);
        Assert.Equal(active.Id, rig.Session.ConversationId);
    }

    [Fact]
    public async Task Initialize_opens_active_leaf()
    {
        var rig = await CreateAsync();
        var active = await rig.Store.CreateAsync("進行中", ConversationKind.Branch);
        rig.Branches.ActiveLeaves[rig.MainId] = active;

        await rig.Vm.InitializeAsync();

        Assert.Contains($"FindActiveLeaf:{rig.MainId}", rig.Branches.Calls);
        Assert.Equal(active.Id, rig.Session.ConversationId);
    }

    [Fact]
    public async Task Changed_event_for_the_parent_locks_the_composer()
    {
        var rig = await CreateAsync();
        await rig.Vm.InitializeAsync();
        var active = await rig.Store.CreateAsync("進行中", ConversationKind.Branch);
        rig.Branches.States[rig.MainId] = new BranchState([Node(rig.MainId, "主線")], null,
            [Child(active.Id, rig.MainId, rig.MainId, 1, "fork")], Child(active.Id, rig.MainId, rig.MainId, 1, "fork"));

        rig.Branches.RaiseChanged(new BranchChangedArgs(active.Id, rig.MainId, rig.MainId, BranchStatus.Active));

        Assert.True(rig.Vm.IsBranchLocked);
        Assert.False(rig.Vm.InputEnabled);
    }

    // ---- 研究本、用量 ----

    [Fact]
    public async Task Note_button_follows_root_and_disables_without_note()
    {
        var rig = await CreateAsync();   // 主線有研究本
        var branch = await rig.Store.CreateAsync("支線", ConversationKind.Branch);
        SetBranchState(rig.Branches, rig.MainId, "主線", branch, "fork");
        await rig.Vm.OpenConversationAsync(branch);

        Assert.True(rig.Vm.HasContextNote);
        Assert.True(rig.Vm.ContextNoteButtonEnabled);
        await rig.Vm.OpenContextNoteCommand.ExecuteAsync(null);
        Assert.Contains($"Show:{rig.MainId}", rig.Window.Calls);

        // 主線沒有研究本：支線裡停用，說明原因
        var bare = await CreateAsync(withNote: false);
        var bareBranch = await bare.Store.CreateAsync("支線", ConversationKind.Branch);
        SetBranchState(bare.Branches, bare.MainId, "主線", bareBranch, "fork");
        await bare.Vm.OpenConversationAsync(bareBranch);

        Assert.False(bare.Vm.ContextNoteButtonEnabled);
        Assert.Equal(Strings.Branch_NoteDisabledTip, bare.Vm.ContextNoteButtonTip);
    }

    [Fact]
    public async Task Cost_uses_root_id()
    {
        var usage = new InMemoryUsageStore();
        var rig = await CreateAsync(usage: usage);
        usage.Records.Add(new UsageRecord(UsageRecord.NewId(), new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero), rig.MainId,
            UsagePurpose.Chat, ChatMode.Workbench, "ms", "m", "連線", new TokenUsage(100, 10, 0, 0), false, null, 1m, 0));
        var branch = await rig.Store.CreateAsync("支線", ConversationKind.Branch);
        SetBranchState(rig.Branches, rig.MainId, "主線", branch, "fork");

        await rig.Vm.OpenConversationAsync(branch);

        Assert.Equal("NT$32", rig.Vm.Usage.ConversationCostText);
    }

    [Fact]
    public async Task Empty_branch_uses_parent_last_usage()
    {
        var rig = await CreateAsync();
        var usage = new TokenUsage(1_200, 80, 0, 0);
        await AddAsync(rig.Store, rig.MainId, ChatRole.Assistant, "回答", usage);
        var branch = await rig.Store.CreateAsync("空的", ConversationKind.Branch);
        SetBranchState(rig.Branches, rig.MainId, "主線", branch, "fork");

        await rig.Vm.OpenConversationAsync(branch);

        Assert.Equal(usage, rig.Vm.LastUsage);
    }

    // ---- 訊息列表的旗標：略過分隔線與支線的卡片 ----

    [Fact]
    public async Task Markers_are_ignored_for_regenerate_and_empty_check()
    {
        var rig = await CreateAsync();
        await AddAsync(rig.Store, rig.MainId, ChatRole.User, "問");
        await AddAsync(rig.Store, rig.MainId, ChatRole.Assistant, "答");
        await rig.Vm.InitializeAsync();
        Assert.True(rig.Vm.RegenerateCommand.CanExecute(null));

        // 摘要卡放在最後面（還沒交出）：重新產生照常可以按
        rig.Vm.Messages.Add(new BranchSummaryCardViewModel(
            new BranchCardInfo("child", "支線", "摘要", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 2, 0, null),
            new FakeTimeProvider()));

        Assert.True(rig.Vm.RegenerateCommand.CanExecute(null));

        // 只剩分隔線與卡片：「沒有訊息」的判斷略過它們，所以「新對話」不會再建一則（FakeChatSession 的 NewConversationAsync 會丟例外）
        rig.Vm.Messages.Clear();
        rig.Vm.Messages.Add(new BranchSummaryCardViewModel(
            new BranchCardInfo("child", "支線", "摘要", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 2, 0, null),
            new FakeTimeProvider()));
        await rig.Vm.NewConversationCommand.ExecuteAsync(null);
        Assert.Equal(rig.MainId, rig.Session.ConversationId);
    }

    [Fact]
    public async Task Streaming_bubble_gets_message_id_on_completed()
    {
        var rig = await CreateAsync();
        await rig.Vm.InitializeAsync();
        var final = new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock("回答")], DateTimeOffset.UtcNow, ModelId: "m");

        rig.Session.StartRun();
        rig.Session.Emit(new TextDelta("回答"));
        var streaming = Assert.Single(rig.Vm.Messages, m => m.IsAssistant);
        Assert.Null(streaming.MessageId);
        rig.Session.Emit(new Completed(final, StopReason.EndTurn));
        rig.Session.FinishRun();

        var reply = Assert.Single(rig.Vm.Messages, m => m.IsAssistant);
        Assert.Equal(final.Id, reply.MessageId);
        Assert.True(reply.CanBranch);
    }

    [Fact]
    public async Task Start_is_refused_when_the_service_throws()
    {
        var rig = await CreateAsync();
        await AddAsync(rig.Store, rig.MainId, ChatRole.Assistant, "回答");
        await rig.Vm.InitializeAsync();
        rig.Branches.ThrowOnStart = new InvalidOperationException(BranchMessages.DepthMessage);
        var message = Assert.Single(rig.Vm.Messages, m => m.IsAssistant);

        await rig.Vm.StartBranchCommand.ExecuteAsync(message);

        Assert.Equal(BranchMessages.DepthMessage, rig.Vm.Notice);
        Assert.Equal(rig.MainId, rig.Session.ConversationId);
    }
}
