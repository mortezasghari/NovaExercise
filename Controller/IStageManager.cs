using Microsoft.Extensions.Logging;
using Resources;
using Sensor;

namespace Controller;

public enum StageState : byte
{
    /// <summary>Not running; evaluates its rule on every reading.</summary>
    Idle,

    /// <summary>Rule fired; acquiring resources. Readings are recorded but not evaluated.</summary>
    Acquiring,

    /// <summary>Holds every resource and is doing its work.</summary>
    Running,

    /// <summary>Last run ended because a resource was in Error. Behaves like Idle once its resources are healthy.</summary>
    Faulted,
}

public interface IStageManager
{
    string Name { get; }

    StageState State { get; }

    /// <summary>Runs the stage as an independent process until cancelled.</summary>
    Task RunAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One stage as an independent process. It listens to the sensor registry, keeps the latest reading of each sensor, and
/// while idle evaluates its rule on every reading once all sensors have reported and no reading is stale. When
/// the rule holds it acquires its resources in the order given, does its work, releases, and goes back to idle
/// (run-to-completion: readings arriving mid-run are recorded but trigger nothing). A resource that enters
/// Error while held cancels the work; a resource in Error keeps the stage from starting until it recovers.
/// Several stage managers run side by side and contend for the same resources with no central coordinator;
/// the order in which each lists its resources is therefore the locking protocol of the whole machine.
/// </summary>
public class StageManager(
    string name,
    IReadOnlyList<IResource> resources,
    SensorRegistry sensors,
    Func<IReadOnlyDictionary<SensorType, double>, bool> stageRule,
    Func<CancellationToken, Task> work,
    Action<StageState> stateChanged,
    ILogger<StageManager> logger,
    TimeSpan? maxReadingSkew = null)
    : IStageManager
{
    public static readonly TimeSpan DefaultMaxReadingSkew = TimeSpan.FromMilliseconds(250);

    private readonly TimeSpan _maxSkew = maxReadingSkew ?? DefaultMaxReadingSkew;

    // Touched only by the reading loop (and by tests calling OnReading directly), so no lock is needed.
    private readonly Dictionary<Guid, SensorData> _latest = [];

    private volatile StageState _state = StageState.Idle;
    private CancellationTokenSource? _runCts;

    public string Name => name;

    public StageState State => _state;

    /// <summary>Completes when the current run ends; completed immediately when not running. For tests.</summary>
    public Task CurrentRun { get; private set; } = Task.CompletedTask;

    /// <summary>Work that simply takes time.</summary>
    public static Func<CancellationToken, Task> Delay(TimeSpan duration) => ct => Task.Delay(duration, ct);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("{Stage} started: listening to {SensorCount} sensors, needs {Resources}",
            name, sensors.Sensors.Count, resources.Select(r => r.Name));

        try
        {
            await foreach (var reading in sensors.ReadAllAsync(cancellationToken))
            {
                OnReading(reading, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown requested
        }
        finally
        {
            _runCts?.Cancel();
            await CurrentRun;
            logger.LogInformation("{Stage} stopped", name);
        }
    }

    /// <summary>
    /// Feeds one reading. Public so tests can drive the stage without streams or clocks.
    /// Must be called from one thread at a time.
    /// </summary>
    public void OnReading(SensorData reading, CancellationToken cancellationToken = default)
    {
        _latest[reading.SensorId] = reading;

        switch (_state)
        {
            case StageState.Idle or StageState.Faulted:
                if (!TryGetConsistentValues(out var values))
                {
                    return;
                }

                if (resources.Any(r => r.State == ResourceState.Error))
                {
                    logger.LogTrace("{Stage} not started: a resource is in Error", name);
                    return;
                }

                if (!stageRule(values))
                {
                    return;
                }

                logger.LogInformation("{Stage} rule fired on {Values}", name, values);
                _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                CurrentRun = ExecuteAsync(_runCts.Token);
                break;

            case StageState.Running when _runCts is { IsCancellationRequested: false }:
                // The scan cycle: every reading is a chance to notice a held resource failing under us.
                var faulted = resources.FirstOrDefault(r => r.State == ResourceState.Error);
                if (faulted is not null)
                {
                    logger.LogWarning("{Stage} aborting: {Resource} failed while held", name, faulted.Name);
                    _runCts.Cancel();
                }

                break;
        }
    }

    /// <summary>
    /// The atomic view the rule sees: every sensor has measured at least once, and no reading is older than the
    /// newest by more than the allowed skew. Evaluating one sensor from one moment and another from a different
    /// moment as if they were one state is the atomicity violation this guards against.
    /// </summary>
    private bool TryGetConsistentValues(out IReadOnlyDictionary<SensorType, double> values)
    {
        values = null!;

        if (!sensors.Covers(_latest) || _latest.Values.Any(r => r.Sequence == 0))
        {
            return false;
        }

        var newest = _latest.Values.Max(r => r.Timestamp);
        if (_latest.Values.Any(r => newest - r.Timestamp > _maxSkew))
        {
            return false;
        }

        var byType = new Dictionary<SensorType, double>();
        foreach (var reading in _latest.Values.OrderBy(r => r.Timestamp))
        {
            byType[reading.Type] = reading.Value; // several sensors of one type: the newest wins
        }

        values = byType;
        return true;
    }

    private async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var held = new List<IResource>();
        var next = StageState.Idle;

        try
        {
            SetState(StageState.Acquiring);

            // Acquired one after another in the order given, holding each while waiting for the next.
            // Whether that can deadlock against the other stages depends on the orders they use.
            foreach (var resource in resources)
            {
                await resource.AcquireAsync(cancellationToken);
                held.Add(resource);
                logger.LogDebug("{Stage} holds {Resource}", name, resource.Name);
            }

            SetState(StageState.Running);
            await work(cancellationToken);
            logger.LogInformation("{Stage} completed", name);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("{Stage} cancelled while {State}", name, _state);
        }
        catch (ResourceErrorException e)
        {
            logger.LogWarning("{Stage} failed: {Reason}", name, e.Message);
            next = StageState.Faulted;
        }
        finally
        {
            foreach (var resource in held)
            {
                resource.Release();
            }

            if (held.Count > 0)
            {
                logger.LogDebug("{Stage} released {Resources}", name, held.Select(r => r.Name));
            }

            SetState(next);
        }
    }

    private void SetState(StageState state)
    {
        _state = state;
        logger.LogInformation("{Stage} -> {State}", name, state);
        stateChanged(state);
    }
}
