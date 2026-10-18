using Lumi.App.ViewModels;

namespace Lumi.App.Tests.TestSupport;

internal sealed class SyncUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}
