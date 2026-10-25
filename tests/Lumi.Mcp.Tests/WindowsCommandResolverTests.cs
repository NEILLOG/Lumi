namespace Lumi.Mcp.Tests;

public sealed class WindowsCommandResolverTests
{
    [Fact]
    public void Finds_the_cmd_shim_on_the_path()
    {
        var expected = Path.Combine(@"C:\nodejs", "npx.CMD");   // PATHEXT 的大小寫照用

        var found = SdkMcpConnectionFactory.ResolveWindowsCommand(
            "npx", @"C:\tools;C:\nodejs", ".COM;.EXE;.CMD", p => p == expected);

        Assert.Equal(expected, found);
    }

    [Fact]
    public void Tries_the_extensions_in_pathext_order()
    {
        var com = Path.Combine(@"C:\bin", "tool.COM");
        var exe = Path.Combine(@"C:\bin", "tool.EXE");

        var found = SdkMcpConnectionFactory.ResolveWindowsCommand(
            "tool", @"C:\bin", ".COM;.EXE", p => p == com || p == exe);

        Assert.Equal(com, found);
    }

    [Fact]
    public void A_command_that_already_has_an_extension_is_left_alone()
    {
        var found = SdkMcpConnectionFactory.ResolveWindowsCommand(
            "npx.cmd", @"C:\nodejs", ".COM;.CMD", _ => true);

        Assert.Null(found);
    }

    [Fact]
    public void Returns_null_when_nothing_matches()
    {
        var found = SdkMcpConnectionFactory.ResolveWindowsCommand(
            "nothere", @"C:\tools", ".COM;.EXE", _ => false);

        Assert.Null(found);
    }

    [Fact]
    public void A_command_with_a_directory_only_looks_in_that_place()
    {
        var expected = @"C:\bin\tool.EXE";

        var found = SdkMcpConnectionFactory.ResolveWindowsCommand(
            @"C:\bin\tool", @"D:\other", ".COM;.EXE", p => p == expected);

        Assert.Equal(expected, found);
    }
}
