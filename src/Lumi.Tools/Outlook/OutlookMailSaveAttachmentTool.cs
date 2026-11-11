using System.ComponentModel;
using Lumi.Core.Outlook;
using Lumi.Core.Tools;

namespace Lumi.Tools.Outlook;

public sealed record OutlookMailSaveAttachmentArgs(
    [property: Description("信件代號（m 開頭，來自 outlook_mail_search）")] string Id,
    [property: Description("附件序號，從 1 開始（對應 outlook_mail_read 的附件清單）")] int Attachment,
    [property: Description("要存到的資料夾（對話裡加入的資料夾，完整路徑或相對路徑）；對話只加入一個資料夾時可以不填")] string? Folder = null);

/// <summary>把信件的一個附件存到使用者加入對話的資料夾（M30 設計 1、5）。寫入，只限工作台；同名不覆蓋，加 (1)、(2)。</summary>
public sealed class OutlookMailSaveAttachmentTool(IOutlookClient client) : OutlookToolBase<OutlookMailSaveAttachmentArgs>(client)
{
    private const int MaxNameTries = 1000;

    public override ToolDescriptor Descriptor { get; } = new(
        "outlook_mail_save_attachment", "儲存信件附件",
        "把信件的一個附件存到使用者加入對話的資料夾。id 是信件代號，attachment 是 outlook_mail_read 附件清單的序號（從 1 開始）。" +
        "folder 沒給時，對話只加入一個資料夾就存到那裡，否則請使用者指定資料夾。檔名用附件原名，同名會加 (1)、(2)，不會覆蓋既有檔案。" +
        "只有使用者要求存檔時才呼叫。",
        ToolPermission.Write, ToolModes.Workbench, TimeSpan.FromSeconds(40),
        RequiresScope: true, Group: ToolGroups.Outlook, Summary: "把信件的附件存到你加入的資料夾。",
        OptInWarning: OutlookToolText.OptInWarning);

    protected override ToolPreview Describe(OutlookMailSaveAttachmentArgs args, ToolContext context)
    {
        var target = $"附件 {args.Attachment}";
        if (!TryResolveFolder(context.Scope, args.Folder, out var folder, out _))
            return new ToolPreview(target, null);
        return new ToolPreview(target, $"存到 {context.Scope.ToDisplayPath(folder!)}");
    }

    protected override async Task<ToolResult> RunAsync(OutlookMailSaveAttachmentArgs args, ToolContext context, CancellationToken ct)
    {
        var id = (args.Id ?? "").Trim();
        if (id.Length == 0)
            return ToolResult.Error("id 不能是空的。", "參數不正確");
        if (args.Attachment < 1)
            return ToolResult.Error("attachment 從 1 開始。", "參數不正確");

        if (!TryResolveFolder(context.Scope, args.Folder, out var folder, out var folderError))
            return ToolResult.Error(folderError!, "無法寫入");

        // 檔名要先問 Outlook：附件清單只有讀信才拿得到
        var mail = await Client.ReadMailAsync(context.ConversationId, id, ct);
        var info = mail.Attachments.FirstOrDefault(a => a.Index == args.Attachment);
        if (info is null)
        {
            var message = mail.Attachments.Count == 0
                ? "這封信沒有附件。"
                : $"這封信只有 {mail.Attachments.Count} 個附件，沒有第 {args.Attachment} 個。";
            return ToolResult.Error(message, "參數不正確");
        }

        var target = ChooseTarget(context.Scope, folder!, SafeFileName(info.FileName), out var targetError);
        if (target is null)
            return ToolResult.Error(targetError!, "無法寫入");

        var saved = await Client.SaveAttachmentAsync(context.ConversationId, id, args.Attachment, target, ct);
        var display = context.Scope.ToDisplayPath(target);
        return ToolResult.Ok($"已存到 {display}（{TextFiles.FormatSize(saved.SizeBytes)}）。", "已存檔");
    }

    /// <summary>
    /// 決定要存到的資料夾（完整路徑，經範圍解析）。沒給 folder 時：範圍裡只有一個資料夾才用它。
    /// </summary>
    private static bool TryResolveFolder(IFileScope scope, string? folder, out string? full, out string? error)
    {
        full = null;
        error = null;

        var input = folder;
        if (string.IsNullOrWhiteSpace(input))
        {
            if (scope.Roots.Count == 0)
            {
                error = "目前沒有加入任何資料夾，請先把要存附件的資料夾加入對話。";
                return false;
            }
            if (scope.Roots is { Count: 1 } roots && roots[0].IsDirectory)
            {
                // 空字串在只有一個根時代表根本身
                input = "";
            }
            else
            {
                error = "請用 folder 指定要存到哪個資料夾（對話裡加入的資料夾）。";
                return false;
            }
        }

        var resolved = scope.Resolve(input.Trim());
        if (!resolved.IsAllowed)
        {
            error = resolved.Error;
            return false;
        }

        var path = resolved.FullPath!;
        if (!Directory.Exists(path))
        {
            error = $"「{scope.ToDisplayPath(path)}」不是現有的資料夾，請指定資料夾。";
            return false;
        }

        full = path;
        return true;
    }

    /// <summary>
    /// 第一個沒有重複的名稱：原名，然後 `名稱 (1).ext`、`名稱 (2).ext`…。每個候選都要經過範圍解析。
    /// </summary>
    private static string? ChooseTarget(IFileScope scope, string folder, string fileName, out string? error)
    {
        error = null;
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var i = 0; i < MaxNameTries; i++)
        {
            var candidateName = i == 0 ? fileName : $"{baseName} ({i}){extension}";
            var resolved = scope.Resolve(Path.Combine(folder, candidateName));
            if (!resolved.IsAllowed)
            {
                error = resolved.Error;
                return null;
            }

            var full = resolved.FullPath!;
            if (!File.Exists(full) && !Directory.Exists(full))
                return full;
        }

        error = "同名的檔案太多，請換一個資料夾。";
        return null;
    }

    /// <summary>附件原名去掉路徑分隔字元與不能用在檔名的字元；什麼都不剩時用「附件」。</summary>
    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name
            .Where(c => c is not ('/' or '\\'))
            .Select(c => invalid.Contains(c) ? '_' : c)
            .ToArray())
            .Trim();
        return cleaned.Length == 0 ? "附件" : cleaned;
    }
}
