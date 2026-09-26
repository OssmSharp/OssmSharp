using Microsoft.Extensions.Time.Testing;

namespace OssmSharp.Web.Application.Tests.Helpers;

/// <summary>
/// Provides a connected engine, controllable clock, and bridge for execution tests.
/// </summary>
internal sealed class CoreRig : IAsyncDisposable
{
    private readonly WallClockTimeProvider _clock;

    internal CoreRig()
    {
        _clock = new(Time);
        Engine = new(Bridge, _clock);
        Bridge.Connected = true;
        Bridge.Publish();
    }

    /// <summary>
    /// Gets the clock used for deadlines and elapsed time.
    /// </summary>
    internal FakeTimeProvider Time { get; } = new();

    /// <summary>
    /// Gets the clock whose UTC offset can change independently of elapsed time.
    /// </summary>
    internal WallClockTimeProvider Clock => _clock;
    internal FakeBridge Bridge { get; } = new();
    internal ControlEngine Engine { get; }

    /// <summary>
    /// Converts a duration to a deadline on the engine's timestamp clock.
    /// </summary>
    /// <param name="duration">The interval before the deadline.</param>
    /// <returns>The deadline in timestamp ticks.</returns>
    internal long Deadline(TimeSpan duration) =>
        checked(Time.GetTimestamp() + (long)Math.Ceiling(duration.TotalSeconds * Time.TimestampFrequency));

    /// <summary>
    /// Recovers and arms the engine, then clears bridge calls from setup.
    /// </summary>
    internal void Arm()
    {
        Engine.Recover();
        Engine.Arm();
        Bridge.ClearCalls();
    }

    internal void Submit(MotionSettings desired, int lifetimeMilliseconds = 750) =>
        Engine.Submit(desired, Deadline(TimeSpan.FromMilliseconds(lifetimeMilliseconds)));

    internal void Pump()
    {
        for (var index = 0; index < 8; index++)
            Engine.Tick();
    }

    /// <summary>
    /// Advances the fake clock, publishes bridge state, and ticks the engine once.
    /// </summary>
    /// <param name="milliseconds">The clock advance in milliseconds.</param>
    internal void Advance(int milliseconds)
    {
        Time.Advance(TimeSpan.FromMilliseconds(milliseconds));
        Bridge.Publish();
        Engine.Tick();
    }

    public async ValueTask DisposeAsync()
    {
        Bridge.AutoComplete = true;
        Bridge.CompleteAll();
        Pump();
        try { await Engine.DisposeAsync(); }
        finally { await Bridge.DisposeAsync(); }
    }
}
