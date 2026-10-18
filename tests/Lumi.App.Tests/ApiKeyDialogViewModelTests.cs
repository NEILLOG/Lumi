using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

public class ApiKeyDialogViewModelTests
{
    [Fact]
    public async Task Save_StoresKeyThroughCatalog_ForGivenConnection_AndClearsField()
    {
        var catalog = new FakeModelCatalog();
        var vm = new ApiKeyDialogViewModel(catalog, FakeModelCatalog.Connection, ApiKeyPromptReason.Missing, NullLogger.Instance);
        bool? closed = null;
        vm.CloseRequested += saved => closed = saved;
        vm.ApiKey = "  sk-ant-new-key  ";

        await vm.SaveCommand.ExecuteAsync(null);

        var (connection, key) = Assert.Single(catalog.SavedConnections);
        Assert.Equal(FakeModelCatalog.Connection.Id, connection.Id);
        Assert.Equal("sk-ant-new-key", key!.Reveal());
        Assert.Equal("", vm.ApiKey);
        Assert.True(closed);
    }

    [Fact]
    public void BlankKey_CannotBeSaved()
    {
        var vm = new ApiKeyDialogViewModel(new FakeModelCatalog(), FakeModelCatalog.Connection, ApiKeyPromptReason.Invalid, NullLogger.Instance);

        vm.ApiKey = "   ";

        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task NoConnection_ShowsError_AndStaysOpen()
    {
        var catalog = new FakeModelCatalog(withModel: false);
        var vm = new ApiKeyDialogViewModel(catalog, null, ApiKeyPromptReason.Missing, NullLogger.Instance);
        var closed = false;
        vm.CloseRequested += _ => closed = true;
        vm.ApiKey = "sk-ant-x";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(vm.Error);
        Assert.False(closed);
        Assert.Empty(catalog.SavedConnections);
    }

    [Fact]
    public void Cancel_ClearsField_AndReportsNotSaved()
    {
        var vm = new ApiKeyDialogViewModel(new FakeModelCatalog(), FakeModelCatalog.Connection, ApiKeyPromptReason.Missing, NullLogger.Instance);
        bool? closed = null;
        vm.CloseRequested += saved => closed = saved;
        vm.ApiKey = "sk-ant-x";

        vm.CancelCommand.Execute(null);

        Assert.Equal("", vm.ApiKey);
        Assert.False(closed);
    }
}
