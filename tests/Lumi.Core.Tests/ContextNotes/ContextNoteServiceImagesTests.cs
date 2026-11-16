using Lumi.Core.Chat;
using Lumi.Core.ContextNotes;

namespace Lumi.Core.Tests.ContextNotes;

/// <summary>研究本服務：書記引用對話裡的圖片（計畫 4.7）。</summary>
public sealed class ContextNoteServiceImagesTests
{
    /// <summary>最小的 PNG（只要開頭的魔術數字對得上）。</summary>
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    [Fact]
    public async Task ImageReference_IsCopiedIntoTheNoteFolder_AndRewritten()
    {
        var rig = new ContextNoteRig { Scribe = { Respond = (_, _) => Task.FromResult(FakeScribe.Answer(ContextNoteRig.Doc(("問題拆解", "![說明](圖1)")))) } };
        rig.Attachments.Files["shot.png"] = Png;
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);
        await rig.AddUserAsync(conversation, "看這張", new ImageBlock("shot.png", "image/png", "截圖"));

        rig.Service.NotifyTurnFinished(conversation);
        await rig.IdleAsync(note.Id);

        var content = (await rig.CurrentAsync(note.Id)).Content;
        var line = content.Split('\n').Single(l => l.Contains("](images/", StringComparison.Ordinal));
        Assert.StartsWith("![說明](images/", line, StringComparison.Ordinal);
        var path = line[(line.IndexOf('(') + 1)..^1];
        Assert.Equal(Png, rig.Files.GetImage(note.Id, path));
    }

    [Fact]
    public async Task ReferenceToMissingImage_LineIsRemoved()
    {
        var rig = new ContextNoteRig { Scribe = { Respond = (_, _) => Task.FromResult(FakeScribe.Answer(ContextNoteRig.Doc(("問題拆解", "前段文字"), ("問題拆解", "![x](圖9)")))) } };
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);
        await rig.AddUserAsync(conversation, "沒有圖");

        rig.Service.NotifyTurnFinished(conversation);
        await rig.IdleAsync(note.Id);

        var content = (await rig.CurrentAsync(note.Id)).Content;
        Assert.Contains("前段文字", content);
        Assert.DoesNotContain("圖9", content);
        Assert.DoesNotContain("![x]", content);
    }

    [Fact]
    public async Task UnknownImagePath_LineIsRemoved()
    {
        var rig = new ContextNoteRig { Scribe = { Respond = (_, _) => Task.FromResult(FakeScribe.Answer(ContextNoteRig.Doc(("問題拆解", "文字"), ("問題拆解", "![x](http://example.com/a.png)")))) } };
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);
        await rig.AddUserAsync(conversation, "問題");

        rig.Service.NotifyTurnFinished(conversation);
        await rig.IdleAsync(note.Id);

        var content = (await rig.CurrentAsync(note.Id)).Content;
        Assert.Contains("文字", content);
        Assert.DoesNotContain("example.com", content);
    }

    [Fact]
    public async Task ImageLineAlreadyInTheOldVersion_IsKept()
    {
        var rig = new ContextNoteRig { Scribe = { Respond = (_, _) => Task.FromResult(FakeScribe.Answer(ContextNoteRig.Doc(("問題拆解", "![舊圖](images/old.png)")))) } };
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);
        await rig.Service.EndUserEditAsync(note.Id, ContextNoteRig.Doc(("問題拆解", "![舊圖](images/old.png)")));
        await rig.AddUserAsync(conversation, "問題");

        rig.Service.NotifyTurnFinished(conversation);
        await rig.IdleAsync(note.Id);

        Assert.Contains("![舊圖](images/old.png)", (await rig.CurrentAsync(note.Id)).Content);
    }

    [Fact]
    public async Task MoreThanFourImages_OnlyTheFirstFourAreCopied()
    {
        var answer = ContextNoteRig.Doc(Enumerable.Range(1, 5).Select(k => ("問題拆解", $"![第{k}張](圖{k})")).ToArray());
        var rig = new ContextNoteRig { Scribe = { Respond = (_, _) => Task.FromResult(FakeScribe.Answer(answer)) } };
        var conversation = await rig.ConversationAsync();
        var note = await rig.StartAsync(conversation);
        for (var k = 1; k <= 5; k++)
        {
            rig.Attachments.Files[$"p{k}.png"] = Png;
            await rig.AddUserAsync(conversation, $"第{k}張", new ImageBlock($"p{k}.png", "image/png", $"圖{k}"));
        }

        rig.Service.NotifyTurnFinished(conversation);
        await rig.IdleAsync(note.Id);

        var content = (await rig.CurrentAsync(note.Id)).Content;
        var copied = content.Split('\n').Count(l => l.Contains("](images/", StringComparison.Ordinal));
        Assert.Equal(ContextNoteLimits.MaxImagesPerRun, copied);
        Assert.Contains("![第4張](images/", content, StringComparison.Ordinal);
        Assert.DoesNotContain("第5張", content, StringComparison.Ordinal);
    }
}
