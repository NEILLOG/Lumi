using Lumi.App.ViewModels;

namespace Lumi.App.Services;

/// <summary>骨架：由包 20C 實作。</summary>
public sealed class DataLocationProvider : IDataLocationProvider
{
    public IReadOnlyList<DataLocation> GetLocations() => [];
}
