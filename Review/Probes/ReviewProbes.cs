using Controller;
using Microsoft.Extensions.Logging.Abstractions;
using Resources;
using Sensor;
using Simulator;

namespace NovaTests;

// Characterization probes: PASS means the reported behavior was reproduced,
// not that the behavior is correct. Kept outside the submitted solution/test suite.
public class ReviewProbes
{
    [Fact]
    public async Task F01_Mixed_ticks_start_work_even_when_both_complete_frames_fail_the_rule()
    {
        var h = new StageHarness();
        var starts = 0;
        var stage = h.Stage([], work: _ => { starts++; return Task.CompletedTask; });
        h.Feed(stage, 5, 40, 1);   // Rule 1 false.
        h.Feed(stage, 25, 150, 2); // Rule 1 false, but Temp(2) + Pressure(1) is true.
        await stage.CurrentRun;
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task F02_Stale_false_reading_does_not_abort_running_work()
    {
        var h = new StageHarness();
        using var stop = new CancellationTokenSource();
        var a = new ResourceSimulator("A", acquisitionLatency: TimeSpan.Zero);
        var stage = h.Stage([a], work: ct => Task.Delay(Timeout.InfiniteTimeSpan, ct));
        try
        {
            h.Feed(stage, 25, 40, 1, stop.Token);
            Assert.Equal(StageState.Running, stage.State);
            stage.OnReading(h.Temp(5, 10), stop.Token); // Pressure stopped; fresh temperature is false.
            Assert.Equal(StageState.Running, stage.State);
            Assert.Equal(ResourceState.Busy, a.State);
        }
        finally { stop.Cancel(); await stage.CurrentRun.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [Fact]
    public async Task F03_A_completed_sensor_stream_leaves_the_merged_reader_pending()
    {
        var registry = new SensorRegistry();
        registry.Register(new EndingSensor());
        using var stop = new CancellationTokenSource();
        await using var reader = registry.ReadAllAsync(stop.Token).GetAsyncEnumerator();
        var read = reader.MoveNextAsync().AsTask();
        try { Assert.False(await Wait.Completes(read, 50)); }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        }
    }

    [Fact]
    public async Task F03_A_sensor_failure_is_only_reported_during_disposal()
    {
        var registry = new SensorRegistry();
        registry.Register(new EndingSensor(fail: true));
        using var stop = new CancellationTokenSource();
        var reader = registry.ReadAllAsync(stop.Token).GetAsyncEnumerator();
        var read = reader.MoveNextAsync().AsTask();
        try { Assert.False(await Wait.Completes(read, 50)); }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAsync<InvalidOperationException>(() => read);
            await reader.DisposeAsync();
        }
    }

    [Fact]
    public async Task F04_A_previous_runs_idle_notification_can_overwrite_the_new_runs_active_state()
    {
        var h = new StageHarness();
        var active = new ActiveStages();
        var firstWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var idleEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseIdle = new ManualResetEventSlim();
        using var stop = new CancellationTokenSource();
        var idleCalls = 0;
        var workCalls = 0;
        var stage = new StageManager("s", [], h.Registry, _ => true,
            ct => Interlocked.Increment(ref workCalls) == 1 ? firstWork.Task : Task.Delay(Timeout.InfiniteTimeSpan, ct),
            state =>
            {
                if (state == StageState.Idle && Interlocked.Increment(ref idleCalls) == 1)
                {
                    idleEntered.TrySetResult();
                    if (!releaseIdle.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException();
                }
                active.Set(Stage.Stage_1, state == StageState.Running);
            }, NullLogger<StageManager>.Instance);
        h.Feed(stage, 25, 40, 1, stop.Token);
        var first = stage.CurrentRun;
        try
        {
            firstWork.SetResult();
            await idleEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            stage.OnReading(h.Temp(25, 2), stop.Token); // Old run published Idle, callback still pending.
            Assert.Equal(StageState.Running, stage.State);
            Assert.Contains(Stage.Stage_1, active.Current);
            releaseIdle.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(StageState.Running, stage.State);
            Assert.DoesNotContain(Stage.Stage_1, active.Current);
        }
        finally
        {
            releaseIdle.Set();
            stop.Cancel();
            await Task.WhenAll(first, stage.CurrentRun).WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task F05_Work_begins_with_an_earlier_acquired_resource_in_error()
    {
        var h = new StageHarness();
        var a = new ResourceSimulator("A", acquisitionLatency: TimeSpan.Zero);
        var b = new FakeResource("B") { BlockAcquire = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        ResourceState? stateAtWork = null;
        var stage = h.Stage([a, b], work: _ => { stateAtWork = a.State; return Task.CompletedTask; });
        h.Feed(stage, 25, 40, 1);
        Assert.Equal(ResourceState.Busy, a.State);
        a.Fail();
        b.BlockAcquire.SetResult(); // No intervening sensor reading to drive the error scan.
        await stage.CurrentRun.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ResourceState.Error, stateAtWork);
    }

    [Fact]
    public async Task F06_Duplicate_resource_names_self_deadlock_even_with_ByName()
    {
        var registry = new SensorRegistry();
        var t = new TemperatureSensorSimulator(Guid.NewGuid(), 25);
        var p = new PressureSensorSimulator(Guid.NewGuid(), 40);
        registry.Register(t);
        registry.Register(p);
        var a = new ResourceSimulator("A", acquisitionLatency: TimeSpan.Zero);
        var controller = new MachineController(registry, [a], [new("s", ["A", "A"], _ => true, _ => Task.CompletedTask)]);
        var stage = Assert.IsType<StageManager>(Assert.Single(controller.Stages));
        using var stop = new CancellationTokenSource();
        try
        {
            stage.OnReading(new(t.Id, 25, SensorType.Temperature, DateTime.UtcNow, 1), stop.Token);
            stage.OnReading(new(p.Id, 40, SensorType.Pressure, DateTime.UtcNow, 1), stop.Token);
            Assert.Equal(StageState.Acquiring, stage.State);
            Assert.Equal(ResourceState.Busy, a.State);
            Assert.False(stage.CurrentRun.IsCompleted); // Second acquisition waits for its own first hold.
        }
        finally { stop.Cancel(); await stage.CurrentRun.WaitAsync(TimeSpan.FromSeconds(2)); }
        Assert.Equal(ResourceState.Idle, a.State);
    }

    [Fact]
    public async Task F07_Older_sequence_overwrites_a_newer_measurement_and_starts_work()
    {
        var h = new StageHarness();
        var starts = 0;
        var stage = h.Stage([], work: _ => { starts++; return Task.CompletedTask; });
        h.Feed(stage, 5, 40, 2);
        stage.OnReading(h.Temp(25, 1));
        await stage.CurrentRun;
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task F08_Completed_runs_remain_linked_to_the_parent_cancellation_source()
    {
        var h = new StageHarness();
        using var parent = new CancellationTokenSource();
        var callbacks = 0;
        var stage = h.Stage([], work: ct =>
        {
            ct.Register(() => Interlocked.Increment(ref callbacks));
            return Task.CompletedTask;
        });
        h.Feed(stage, 25, 40, 1, parent.Token);
        await stage.CurrentRun;
        stage.OnReading(h.Temp(25, 2), parent.Token);
        await stage.CurrentRun;
        Assert.Equal(0, callbacks);
        parent.Cancel();
        Assert.Equal(2, callbacks);
    }

    [Theory]
    [InlineData(Stage.Stage_1, 60)]
    [InlineData(Stage.Stage_2, 22)]
    [InlineData(Stage.Stage_3, 9)]
    public void F10_Default_physics_invalidates_each_stage_before_ten_seconds(Stage activeStage, int expectedTicks)
    {
        var temperature = new TemperatureSensorSimulator(Guid.NewGuid(), 20);
        var pressure = new PressureSensorSimulator(Guid.NewGuid(), 30);
        var definition = ExerciseMachine.Stages(TimeSpan.FromSeconds(10))[(int)activeStage];
        IReadOnlySet<Stage> active = new HashSet<Stage> { activeStage };
        var values = new Dictionary<SensorType, double>
        {
            [SensorType.Temperature] = 20,
            [SensorType.Pressure] = 30,
        };
        Assert.True(definition.Rule(values));
        var ticks = 0;
        while (definition.Rule(values) && ticks < 100)
        {
            values[SensorType.Temperature] = temperature.ComputeNext(values[SensorType.Temperature], active, noise: 0);
            values[SensorType.Pressure] = pressure.ComputeNext(values[SensorType.Pressure], active, noise: 0);
            ticks++;
        }
        Assert.Equal(expectedTicks, ticks);
        Assert.False(definition.Rule(values));
        Assert.True(ticks < 100);
    }

    private sealed class EndingSensor(bool fail = false) : ISensor<SensorData>
    {
        public Guid Id { get; } = Guid.NewGuid();
        public SensorData GetSnapshot() => new(Id, 25, SensorType.Temperature, DateTime.UtcNow, 1);
        public async IAsyncEnumerable<SensorData> GetAsyncEnumerable(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            if (fail) throw new InvalidOperationException("Injected sensor failure");
            yield break;
        }
    }
}
