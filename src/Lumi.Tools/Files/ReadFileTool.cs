using System.ComponentModel;
using System.Globalization;
using System.Text;
using Lumi.Core.Tools;

namespace Lumi.Tools;

public sealed record ReadFileArgs(
    [property: Description("要讀的檔案路徑：相對於使用者加入的資料夾（例如 src/App.cs），或範圍內的絕對路徑")] string Path,
    [property: Description("從第幾行開始讀（從 1 算起），預設 1")] int? Offset = null,
    [property: Description("最多讀幾行，預設 2000；內容太長時會自動截斷，並告訴你用哪個 offset 繼續")] int? Limit = null);

public sealed class ReadFileTool(FileToolOptions options) : ToolBase<ReadFileArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "read_file", "讀取檔案",
        "讀取使用者加入的資料夾或檔案範圍內的一個文字檔，每行附行號。只能讀範圍內的文字檔；內容很長時用 offset 與 limit 分段讀。",
        ToolPermission.ReadOnly, ToolModes.Both, TimeSpan.FromSeconds(15), RequiresScope: true, Group: ToolGroups.FilesRead, Summary: "讀取你加入的文字檔內容。");

    protected override ToolPreview Describe(ReadFileArgs args, ToolContext context) =>
        new(string.IsNullOrWhiteSpace(args.Path) ? null : args.Path.Trim(), null);

    protected override Task<ToolResult> InvokeAsync(ReadFileArgs args, ToolContext context, CancellationToken ct) =>
        Task.Run(() => Read(args, context, ct), ct);

    private ToolResult Read(ReadFileArgs args, ToolContext context, CancellationToken ct)
    {
        var resolved = context.Scope.Resolve(args.Path ?? "");
        if (!resolved.IsAllowed)
            return ToolResult.Error(resolved.Error!, "無法讀取");

        var full = resolved.FullPath!;
        var display = context.Scope.ToDisplayPath(full);
        if (Directory.Exists(full))
            return ToolResult.Error($"「{display}」是資料夾，請用 list_dir 列出內容。", "是資料夾");
        if (!File.Exists(full))
            return ToolResult.Error($"找不到檔案：{display}", "找不到檔案");

        var offset = args.Offset ?? 1;
        if (offset < 1)
            return ToolResult.Error("offset 必須從 1 開始。", "參數不正確");
        var limit = Math.Clamp(args.Limit ?? options.DefaultReadLines, 1, options.MaxReadLines);

        try
        {
            var size = new FileInfo(full).Length;
            if (size > options.MaxReadFileBytes)
                return ToolResult.Error($"檔案太大（{TextFiles.FormatSize(size)}），不能整份讀取；請用 search 找要的內容。", "檔案太大");

            using var reader = TextFiles.Open(full, out var openError);
            if (reader is null)
                return ToolResult.Error($"{display}：{openError}", "無法讀取");

            var lines = new List<(int Number, string Text)>();
            var chars = 0;
            var total = 0;
            var stoppedEarly = false;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                total++;
                if ((total & 0x3FF) == 0)
                    ct.ThrowIfCancellationRequested();
                if (total < offset)
                    continue;
                if (stoppedEarly)
                    continue;   // 只為了數總行數

                var text = TextFiles.ClipLine(line, options.MaxLineChars);
                if (lines.Count >= limit || (lines.Count > 0 && chars + text.Length > options.MaxReadChars))
                {
                    stoppedEarly = true;
                    continue;
                }
                lines.Add((total, text));
                chars += text.Length + 8;
            }

            if (total == 0)
                return ToolResult.Ok($"{display}：空檔案。", "空檔案");
            if (lines.Count == 0)
                return ToolResult.Error($"offset 超過檔案的行數（共 {total} 行）。", "超出範圍");

            var first = lines[0].Number;
            var last = lines[^1].Number;
            var whole = first == 1 && last == total;
            var builder = new StringBuilder();
            builder.Append(display).Append(whole
                ? string.Create(CultureInfo.InvariantCulture, $"（共 {total:N0} 行）")
                : string.Create(CultureInfo.InvariantCulture, $"（第 {first:N0}–{last:N0} 行，共 {total:N0} 行）")).Append('\n');
            foreach (var (number, text) in lines)
                builder.Append(number.ToString(CultureInfo.InvariantCulture).PadLeft(6)).Append('\t').Append(text).Append('\n');
            if (last < total)
                builder.Append(string.Create(CultureInfo.InvariantCulture, $"（還有 {total - last:N0} 行沒有顯示，用 offset={last + 1} 繼續讀。）"));

            var summary = whole
                ? string.Create(CultureInfo.InvariantCulture, $"{total:N0} 行")
                : string.Create(CultureInfo.InvariantCulture, $"第 {first:N0}–{last:N0} 行 / 共 {total:N0} 行");
            return ToolResult.Ok(builder.ToString().TrimEnd('\n'), summary);
        }
        catch (DecoderFallbackException)
        {
            return ToolResult.Error($"{display}：{TextFiles.InvalidUtf8Message}", "無法讀取");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"無法讀取：{display}", "無法讀取");
        }
    }
}
