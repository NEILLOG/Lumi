using System.Globalization;
using Lumi.Core.Memory;
using Lumi.Core.Models;
using Lumi.Core.Tools;

namespace Lumi.Tools.Memory;

/// <summary>三個記憶工具共用的小工具（M14 14C）：開關檢查、數字與日期格式、確認卡片的文字、容量訊息。</summary>
internal static class MemoryToolSupport
{
    /// <summary>確認卡片的影響說明（寫進記憶或送出都會隨每則訊息送到供應商）。</summary>
    public const string SendsToProviderNote = "會隨每則訊息送到模型供應商";

    /// <summary>確認卡片上「對象」（內容）的字數上限。</summary>
    public const int TargetChars = 60;

    public static ToolResult Disabled { get; } =
        ToolResult.Error("長期記憶目前已關閉（使用者可以在 設定 › 記憶 重新開啟）。", "記憶已關閉");

    /// <summary>記憶是否開啟（設定；不依模式）。</summary>
    public static bool IsEnabled(IModelCatalog catalog) => catalog.Current.Memory.Enabled;

    /// <summary>以字形叢集計，超過就截斷並加「…」（中文、emoji 各算一字，不會切開 emoji）。</summary>
    public static string Clip(string text, int maxChars)
    {
        if (MemoryRules.Length(text) <= maxChars)
            return text;
        return new StringInfo(text).SubstringByTextElements(0, maxChars) + "…";
    }

    /// <summary>千分位（2000 → 2,000），給錯誤訊息用。</summary>
    public static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>容量已滿的訊息：目前的則數與字數，以及上限（由 <see cref="MemoryOptions"/> 來）。</summary>
    public static async Task<string> CapacityMessageAsync(IMemoryStore memory, MemoryOptions options, CancellationToken ct)
    {
        var items = await memory.ListAsync(ct);
        var characters = items.Sum(i => MemoryRules.Length(i.Content));
        return $"長期記憶已滿（目前 {Count(items.Count)} 則、{Count(characters)} 字；上限 {Count(options.MaxItems)} 則、{Count(options.MaxTotalLength)} 字）。"
            + "請告訴使用者，由使用者決定要刪除或合併哪些記憶後再重試。";
    }
}
