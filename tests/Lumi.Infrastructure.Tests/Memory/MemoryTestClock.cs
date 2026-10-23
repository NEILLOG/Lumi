namespace Lumi.Infrastructure.Tests.Memory;

/// <summary>手動推進的時鐘：記憶的時間戳與到期判斷只讀 <see cref="TimeProvider.GetUtcNow"/>。</summary>
public sealed class MemoryTestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
