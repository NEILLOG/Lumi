using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Lumi.App.Resources;
using Lumi.Infrastructure;
using Lumi.Infrastructure.Startup;

namespace Lumi.App.Services;

/// <summary>啟動失敗時說明視窗要顯示的內容（M9 9C）。純資料與字串處理，不碰 UI，不依賴 DI。</summary>
public sealed record StartupFailureReport(StartupFailureKind Kind, string Reason, string LogDirectory, string Details)
{
    /// <summary>
    /// 從例外組出說明。詳細資訊包含時間、版本、作業系統、分類、資料夾位置與例外的型別與訊息；
    /// 訊息經過 <see cref="SecretRedactor"/>（金鑰樣式與使用者資料夾），不包含聊天內容、金鑰或設定檔內文。
    /// </summary>
    public static StartupFailureReport Create(Exception exception, StartupEnvironment environment)
    {
        var kind = StartupFailureClassifier.Classify(exception);
        var details = new StringBuilder();
        details.AppendLine("Lumi 啟動失敗");
        details.AppendLine($"時間：{environment.Now:yyyy-MM-dd HH:mm:ss zzz}");
        details.AppendLine($"Lumi 版本：{environment.AppVersion}");
        details.AppendLine($"作業系統：{environment.OperatingSystem}");
        details.AppendLine($"執行環境：{environment.Runtime}");
        details.AppendLine($"原因分類：{kind}");
        details.AppendLine($"資料資料夾：{environment.DataDirectory}");
        details.AppendLine($"記錄檔資料夾：{environment.LogDirectory}");

        var depth = 0;
        foreach (var ex in Unwrap(exception))
        {
            var label = depth == 0 ? "例外" : $"內層例外 {depth}";
            details.AppendLine($"{label}：{ex.GetType().FullName}（HResult 0x{ex.HResult:X8}）");
            var message = SecretRedactor.Redact(ex.Message, environment.HomeDirectory);
            if (!string.IsNullOrWhiteSpace(message))
                details.AppendLine($"  訊息：{message}");
            depth++;
            if (depth >= MaxDepth)
                break;
        }

        return new StartupFailureReport(kind, ReasonFor(kind), environment.LogDirectory, details.ToString().TrimEnd());
    }

    private const int MaxDepth = 5;

    public static string ReasonFor(StartupFailureKind kind) => kind switch
    {
        StartupFailureKind.DatabaseLocked => Strings.StartupFailure_Reason_DatabaseLocked,
        StartupFailureKind.AccessDenied => Strings.StartupFailure_Reason_AccessDenied,
        StartupFailureKind.DiskFull => Strings.StartupFailure_Reason_DiskFull,
        StartupFailureKind.DatabaseTooNew => Strings.StartupFailure_Reason_DatabaseTooNew,
        StartupFailureKind.SettingsUnreadable => Strings.StartupFailure_Reason_SettingsUnreadable,
        _ => Strings.StartupFailure_Reason_Other,
    };

    private static IEnumerable<Exception> Unwrap(Exception exception)
    {
        yield return exception;
        var inner = exception.InnerException;
        while (inner is not null)
        {
            yield return inner;
            inner = inner.InnerException;
        }
    }
}

/// <summary>說明視窗需要的環境資訊，由呼叫端在啟動時取得（不從 DI 取）。</summary>
public sealed record StartupEnvironment(
    string DataDirectory,
    string LogDirectory,
    string AppVersion,
    string OperatingSystem,
    string Runtime,
    string HomeDirectory,
    DateTimeOffset Now)
{
    public static StartupEnvironment Current() => new(
        LumiPaths.DataDirectory,
        LumiPaths.LogDirectory,
        typeof(App).Assembly.GetName().Version?.ToString() ?? "未知",
        RuntimeInformation.OSDescription,
        RuntimeInformation.FrameworkDescription,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        DateTimeOffset.Now);
}

/// <summary>
/// 寫進說明或 log 之前的遮蔽：使用者資料夾換成 <c>~</c>；金鑰樣式（<c>sk-…</c>、Bearer、<c>api_key=…</c>）與
/// 很長的英數字串換成 <c>[遮蔽]</c>。寧可多遮，不讓金鑰出現在剪貼簿裡。
/// </summary>
public static class SecretRedactor
{
    private const string Mask = "[遮蔽]";

    private static readonly Regex KeyLike = new(@"sk-[A-Za-z0-9_\-]{6,}", RegexOptions.CultureInvariant);
    private static readonly Regex Labelled = new(@"(?i)(bearer\s+|api[_\-]?key\s*[=:]\s*|token\s*[=:]\s*|secret\s*[=:]\s*)\S+", RegexOptions.CultureInvariant);
    private static readonly Regex LongToken = new(@"[A-Za-z0-9_\-]{32,}", RegexOptions.CultureInvariant);

    public static string Redact(string? text, string? homeDirectory)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var result = text;
        if (!string.IsNullOrEmpty(homeDirectory) && homeDirectory.Length > 1)
            result = result.Replace(homeDirectory, "~", StringComparison.Ordinal);

        result = KeyLike.Replace(result, Mask);
        result = Labelled.Replace(result, m => m.Groups[1].Value + Mask);
        result = LongToken.Replace(result, Mask);
        return result;
    }
}
