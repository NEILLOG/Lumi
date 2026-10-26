namespace Lumi.Core.Models;

/// <summary>
/// 顯示設定（進階）。<paramref name="ShowTokenUsage"/>：在每則回答下方顯示輸入／輸出 token 數；預設關閉。
/// </summary>
public sealed record DisplaySettings(bool ShowTokenUsage = false)
{
    public static readonly DisplaySettings Default = new();

    public DisplaySettings Normalize() => this;
}
