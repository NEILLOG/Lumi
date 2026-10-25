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
        var maxTokens = request.EffectiveMaxOutputTokens;   // 小精靈模式有較低的上限（M5）
        var messages = ToMessageParams(request.Messages, images ?? ImageAttachments.None);
        // 最後一則的模式指示掛在快取斷點之後；它之前的每一則都帶著自己存下來的指示（ToMessageParams），歷史才是只往後加
        messages = WithCacheBreakpointAndSuffix(messages, request.Messages is [.., { } last] && last.TurnSuffix is { Length: > 0 } stored ? stored : request.TurnSuffix);
        BetaMessages.BetaOutputConfig? outputConfig = options.Effort is { Length: > 0 } effort
            ? new BetaMessages.BetaOutputConfig { Effort = effort }
            : null;
        var fallbacks = options.UseServerFallbacks && SupportsServerFallbacks(model);

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

        // system 是第一個快取斷點：前綴順序是 tools → system → messages，斷點涵蓋它前面的全部（含 tools）。
        // 沒有 system 時完全不送這個欄位。
        if (request.System is { Length: > 0 } system)
        {
            parameters = parameters with
            {
                System = new List<BetaMessages.BetaTextBlockParam>
                {
                    new() { Text = system, CacheControl = new BetaMessages.BetaCacheControlEphemeral() },
                },
            };
        }

        // 沒有工具時完全不送 tools 欄位（空陣列在某些版本會被拒絕）
        return request.Tools is { Count: > 0 } tools
            ? parameters with { Tools = tools.Select(t => (BetaMessages.BetaToolUnion)ToTool(t)).ToList() }
            : parameters;
    }

    /// <summary>
    /// 陣列形式的 server-side fallback 只有 Opus 5 世代與 Fable 5 世代接受；其他模型（例如 Haiku 5.5 沒有
    /// server-side fallback）帶了這個參數會整個請求 400，所以不送。
    /// </summary>
    internal static bool SupportsServerFallbacks(string modelId) =>
        modelId.StartsWith("claude-opus-5", StringComparison.Ordinal) || modelId.StartsWith("claude-fable-5", StringComparison.Ordinal);

    /// <summary>
    /// 第二個快取斷點放在歷史的最後一個區塊（下一次請求的前綴就是這一次的歷史往後加）；
    /// 模式指示接在斷點<b>之後</b>。下一次請求時它就成了這一則自己的一部分（ToMessageParams 原樣帶上），
    /// 所以前綴一個位元組都沒變——thinking 簽章綁定前綴，事後拿掉這段會讓帶著 thinking 的請求被 400（M5 的 Q2，2026-10-25 修改）。
    /// 斷點只能掛在文字、圖片、tool_result 上；歷史的最後一則一定是使用者訊息或工具結果，不會是 thinking。
    /// </summary>
    private static List<BetaMessages.BetaMessageParam> WithCacheBreakpointAndSuffix(List<BetaMessages.BetaMessageParam> messages, string? suffix)
    {
        if (messages.Count == 0 || messages[^1].Role != BetaMessages.Role.User)
            return messages;

        var last = messages[^1];
        var blocks = last.Content.TryPickBetaContentBlockParams(out var existing) ? existing.ToList() : [];
        if (blocks.Count == 0)
            return messages;

        blocks[^1] = WithCacheControl(blocks[^1]);
        if (suffix is { Length: > 0 })
            blocks.Add(new BetaMessages.BetaTextBlockParam { Text = suffix });

        var result = messages.ToList();
        result[^1] = new BetaMessages.BetaMessageParam { Role = BetaMessages.Role.User, Content = blocks };
        return result;
    }

    private static BetaMessages.BetaContentBlockParam WithCacheControl(BetaMessages.BetaContentBlockParam block)
    {
        var cache = new BetaMessages.BetaCacheControlEphemeral();
        if (block.TryPickText(out var text))
            return text with { CacheControl = cache };
        if (block.TryPickImage(out var image))
            return image with { CacheControl = cache };
        if (block.TryPickToolResult(out var result))
            return result with { CacheControl = cache };
        return block;   // 其他類型（不應該出現在最後）就不掛，寧可少一個斷點
    }

    /// <summary>
    /// 工具定義。Schema 的所有頂層欄位（type、properties、required、additionalProperties）原樣帶過去：
    /// 用 SDK 的 rawData 建構式而不是只設 Properties／Required，才不會丟掉 additionalProperties。
    /// </summary>
    private static BetaMessages.BetaTool ToTool(ToolDefinition tool) => new()
    {
        Name = tool.Name,
        Description = tool.Description,
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
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            var isUser = message.Role is ChatRole.User or ChatRole.Tool;
            var content = message.Content.Where(IsSendable).Select(b => ToContentBlockParam(b, images)).ToList();
            if (content.Count == 0)
                continue;

            // 存下來的模式指示接在這一則的最後。最後一則例外：它的指示要放在快取斷點之後，由 WithCacheBreakpointAndSuffix 加
            if (i < messages.Count - 1 && message.TurnSuffix is { Length: > 0 } suffix)
                content.Add(new BetaMessages.BetaTextBlockParam { Text = suffix });

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
        ToolResultBlock r => ToToolResult(r, images),
        ProviderBlock p => AnthropicProviderBlocks.ToParam(p),   // 只剩 Anthropic 自己的（見 IsSendable）
        _ => throw new NotSupportedException($"未知的內容區塊類型：{block.GetType()}"),
    };

    /// <summary>
    /// 工具結果。沒有圖片（或圖片一張都讀不到）時維持字串內容，既有請求的形狀不變（守住快取）；
    /// 有圖片時內容改成區塊清單：文字在前，後面每張圖一個圖片區塊，讀不到的圖改成文字說明（M18 D12）。
    /// </summary>
    private static BetaMessages.BetaToolResultBlockParam ToToolResult(ToolResultBlock r, IReadOnlyDictionary<string, byte[]> images)
    {
        var isError = r.IsError ? true : (bool?)null;
        if (r.Images is not { Count: > 0 } resultImages || !resultImages.Any(i => images.ContainsKey(i.FileName)))
            return new BetaMessages.BetaToolResultBlockParam { ToolUseID = r.ToolUseId, Content = r.Content, IsError = isError };

        var blocks = new List<BetaMessages.Block>();
        if (r.Content.Length > 0)   // API 不接受空的文字區塊
            blocks.Add(new BetaMessages.BetaTextBlockParam { Text = r.Content });
        foreach (var image in resultImages)
        {
            if (images.TryGetValue(image.FileName, out var bytes) && ToMediaType(image.MediaType) is { } mediaType)
            {
                blocks.Add(new BetaMessages.BetaImageBlockParam
                {
                    Source = new BetaMessages.BetaBase64ImageSource { Data = Convert.ToBase64String(bytes), MediaType = mediaType },
                });
            }
            else
            {
                blocks.Add(new BetaMessages.BetaTextBlockParam { Text = ImageAttachments.MissingText(image) });
            }
        }
        return new BetaMessages.BetaToolResultBlockParam { ToolUseID = r.ToolUseId, Content = blocks, IsError = isError };
    }

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
