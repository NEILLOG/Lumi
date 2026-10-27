using Lumi.App.Services;
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
