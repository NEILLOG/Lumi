using System.Globalization;
using Avalonia.Controls;
using Lumi.App.Resources;
using Lumi.App.ViewModels;
using Lumi.Core.ContextNotes;

namespace Lumi.App.Views;

/// <summary>
/// 啟動研究本的對話框（仿 <see cref="RenameDialog"/>）：回傳 <see cref="ContextNoteStartRequest"/>，取消回 null。
/// 目標空白時「開始」停用；對話沒有訊息時「從目前的對話整理」停用，改選「從空白開始」。
/// </summary>
public partial class ContextNoteStartDialog : Window
{
    /// <param name="suggestedGoal">預先填好的目標（建議卡片帶來的），沒有就是空字串。</param>
    /// <param name="messageCount">這則對話的訊息數；0 時第一張卡片停用。</param>
    /// <param name="modelText">目前模型的「供應商 · model ID」；null＝沒有可用的模型。</param>
    public ContextNoteStartDialog(string suggestedGoal, int messageCount, string? modelText)
    {
        InitializeComponent();

        GoalBox.MaxLength = ContextNoteLimits.GoalMaxLength;
        var goal = (suggestedGoal ?? "").Trim();
        GoalBox.Text = goal.Length > ContextNoteLimits.GoalMaxLength ? goal[..ContextNoteLimits.GoalMaxLength] : goal;

        var hasMessages = messageCount > 0;
        FromConversationCard.IsEnabled = hasMessages;
        FromConversationDesc.Text = hasMessages
            ? string.Format(CultureInfo.CurrentCulture, Strings.ContextNote_Start_FromConversationDescFormat, messageCount)
            : Strings.ContextNote_Start_FromConversationEmpty;
        FromConversationCard.IsChecked = hasMessages;
        BlankCard.IsChecked = !hasMessages;

        CostNote.Text = modelText is null
            ? Strings.ContextNote_Start_NoModel
            : string.Format(CultureInfo.CurrentCulture, Strings.ContextNote_Start_CostNoteFormat, modelText);

        // 用屬性變動而不是 TextChanged：程式設定文字也要更新「開始」的狀態
        GoalBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                UpdateStartEnabled();
        };
        UpdateStartEnabled();

        Opened += (_, _) => GoalBox.Focus();
        StartButton.Click += (_, _) => Close(CurrentRequest());
        CancelButton.Click += (_, _) => Close(null);
    }

    private void UpdateStartEnabled() => StartButton.IsEnabled = GoalText().Length > 0;

    private string GoalText() => (GoalBox.Text ?? "").Trim();

    /// <summary>目前的選擇；目標空白時不應該被呼叫（按鈕已停用），這裡仍然不回傳空目標。</summary>
    private ContextNoteStartRequest? CurrentRequest()
    {
        var goal = GoalText();
        if (goal.Length == 0)
            return null;
        var mode = BlankCard.IsChecked == true ? ContextNoteStartMode.Blank : ContextNoteStartMode.FromConversation;
        return new ContextNoteStartRequest(goal, mode);
    }
}
