namespace Lumi.Core.Models;

/// <summary>
/// 顯示設定（進階），都預設關閉。<paramref name="ShowTokenUsage"/>：在每則回答下方顯示輸入／輸出 token 數；
/// <paramref name="ShowModelName"/>：在同一列顯示回答這則的模型（存檔裡的 <c>ChatMessage.ModelId</c>）。
/// </summary>
public sealed record DisplaySettings(bool ShowTokenUsage = false, bool ShowModelName = false)
{
    public static readonly DisplaySettings Default = new();

    public DisplaySettings Normalize() => this;
}
