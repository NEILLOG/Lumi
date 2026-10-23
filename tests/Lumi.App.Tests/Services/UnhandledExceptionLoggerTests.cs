using Lumi.App.Services;
using Microsoft.Extensions.Logging;

namespace Lumi.App.Tests.Services;

/// <summary>
/// 全域未處理例外的處理邏輯：直接呼叫事件處理方法，確認有記 log、正確標記 observed／不標記 Handled。
/// 不呼叫 <see cref="UnhandledExceptionLogger.Attach"/>：那會改掉整個測試程序的事件（之後的未觀察例外會被吞掉）。
/// </summary>
public class UnhandledExceptionLoggerTests
{
    private sealed class Entry(LogLevel level, Exception? exception, string message)
    {
        public LogLevel Level { get; } = level;
        public Exception? Exception { get; } = exception;
        public string Message { get; } = message;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<Entry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(new Entry(logLevel, exception, formatter(state, exception)));
    }

    [Fact]
    public void AppDomainException_IsLoggedAsCritical_WithTheException()
    {
        var logger = new CapturingLogger<UnhandledExceptionLogger>();
        var hooks = new UnhandledExceptionLogger(logger);
        var boom = new InvalidOperationException("炸了");

        hooks.OnAppDomainUnhandledException(this, new UnhandledExceptionEventArgs(boom, isTerminating: true));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Critical, entry.Level);
        Assert.Same(boom, entry.Exception);
        Assert.Contains("即將結束", entry.Message);
    }

    [Fact]
    public void AppDomainNonExceptionObject_IsLoggedWithoutAnException()
    {
        var logger = new CapturingLogger<UnhandledExceptionLogger>();
        var hooks = new UnhandledExceptionLogger(logger);

        hooks.OnAppDomainUnhandledException(this, new UnhandledExceptionEventArgs("不是例外", isTerminating: false));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Critical, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Contains("String", entry.Message);
    }

    [Fact]
    public void UnobservedTaskException_IsLogged_AndMarkedObserved()
    {
        var logger = new CapturingLogger<UnhandledExceptionLogger>();
        var hooks = new UnhandledExceptionLogger(logger);
        var failure = new AggregateException(new InvalidOperationException("背景工作失敗"));
        var args = new UnobservedTaskExceptionEventArgs(failure);

        hooks.OnUnobservedTaskException(this, args);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(failure, entry.Exception);
        Assert.True(args.Observed);   // 只有這個事件可以標記為已觀察
    }

    [Fact]
    public void UiThreadException_IsLogged_AndNotMarkedHandled()
    {
        // 這裡沒有 Handled 可以標記（UI 例外的處理交給 Avalonia）：只要確認有記 log、而且處理方法不吞掉例外
        var logger = new CapturingLogger<UnhandledExceptionLogger>();
        var hooks = new UnhandledExceptionLogger(logger);
        var boom = new NullReferenceException("UI 壞掉");

        hooks.OnUiThreadException(boom);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(boom, entry.Exception);
        Assert.Contains("UI 執行緒", entry.Message);
    }

    [Fact]
    public void RepeatedUiExceptions_EachGetAnEntry()
    {
        var logger = new CapturingLogger<UnhandledExceptionLogger>();
        var hooks = new UnhandledExceptionLogger(logger);

        hooks.OnUiThreadException(new InvalidOperationException("一"));
        hooks.OnUiThreadException(new InvalidOperationException("二"));

        Assert.Equal(2, logger.Entries.Count);
    }
}
