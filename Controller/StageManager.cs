using Microsoft.Extensions.Logging;
using Resources;
using Sensor;

namespace Controller;

/// <summary>
/// One stage as an independent process.
/// <para>
/// <b>Input.</b> It listens to the sensor registry and assembles readings into frames: a frame is complete when
/// every registered sensor has delivered a new reading since the last frame, and those readings lie within
/// <see cref="StageOptions.MaxReadingSkew"/> of each other. Rules are evaluated on complete frames only, never on
/// a mixture of one sensor's new value and another's old one. Readings whose sequence does not increase are
/// dropped, so a delayed or replayed measurement can never overwrite a newer one.
/// </para>
/// <para>
/// <b>Lifecycle.</b> When the rule holds it acquires its resources in the order given, does its work, releases,
/// and goes back to idle. Every transition (reading, run ended, watchdog, abort) is applied under one lock as a
/// single step that writes the state and publishes it, so an old run's completion can never interleave with a
/// new run's start. Each run has an identity; a transition from a run that is no longer current is ignored.
/// </para>
/// <para>
/// <b>Aborts.</b> While acquiring or running, a complete frame on which the rule no longer holds aborts the run
/// (to Idle), and a needed resource entering Error aborts it (to Faulted). Resource health is checked on every
/// event and by a watchdog timer, so a silent sensor cannot silence resource monitoring. All held resources are
/// re-checked immediately before the work starts.
/// </para>
/// <para>
/// <b>Sensor loss.</b> Sensors and resources fail differently. A missing reading is absence of evidence, not
/// evidence, and the stage cannot know which direction is safe, so work in progress continues. After
/// <see cref="StageOptions.StaleAfter"/> a warning is logged; after <see cref="StageOptions.DataBudget"/> the
/// data alarm is raised and logged as an error for the operator. Nothing new starts without a complete frame,
/// and a fresh frame clears the alarm. A sensor stream that ends or throws raises the alarm at once.
/// </para>
/// Several stage managers run side by side and contend for the same resources with no central coordinator; the
/// order in which each lists its resources is therefore the locking protocol of the whole machine.
/// </summary>
public sealed class StageManager : IStageManager
{
    private readonly string _name;
    private readonly IReadOnlyList<IResource> _resources;
    private readonly SensorRegistry _sensors;
    private readonly Func<IReadOnlyDictionary<SensorType, double>, bool> _stageRule;
    private readonly Func<CancellationToken, Task> _work;
    private readonly Action<StageState> _stateChanged;
    private readonly ILogger _logger;
    private readonly StageOptions _options;
    private readonly TimeProvider _time;

    // Serialises every transition. Held only for bookkeeping and the state callback, never across an await.
    private readonly Lock _lock = new();

    // Frame assembly (under _lock)
    private readonly Dictionary<Guid, SensorData> _latest = [];
    private readonly HashSet<Guid> _reportedSinceLastFrame = [];
    private DateTimeOffset _lastFrameAt;
    private bool _staleWarned;
    private bool _streamFailed;

    // Lifecycle (under _lock)
    private volatile StageState _state = StageState.Idle;
    private volatile bool _dataAlarm;
    private Run? _run;
    private int _nextRunId;
    private int _started;

    private sealed class Run(int id, CancellationTokenSource cts)
    {
        public int Id { get; } = id;
        public CancellationTokenSource Cts { get; } = cts;
        public StageState StateAfterAbort { get; set; } = StageState.Idle;
        public Task Completion { get; set; } = Task.CompletedTask;
    }

    public StageManager(
        string name,
        IReadOnlyList<IResource> resources,
        SensorRegistry sensors,
        Func<IReadOnlyDictionary<SensorType, double>, bool> stageRule,
        Func<CancellationToken, Task> work,
        Action<StageState> stateChanged,
        ILogger<StageManager> logger,
        StageOptions? options = null)
    {
        _name = name;
        _resources = resources;
        _sensors = sensors;
        _stageRule = stageRule;
        _work = work;
        _stateChanged = stateChanged;
        _logger = logger;
        _options = options ?? StageOptions.Default;
        _time = _options.TimeProvider;
        _lastFrameAt = _time.GetUtcNow();
    }

    public string Name => _name;

    public StageState State => _state;

    public bool DataAlarm => _dataAlarm;

    /// <summary>Completes when the most recently started run ends; completed immediately if none started. For tests.</summary>
    public Task CurrentRun { get; private set; } = Task.CompletedTask;

    /// <summary>Work that simply takes time.</summary>
    public static Func<CancellationToken, Task> Delay(TimeSpan duration) => ct => Task.Delay(duration, ct);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            throw new InvalidOperationException($"{_name} is already running.");
        }

        lock (_lock)
        {
            _lastFrameAt = _time.GetUtcNow();
        }

        _logger.LogInformation("{Stage} started: listening to {SensorCount} sensors, needs {Resources}",
            _name, _sensors.Sensors.Count, _resources.Select(r => r.Name));

        await Task.WhenAll(PumpReadingsAsync(cancellationToken), WatchdogAsync(cancellationToken));

        // Shutdown: whatever run is in flight is cancelled and awaited so resources are released.
        Run? run;
        lock (_lock)
        {
            run = _run;
            if (run is not null)
            {
                run.StateAfterAbort = StageState.Idle;
                run.Cts.Cancel();
            }
        }

        if (run is not null)
        {
            await run.Completion;
        }

        _logger.LogInformation("{Stage} stopped", _name);
    }

    /// <summary>
    /// Feeds one reading and applies whatever follows from it. Public so tests can drive the stage without
    /// streams or clocks. Safe to call from any thread; transitions are serialised.
    /// </summary>
    public void OnReading(SensorData reading, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!Accept(reading))
            {
                return;
            }

            if (TryCompleteFrame(out var values))
            {
                OnFrame(values, cancellationToken);
            }

            Scan(_time.GetUtcNow());
        }
    }

    /// <summary>Checks resource health and data age. Called by the watchdog timer and after every reading; public for tests.</summary>
    public void Scan()
    {
        lock (_lock)
        {
            Scan(_time.GetUtcNow());
        }
    }

    // ------------------------------------------------------------------ frames

    private bool Accept(SensorData reading)
    {
        if (_latest.TryGetValue(reading.SensorId, out var previous) && reading.Sequence <= previous.Sequence)
        {
            _logger.LogDebug("{Stage} dropped reading #{Sequence} from {SensorId}: not newer than #{Previous}",
                _name, reading.Sequence, reading.SensorId, previous.Sequence);
            return false;
        }

        if (reading.Sequence == 0)
        {
            return false; // the constructor value: nothing measured it
        }

        _latest[reading.SensorId] = reading;
        _reportedSinceLastFrame.Add(reading.SensorId);
        return true;
    }

    /// <summary>
    /// A frame is every registered sensor reporting once since the last frame, within the skew window. This is
    /// what makes the view the rule sees consistent: it is never one sensor's new value with another's old one.
    /// </summary>
    private bool TryCompleteFrame(out IReadOnlyDictionary<SensorType, double> values)
    {
        values = null!;

        if (!_sensors.Covers(_reportedSinceLastFrame))
        {
            return false;
        }

        var newest = _latest.Values.Max(r => r.Timestamp);
        if (_latest.Values.Any(r => newest - r.Timestamp > _options.MaxReadingSkew))
        {
            return false; // wait for the laggard's next reading
        }

        var byType = new Dictionary<SensorType, double>();
        foreach (var reading in _latest.Values.OrderBy(r => r.Timestamp))
        {
            byType[reading.Type] = reading.Value; // several sensors of one type: the newest wins
        }

        values = byType;
        _reportedSinceLastFrame.Clear();
        _lastFrameAt = _time.GetUtcNow();
        _staleWarned = false;

        if (_dataAlarm && !_streamFailed)
        {
            _dataAlarm = false;
            _logger.LogInformation("{Stage} sensor data restored: alarm cleared", _name);
        }

        return true;
    }

    private void OnFrame(IReadOnlyDictionary<SensorType, double> values, CancellationToken cancellationToken)
    {
        switch (_state)
        {
            case StageState.Idle or StageState.Faulted:
                if (_resources.Any(r => r.State == ResourceState.Error))
                {
                    _logger.LogTrace("{Stage} not started: a resource is in Error", _name);
                    return;
                }

                if (!_stageRule(values))
                {
                    return;
                }

                _logger.LogInformation("{Stage} rule fired on {Values}", _name, values);
                StartRun(cancellationToken);
                break;

            case StageState.Acquiring or StageState.Running:
                if (!_stageRule(values))
                {
                    _logger.LogInformation("{Stage} aborting while {State}: rule no longer holds on {Values}", _name, _state, values);
                    Abort(StageState.Idle);
                }

                break;
        }
    }

    // ------------------------------------------------------------------ watchdog

    private void Scan(DateTimeOffset now)
    {
        if (_run is { Cts.IsCancellationRequested: false } && _state is StageState.Acquiring or StageState.Running)
        {
            var faulted = _resources.FirstOrDefault(r => r.State == ResourceState.Error);
            if (faulted is not null)
            {
                _logger.LogWarning("{Stage} aborting while {State}: {Resource} is in Error", _name, _state, faulted.Name);
                Abort(StageState.Faulted);
            }
        }

        if (_streamFailed)
        {
            return; // already alarmed, and no frame can arrive
        }

        var age = now - _lastFrameAt;
        if (age > _options.DataBudget)
        {
            if (!_dataAlarm)
            {
                _dataAlarm = true;
                _logger.LogError("{Stage} data alarm: no complete sensor frame for {Age}; work in progress continues, nothing new starts until data returns",
                    _name, age);
            }
        }
        else if (age > _options.StaleAfter && !_staleWarned)
        {
            _staleWarned = true;
            _logger.LogWarning("{Stage} sensor data is stale: no complete frame for {Age}", _name, age);
        }
    }

    private void OnStreamFailed(Exception error)
    {
        lock (_lock)
        {
            _streamFailed = true;
            _dataAlarm = true;
            _logger.LogError(error, "{Stage} data alarm: sensor stream failed; work in progress continues, nothing new can start", _name);
        }
    }

    // ------------------------------------------------------------------ runs

    private void StartRun(CancellationToken cancellationToken)
    {
        var run = new Run(++_nextRunId, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
        _run = run;
        SetState(StageState.Acquiring);

        // The run itself never executes under the lock: it is scheduled and talks back through transitions.
        run.Completion = Task.Run(() => ExecuteAsync(run), CancellationToken.None);
        CurrentRun = run.Completion;
    }

    private void Abort(StageState stateAfterAbort)
    {
        if (_run is null)
        {
            return;
        }

        _run.StateAfterAbort = stateAfterAbort;
        _run.Cts.Cancel();
    }

    private async Task ExecuteAsync(Run run)
    {
        var cancellationToken = run.Cts.Token;
        var held = new List<IResource>();
        var next = StageState.Idle;

        try
        {
            // Acquired one after another in the order given, holding each while waiting for the next.
            // Whether that can deadlock against the other stages depends on the orders they use.
            foreach (var resource in _resources)
            {
                await resource.AcquireAsync(cancellationToken);
                held.Add(resource);
                _logger.LogDebug("{Stage} holds {Resource}", _name, resource.Name);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // A resource acquired early may have failed while we waited for a later one.
            var faulted = held.FirstOrDefault(r => r.State == ResourceState.Error);
            if (faulted is not null)
            {
                throw new ResourceErrorException(faulted.Name);
            }

            if (!Transition(run, StageState.Running))
            {
                throw new OperationCanceledException(); // superseded: never happens while a run is current
            }

            await _work(cancellationToken);
            _logger.LogInformation("{Stage} completed", _name);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("{Stage} cancelled while {State}", _name, _state);
            next = run.StateAfterAbort;
        }
        catch (ResourceErrorException e)
        {
            _logger.LogWarning("{Stage} failed: {Reason}", _name, e.Message);
            next = StageState.Faulted;
        }
        catch (Exception e)
        {
            // The stage's own work failed. It is a bug in that stage, not a reason for the stage to die.
            _logger.LogError(e, "{Stage} work threw", _name);
        }
        finally
        {
            ReleaseAll(held);
            EndRun(run, next);
        }
    }

    private void ReleaseAll(List<IResource> held)
    {
        foreach (var resource in held)
        {
            try
            {
                resource.Release();
            }
            catch (Exception e)
            {
                // Keep releasing the others: one bad adapter must not leave the rest held.
                _logger.LogError(e, "{Stage} failed to release {Resource}", _name, resource.Name);
            }
        }

        if (held.Count > 0)
        {
            _logger.LogDebug("{Stage} released {Resources}", _name, held.Select(r => r.Name));
        }
    }

    /// <summary>Applies a state change from a run, if that run is still the current one.</summary>
    private bool Transition(Run run, StageState state)
    {
        lock (_lock)
        {
            if (_run != run)
            {
                return false;
            }

            SetState(state);
            return true;
        }
    }

    private void EndRun(Run run, StageState next)
    {
        lock (_lock)
        {
            if (_run == run)
            {
                _run = null;
                SetState(next);
            }

            run.Cts.Dispose();
        }
    }

    /// <summary>State write and publication are one step under the lock, so observers see transitions in order.</summary>
    private void SetState(StageState state)
    {
        _state = state;
        _logger.LogInformation("{Stage} -> {State}", _name, state);
        _stateChanged(state);
    }

    // ------------------------------------------------------------------ loops

    private async Task PumpReadingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var reading in _sensors.ReadAllAsync(cancellationToken))
            {
                OnReading(reading, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown requested
        }
        catch (SensorStreamException e)
        {
            OnStreamFailed(e); // the stage stays alive: the watchdog keeps running and the current run may finish
        }
    }

    private async Task WatchdogAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.WatchdogPeriod, _time);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                Scan();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown requested
        }
    }
}
