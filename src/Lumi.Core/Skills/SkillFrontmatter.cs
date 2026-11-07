using System.Text;

namespace Lumi.Core.Skills;

/// <summary>
/// 解析後的 SKILL.md（M29 5.2）。<paramref name="OtherLines"/> 是 frontmatter 裡 <c>name</c>、<c>description</c> 以外的原始行
/// （含縮排的子項），寫回時原樣保留；<paramref name="OtherKeys"/> 是其中頂層鍵的名稱（匯入時判斷要不要警告，例如 <c>allowed-tools</c>）。
/// </summary>
public sealed record SkillMarkdown(
    bool HasFrontmatter,
    string? Name,
    string? Description,
    string Body,
    IReadOnlyList<string> OtherLines,
    IReadOnlyList<string> OtherKeys);

/// <summary>
/// SKILL.md 開頭 frontmatter 的極小子集解析與寫出（純函式，沒有 YAML 套件）。
/// 只支援頂層 <c>key: value</c>：純文字、單引號、雙引號，以及 <c>|</c>、<c>&gt;</c> 區塊；縮排的子項當成上一個鍵的原始文字。
/// 第一行不是 <c>---</c> 或找不到結尾的 <c>---</c>：當作沒有 frontmatter，整份都是內文。
/// </summary>
public static class SkillFrontmatter
{
    private const string Fence = "---";

    /// <summary>可以接受 <c>\r\n</c>；回傳的內文已經用 <see cref="SkillRules.NormalizeBody"/> 正規化。</summary>
    public static SkillMarkdown Parse(string text)
    {
        var source = text.StartsWith('﻿') ? text[1..] : text;
        var lines = source.Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 2 || lines[0].TrimEnd() != Fence)
            return Plain(source);

        var close = Array.FindIndex(lines, 1, line => line.TrimEnd() == Fence);
        if (close < 0)
            return Plain(source);

        var body = SkillRules.NormalizeBody(string.Join('\n', lines, close + 1, lines.Length - close - 1));

        string? name = null;
        string? description = null;
        var otherLines = new List<string>();
        var otherKeys = new List<string>();
        var nameSeen = false;
        var descriptionSeen = false;
        foreach (var entry in ReadEntries(lines, 1, close))
        {
            // name、description 只認第一次出現的；重複的鍵不保留（寫回會變成重複鍵）
            if (entry.Key == "name")
            {
                if (!nameSeen)
                    name = ResolveValue(entry);
                nameSeen = true;
                continue;
            }
            if (entry.Key == "description")
            {
                if (!descriptionSeen)
                    description = ResolveValue(entry);
                descriptionSeen = true;
                continue;
            }
            otherKeys.Add(entry.Key);
            otherLines.AddRange(entry.Lines);
        }

        return new SkillMarkdown(true, name, description, body, otherLines, otherKeys);
    }

    /// <summary>
    /// 寫出 SKILL.md：<c>name</c>、<c>description</c> 一律用雙引號並跳脫（<c>\</c>、<c>"</c>），接著原樣寫出 <paramref name="otherLines"/>，
    /// 然後空一行接內文，結尾一個換行。換行一律 <c>\n</c>。
    /// </summary>
    public static string Write(string name, string description, string body, IReadOnlyList<string>? otherLines = null)
    {
        var sb = new StringBuilder();
        sb.Append(Fence).Append('\n');
        sb.Append("name: \"").Append(Quote(SkillRules.NormalizeName(name))).Append("\"\n");
        sb.Append("description: \"").Append(Quote(SkillRules.NormalizeDescription(description))).Append("\"\n");
        foreach (var line in otherLines ?? [])
            sb.Append(line).Append('\n');
        sb.Append(Fence).Append("\n\n");
        sb.Append(SkillRules.NormalizeBody(body)).Append('\n');
        return sb.ToString();
    }

    private static SkillMarkdown Plain(string source) =>
        new(false, null, null, SkillRules.NormalizeBody(source), [], []);

    private static string Quote(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// 頂層的鍵一行一個；其他行（縮排的子項、空行、沒有鍵的行）接在前一個鍵後面。
    /// 第一個鍵之前沒有對應的鍵，直接略過。每個鍵尾端的空行不算它的內容。
    /// </summary>
    private static List<RawEntry> ReadEntries(string[] lines, int from, int to)
    {
        var entries = new List<RawEntry>();
        RawEntry? current = null;
        for (var i = from; i < to; i++)
        {
            var line = lines[i];
            if (TryReadKey(line, out var key, out var inline))
            {
                current = new RawEntry(key, inline, line);
                entries.Add(current);
            }
            else
            {
                current?.Lines.Add(line);
            }
        }

        foreach (var entry in entries)
        {
            while (entry.Lines.Count > 1 && entry.Lines[^1].Trim().Length == 0)
                entry.Lines.RemoveAt(entry.Lines.Count - 1);
        }
        return entries;
    }

    /// <summary>頂層鍵：第一個字不是空白、不是 <c>#</c>、不是清單的 <c>- </c>，鍵後面要有冒號（冒號後接空白或行尾）。</summary>
    private static bool TryReadKey(string line, out string key, out string inline)
    {
        key = "";
        inline = "";
        if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#')
            return false;
        if (line[0] == '-' && (line.Length == 1 || line[1] == ' '))
            return false;

        var colon = line.IndexOf(':');
        if (colon <= 0)
            return false;
        if (colon + 1 < line.Length && line[colon + 1] != ' ' && line[colon + 1] != '\t')
            return false;

        key = line[..colon].TrimEnd();
        inline = colon + 1 < line.Length ? line[(colon + 1)..].Trim(' ', '\t') : "";
        return key.Length > 0;
    }

    /// <summary>取出 name、description 的值：區塊、雙引號、單引號、純文字（純文字的縮排續行併成一行）。</summary>
    private static string ResolveValue(RawEntry entry)
    {
        var inline = entry.Inline;
        var children = entry.Lines.Skip(1).ToList();

        if (inline is "|" or "|-" or "|+" or ">" or ">-" or ">+")
            return ReadBlock(inline, children);
        if (inline.StartsWith('"') && TryDecodeDouble(inline, out var doubleQuoted))
            return doubleQuoted;
        if (inline.StartsWith('\'') && TryDecodeSingle(inline, out var singleQuoted))
            return singleQuoted;

        var parts = new List<string>();
        if (inline.Length > 0)
            parts.Add(inline);
        foreach (var child in children)
        {
            if (child.Length == 0 || child[0] is not (' ' or '\t') || child.Trim().Length == 0)
                break;
            parts.Add(child.Trim());
        }
        return string.Join(' ', parts);
    }

    /// <summary>區塊字串：後面縮排較深的行（空行也算），去掉共同縮排；<c>|</c> 保留換行，<c>&gt;</c> 把行接成空格、空行變換行。</summary>
    private static string ReadBlock(string header, List<string> children)
    {
        var content = new List<string>();
        foreach (var child in children)
        {
            if (child.Trim().Length == 0 || child[0] is ' ' or '\t')
                content.Add(child);
            else
                break;
        }
        while (content.Count > 0 && content[^1].Trim().Length == 0)
            content.RemoveAt(content.Count - 1);

        var indent = content
            .Where(line => line.Trim().Length > 0)
            .Select(line => line.Length - line.TrimStart(' ', '\t').Length)
            .DefaultIfEmpty(0)
            .Min();
        var lines = content.Select(line => line.Trim().Length == 0 ? "" : line[indent..]).ToList();

        if (header[0] == '|')
            return string.Join('\n', lines);

        var sb = new StringBuilder();
        var previousHasText = false;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                sb.Append('\n');
                previousHasText = false;
                continue;
            }
            if (previousHasText)
                sb.Append(' ');
            sb.Append(line);
            previousHasText = true;
        }
        return sb.ToString();
    }

    /// <summary>雙引號：<c>\"</c>、<c>\\</c>、<c>\n</c> 等跳脫。沒有結束引號時回 false（改當純文字）。</summary>
    private static bool TryDecodeDouble(string text, out string value)
    {
        var sb = new StringBuilder();
        for (var i = 1; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                var next = text[++i];
                sb.Append(next switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => next });
                continue;
            }
            if (c == '"')
            {
                value = sb.ToString();
                return true;
            }
            sb.Append(c);
        }
        value = "";
        return false;
    }

    /// <summary>單引號：<c>''</c> 是一個單引號。沒有結束引號時回 false（改當純文字）。</summary>
    private static bool TryDecodeSingle(string text, out string value)
    {
        var sb = new StringBuilder();
        for (var i = 1; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'')
            {
                if (i + 1 < text.Length && text[i + 1] == '\'')
                {
                    sb.Append('\'');
                    i++;
                    continue;
                }
                value = sb.ToString();
                return true;
            }
            sb.Append(c);
        }
        value = "";
        return false;
    }

    private sealed class RawEntry(string key, string inline, string keyLine)
    {
        public string Key { get; } = key;

        /// <summary>冒號後面同一行的文字（已去掉前後空白）。</summary>
        public string Inline { get; } = inline;

        /// <summary>鍵那一行（第一項）加上之後的子行，原樣保留。</summary>
        public List<string> Lines { get; } = [keyLine];
    }
}
