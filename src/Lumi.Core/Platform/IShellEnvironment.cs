namespace Lumi.Core.Platform;

/// <summary>
/// 啟動外部程序用的環境變數（M16 D14）。macOS 從登入 shell 取得（Finder 開啟的 App 的 PATH 很短，找不到 npx、uvx）；
/// 其他平台＝目前程序的環境變數。
/// </summary>
public interface IShellEnvironment
{
    Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken ct);
}
