using Lumi.Platform.Shell;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Platform.Tests;

public class ShellEnvironmentTests
{
    [SkippableFact]
    public async Task Returns_current_environment_on_non_mac()
    {
        Skip.If(OperatingSystem.IsMacOS(), "macOS 會讀登入 shell，見 Mac_path_contains_usr_local_or_homebrew");
        var env = new ShellEnvironment(NullLogger<ShellEnvironment>.Instance);

        var result = await env.GetAsync(CancellationToken.None);

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
            Assert.Equal(path, result["PATH"]);
    }

    [Fact]
    public void Parses_null_separated_output()
    {
        const string output = "HOME=/Users/me\0EMPTY=\0EQ=a=b=c\0PATH=/usr/bin:/bin\0";

        var result = ShellEnvironment.Parse(output);

        Assert.Equal(4, result.Count);
        Assert.Equal("/Users/me", result["HOME"]);
        Assert.Equal(string.Empty, result["EMPTY"]);
        Assert.Equal("a=b=c", result["EQ"]);
        Assert.Equal("/usr/bin:/bin", result["PATH"]);
    }

    [Fact]
    public void Parse_skips_entries_without_a_valid_name()
    {
        // 登入 shell 可能在 env 之前印出其他文字（第一個項目會黏著前面的文字）
        const string output = "Welcome to zsh\nPATH=/usr/bin\0no-equals\0=novalue\0a-b=1\0OK_1=yes\0";

        var result = ShellEnvironment.Parse(output);

        Assert.Equal(new[] { "OK_1", "PATH" }, result.Keys.Order().ToArray());
        Assert.Equal("/usr/bin", result["PATH"]);
    }

    [SkippableFact]
    public async Task Falls_back_when_shell_fails()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "只有 macOS 會執行登入 shell");
        var env = new ShellEnvironment(NullLogger<ShellEnvironment>.Instance, "/nonexistent/lumi-test-shell");

        var result = await env.GetAsync(CancellationToken.None);

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
            Assert.Equal(path, result["PATH"]);
    }

    [SkippableFact]
    public async Task Mac_path_contains_usr_local_or_homebrew()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "只在 macOS 執行");
        var env = new ShellEnvironment(NullLogger<ShellEnvironment>.Instance);

        var result = await env.GetAsync(CancellationToken.None);

        var path = result["PATH"];
        Assert.True(path.Contains("/usr/local/bin") || path.Contains("/opt/homebrew/bin"), "PATH 沒有 /usr/local/bin 也沒有 /opt/homebrew/bin");
    }
}
