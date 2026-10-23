using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Lumi.Infrastructure.Logging;

/// <summary>
/// 寫進使用者資料夾 <c>logs/lumi-yyyy-MM-dd.log</c> 的最小記錄器（M9 9C）：沒有這個，啟動失敗與未處理例外
/// 在打包後的程式裡就沒有任何地方可以看。Information 以上（Microsoft.* 只記 Warning 以上），一行一則，例外接在後面。
/// 寫檔失敗時吞掉：記錄不能讓程式本身出錯。沒有外部套件。
/// </summary>
public sealed class LumiFileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();

    public LumiFileLoggerProvider(string directory, Func<DateTimeOffset>? now = null)
    {
        _directory = directory;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>目前這一天的記錄檔完整路徑（測試與說明用）。</summary>
    public string CurrentFilePath => Path.Combine(_directory, $"lumi-{_now():yyyy-MM-dd}.log");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    internal void Append(LogLevel level, string category, string message, Exception? exception)
    {
        var builder = new StringBuilder();
        builder.Append(_now().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(" [").Append(LevelName(level)).Append("] ")
            .Append(category).Append(": ").Append(message).Append(Environment.NewLine);
        if (exception is not null)
            builder.Append(exception).Append(Environment.NewLine);

        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(CurrentFilePath, builder.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 記錄失敗不影響程式
        }
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "Trace",
        LogLevel.Debug => "Debug",
        LogLevel.Information => "Information",
        LogLevel.Warning => "Warning",
        LogLevel.Error => "Error",
        LogLevel.Critical => "Critical",
        _ => "None",
    };

    private sealed class FileLogger(LumiFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
        {
            if (logLevel == LogLevel.None)
                return false;
            var minimum = category.StartsWith("Microsoft", StringComparison.Ordinal) ? LogLevel.Warning : LogLevel.Information;
            return logLevel >= minimum;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            provider.Append(logLevel, category, formatter(state, exception), exception);
        }
    }
}
