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

    /// <summary>Whether a task completes within a short window: the way to show something is, or is not, stuck.</summary>
    public static async Task<bool> Completes(Task task, int withinMs = 300) =>
        await Task.WhenAny(task, Task.Delay(withinMs)) == task;
}

/// <summary>A clock the test moves by hand. Timers are not used by the code under test in unit tests.</summary>
internal sealed class ManualTime : TimeProvider
{
    public static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private DateTimeOffset _now = Start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
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

    /// <summary>When true, <see cref="Release"/> throws after releasing, as a misbehaving adapter might.</summary>
    public bool ThrowOnRelease { get; set; }

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

        if (ThrowOnRelease)
        {
            throw new InvalidOperationException($"{name} release failed");
        }
    }

    private void Record(string what)
    {
        lock (_journal)
        {
            _journal.Add($"{what}:{name}");
        }
    }
}

/// <summary>
/// Counts one stage's acquisitions across all of its resources. The first passes; every later one waits for
/// <paramref name="release"/>. One instance per stage, shared by that stage's <see cref="StageView"/>s.
/// </summary>
internal sealed class StageGate(TaskCompletionSource release)
{
    private int _acquisitions;

    public Task WaitUnlessFirst(CancellationToken cancellationToken) =>
        Interlocked.Increment(ref _acquisitions) > 1 ? release.Task.WaitAsync(cancellationToken) : Task.CompletedTask;
}

/// <summary>
/// One stage's view of a shared resource, gated by that stage's <see cref="StageGate"/>. Give each stage its own
/// views of the same underlying resources and every stage takes exactly its first resource, then all request
/// their second together: the schedule a circular wait needs, produced on purpose rather than by luck.
/// </summary>
internal sealed class StageView(IResource inner, StageGate gate) : IResource
{
    public string Name => inner.Name;
    public ResourceState State => inner.State;
    public bool TryAcquire() => inner.TryAcquire();

    public async Task AcquireAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitUnlessFirst(cancellationToken);
        await inner.AcquireAsync(cancellationToken);
    }

    public void Release() => inner.Release();
}

/// <summary>A tickable that records what the clock handed it.</summary>
internal sealed class RecordingTickable : ITickable
{
    public List<(DateTime Timestamp, IReadOnlySet<Stage> Active)> Ticks { get; } = [];

    public void Tick(DateTime timestamp, IReadOnlySet<Stage> activeStages) => Ticks.Add((timestamp, activeStages));
}

/// <summary>
/// Everything a <see cref="StageManager"/> test needs: a registry with one temperature and one pressure sensor,
/// reading factories whose ids match those sensors, a manual clock, and a recorder for state transitions.
/// </summary>
internal sealed class StageHarness
{
    public Guid TemperatureId { get; } = Guid.NewGuid();
    public Guid PressureId { get; } = Guid.NewGuid();
    public SensorRegistry Registry { get; } = new();
    public ManualTime Time { get; } = new();
    public List<StageState> States { get; } = [];

    public StageHarness()
    {
        Registry.Register(new TemperatureSensorSimulator(TemperatureId, 20.0));
        Registry.Register(new PressureSensorSimulator(PressureId, 30.0));
    }

    public static bool Rule1(IReadOnlyDictionary<SensorType, double> v) => ExerciseMachine.Rule1(v);

    public DateTime At(int tick) => ManualTime.Start.UtcDateTime.AddMilliseconds(100 * tick);

    public SensorData Temp(double value, int tick) => new(TemperatureId, value, SensorType.Temperature, At(tick), tick);

    public SensorData Pres(double value, int tick) => new(PressureId, value, SensorType.Pressure, At(tick), tick);

    public StageOptions Options(TimeSpan? maxSkew = null, TimeSpan? staleAfter = null, TimeSpan? dataBudget = null) => new()
    {
        TimeProvider = Time,
        MaxReadingSkew = maxSkew ?? TimeSpan.FromMilliseconds(250),
        StaleAfter = staleAfter ?? TimeSpan.FromMilliseconds(500),
        DataBudget = dataBudget ?? TimeSpan.FromSeconds(5),
    };

    public StageManager Stage(
        IReadOnlyList<IResource> resources,
        Func<IReadOnlyDictionary<SensorType, double>, bool>? rule = null,
        Func<CancellationToken, Task>? work = null,
        TimeSpan? maxSkew = null,
        StageOptions? options = null,
        Action<StageState>? stateChanged = null,
        string name = "stage") =>
        new(name, resources, Registry, rule ?? Rule1, work ?? (ct => Task.Delay(10, ct)),
            state =>
            {
                lock (States) States.Add(state);
                stateChanged?.Invoke(state);
            },
            NullLogger<StageManager>.Instance,
            options ?? Options(maxSkew));

    /// <summary>Feeds one complete frame for the given tick.</summary>
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
