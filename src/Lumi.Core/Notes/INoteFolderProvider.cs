namespace Lumi.Core.Notes;

/// <summary>
/// 目前的筆記資料夾。每次讀取都要反映最新的設定（使用者改了資料夾立即生效），實作不要快取到永遠。
/// Core 的預設實作是「文件」下的 <c>Lumi/notes</c>；App 的設定實作會取代它（Lumi.Infrastructure）。
/// </summary>
public interface INoteFolderProvider
{
    string Folder { get; }
}

/// <summary>沒有設定時的筆記資料夾：「文件」下的 <c>Lumi/notes</c>。</summary>
public sealed class DefaultNoteFolderProvider : INoteFolderProvider
{
    public string Folder => NoteFolderRules.DefaultFolder;
}
