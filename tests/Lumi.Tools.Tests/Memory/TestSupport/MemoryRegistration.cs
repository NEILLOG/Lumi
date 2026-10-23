using Lumi.Core.Memory;
using Lumi.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Lumi.Tools.Tests.Memory;

/// <summary>把記憶工具需要的服務註冊進 DI（假的 store 與假的目錄，記憶預設開啟）。App 的真實註冊由組裝根負責。</summary>
internal static class MemoryRegistration
{
    public static void RegisterFor(IServiceCollection services)
    {
        services.AddSingleton(new MemoryOptions());
        services.AddSingleton<IMemoryStore>(sp => new FakeMemoryStore(sp.GetRequiredService<MemoryOptions>()));
        services.AddSingleton<IModelCatalog>(new FakeModelCatalog());
    }
}
