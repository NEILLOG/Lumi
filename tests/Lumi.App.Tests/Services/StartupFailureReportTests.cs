using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.Infrastructure.Persistence;
using Lumi.Infrastructure.Startup;
using Microsoft.Data.Sqlite;

namespace Lumi.App.Tests.Services;

public class StartupFailureReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 9, 30, 0, TimeSpan.FromHours(8));

    private static StartupEnvironment Environment(string home = "/Users/tester") => new(
        DataDirectory: "/Users/tester/Library/Application Support/Lumi",
        LogDirectory: "/Users/tester/Library/Application Support/Lumi/logs",
        AppVersion: "1.2.3.0",
        OperatingSystem: "macOS 26.0",
        Runtime: ".NET 10.0.0",
        HomeDirectory: home,
        Now: Now);

    [Fact]
    public void DatabaseLocked_SaysLumiMayAlreadyBeRunning()
    {
        var report = StartupFailureReport.Create(new SqliteException("database is locked", 5), Environment());

        Assert.Equal(StartupFailureKind.DatabaseLocked, report.Kind);
        Assert.Contains("Lumi 可能已經在執行", report.Reason);
        Assert.Equal(Strings.StartupFailure_Reason_DatabaseLocked, report.Reason);
    }

    [Fact]
    public void AccessDenied_GetsItsOwnSentence()
    {
        var report = StartupFailureReport.Create(new UnauthorizedAccessException("拒絕存取"), Environment());

        Assert.Equal(StartupFailureKind.AccessDenied, report.Kind);
        Assert.Equal(Strings.StartupFailure_Reason_AccessDenied, report.Reason);
    }

    [Fact]
    public void DatabaseTooNew_GetsItsOwnSentence()
    {
        var report = StartupFailureReport.Create(new DatabaseTooNewException(9, 4), Environment());

        Assert.Equal(StartupFailureKind.DatabaseTooNew, report.Kind);
        Assert.Equal(Strings.StartupFailure_Reason_DatabaseTooNew, report.Reason);
    }

    [Fact]
    public void AllKinds_HaveDistinctSentences()
    {
        var sentences = Enum.GetValues<StartupFailureKind>().Select(StartupFailureReport.ReasonFor).ToList();

        Assert.Equal(sentences.Count, sentences.Distinct().Count());
        Assert.All(sentences, s => Assert.False(s.StartsWith('!')));   // 缺資源時 Strings 會回 "!名稱!"
    }

    [Fact]
    public void Details_ListTheFactsNeededForABugReport_AndTheLogFolder()
    {
        var report = StartupFailureReport.Create(new InvalidOperationException("某件事失敗了"), Environment());

        Assert.Contains("Lumi 版本：1.2.3.0", report.Details);
        Assert.Contains("作業系統：macOS 26.0", report.Details);
        Assert.Contains("原因分類：Other", report.Details);
        Assert.Contains("記錄檔資料夾：/Users/tester/Library/Application Support/Lumi/logs", report.Details);
        Assert.Contains("System.InvalidOperationException", report.Details);
        Assert.Equal(StartupFailureKind.Other, report.Kind);
        Assert.Equal(Environment().LogDirectory, report.LogDirectory);
    }

    [Fact]
    public void Details_DoNotContainKeysOrTheExceptionMessageVerbatim_WhenItHoldsOne()
    {
        // 假金鑰：只為了驗證遮蔽，不是真的金鑰
        const string fakeKey = "sk-ant-api03-FAKEKEY1234567890ABCDEF";
        var exception = new InvalidOperationException($"設定錯誤：金鑰 {fakeKey} 格式不對");

        var report = StartupFailureReport.Create(exception, Environment());

        Assert.DoesNotContain("FAKEKEY", report.Details);
        Assert.DoesNotContain(fakeKey, report.Details);
        Assert.Contains("[遮蔽]", report.Details);
    }

    [Fact]
    public void Details_MaskBearerTokensAndLongTokenLikeStrings()
    {
        var exception = new InvalidOperationException(
            "Authorization: Bearer FAKEBEARER987654 與 api_key=FAKEAPIKEY555 還有 abcdefghijklmnopqrstuvwxyz0123456789ABCD");

        var report = StartupFailureReport.Create(exception, Environment());

        Assert.DoesNotContain("FAKEBEARER987654", report.Details);
        Assert.DoesNotContain("FAKEAPIKEY555", report.Details);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz0123456789ABCD", report.Details);
    }

    [Fact]
    public void Details_ReplaceTheHomeDirectoryInMessages_WithTilde()
    {
        var exception = new IOException("無法開啟 /Users/tester/secret/notes.txt");

        var report = StartupFailureReport.Create(exception, Environment());

        Assert.Contains("~/secret/notes.txt", report.Details);
        Assert.DoesNotContain("/Users/tester/secret", report.Details);
    }

    [Fact]
    public void Details_IncludeInnerExceptionTypes()
    {
        var exception = new InvalidOperationException("外層", new TimeoutException("內層"));

        var report = StartupFailureReport.Create(exception, Environment());

        Assert.Contains("內層例外 1：System.TimeoutException", report.Details);
    }

    [Fact]
    public void Details_AreBoundedInDepth()
    {
        Exception chain = new InvalidOperationException("最內層");
        for (var i = 0; i < 20; i++)
            chain = new InvalidOperationException($"第 {i} 層", chain);

        var report = StartupFailureReport.Create(chain, Environment());

        Assert.DoesNotContain("內層例外 6", report.Details);
    }

    [Theory]
    [InlineData("sk-ant-api03-ABCDEF123456", "[遮蔽]")]
    [InlineData("Bearer abc.def", "Bearer [遮蔽]")]
    [InlineData("token: xyz", "token: [遮蔽]")]
    [InlineData("nothing to hide", "nothing to hide")]
    [InlineData("", "")]
    public void SecretRedactor_MasksKeyShapes(string input, string expected) =>
        Assert.Equal(expected, SecretRedactor.Redact(input, homeDirectory: null));

    [Fact]
    public void SecretRedactor_IgnoresAnEmptyHome()
    {
        Assert.Equal("a/b", SecretRedactor.Redact("a/b", homeDirectory: ""));
        Assert.Equal("/ x", SecretRedactor.Redact("/ x", homeDirectory: "/"));   // 根目錄不替換
    }

    [Fact]
    public void Environment_Current_HasThePaths()
    {
        var environment = StartupEnvironment.Current();

        Assert.Equal(Lumi.Infrastructure.LumiPaths.DataDirectory, environment.DataDirectory);
        Assert.EndsWith("logs", environment.LogDirectory);
        Assert.False(string.IsNullOrWhiteSpace(environment.AppVersion));
    }
}
