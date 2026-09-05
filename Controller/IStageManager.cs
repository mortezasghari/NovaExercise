namespace Controller;

public enum StageState : byte
{
    /// <summary>Not running; evaluates its rule on every complete sensor frame.</summary>
    Idle,

    /// <summary>Rule fired; acquiring resources. Aborts if a resource fails or the rule stops holding.</summary>
    Acquiring,

    /// <summary>Holds every resource and is doing its work. Aborts if a resource fails or the rule stops holding.</summary>
    Running,

    /// <summary>Last run ended because a resource was in Error. Behaves like Idle once its resources are healthy.</summary>
    Faulted,
}

/// <summary>Timing policy of a stage. Every value is a decision the assignment leaves open; see the design notes.</summary>
public sealed record StageOptions
{
    /// <summary>Readings of one frame may be at most this far apart. Zero for a shared clock; larger for independent sensors.</summary>
    public TimeSpan MaxReadingSkew { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>A warning is logged once when no complete frame has arrived for this long.</summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The data alarm is raised when no complete frame has arrived for this long. Work in progress continues
    /// (the stage has no way to know which direction is safe), but nothing new starts until data returns.
    /// </summary>
    public TimeSpan DataBudget { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the stage checks resource health and data age independently of sensor arrivals.</summary>
    public TimeSpan WatchdogPeriod { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public static StageOptions Default { get; } = new();
}

public interface IStageManager
{
    string Name { get; }

    StageState State { get; }

    /// <summary>
    /// True when the stage has not seen a complete sensor frame within its data budget, or its sensor stream
    /// has failed. Work in progress continues; nothing new starts until a fresh frame clears the alarm.
    /// </summary>
    bool DataAlarm { get; }

    /// <summary>Runs the stage as an independent process until cancelled. One call per instance.</summary>
    Task RunAsync(CancellationToken cancellationToken);
}
