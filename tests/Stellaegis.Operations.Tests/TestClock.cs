namespace Stellaegis.Operations.Tests;

/// <summary>A clock the tests move by hand.</summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public TestClock() : this(new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero)) { }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
