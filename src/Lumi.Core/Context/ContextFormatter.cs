using System.Globalization;
using System.Text;
using Lumi.Core.Chat;

namespace Lumi.Core.Context;

/// <summary>把 <see cref="ContextBlock"/> 轉成送給模型的文字。兩家供應商共用，格式只在這裡定義一次。</summary>
public static class ContextBlockFormatter
{
    public static string ToPromptText(ContextBlock block)
    {
        var tag = block.Kind switch
        {
            ContextKind.Folder => "folder",
            ContextKind.Image => "image",
            _ => "file",
        };

        if (!block.Embedded || block.Content is null)
        {
            // 沒有放內容：告訴模型這個項目在範圍內（檔案與資料夾）以及為什麼沒放內容
            return $"<{tag} path=\"{Escape(block.Path)}\" note=\"{Escape(block.Note ?? "沒有放進內容")}\" />";
        }

        var builder = new StringBuilder();
        builder.Append('<').Append(tag).Append(" path=\"").Append(Escape(block.Path)).Append('"');
        if (block.Kind == ContextKind.Folder && block.ItemCount is { } count)
            builder.Append(" entries=\"").Append(count.ToString(CultureInfo.InvariantCulture)).Append('"');
        builder.Append(">\n");

        if (block.Kind == ContextKind.File)
        {
            // 檔尾的換行不當成多一個空行；每行附行號（和 read_file 一樣的格式，模型可以直接引用）
            var text = block.Content.EndsWith('\n') ? block.Content[..^1] : block.Content;
            var number = 0;
            foreach (var line in text.Split('\n'))
            {
                number++;
                builder.Append(number.ToString(CultureInfo.InvariantCulture).PadLeft(6)).Append('\t').Append(line.TrimEnd('\r')).Append('\n');
            }
        }
        else
        {
            builder.Append(block.Content.TrimEnd('\n')).Append('\n');
        }

        builder.Append("</").Append(tag).Append('>');
        return builder.ToString();
    }

    /// <summary>一則使用者訊息裡所有要變成文字的部分（上下文區塊在前、使用者打的字在後），用空行隔開。</summary>
    public static string ToPromptText(IEnumerable<ContentBlock> blocks) =>
        string.Join("\n\n", blocks.Select(b => b switch
        {
            ContextBlock c => ToPromptText(c),
            TextBlock t => t.Text,
            _ => "",
        }).Where(t => t.Length > 0));

    private static string Escape(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal);
}
