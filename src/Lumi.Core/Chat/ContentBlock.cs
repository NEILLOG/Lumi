using System.Text.Json.Serialization;
using Lumi.Core.Context;

namespace Lumi.Core.Chat;

/// <summary>
/// 一則訊息裡的一個內容區塊。對話歷史只能往後加，已送出的區塊不能修改或重排
/// （新模型會檢查 thinking 區塊的 signature 與前面的對話是否一致，改過歷史會回 400）。
/// 見 agent.md 第 7 節。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextBlock), "text")]
[JsonDerivedType(typeof(ThinkingBlock), "thinking")]
[JsonDerivedType(typeof(RedactedThinkingBlock), "redacted_thinking")]
[JsonDerivedType(typeof(ProviderBlock), "provider")]
[JsonDerivedType(typeof(ToolUseBlock), "tool_use")]
[JsonDerivedType(typeof(ToolResultBlock), "tool_result")]
[JsonDerivedType(typeof(ContextBlock), "context")]
[JsonDerivedType(typeof(ImageBlock), "image")]
public abstract record ContentBlock;

public sealed record TextBlock(string Text) : ContentBlock;

/// <summary>
/// <paramref name="Signature"/> 是供應商的不透明資料，必須原樣送回，不能自己產生或修改。
/// </summary>
public sealed record ThinkingBlock(string Thinking, string Signature) : ContentBlock;

/// <summary>供應商判斷內容需要隱藏時回傳的不透明資料，一樣要原樣送回。</summary>
public sealed record RedactedThinkingBlock(string Data) : ContentBlock;

/// <summary>
/// 不認得或供應商專屬的區塊（例如 Anthropic 的 fallback 區塊）：存原始 JSON 原樣送回。
/// 只有產生它的供應商會認得；換成別家供應商時應該略過，不要送出。
/// </summary>
public sealed record ProviderBlock(string Provider, string Type, string RawJson) : ContentBlock;

/// <summary>
/// 助理要呼叫工具。<paramref name="Id"/> 由供應商產生，必須原樣送回；
/// <paramref name="InputJson"/> 是模型給的引數（原始 JSON 字串，不在這裡解析）。
/// </summary>
public sealed record ToolUseBlock(string Id, string Name, string InputJson) : ContentBlock;

/// <summary>
/// 工具結果。<paramref name="Content"/> 是送給模型的文字；其餘欄位只給畫面用、不送給模型，
/// 這樣重開程式後卡片的標題與狀態都還在，不依賴工具還註冊著。
/// </summary>
public sealed record ToolResultBlock(
    string ToolUseId,
    string Content,
    bool IsError,
    string DisplayName,
    string? Target,
    string? Summary,
    long DurationMs,
    ToolApproval Approval) : ContentBlock;

[JsonConverter(typeof(JsonStringEnumConverter<ToolApproval>))]
public enum ToolApproval { NotRequired, AllowedOnce, AllowedForConversation, Denied }

/// <summary>
/// 使用者加入的檔案或資料夾在「送出那一刻」的快照（M4 的 Q1）：UI 在使用者氣泡上方畫成 chip，送給模型時轉成文字
/// （見 <see cref="ContextBlockFormatter"/>）。<paramref name="Content"/> 是檔案文字或資料夾的檔案樹；
/// <paramref name="Embedded"/> 是 false 時沒有放內容，<paramref name="Note"/> 說明原因（太大、找不到、不是文字檔…），
/// 檔案仍在範圍內，模型可以用 <c>read_file</c> 讀。
/// </summary>
public sealed record ContextBlock(
    ContextKind Kind,
    string DisplayName,
    string Path,
    string? Content,
    bool Embedded,
    string? Note = null,
    int? ItemCount = null) : ContentBlock;

/// <summary>
/// 使用者加入的圖片。位元組存在附件資料夾（<see cref="IAttachmentStore"/>），這裡只記檔名（內容雜湊）、媒體類型與原始名稱；
/// 送給供應商時才讀出來轉 base64。
/// </summary>
public sealed record ImageBlock(string FileName, string MediaType, string DisplayName) : ContentBlock;
