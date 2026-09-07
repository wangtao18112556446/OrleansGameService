namespace GameServer.Tests;

/// <summary>独立控制 UTC 与单调时间，用于验证回退、停顿和重激活时的时间预算。</summary>
public sealed class ManualTimeProvider(DateTimeOffset initialUtc) : TimeProvider
{
    private DateTimeOffset utcNow = initialUtc;
    private long timestamp;

    public override DateTimeOffset GetUtcNow() => utcNow;

    public override long GetTimestamp() => timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        utcNow += elapsed;
        timestamp += elapsed.Ticks;
    }

    public void RewindUtc(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        utcNow -= elapsed;
    }
}
