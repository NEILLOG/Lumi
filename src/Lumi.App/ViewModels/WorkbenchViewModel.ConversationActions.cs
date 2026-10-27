using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.App.Services;
using Microsoft.Extensions.Logging;

namespace Lumi.App.ViewModels;

/// <summary>
/// 「⋯」的對話動作：重新命名、匯出為 Markdown、刪除這則對話（小精靈的專屬對話改成「清除內容」，見 SpriteConversation.cs）。
/// 重新命名的規則（清理、長度上限）與刪除的流程（確認、切到下一則）都在 <see cref="HistoryViewModel"/>，這裡只負責接線。
/// </summary>
public partial class WorkbenchViewModel
{
    private readonly IFilePickerService? _filePicker;
    private readonly IRenameService? _rename;

    // 啟用條件：空對話（沒有任何訊息）三項全部停用；回應中只有重新命名可用（ui-usage-and-topbar.md 1.2）
    public bool RenameConversationEnabled => _rename is not null && CurrentConversationId is not null && Messages.Count > 0;

    public bool ExportConversationEnabled => _filePicker is not null && !IsResponding && CurrentConversationId is not null && Messages.Count > 0;

    public bool DeleteConversationEnabled => !IsResponding && CurrentConversationId is not null && Messages.Count > 0 && History.CanDeleteConversation && !IsSpriteConversation;

    /// <summary>訊息數、回應狀態變了：更新「⋯」各項的啟用狀態。</summary>
    private void NotifyConversationActions()
    {
        OnPropertyChanged(nameof(RenameConversationEnabled));
        OnPropertyChanged(nameof(ExportConversationEnabled));
        OnPropertyChanged(nameof(DeleteConversationEnabled));
        OnPropertyChanged(nameof(ClearConversationEnabled));
        ClearConversationCommand.NotifyCanExecuteChanged();
        RenameConversationCommand.NotifyCanExecuteChanged();
        ExportConversationCommand.NotifyCanExecuteChanged();
        DeleteConversationCommand.NotifyCanExecuteChanged();
    }

    private bool CanRenameConversation() => RenameConversationEnabled;

    /// <summary>
    /// 輸入框預填目前標題；儲存後的標題經過與清單改名相同的清理（去頭尾空白、上限字數）。空白或沒有變動＝取消。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRenameConversation))]
    private async Task RenameConversationAsync()
    {
        if (CurrentConversationId is not { } id || _rename is null)
            return;

        var entered = await _rename.PromptAsync(Title, History.TitleMaxLength);
        var title = HistoryViewModel.NormalizeTitle(entered, History.TitleMaxLength);
        // 對話在輸入框開著的時候被換掉了：不改別的對話的名字
        if (title is null || title == Title || id != CurrentConversationId)
            return;

        await History.RenameConversationAsync(id, title);
    }

    private bool CanExportConversation() => ExportConversationEnabled;

    /// <summary>從資料庫讀訊息，轉成 Markdown 寫到使用者選的位置。取消選擇就什麼都不做；成功或失敗都用提示列說明。</summary>
    [RelayCommand(CanExecute = nameof(CanExportConversation))]
    private async Task ExportConversationAsync()
    {
        if (CurrentConversationId is not { } id || _filePicker is null)
            return;

        var title = Title;
        try
        {
            var messages = await _store.GetMessagesAsync(id);
            var markdown = ConversationMarkdownExporter.Export(title, messages, _time.GetUtcNow());
            var path = await _filePicker.SaveFileAsync(ConversationMarkdownExporter.SuggestedFileName(title), "md");
            if (path is null)
                return;

            await File.WriteAllTextAsync(path, markdown);
            Notice = string.Format(CultureInfo.CurrentCulture, Strings.Export_DoneFormat, path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "匯出對話失敗");
            Notice = Strings.Export_Failed;
        }
    }

    private bool CanDeleteConversation() => DeleteConversationEnabled;

    /// <summary>刪除前確認；確認後的流程（資料庫、清單、切到下一則）與清單的刪除相同（<see cref="HistoryViewModel.DeleteConversationAsync"/>）。</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteConversation))]
    private Task DeleteConversationAsync() =>
        CurrentConversationId is { } id ? History.DeleteConversationAsync(id, Title) : Task.CompletedTask;
}
