using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Mcp;
using Lumi.Core.Models;
using Lumi.Core.Secrets;

namespace Lumi.App.ViewModels;

/// <summary>匯入預覽的一列：名稱、連線方式、指令或 URL；改名或錯誤時有說明。有錯誤的那一列不會匯入。</summary>
public sealed record McpImportRow(string Name, string TransportText, string Target, string? RenamedNote, string? Error)
{
    public bool HasRenamedNote => RenamedNote is not null;

    public bool HasError => Error is not null;
}

/// <summary>
/// 「從 JSON 匯入」（M16 D5）：貼上 → 解析預覽 → 逐筆寫入。預覽由 <see cref="McpConfigImporter"/> 產生，這裡只負責畫面與寫入。
/// </summary>
public sealed partial class McpImportViewModel : ObservableObject
{
    private readonly IModelCatalog _catalog;
    private List<McpImportItem> _items = [];

    public McpImportViewModel(IModelCatalog catalog)
    {
        _catalog = catalog;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanParse))]
    private string _text = "";

    /// <summary>整份 JSON 解析失敗的原因（例如不是 JSON、找不到 mcpServers）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasParseError))]
    private string? _parseError;

    /// <summary>寫入中途失敗的訊息（「已匯入 n 筆，第 n+1 筆失敗：原因」）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportError))]
    private string? _importError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    private bool _isImporting;

    /// <summary>預覽清單（每一列對應一筆還沒匯入的解析結果）。</summary>
    public ObservableCollection<McpImportRow> Rows { get; } = [];

    /// <summary>可以匯入的筆數（沒有錯誤的列）。</summary>
    public int ImportCount => _items.Count(i => i.Error is null);

    public bool CanParse => Text.Trim().Length > 0;

    public bool CanImport => ImportCount > 0 && !IsImporting;

    public bool HasParseError => ParseError is not null;

    public bool HasImportError => ImportError is not null;

    public string ImportText => string.Format(CultureInfo.CurrentCulture, Strings.Mcp_ImportConfirm, ImportCount);

    /// <summary>全部寫入成功時回報第一筆新伺服器的 Id，讓擁有者選取它。</summary>
    public event Action<string>? Imported;

    /// <summary>使用者按「取消」。</summary>
    public event Action? Cancelled;

    [RelayCommand(CanExecute = nameof(CanParse))]
    private void Parse()
    {
        var preview = McpConfigImporter.Parse(Text, _catalog.Current.McpServers);
        _items = preview.Items.ToList();
        ParseError = preview.Error;
        ImportError = null;
        RebuildRows();
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        IsImporting = true;
        ImportError = null;
        string? firstId = null;
        var imported = 0;
        try
        {
            foreach (var item in _items.Where(i => i.Error is null).ToList())
            {
                try
                {
                    await _catalog.SaveMcpServerAsync(item.Server, HasAnySecret(item.Secrets) ? item.Secrets : null);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or SecretStoreException)
                {
                    // 只顯示我們自己的繁中原因；其他情況不帶原始例外訊息（可能含路徑或祕密）
                    var reason = ex is ArgumentException or InvalidOperationException ? ex.Message : Strings.Settings_SaveFailed;
                    ImportError = string.Format(CultureInfo.CurrentCulture, Strings.Mcp_ImportPartialFailure, imported, imported + 1, reason);
                    return;
                }

                firstId ??= item.Server.Id;
                imported++;
                _items.Remove(item);   // 已匯入的不留在預覽裡，重按一次不會重複匯入
                RebuildRows();
            }

            if (firstId is not null)
                Imported?.Invoke(firstId);
        }
        finally
        {
            IsImporting = false;
        }
    }

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke();

    partial void OnIsImportingChanged(bool value) => ImportCommand.NotifyCanExecuteChanged();

    partial void OnTextChanged(string value) => ParseCommand.NotifyCanExecuteChanged();

    private void RebuildRows()
    {
        Rows.Clear();
        foreach (var item in _items)
        {
            var isStdio = item.Server.Transport == McpTransportKind.Stdio;
            var transport = isStdio ? Strings.Mcp_TransportStdio : Strings.Mcp_TransportHttp;
            var target = (isStdio ? item.Server.Command : item.Server.Url) ?? "";
            var renamed = item.RenamedFrom is null ? null
                : string.Format(CultureInfo.CurrentCulture, Strings.Mcp_ImportRenamed, item.Server.Name);
            Rows.Add(new McpImportRow(item.Server.Name, transport, target, renamed, item.Error));
        }

        OnPropertyChanged(nameof(ImportCount));
        OnPropertyChanged(nameof(ImportText));
        OnPropertyChanged(nameof(CanImport));
        ImportCommand.NotifyCanExecuteChanged();
    }

    private static bool HasAnySecret(McpServerSecrets secrets) => secrets.Env.Count > 0 || secrets.Headers.Count > 0;
}
