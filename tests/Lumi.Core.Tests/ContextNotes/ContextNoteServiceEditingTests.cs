using Lumi.Core.Chat;
using Lumi.Core.ContextNotes;

namespace Lumi.Core.Tests.ContextNotes;

/// <summary>研究本服務：使用者編輯、保護、已決定、退回、外部修改（計畫 4.2、4.5、6.6 包 35E）。</summary>
public sealed class ContextNoteServiceEditingTests
{
    [Fact]
    public async Task WhileEditing_TheScribeDoesNotRun_AndStatusIsWaitingForUser()
    {
        var rig = new ContextNoteRig();
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);
        await rig.AddUserAsync(conversation, "問題一");

        rig.Service.BeginUserEdit(note.Id);
        rig.Service.NotifyTurnFinished(conversation);
        await rig.IdleAsync(note.Id);

        Assert.Empty(rig.Scribe.Requests);
        Assert.Equal(ScribeState.WaitingForUser, rig.Service.GetStatus(note.Id).State);
    }

    [Fact]
    public async Task AfterEditing_TheScribeRuns_WithTheUsersVersion()
    {
        var rig = new ContextNoteRig();
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);
        await rig.AddUserAsync(conversation, "問題一");
        rig.Service.BeginUserEdit(note.Id);
        rig.Service.NotifyTurnFinished(conversation);
        await rig.IdleAsync(note.Id);

        await rig.Service.EndUserEditAsync(note.Id, ContextNoteRig.Doc(("問題拆解", "使用者寫的")));
        await rig.IdleAsync(note.Id);

        var request = Assert.Single(rig.Scribe.Requests);
        Assert.Equal("使用者寫的", Assert.Single(request.Masked.Entries).Text);
        Assert.Equal(ContextNoteRevisionKind.UserEdit, (await rig.RevisionAsync(note.Id, 2)).Kind);
    }

    [Fact]
    public async Task UserEditStartedDuringTheScribe_DiscardsTheResult_AndRerunsAfterEditing()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = new ContextNoteRig
        {
            Scribe =
            {
                Respond = async (n, _) =>
                {
                    if (n == 1)
                    {
                        entered.TrySetResult();
                        await gate.Task;
                        return FakeScribe.Answer(ContextNoteRig.Doc(("問題拆解", "書記的版本")));
                    }
                    return FakeScribe.NoChange();
                },
            },
        };
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);
        await rig.AddUserAsync(conversation, "問題一");
        rig.Service.NotifyTurnFinished(conversation);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        rig.Service.BeginUserEdit(note.Id);
        gate.TrySetResult();
        await rig.IdleAsync(note.Id);

        Assert.Single(rig.Scribe.Requests);
        Assert.Equal(1, (await rig.NoteAsync(note.Id)).CurrentVersion);
        Assert.Equal(ScribeState.WaitingForUser, rig.Service.GetStatus(note.Id).State);

        await rig.Service.EndUserEditAsync(note.Id, ContextNoteRig.Doc(("問題拆解", "使用者的版本")));
        await rig.IdleAsync(note.Id);

        Assert.Equal(2, rig.Scribe.Requests.Count);
        Assert.Equal("使用者的版本", Assert.Single(rig.Scribe.Requests[1].Masked.Entries).Text);
        var versions = await rig.Store.ListRevisionsAsync(note.Id);
        Assert.DoesNotContain(versions, v => v.Kind == ContextNoteRevisionKind.ScribeAuto);
    }

    [Fact]
    public async Task GoalChangedDuringTheScribe_DiscardsTheResult_AndRerunsWithTheNewGoal()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = new ContextNoteRig
        {
            Scribe =
            {
                Respond = async (n, _) =>
                {
                    if (n == 1)
                    {
                        entered.TrySetResult();
                        await gate.Task;
                        return FakeScribe.Answer(ContextNoteRig.Doc(("問題拆解", "舊目標的結果")));
                    }
                    return FakeScribe.NoChange();
                },
            },
        };
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation, "舊目標");
        await rig.AddUserAsync(conversation, "問題一");
        rig.Service.NotifyTurnFinished(conversation);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await rig.Service.SetGoalAsync(note.Id, "新目標");
        gate.TrySetResult();
        await rig.IdleAsync(note.Id);

        Assert.Equal(2, rig.Scribe.Requests.Count);
        Assert.Equal("新目標", rig.Scribe.Requests[1].Goal);
        Assert.DoesNotContain(await rig.Store.ListRevisionsAsync(note.Id), v => v.Kind == ContextNoteRevisionKind.ScribeAuto);
    }

    [Fact]
    public async Task UserEditedBlocks_BecomeProtected()
    {
        var rig = new ContextNoteRig();
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);

        await rig.Service.EndUserEditAsync(note.Id, ContextNoteRig.Doc(("問題拆解", "我寫的一段")));
        await rig.IdleAsync(note.Id);

        var edited = await rig.RevisionAsync(note.Id, 2);
        Assert.Equal(ContextNoteRevisionKind.UserEdit, edited.Kind);
        Assert.Contains("我寫的一段", edited.ProtectedBlocks);
    }

    [Fact]
    public async Task MarkDecided_MovesTheBlockToDecided_AndProtectsIt()
    {
        var rig = new ContextNoteRig();
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);
        await rig.Service.EndUserEditAsync(note.Id, ContextNoteRig.Doc(("問題拆解", "方案A")));

        await rig.Service.MarkDecidedAsync(note.Id, "方案A");

        var current = await rig.CurrentAsync(note.Id);
        Assert.Equal(ContextNoteRevisionKind.UserAction, current.Kind);
        var document = ContextNoteDocument.Parse(current.Content);
        Assert.Empty(document.Section(ContextNoteDocument.Breakdown).Blocks);
        Assert.Equal("方案A", Assert.Single(document.Section(ContextNoteDocument.Decided).Blocks).Text);
        Assert.Contains("方案A", current.ProtectedBlocks);
    }

    [Fact]
    public async Task MarkDecided_OnAnAlreadyDecidedBlock_OnlyProtects()
    {
        var rig = new ContextNoteRig();
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);
        await rig.Service.EndUserEditAsync(note.Id, ContextNoteRig.Doc(("已決定", "定案")));

        await rig.Service.MarkDecidedAsync(note.Id, "定案");

        var current = await rig.CurrentAsync(note.Id);
        var decided = ContextNoteDocument.Parse(current.Content).Section(ContextNoteDocument.Decided).Blocks;
        Assert.Equal("定案", Assert.Single(decided).Text);
        Assert.Contains("定案", current.ProtectedBlocks);
    }

    [Fact]
    public async Task SetProtected_False_RemovesTheProtection()
    {
        var rig = new ContextNoteRig();
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);
        await rig.Service.EndUserEditAsync(note.Id, ContextNoteRig.Doc(("問題拆解", "保護我")));
        var before = await rig.CurrentAsync(note.Id);
        Assert.Contains("保護我", before.ProtectedBlocks);

        await rig.Service.SetProtectedAsync(note.Id, "保護我", false);

        var after = await rig.CurrentAsync(note.Id);
        Assert.Equal(ContextNoteRevisionKind.UserAction, after.Kind);
        Assert.Empty(after.ProtectedBlocks);
        Assert.Equal(before.Content, after.Content);
    }

    [Fact]
    public async Task Revert_CopiesTheGoalContentAndProtection_AsANewVersion()
    {
        var rig = new ContextNoteRig();
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation, "目標一");
        await rig.Service.EndUserEditAsync(note.Id, ContextNoteRig.Doc(("問題拆解", "甲")));
        var keep = await rig.CurrentAsync(note.Id);
        await rig.Service.SetGoalAsync(note.Id, "目標二");
        await rig.Service.EndUserEditAsync(note.Id, ContextNoteRig.Doc(("問題拆解", "乙")));

        await rig.Service.RevertAsync(note.Id, keep.Version);

        var reverted = await rig.CurrentAsync(note.Id);
        Assert.Equal(ContextNoteRevisionKind.Revert, reverted.Kind);
        Assert.Equal(keep.Content, reverted.Content);
        Assert.Equal(keep.Goal, reverted.Goal);
        Assert.Equal("目標一", (await rig.NoteAsync(note.Id)).Goal);
        Assert.Equal(keep.Version.ToString(), reverted.TriggerExcerpt);
        Assert.Equal(keep.ProtectedBlocks, reverted.ProtectedBlocks);
    }

    [Fact]
    public async Task ExternalModification_IsSavedAsAVersion()
    {
        var rig = new ContextNoteRig();
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation, "原本目標");
        var before = await rig.CurrentAsync(note.Id);

        await rig.Files.WriteNoteAsync(note.Id, "# 舊標題\n\n**目標**：其他人改的目標\n\n" + ContextNoteRig.Doc(("問題拆解", "外面改的")));
        await rig.Service.SyncExternalAsync(note.Id);

        var current = await rig.CurrentAsync(note.Id);
        Assert.Equal(before.Version + 1, current.Version);
        Assert.Equal(ContextNoteRevisionKind.External, current.Kind);
        Assert.Equal("其他人改的目標", current.Goal);
        Assert.Contains("外面改的", current.Content);
        // 檔案重寫成標準格式（標題是對話標題，不是檔案裡的舊標題）
        var file = await rig.Files.ReadNoteAsync(note.Id);
        Assert.DoesNotContain("舊標題", file!);
        Assert.Contains("**目標**：其他人改的目標", file!);
    }

    [Fact]
    public async Task ExternalModification_OfTheTitleLineOnly_CreatesNoVersion()
    {
        var rig = new ContextNoteRig();
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);
        var file = await rig.Files.ReadNoteAsync(note.Id);

        await rig.Files.WriteNoteAsync(note.Id, file!.Replace("# 測試對話", "# 改名了", StringComparison.Ordinal));
        await rig.Service.SyncExternalAsync(note.Id);

        Assert.Equal(1, (await rig.NoteAsync(note.Id)).CurrentVersion);
    }

    [Fact]
    public async Task ExternalDeletion_RewritesTheFileFromTheCurrentVersion()
    {
        var rig = new ContextNoteRig();
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);
        await rig.Service.EndUserEditAsync(note.Id, ContextNoteRig.Doc(("問題拆解", "還在")));
        rig.Files.SetNoteText(note.Id, null);

        await rig.Service.SyncExternalAsync(note.Id);

        var file = await rig.Files.ReadNoteAsync(note.Id);
        Assert.NotNull(file);
        Assert.Contains("還在", file);
        Assert.Equal(2, (await rig.NoteAsync(note.Id)).CurrentVersion);
    }

    [Fact]
    public async Task EndUserEdit_OverTwentyThousandChars_Throws()
    {
        var rig = new ContextNoteRig();
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);

        await Assert.ThrowsAsync<ArgumentException>(() => rig.Service.EndUserEditAsync(note.Id, new string('字', 20_001)));

        Assert.Equal(1, (await rig.NoteAsync(note.Id)).CurrentVersion);
    }

    [Fact]
    public async Task EndUserEdit_NullOrSameContent_CreatesNoVersion()
    {
        var rig = new ContextNoteRig();
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);

        await rig.Service.EndUserEditAsync(note.Id, null);
        await rig.Service.EndUserEditAsync(note.Id, ContextNoteDocument.Empty.ToMarkdown());
        await rig.IdleAsync(note.Id);

        Assert.Equal(1, (await rig.NoteAsync(note.Id)).CurrentVersion);
    }
}
