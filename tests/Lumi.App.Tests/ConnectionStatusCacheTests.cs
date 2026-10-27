using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.Core.Chat;
using Lumi.Core.Models;

namespace Lumi.App.Tests;

public class ConnectionStatusCacheTests
{
    [Fact]
    public void Mark_ChangesStatus_ButKeepsModelListFromTestConnection()
    {
        var cache = new ConnectionStatusCache();
        cache.Set("c", ConnectionTestResult.Ok(["a", "b"]));

        cache.Mark("c", ConnectionStatus.Failed);

        var entry = cache.Get("c");
        Assert.Equal(ConnectionStatus.Failed, entry.Status);
        Assert.Equal(["a", "b"], entry.ModelIds);
    }

    [Fact]
    public void Mark_RaisesChangedOnlyWhenStatusActuallyChanges()
    {
        var cache = new ConnectionStatusCache();
        var changes = 0;
        cache.Changed += (_, _) => changes++;

        cache.Mark("c", ConnectionStatus.Ok);
        cache.Mark("c", ConnectionStatus.Ok);
        cache.Mark("c", ConnectionStatus.Failed);

        Assert.Equal(2, changes);
    }

    [Fact]
    public void Get_OfUnknownConnection_IsUnknownWithNoModels()
    {
        var entry = new ConnectionStatusCache().Get("nope");

        Assert.Equal(ConnectionStatus.Unknown, entry.Status);
        Assert.Empty(entry.ModelIds);
    }

    [Fact]
    public void Set_WritesResultToCatalog_WithTheTime()
    {
        var catalog = new FakeModelCatalog();
        var time = new FakeTimeProvider();
        var cache = new ConnectionStatusCache(catalog, time);

        cache.Set(FakeModelCatalog.Connection.Id, ConnectionTestResult.Ok(["a"]));

        var (id, test) = Assert.Single(catalog.TestSaves);
        Assert.Equal(FakeModelCatalog.Connection.Id, id);
        Assert.Equal(new ConnectionTestRecord(true, time.GetUtcNow()), test);
    }

    [Fact]
    public void Mark_WritesToCatalog_OnlyWhenStatusChanges()
    {
        var catalog = new FakeModelCatalog();
        var cache = new ConnectionStatusCache(catalog);

        cache.Mark("c", ConnectionStatus.Ok);
        cache.Mark("c", ConnectionStatus.Ok);
        cache.Mark("c", ConnectionStatus.Failed);

        Assert.Equal([true, false], catalog.TestSaves.Select(t => t.Test!.Success));
    }

    [Fact]
    public void Remove_WritesNull_OnlyWhenThereWasAResult()
    {
        var catalog = new FakeModelCatalog();
        var cache = new ConnectionStatusCache(catalog);
        cache.Remove("c");
        Assert.Empty(catalog.TestSaves);

        cache.Set("c", ConnectionTestResult.Ok([]));
        cache.Remove("c");

        Assert.Null(catalog.TestSaves[^1].Test);
        Assert.Equal(ConnectionStatus.Unknown, cache.Get("c").Status);
    }

    [Fact]
    public void Restart_ReadsLastResult_WithoutModelList()
    {
        var catalog = new FakeModelCatalog();
        new ConnectionStatusCache(catalog).Set(FakeModelCatalog.Connection.Id, ConnectionTestResult.Ok(["a", "b"]));

        var restarted = new ConnectionStatusCache(catalog).Get(FakeModelCatalog.Connection.Id);

        Assert.Equal(ConnectionStatus.Ok, restarted.Status);
        Assert.Empty(restarted.ModelIds);
    }

    [Fact]
    public void Restart_FailedResult_ReadsAsFailed()
    {
        var catalog = new FakeModelCatalog();
        new ConnectionStatusCache(catalog).Mark(FakeModelCatalog.Connection.Id, ConnectionStatus.Failed);

        Assert.Equal(ConnectionStatus.Failed, new ConnectionStatusCache(catalog).Get(FakeModelCatalog.Connection.Id).Status);
    }

    [Theory]
    [InlineData(ChatFailureKind.Network, ConnectionStatus.Failed)]
    [InlineData(ChatFailureKind.ServerError, ConnectionStatus.Failed)]
    [InlineData(ChatFailureKind.Overloaded, ConnectionStatus.Failed)]
    [InlineData(ChatFailureKind.StreamError, ConnectionStatus.Failed)]
    [InlineData(ChatFailureKind.InvalidApiKey, ConnectionStatus.Failed)]
    [InlineData(ChatFailureKind.PermissionDenied, ConnectionStatus.Failed)]
    [InlineData(ChatFailureKind.ModelNotFound, ConnectionStatus.Failed)]
    public void StatusAfterFailure_Failed(ChatFailureKind kind, ConnectionStatus expected) =>
        Assert.Equal(expected, ConnectionStatusCache.StatusAfterFailure(kind));

    [Theory]
    [InlineData(ChatFailureKind.RateLimited)]
    [InlineData(ChatFailureKind.Refused)]
    [InlineData(ChatFailureKind.BadRequest)]
    [InlineData(ChatFailureKind.MissingApiKey)]
    [InlineData(ChatFailureKind.SecretUnreadable)]
    [InlineData(ChatFailureKind.ModelNotConfigured)]
    [InlineData(ChatFailureKind.AppFilesMissing)]
    [InlineData(ChatFailureKind.Unknown)]
    public void StatusAfterFailure_Unchanged(ChatFailureKind kind) =>
        Assert.Null(ConnectionStatusCache.StatusAfterFailure(kind));
}
