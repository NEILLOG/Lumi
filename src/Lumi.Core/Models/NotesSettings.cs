using Lumi.Core.Notes;

namespace Lumi.Core.Models;

/// <summary>
/// 筆記設定（M10 10B）。<paramref name="Folder"/> 是使用者輸入的原文：空字串＝預設資料夾；
/// 非空時可以含環境變數（<c>%USERPROFILE%</c>、<c>$HOME</c>），由 <see cref="NoteFolderRules"/> 展開與檢查。
/// </summary>
public sealed record NotesSettings(string Folder = "")
{
    public static readonly NotesSettings Default = new("");

    public NotesSettings Normalize() => new((Folder ?? "").Trim());
}
