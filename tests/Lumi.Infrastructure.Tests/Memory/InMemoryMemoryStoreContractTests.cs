using Lumi.Core.Memory;

namespace Lumi.Infrastructure.Tests.Memory;

/// <summary>記憶體版跑同一組契約測試。</summary>
public sealed class InMemoryMemoryStoreContractTests : MemoryStoreContractTests
{
    protected override IMemoryStore CreateStore(MemoryOptions options, MemoryTestClock clock) => new InMemoryMemoryStore(options, clock);
}
