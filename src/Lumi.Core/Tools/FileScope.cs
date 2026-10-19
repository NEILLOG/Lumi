using Lumi.Core.Context;

namespace Lumi.Core.Tools;

/// <summary>範圍裡的一個根：使用者加入的資料夾，或單一檔案（這時範圍只有那一個檔案）。</summary>
/// <param name="Name">給模型看的名稱（資料夾或檔案名稱；重複時加上 " (2)"）。有多個根時，相對路徑要以它開頭。</param>
/// <param name="Path">加入時的絕對路徑。</param>
public sealed record ScopeRoot(string Name, string Path, bool IsDirectory);

/// <summary>
/// 路徑解析的結果：成功時 <see cref="FullPath"/> 是範圍內的真實路徑（符號連結已展開）；
/// 失敗時 <see cref="Error"/> 是可以直接回給模型的繁中說明。不丟例外。
/// </summary>
public sealed record ScopeResolution(string? FullPath, ScopeRoot? Root, string? Error)
{
    public bool IsAllowed => FullPath is not null;

    public static ScopeResolution Denied(string error) => new(null, null, error);
}

/// <summary>
/// 對話目前的範圍：使用者加入的檔案與資料夾。檔案類工具只能經由它存取檔案系統（agent.md 第 8 節），
/// 範圍外、符號連結跳出去、機密檔案一律回失敗。
/// </summary>
public interface IFileScope
{
    IReadOnlyList<ScopeRoot> Roots { get; }

    bool HasRoots => Roots.Count > 0;

    /// <summary>
    /// 把工具收到的路徑解析成範圍內的真實路徑。絕對路徑必須在某個根之下；相對路徑：只有一個根時相對於它，
    /// 有多個根時要以根的名稱開頭（<c>專案/src/a.cs</c>）。空字串代表根本身（只有一個根時）。
    /// </summary>
    ScopeResolution Resolve(string path);

    /// <summary>給畫面與模型看的路徑：相對於所屬的根（有多個根時以根的名稱開頭），用 <c>/</c> 分隔。不在範圍內就原樣回傳。</summary>
    string ToDisplayPath(string fullPath);
}

/// <summary>由對話目前的上下文項目建立範圍。實作在 Lumi.Tools（要碰檔案系統）。</summary>
public interface IFileScopeFactory
{
    IFileScope Create(IReadOnlyList<ContextItem> items);
}

/// <summary>沒有任何範圍。</summary>
public sealed class EmptyFileScope : IFileScope
{
    public static readonly EmptyFileScope Instance = new();

    public IReadOnlyList<ScopeRoot> Roots => [];

    public ScopeResolution Resolve(string path) => ScopeResolution.Denied("目前沒有加入任何檔案或資料夾。");

    public string ToDisplayPath(string fullPath) => fullPath;
}

public sealed class EmptyFileScopeFactory : IFileScopeFactory
{
    public IFileScope Create(IReadOnlyList<ContextItem> items) => EmptyFileScope.Instance;
}
