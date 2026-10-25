using System.Collections;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Lumi.Core.Platform;
using Microsoft.Extensions.Logging;

// 測試需要呼叫 Parse 與可指定 shell 路徑的建構式
[assembly: InternalsVisibleTo("Lumi.Platform.Tests")]

namespace Lumi.Platform.Shell;

/// <summary>
/// 啟動外部程序用的環境變數（M16 D14）。macOS 用登入 shell（<c>$SHELL -ilc "env -0"</c>）取得 PATH 等變數，
/// 再和目前程序的環境變數合併（shell 的優先）；失敗或逾時就只用目前程序的環境變數。
/// 其他平台＝目前程序的環境變數。結果只算一次並快取。
/// </summary>
/// <remarks>
/// 環境變數的值可能含祕密，所以 log 只寫失敗的原因，不寫任何值。
/// </remarks>
public sealed class ShellEnvironment : IShellEnvironment
{
    private static readonly TimeSpan ShellTimeout = TimeSpan.FromSeconds(5);
    private static readonly Regex ValidName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    private readonly ILogger<ShellEnvironment> _logger;
    private readonly string? _shellPath;
    private readonly Lazy<Task<IReadOnlyDictionary<string, string>>> _result;

    public ShellEnvironment(ILogger<ShellEnvironment> logger)
        : this(logger, shellPath: null)
    {
    }

    /// <param name="shellPath">登入 shell 的路徑；null 時讀 <c>SHELL</c>，沒有就用 <c>/bin/zsh</c>。</param>
    internal ShellEnvironment(ILogger<ShellEnvironment> logger, string? shellPath)
    {
        _logger = logger;
        _shellPath = shellPath;
        _result = new Lazy<Task<IReadOnlyDictionary<string, string>>>(() => Task.Run(LoadAsync));
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken ct)
    {
        return await _result.Value.WaitAsync(ct);
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadAsync()
    {
        var current = CurrentEnvironment();
        if (!OperatingSystem.IsMacOS())
            return current;

        try
        {
            var fromShell = await ReadLoginShellAsync(ResolveShellPath());
            var merged = new Dictionary<string, string>(current, Comparer);
            foreach (var (key, value) in fromShell)
                merged[key] = value;
            return merged;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "讀取登入 shell 的環境變數失敗，改用目前程序的環境變數");
            return current;
        }
    }

    private string ResolveShellPath()
    {
        if (!string.IsNullOrEmpty(_shellPath))
            return _shellPath;
        var fromEnv = Environment.GetEnvironmentVariable("SHELL");
        return string.IsNullOrEmpty(fromEnv) ? "/bin/zsh" : fromEnv;
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadLoginShellAsync(string shell)
    {
        var psi = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-ilc");
        psi.ArgumentList.Add("env -0");

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("無法啟動登入 shell");
        process.StandardInput.Close(); // 不讓 shell 等待輸入
        var stdout = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync(); // 排空 stderr，避免緩衝區塞住

        using var timeout = new CancellationTokenSource(ShellTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"登入 shell 在 {ShellTimeout.TotalSeconds} 秒內沒有結束");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"登入 shell 結束代碼 {process.ExitCode}");

        return Parse(await stdout);
    }

    /// <summary>解析 <c>env -0</c> 的輸出（以 \0 分隔的 KEY=VALUE）。登入 shell 可能在前面印出其他文字，名稱不合法的項目會略過。</summary>
    internal static IReadOnlyDictionary<string, string> Parse(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0)
                continue;
            // 第一個項目前面可能黏著 shell 印出的文字（沒有 \0 分隔）；名稱不會含換行，所以只取最後一個換行之後的部分
            var key = entry[(entry.LastIndexOf('\n', eq - 1) + 1)..eq];
            if (!ValidName.IsMatch(key))
                continue;
            result[key] = entry[(eq + 1)..];
        }
        return result;
    }

    private static Dictionary<string, string> CurrentEnvironment()
    {
        var result = new Dictionary<string, string>(Comparer);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
                result[key] = value;
        }
        return result;
    }

    // Windows 的環境變數名稱不分大小寫
    private static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
