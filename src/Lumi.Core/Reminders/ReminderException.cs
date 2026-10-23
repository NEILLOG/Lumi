namespace Lumi.Core.Reminders;

/// <summary>
/// 提醒相關的業務錯誤（超過上限、時間在過去等）。訊息是帶給模型看的繁體中文，可以直接放進工具結果。
/// </summary>
public sealed class ReminderException(string message) : Exception(message);
