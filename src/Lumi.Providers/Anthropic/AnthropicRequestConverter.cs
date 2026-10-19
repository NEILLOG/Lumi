using BetaMessages = global::Anthropic.Models.Beta.Messages;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Tools;

namespace Lumi.Providers.Anthropic;

/// <summary>把 Core 的 <see cref="ChatRequest"/> 轉成 Anthropic SDK 的 beta 請求參數。</summary>
internal static class AnthropicRequestConverter
{
    public static BetaMessages.MessageCreateParams ToCreateParams(
        ChatRequest request, AnthropicProviderOptions options, IReadOnlyDictionary<string, byte[]>? images = null)
    {
        var settings = request.Model.Model;
        var model = settings.ModelId;
        var maxTokens = settings.MaxOutputTokens;
        var messages = ToMessageParams(request.Messages, images ?? ImageAttachments.None);
        BetaMessages.BetaOutputConfig? outputConfig = options.Effort is { Length: > 0 } effort
            ? new BetaMessages.BetaOutputConfig { Effort = effort }
            : null;
        var fallbacks = options.UseServerFallbacks;

        // ShowThinking 關掉時完全不送 thinking 參數，讓模型用自己的預設（不支援 adaptive 的舊模型也能用）。
        // 打開時要求 summarized：不設的話 claude-opus-5-5 仍會思考，但內容是空字串，UI 沒東西可顯示。
        // 各種組合分開寫成物件初始設定式：這些屬性是 init-only，而且 null 分支經過 implicit operator 會觸發
        // 可為 Null 的警告（TreatWarningsAsErrors）。
        // 候補模型用陣列形式指名 Opus 4.8（M1 實作紀錄：SDK 沒有 "default" 純量形式的寫法）。
        var parameters = (settings.ShowThinking, fallbacks) switch
        {
            (true, true) => new BetaMessages.MessageCreateParams
            {
                Model = model, MaxTokens = maxTokens, Messages = messages, OutputConfig = outputConfig,
                Thinking = new BetaMessages.BetaThinkingConfigAdaptive { Display = BetaMessages.Display.Summarized },
                Fallbacks = new List<BetaMessages.BetaFallbackParam> { new(global::Anthropic.Models.Messages.Model.ClaudeOpus4_8) },
                Betas = ["server-side-fallback-2026-06-01"],
            },
            (true, false) => new BetaMessages.MessageCreateParams
            {
                Model = model, MaxTokens = maxTokens, Messages = messages, OutputConfig = outputConfig,
                Thinking = new BetaMessages.BetaThinkingConfigAdaptive { Display = BetaMessages.Display.Summarized },
            },
            (false, true) => new BetaMessages.MessageCreateParams
            {
                Model = model, MaxTokens = maxTokens, Messages = messages, OutputConfig = outputConfig,
                Fallbacks = new List<BetaMessages.BetaFallbackParam> { new(global::Anthropic.Models.Messages.Model.ClaudeOpus4_8) },
                Betas = ["server-side-fallback-2026-06-01"],
            },
            (false, false) => new BetaMessages.MessageCreateParams
            {
                Model = model, MaxTokens = maxTokens, Messages = messages, OutputConfig = outputConfig,
            },
        };

        // 沒有工具時完全不送 tools 欄位（空陣列在某些版本會被拒絕）
        return request.Tools is { Count: > 0 } tools
            ? parameters with { Tools = tools.Select(t => (BetaMessages.BetaToolUnion)ToTool(t)).ToList() }
            : parameters;
    }

    /// <summary>
    /// 工具定義。Schema 的所有頂層欄位（type、properties、required、additionalProperties）原樣帶過去：
    /// 用 SDK 的 rawData 建構式而不是只設 Properties／Required，才不會丟掉 additionalProperties。
    /// </summary>
    private static BetaMessages.BetaTool ToTool(ITool tool) => new()
    {
        Name = tool.Descriptor.Name,
        Description = tool.Descriptor.Description,
        InputSchema = new BetaMessages.InputSchema(ToolJson.SchemaFields(tool.InputSchema)),
    };

    /// <summary>
    /// 轉成 Anthropic 的訊息：<c>Tool</c> 訊息變成使用者訊息裡的 <c>tool_result</c>；
    /// 相鄰的使用者訊息合併（工具結果之後緊接著使用者新訊息時，API 要求結果在同一則訊息裡且排在最前面）。
    /// </summary>
    private static List<BetaMessages.BetaMessageParam> ToMessageParams(IReadOnlyList<ChatMessage> messages, IReadOnlyDictionary<string, byte[]> images)
    {
        var result = new List<BetaMessages.BetaMessageParam>();
        var previousWasUser = false;
        foreach (var message in messages)
        {
            var isUser = message.Role is ChatRole.User or ChatRole.Tool;
            var content = message.Content.Where(IsSendable).Select(b => ToContentBlockParam(b, images)).ToList();
            if (content.Count == 0)
                continue;

            if (isUser && previousWasUser)
            {
                var merged = Merge(result[^1], content);
                result[^1] = merged;
                continue;
            }

            result.Add(new BetaMessages.BetaMessageParam
            {
                Role = isUser ? BetaMessages.Role.User : BetaMessages.Role.Assistant,
                Content = content,
            });
            previousWasUser = isUser;
        }
        return result;
    }

    private static BetaMessages.BetaMessageParam Merge(BetaMessages.BetaMessageParam existing, List<BetaMessages.BetaContentBlockParam> added)
    {
        var current = existing.Content.TryPickBetaContentBlockParams(out var blocks) ? blocks.ToList() : [];
        var all = current.Concat(added).ToList();
        // tool_result 必須排在其他內容之前
        var ordered = all.Where(b => b.TryPickToolResult(out _)).Concat(all.Where(b => !b.TryPickToolResult(out _))).ToList();
        return new BetaMessages.BetaMessageParam { Role = BetaMessages.Role.User, Content = ordered };
    }

    private static bool IsSendable(ContentBlock block) =>
        block is not ProviderBlock provider || provider.Provider == AnthropicProviderBlocks.ProviderName;

    private static BetaMessages.BetaContentBlockParam ToContentBlockParam(ContentBlock block, IReadOnlyDictionary<string, byte[]> images) => block switch
    {
        ContextBlock c => new BetaMessages.BetaTextBlockParam { Text = ContextBlockFormatter.ToPromptText(c) },
        ImageBlock i => ToImage(i, images),
        TextBlock t => new BetaMessages.BetaTextBlockParam { Text = t.Text },
        ThinkingBlock t => new BetaMessages.BetaThinkingBlockParam { Thinking = t.Thinking, Signature = t.Signature },
        RedactedThinkingBlock r => new BetaMessages.BetaRedactedThinkingBlockParam { Data = r.Data },
        ToolUseBlock u => new BetaMessages.BetaToolUseBlockParam { ID = u.Id, Name = u.Name, Input = ToolJson.ParseObject(u.InputJson) },
        ToolResultBlock r => new BetaMessages.BetaToolResultBlockParam { ToolUseID = r.ToolUseId, Content = r.Content, IsError = r.IsError ? true : null },
        ProviderBlock p => AnthropicProviderBlocks.ToParam(p),   // 只剩 Anthropic 自己的（見 IsSendable）
        _ => throw new NotSupportedException($"未知的內容區塊類型：{block.GetType()}"),
    };

    private static BetaMessages.BetaContentBlockParam ToImage(ImageBlock block, IReadOnlyDictionary<string, byte[]> images)
    {
        // 圖片檔讀不到時改送一段說明文字，不讓整個請求失敗
        if (!images.TryGetValue(block.FileName, out var bytes) || ToMediaType(block.MediaType) is not { } mediaType)
            return new BetaMessages.BetaTextBlockParam { Text = ImageAttachments.MissingText(block) };

        return new BetaMessages.BetaImageBlockParam
        {
            Source = new BetaMessages.BetaBase64ImageSource { Data = Convert.ToBase64String(bytes), MediaType = mediaType },
        };
    }

    private static BetaMessages.MediaType? ToMediaType(string mediaType) => mediaType switch
    {
        "image/png" => BetaMessages.MediaType.ImagePng,
        "image/jpeg" => BetaMessages.MediaType.ImageJpeg,
        "image/gif" => BetaMessages.MediaType.ImageGif,
        "image/webp" => BetaMessages.MediaType.ImageWebP,
        _ => null,
    };
}
