namespace Lumi.App.Tests.TestSupport;

/// <summary>手動推進的時間：思考秒數、歷史清單的「今天／M/d」都用它，測試不用真的等。</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public void Advance(TimeSpan by) => _now += by;
}
