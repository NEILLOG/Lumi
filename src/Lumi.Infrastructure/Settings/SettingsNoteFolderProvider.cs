using Lumi.Core.Models;
using Lumi.Core.Notes;
using Microsoft.Extensions.Logging;

namespace Lumi.Infrastructure.Settings;

/// <summary>
/// <see cref="INoteFolderProvider"/> 的實作（M10 10B）：每次讀取都看目前的設定，所以使用者改了資料夾立即生效，不快取。
/// 設定不合法時退回預設資料夾，並記一次 log（同一個問題不重複記）。
/// </summary>
public sealed class SettingsNoteFolderProvider(IModelCatalog catalog, ILogger<SettingsNoteFolderProvider> logger) : INoteFolderProvider
{
    private volatile string? _lastProblem;

    public string Folder
    {
        get
        {
            var resolution = NoteFolderRules.Resolve(catalog.Current.Notes.Folder);
            if (resolution.Problem is not null && resolution.Problem != _lastProblem)
            {
                _lastProblem = resolution.Problem;
                logger.LogWarning("{Problem}", resolution.Problem);
            }
            return resolution.Folder;
        }
    }
}
