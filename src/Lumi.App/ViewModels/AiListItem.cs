using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.App.Resources;

namespace Lumi.App.ViewModels;

/// <summary>
/// 合併後「AI 連線」頁清單的一列。清單是一串多型的列（連線、連線底下的模型、連線底下的「新增模型」），
/// 畫面用資料範本依型別挑外觀，<see cref="AiSettingsViewModel"/> 依型別決定選到時詳細區顯示什麼。
/// 要在清單加新種類的項目（例如 M22 的「圖片生成」固定項目）：新增一個子類別、在 <c>AiConnectionsView.axaml</c> 的
/// <c>ListBox.DataTemplates</c> 加一個範本、在 <see cref="AiSettingsViewModel.SelectAsync"/> 與詳細區各加一個分支。
/// </summary>
public abstract class AiListItem : ObservableObject
{
    /// <summary>穩定的識別（重建清單後用它找回選取的那一列）。</summary>
    public abstract string Key { get; }

    /// <summary>可存取名稱（螢幕閱讀器念這一列時用）。</summary>
    public abstract string AutomationName { get; }

    // 清單會隨目錄與連線狀態的變動重建：同一列（同型別、同 Key）重建後視為同一個項目，選取才不會跳掉
    public override bool Equals(object? obj) => obj is AiListItem other && other.GetType() == GetType() && other.Key == Key;

    public override int GetHashCode() => HashCode.Combine(GetType(), Key);
}

/// <summary>連線那一列（分組的標題）：字首圖示、名稱、最近一次測試連線的狀態。</summary>
public sealed class AiConnectionItem(ConnectionListItem connection) : AiListItem
{
    public ConnectionListItem Connection { get; } = connection;

    public string Id => Connection.Id;

    public override string Key => KeyOf(Id);

    public override string AutomationName => $"{Connection.Name}，{Connection.StatusText}";

    public static string KeyOf(string connectionId) => $"connection:{connectionId}";
}

/// <summary>連線底下的一個模型：名稱與「預設」標籤。</summary>
public sealed class AiModelItem(ModelListItem model) : AiListItem
{
    public ModelListItem Model { get; } = model;

    public string Id => Model.Id;

    public string ConnectionId => Model.ConnectionId;

    public override string Key => KeyOf(Id);

    /// <summary>「預設」標籤；不是預設就沒有。</summary>
    public string? Tag => Model.IsWorkbenchDefault ? Strings.AiSettings_DefaultTag : null;

    public bool HasTag => Model.IsWorkbenchDefault;

    /// <summary>完整說明（Tooltip）：名稱被省略時看得到完整內容與確切的 model ID。</summary>
    public string Tooltip => $"{Model.DisplayName}\n{Model.ModelId}";

    public override string AutomationName => HasTag
        ? $"{Model.DisplayName}，{Model.ModelId}，{Tag}"
        : $"{Model.DisplayName}，{Model.ModelId}";

    public static string KeyOf(string modelSettingsId) => $"model:{modelSettingsId}";
}

/// <summary>每個連線最後一列的「＋ 新增模型」：選到它就在這個連線底下開始新增模型設定。</summary>
public sealed class AiAddModelItem(string connectionId) : AiListItem
{
    public string ConnectionId { get; } = connectionId;

    public override string Key => KeyOf(ConnectionId);

    public override string AutomationName => Strings.AiSettings_AddModel;

    public static string KeyOf(string connectionId) => $"add-model:{connectionId}";
}
