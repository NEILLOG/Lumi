using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;

namespace Lumi.App.ViewModels;

/// <summary>
/// 「AI 連線」頁（M22 22D 把「連線」「模型設定」兩個分段合併成一頁）：一份依連線分組的清單
/// （連線 → 它的模型 → 「＋ 新增模型」），選到連線詳細區顯示連線表單、選到模型顯示模型表單。
/// 表單與驗證、儲存、放棄變更的確認都沿用 <see cref="ConnectionsViewModel"/>、<see cref="ModelsViewModel"/>；
/// 這裡只負責清單的組合、兩個編輯器同時只開一個，以及選取的對應。
/// </summary>
public sealed partial class AiSettingsViewModel : ObservableObject
{
    private bool _rebuilding;   // 重建清單時，清單控制項會把選取寫成 null：這時不能當成使用者取消選取
    private readonly AiImageGenerationItem _imageItem;

    public AiSettingsViewModel(ConnectionsViewModel connections, ModelsViewModel models, ImageGenerationViewModel imageGeneration)
    {
        Connections = connections;
        Models = models;
        ImageGeneration = imageGeneration;
        _imageItem = new AiImageGenerationItem(imageGeneration.Summary);
        connections.Items.CollectionChanged += (_, _) => Rebuild();
        models.Items.CollectionChanged += (_, _) => Rebuild();
        connections.PropertyChanged += (_, e) => OnEditorChanged(e.PropertyName);
        models.PropertyChanged += (_, e) => OnEditorChanged(e.PropertyName);
        imageGeneration.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ImageGenerationViewModel.Summary))
                _imageItem.Summary = ImageGeneration.Summary;
            OnEditorChanged(e.PropertyName);   // HasEditor 與 Connections 的 HasEditor 同名，走同一條路
        };
        Rebuild();
    }

    public ConnectionsViewModel Connections { get; }

    public ModelsViewModel Models { get; }

    /// <summary>「圖片生成」固定列的表單（M22 22G）。</summary>
    public ImageGenerationViewModel ImageGeneration { get; }

    /// <summary>清單的所有列（依連線分組，順序就是畫面順序）。</summary>
    public ObservableCollection<AiListItem> Items { get; } = [];

    /// <summary>清單上方的 Caption：「連線與模型」。</summary>
    public string ListTitle => Strings.AiSettings_ListName;

    /// <summary>還沒有任何連線：清單位置顯示空狀態說明。</summary>
    public bool IsEmpty => Connections.Items.Count == 0;

    /// <summary>目前選取的那一列：由兩個編輯器的狀態推出來（模型編輯中＝它的列，新增模型＝所選連線底下的「新增模型」，連線編輯中＝連線的列）。
    /// 清單上點一列不是直接寫這個屬性，而是呼叫 <see cref="SelectAsync"/>（要先詢問未儲存的變更），畫面再依這個屬性同步清單的選取。</summary>
    public AiListItem? SelectedItem => FindSelected();

    /// <summary>正在重建清單：清單控制項這時會清掉選取，畫面不能把它當成使用者的操作。</summary>
    public bool IsRebuilding => _rebuilding;

    public bool ShowConnectionEditor => Connections.HasEditor;

    public bool ShowModelEditor => Models.HasEditor;

    public bool ShowImageEditor => ImageGeneration.HasEditor;

    /// <summary>詳細區沒有表單：顯示「請先選擇…」。</summary>
    public bool ShowPlaceholder => !Connections.HasEditor && !Models.HasEditor && !ImageGeneration.HasEditor;

    /// <summary>選取清單的一列。有未儲存的變更先詢問（放棄才切換）；使用者取消回 false，維持原狀。</summary>
    public async Task<bool> SelectAsync(AiListItem? item)
    {
        switch (item)
        {
            case AiConnectionItem connection:
                return await Models.CloseEditorAsync() && await ImageGeneration.CloseEditorAsync() && await Connections.SelectAsync(connection.Connection);
            case AiModelItem model:
                return await Connections.CloseEditorAsync() && await ImageGeneration.CloseEditorAsync() && await Models.SelectAsync(model.Model);
            case AiAddModelItem add:
                if (!await Connections.CloseEditorAsync() || !await ImageGeneration.CloseEditorAsync())
                    return false;
                await Models.StartNewAsync(add.ConnectionId);
                return true;
            case AiImageGenerationItem when ImageGeneration.HasEditor:
                return true;
            case AiImageGenerationItem:
                if (!await Connections.CloseEditorAsync() || !await Models.CloseEditorAsync())
                    return false;
                await ImageGeneration.OpenAsync();
                return true;
            default:
                return await Connections.CloseEditorAsync() && await Models.CloseEditorAsync() && await ImageGeneration.CloseEditorAsync();
        }
    }

    /// <summary>頁首的「＋ 新增連線」：關掉模型表單（有未儲存的變更先詢問），再開始新增連線。</summary>
    [RelayCommand]
    private async Task NewConnectionAsync()
    {
        if (await Models.CloseEditorAsync() && await ImageGeneration.CloseEditorAsync())
            await Connections.NewConnectionCommand.ExecuteAsync(null);
    }

    /// <summary>從外部（缺金鑰流程、首頁的待處理）導到某個連線；<paramref name="focusApiKey"/> 時聚焦金鑰輸入框。</summary>
    public async Task NavigateToConnectionAsync(string? connectionId, bool focusApiKey, string? notice)
    {
        if (await Models.CloseEditorAsync() && await ImageGeneration.CloseEditorAsync())
            await Connections.NavigateAsync(connectionId, focusApiKey, notice);
    }

    private void Rebuild()
    {
        _rebuilding = true;
        try
        {
            var models = Models.Items.ToLookup(m => m.ConnectionId);
            Items.Clear();
            foreach (var connection in Connections.Items)
            {
                Items.Add(new AiConnectionItem(connection));
                foreach (var model in models[connection.Id])
                    Items.Add(new AiModelItem(model));
                Items.Add(new AiAddModelItem(connection.Id));
            }
            Items.Add(_imageItem);   // 清單最下方的固定列
        }
        finally
        {
            _rebuilding = false;
        }
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(SelectedItem));
    }

    private void OnEditorChanged(string? property)
    {
        if (property is not (nameof(ConnectionsViewModel.SelectedItem) or nameof(ConnectionsViewModel.HasEditor) or nameof(ConnectionsViewModel.IsNew)
            or nameof(ModelsViewModel.SelectedConnection)))
            return;
        OnPropertyChanged(nameof(SelectedItem));
        OnPropertyChanged(nameof(ShowConnectionEditor));
        OnPropertyChanged(nameof(ShowModelEditor));
        OnPropertyChanged(nameof(ShowImageEditor));
        OnPropertyChanged(nameof(ShowPlaceholder));
    }

    private AiListItem? FindSelected()
    {
        if (ImageGeneration.HasEditor)
            return _imageItem;
        var key = Models.HasEditor
            ? (Models.IsNew
                ? (Models.SelectedConnection is { } connection ? AiAddModelItem.KeyOf(connection.Id) : null)
                : (Models.SelectedItem is { } model ? AiModelItem.KeyOf(model.Id) : null))
            : (Connections.SelectedItem is { } selected ? AiConnectionItem.KeyOf(selected.Id) : null);
        return key is null ? null : Items.FirstOrDefault(i => i.Key == key);
    }
}
