using System.Diagnostics;
using Controller;
using Microsoft.Extensions.Logging.Abstractions;
using Resources;
using Sensor;
using Simulator;

namespace NovaTests;

/// <summary>Polls a condition instead of sleeping for a fixed time, so tests are fast when they pass and clear when they fail.</summary>
internal static class Wait
{
    public static async Task Until(Func<bool> condition, int timeoutMs = 2000, string? what = null)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException($"Condition not met within {timeoutMs} ms{(what is null ? "" : ": " + what)}");
            }

            await Task.Delay(5);
        }
    }

    /// <summary>Asserts that a task does not complete within a short window: the way to prove something is stuck.</summary>
    public static async Task<bool> Completes(Task task, int withinMs = 300) =>
        await Task.WhenAny(task, Task.Delay(withinMs)) == task;
}

/// <summary>
/// A resource that records the order of calls and can be made to block or fail on acquisition.
/// Fault semantics are tested with the real <see cref="ResourceSimulator"/>; this fake is for observing order.
/// </summary>
internal sealed class FakeResource(string name, List<string>? journal = null) : IResource
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<string> _journal = journal ?? [];
    private int _held;

    public string Name => name;

    public ResourceState State => Volatile.Read(ref _held) == 1 ? ResourceState.Busy : ResourceState.Idle;

    /// <summary>When set, <see cref="AcquireAsync"/> waits for it before taking the slot.</summary>
    public TaskCompletionSource? BlockAcquire { get; set; }

    /// <summary>When true, <see cref="AcquireAsync"/> throws <see cref="ResourceErrorException"/> after any block.</summary>
    public bool ThrowOnAcquire { get; set; }

    public IReadOnlyList<string> Journal => _journal;

    public bool TryAcquire()
    {
        if (!_gate.Wait(0))
        {
            return false;
        }

        Volatile.Write(ref _held, 1);
        Record("hold");
        return true;
    }

    public async Task AcquireAsync(CancellationToken cancellationToken = default)
    {
        Record("acquire");

        if (BlockAcquire is { } block)
        {
            await block.Task.WaitAsync(cancellationToken);
        }

        if (ThrowOnAcquire)
        {
            throw new ResourceErrorException(name);
        }

        await _gate.WaitAsync(cancellationToken);
        Volatile.Write(ref _held, 1);
        Record("hold");
    }

    public void Release()
    {
        if (Interlocked.Exchange(ref _held, 0) == 0)
        {
            throw new InvalidOperationException($"{name} is not held.");
        }

        _gate.Release();
        Record("release");
    }

    private void Record(string what)
    {
        lock (_journal)
        {
            _journal.Add($"{what}:{name}");
        }
    }
}

/// <summary>A tickable that records what the clock handed it.</summary>
internal sealed class RecordingTickable : ITickable
{
    public List<(DateTime Timestamp, IReadOnlySet<Stage> Active)> Ticks { get; } = [];

    public void Tick(DateTime timestamp, IReadOnlySet<Stage> activeStages) => Ticks.Add((timestamp, activeStages));
}

/// <summary>
/// Everything a <see cref="StageManager"/> test needs: a registry with one temperature and one pressure sensor,
/// reading factories whose ids match those sensors, and a recorder for state transitions.
/// </summary>
internal sealed class StageHarness
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public Guid TemperatureId { get; } = Guid.NewGuid();
    public Guid PressureId { get; } = Guid.NewGuid();
    public SensorRegistry Registry { get; } = new();
    public List<StageState> States { get; } = [];

    public StageHarness()
    {
        Registry.Register(new TemperatureSensorSimulator(TemperatureId, 20.0));
        Registry.Register(new PressureSensorSimulator(PressureId, 30.0));
    }

    public static bool Rule1(IReadOnlyDictionary<SensorType, double> v) => ExerciseMachine.Rule1(v);

    public SensorData Temp(double value, int tick) =>
        new(TemperatureId, value, SensorType.Temperature, T0.AddMilliseconds(100 * tick), tick);

    public SensorData Pres(double value, int tick) =>
        new(PressureId, value, SensorType.Pressure, T0.AddMilliseconds(100 * tick), tick);

    public StageManager Stage(
        IReadOnlyList<IResource> resources,
        Func<IReadOnlyDictionary<SensorType, double>, bool>? rule = null,
        Func<CancellationToken, Task>? work = null,
        TimeSpan? maxSkew = null,
        string name = "stage") =>
        new(name, resources, Registry, rule ?? Rule1, work ?? (ct => Task.Delay(10, ct)),
            state => { lock (States) States.Add(state); }, NullLogger<StageManager>.Instance, maxSkew);

    /// <summary>Feeds one consistent pair of readings for the given tick.</summary>
    public void Feed(StageManager stage, double temperature, double pressure, int tick, CancellationToken cancellationToken = default)
    {
        stage.OnReading(Temp(temperature, tick), cancellationToken);
        stage.OnReading(Pres(pressure, tick), cancellationToken);
    }

    public IReadOnlyList<StageState> StatesSoFar()
    {
        lock (States)
        {
            return States.ToList();
        }
    }
}
