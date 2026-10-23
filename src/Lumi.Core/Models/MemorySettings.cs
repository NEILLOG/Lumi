namespace Lumi.Core.Models;

/// <summary>
/// 長期記憶設定（M14）。<paramref name="Enabled"/> 關閉時：第 5 層不出現、三個記憶工具的定義不送；記憶資料保留。
/// 改動它會改到 system 與 tools，下一則訊息的快取會失效一次（使用者的動作，可以接受）。
/// </summary>
public sealed record MemorySettings(bool Enabled = true)
{
    public static readonly MemorySettings Default = new();

    public MemorySettings Normalize() => this;
}
