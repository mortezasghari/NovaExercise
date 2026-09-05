using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Simulator;

/// <summary>Anything the simulation clock advances: sensors, resources, or any future simulated part.</summary>
public interface ITickable
{
    /// <summary>Advances the object by one tick. Every tickable receives the same timestamp and stage snapshot.</summary>
    void Tick(DateTime timestamp, IReadOnlySet<Stage> activeStages);
}

/// <summary>
/// The single source of simulated time. Every period it takes one snapshot of the active stages and ticks
/// every registered object with it, so everything advances in lock-step with the same timestamp and stage set.
/// Time comes from an injectable <see cref="TimeProvider"/>, and <see cref="Tick"/> is public, so tests can
/// step the simulation by hand instead of waiting on the wall clock.
/// </summary>
public sealed class ClockService
{
    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromMilliseconds(100);

    private readonly IReadOnlyList<ITickable> _tickables;
    private readonly ActiveStages _activeStages;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private long _tickCount;

    public ClockService(
        IReadOnlyList<ITickable> tickables,
        ActiveStages activeStages,
        TimeSpan? period = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
    {
        Period = period ?? DefaultPeriod;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Period, TimeSpan.Zero, nameof(period));

        _tickables = tickables;
        _activeStages = activeStages;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    public TimeSpan Period { get; }

    /// <summary>Number of ticks emitted so far.</summary>
    public long TickCount => Volatile.Read(ref _tickCount);

    /// <summary>Ticks everything once per period until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var timer = new PeriodicTimer(Period, _time);
        _logger.LogInformation("Clock started: {Period} period, {TickableCount} tickables", Period, _tickables.Count);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                Tick();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown requested
        }

        _logger.LogInformation("Clock stopped after {TickCount} ticks", TickCount);
    }

    /// <summary>One simulation step: a single stage snapshot and timestamp applied to every tickable.</summary>
    public void Tick()
    {
        var timestamp = _time.GetUtcNow().UtcDateTime;
        var activeStages = _activeStages.Current;
        var tick = Interlocked.Increment(ref _tickCount);
        _logger.LogTrace("Tick {Tick} at {Timestamp:HH:mm:ss.fff}, active stages {ActiveStages}", tick, timestamp, activeStages);

        foreach (var tickable in _tickables)
        {
            tickable.Tick(timestamp, activeStages);
        }
    }
}
