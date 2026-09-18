namespace Observer.App.Tests;

/// <summary>
/// A clock that advances only when the test says so.
/// </summary>
/// <remarks>
/// The ticks live in a <c>long</c> read and written with <see cref="Volatile"/>: the refresh
/// loop runs on a pool thread, the test advances the clock from its own, and a sixteen-byte
/// struct could be read halfway through a write.
/// </remarks>
internal sealed class FakeClock
{
    private long utcTicks = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero).UtcTicks;

    public DateTimeOffset Now() => new(Volatile.Read(ref utcTicks), TimeSpan.Zero);

    public void Advance(TimeSpan amount) =>
        Volatile.Write(ref utcTicks, Volatile.Read(ref utcTicks) + amount.Ticks);
}