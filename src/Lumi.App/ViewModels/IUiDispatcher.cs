namespace Lumi.App.ViewModels;

/// <summary>
/// 把工作丟回 UI 執行緒。命令本身就在 UI 執行緒上；這是給「從別的執行緒來的事件」用的
/// （例如 <see cref="Lumi.Core.Models.IModelCatalog.Changed"/>）。ViewModel 不直接碰 Avalonia 的 Dispatcher，測試用同步版本。
/// </summary>
public interface IUiDispatcher
{
    void Post(Action action);
}
