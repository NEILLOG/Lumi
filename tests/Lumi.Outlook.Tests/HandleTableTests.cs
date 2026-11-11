namespace Lumi.Outlook.Tests;

public class HandleTableTests
{
    [Fact]
    public void Handle_table_reuses_id_and_evicts_oldest()
    {
        var table = new HandleTable();

        var first = table.Add("c1", 'm', "dup", "store");
        var again = table.Add("c1", 'm', "dup", "store");
        Assert.Equal("m1", first);
        Assert.Equal(first, again);

        // 再加 500 個不同的：連同 dup 共 501 個，最舊的 m1 會被丟掉
        for (var i = 1; i <= 500; i++)
            table.Add("c1", 'm', $"x{i}", "store");

        Assert.False(table.TryGet("c1", "m1", out _));
        Assert.True(table.TryGet("c1", "m2", out var second));
        Assert.Equal("x1", second.EntryId);
        Assert.True(table.TryGet("c1", "m501", out var last));
        Assert.Equal("x500", last.EntryId);
    }

    [Fact]
    public void Handles_are_per_conversation()
    {
        var table = new HandleTable();
        var a = table.Add("A", 'm', "entry-a", "store-a");
        var b = table.Add("B", 'm', "entry-b", "store-b");

        Assert.Equal("m1", a);
        Assert.Equal("m1", b);
        Assert.True(table.TryGet("A", "m1", out var fromA));
        Assert.Equal("entry-a", fromA.EntryId);
        Assert.True(table.TryGet("B", "m1", out var fromB));
        Assert.Equal("entry-b", fromB.EntryId);
        Assert.False(table.TryGet("B", "m2", out _));
    }

    [Fact]
    public void Prefixes_number_separately()
    {
        var table = new HandleTable();

        Assert.Equal("m1", table.Add("c", 'm', "mail-1", "s"));
        Assert.Equal("e1", table.Add("c", 'e', "event-1", "s"));
        Assert.Equal("m2", table.Add("c", 'm', "mail-2", "s"));
        Assert.Equal("e2", table.Add("c", 'e', "event-2", "s"));
    }

    [Fact]
    public void Unknown_id_returns_false_with_default_handle()
    {
        var table = new HandleTable();

        Assert.False(table.TryGet("missing", "m1", out var handle));
        Assert.Null(handle.EntryId);
        Assert.Null(handle.StoreId);
    }
}
