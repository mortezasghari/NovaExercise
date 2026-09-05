using Controller;
using Microsoft.Extensions.Logging.Abstractions;
using Resources;
using Sensor;
using Simulator;

namespace NovaTests;

/// <summary>
/// Regressions for the findings in Review/REVIEW_REPORT.md. Each test asserts the corrected behaviour the
/// corresponding probe showed to be missing. Names carry the finding id so the report and the tests line up.
/// </summary>
public class ReviewRegressionTests
{
    private static ResourceSimulator Resource(string name) => new(name, acquisitionLatency: TimeSpan.Zero);

    // ---------------------------------------------------------------- F01: frames, not a skew window

    [Fact]
    public async Task F01_A_mixed_frame_never_triggers_when_both_complete_frames_are_false()
    {
        var h = new StageHarness();
        var starts = 0;
        var stage = h.Stage([Resource("A")], work: _ => { Interlocked.Increment(ref starts); return Task.CompletedTask; });

        h.Feed(stage, 5, 40, 1);      // complete frame 1: rule 1 false
        stage.OnReading(h.Temp(25, 2)); // with pressure 40 from tick 1 the rule would be true, but that pair never existed
        Assert.Equal(StageState.Idle, stage.State);

        stage.OnReading(h.Pres(150, 2)); // complete frame 2: rule 1 false
        await stage.CurrentRun;

        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task F01_A_frame_needs_every_sensor_to_report_again_after_the_previous_frame()
    {
        var h = new StageHarness();
        var stage = h.Stage([Resource("A")]);

        h.Feed(stage, 5, 40, 1);       // frame 1, false
        stage.OnReading(h.Temp(25, 2)); // temperature only
        stage.OnReading(h.Temp(25, 3)); // temperature again: still no new pressure
        Assert.Equal(StageState.Idle, stage.State);

        stage.OnReading(h.Pres(40, 3)); // now the frame is complete and true
        await stage.CurrentRun;
        Assert.Contains(StageState.Running, h.StatesSoFar());
    }

    // ---------------------------------------------------------------- F02: sensor loss policy

    [Fact]
    public async Task F02_Work_continues_while_sensors_are_silent_and_the_alarm_rises_after_the_budget()
    {
        var h = new StageHarness();
        var a = Resource("A");
        var gate = new TaskCompletionSource();
        var stage = h.Stage([a], work: ct => gate.Task.WaitAsync(ct), options: h.Options(staleAfter: TimeSpan.FromSeconds(1), dataBudget: TimeSpan.FromSeconds(5)));
        h.Feed(stage, 25, 40, 1);
        await Wait.Until(() => stage.State == StageState.Running);

        h.Time.Advance(TimeSpan.FromSeconds(2));
        stage.Scan();
        Assert.Equal(StageState.Running, stage.State);   // stale, but still working
        Assert.False(stage.DataAlarm);

        h.Time.Advance(TimeSpan.FromSeconds(4));
        stage.Scan();
        Assert.Equal(StageState.Running, stage.State);   // blind, still working: it cannot know which direction is safe
        Assert.True(stage.DataAlarm);
        Assert.Equal(ResourceState.Busy, a.State);

        gate.SetResult();
    }

    [Fact]
    public async Task F02_Nothing_starts_without_a_fresh_frame_and_a_fresh_frame_clears_the_alarm()
    {
        var h = new StageHarness();
        var stage = h.Stage([Resource("A")], options: h.Options(dataBudget: TimeSpan.FromSeconds(1)));

        h.Time.Advance(TimeSpan.FromSeconds(2));
        stage.Scan();
        Assert.True(stage.DataAlarm);
        Assert.Equal(StageState.Idle, stage.State);

        stage.OnReading(h.Temp(25, 1));   // a lone reading is not a frame
        stage.Scan();
        Assert.True(stage.DataAlarm);
        Assert.Equal(StageState.Idle, stage.State);

        stage.OnReading(h.Pres(40, 1));   // a complete frame: alarm off, rule evaluated
        await stage.CurrentRun;
        Assert.False(stage.DataAlarm);
        Assert.Contains(StageState.Running, h.StatesSoFar());
    }

    [Fact]
    public async Task F02_A_silent_sensor_does_not_silence_resource_monitoring()
    {
        var h = new StageHarness();
        var a = Resource("A");
        var gate = new TaskCompletionSource();
        var stage = h.Stage([a], work: ct => gate.Task.WaitAsync(ct));
        h.Feed(stage, 25, 40, 1);

        a.Fail();
        stage.Scan();   // the watchdog, with no reading at all

        await stage.CurrentRun.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(StageState.Faulted, stage.State);
    }

    // ---------------------------------------------------------------- F03: dead sensor streams

    [Fact]
    public async Task F03_A_sensor_stream_that_ends_fails_the_merged_stream_immediately()
    {
        var registry = new SensorRegistry();
        var sensor = new EndingSensor();
        registry.Register(sensor);
        using var cts = new CancellationTokenSource();

        var e = await Assert.ThrowsAsync<SensorStreamException>(async () =>
        {
            await foreach (var _ in registry.ReadAllAsync(cts.Token)) { }
        }).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(sensor.Id, e.SensorId);
    }

    [Fact]
    public async Task F03_A_sensor_stream_that_throws_fails_the_merged_stream_with_the_cause()
    {
        var registry = new SensorRegistry();
        registry.Register(new EndingSensor(fail: true));
        using var cts = new CancellationTokenSource();

        var e = await Assert.ThrowsAsync<SensorStreamException>(async () =>
        {
            await foreach (var _ in registry.ReadAllAsync(cts.Token)) { }
        }).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsType<InvalidOperationException>(e.InnerException);
    }

    [Fact]
    public async Task F03_A_dead_stream_raises_the_stage_alarm_at_once_and_the_stage_stays_alive()
    {
        var registry = new SensorRegistry();
        registry.Register(new EndingSensor(fail: true));
        var stage = new StageManager("s", [Resource("A")], registry, _ => true, ct => Task.CompletedTask, _ => { },
            NullLogger<StageManager>.Instance, new StageOptions { WatchdogPeriod = TimeSpan.FromMilliseconds(10) });
        using var cts = new CancellationTokenSource();

        var run = stage.RunAsync(cts.Token);
        await Wait.Until(() => stage.DataAlarm);

        Assert.False(run.IsCompleted);   // still running its watchdog, ready to be shut down cleanly
        cts.Cancel();
        await run;
    }

    // ---------------------------------------------------------------- F04: ordered lifecycle transitions

    [Fact]
    public async Task F04_An_old_runs_idle_publication_cannot_clear_a_new_runs_active_flag()
    {
        var h = new StageHarness();
        var active = new ActiveStages();
        var firstWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var idleEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseIdle = new ManualResetEventSlim();
        var workCalls = 0;
        var idleCalls = 0;
        var stage = h.Stage([], rule: _ => true,
            work: ct => Interlocked.Increment(ref workCalls) == 1 ? firstWork.Task : Task.Delay(Timeout.InfiniteTimeSpan, ct),
            stateChanged: state =>
            {
                if (state == StageState.Idle && Interlocked.Increment(ref idleCalls) == 1)
                {
                    idleEntered.TrySetResult();
                    releaseIdle.Wait(TimeSpan.FromSeconds(5)); // the old run's publication is slow
                }

                active.Set(Stage.Stage_1, state == StageState.Running);
            });
        using var cts = new CancellationTokenSource();

        h.Feed(stage, 25, 40, 1, cts.Token);
        var first = stage.CurrentRun;
        firstWork.SetResult();
        await idleEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // A new frame arrives while the old run is still publishing Idle. It must wait: transitions are one step.
        var second = Task.Run(() => h.Feed(stage, 25, 40, 2, cts.Token));
        Assert.False(await Wait.Completes(second, 100));

        releaseIdle.Set();
        await second;
        await first;
        await Wait.Until(() => stage.State == StageState.Running);
        Assert.Contains(Stage.Stage_1, active.Current);   // the new run's flag survived the old run's publication

        cts.Cancel();
        await stage.CurrentRun;
    }

    // ---------------------------------------------------------------- F05: held resources re-checked before work

    [Fact]
    public async Task F05_Work_never_starts_with_an_earlier_acquired_resource_in_error()
    {
        var h = new StageHarness();
        var a = Resource("A");
        var b = new FakeResource("B") { BlockAcquire = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var workStarted = false;
        var stage = h.Stage([a, b], work: _ => { workStarted = true; return Task.CompletedTask; });

        h.Feed(stage, 25, 40, 1);
        await Wait.Until(() => a.State == ResourceState.Busy);
        a.Fail();
        b.BlockAcquire.SetResult();   // no reading and no scan in between: the run itself must notice
        await stage.CurrentRun.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(workStarted);
        Assert.Equal(StageState.Faulted, stage.State);
        Assert.Equal(ResourceState.Idle, b.State);   // released, not leaked
    }

    // ---------------------------------------------------------------- F06: configuration validation

    [Fact]
    public void F06_A_stage_listing_the_same_resource_twice_is_rejected()
    {
        var registry = Registry();

        var e = Assert.Throws<ArgumentException>(() =>
            new MachineController(registry, [Resource("A")], [new("s", ["A", "A"], _ => true, _ => Task.CompletedTask)]));

        Assert.Contains("more than once", e.Message);
    }

    [Fact]
    public void F06_Duplicate_stage_names_are_rejected()
    {
        var registry = Registry();

        Assert.Throws<ArgumentException>(() => new MachineController(registry, [Resource("A")],
        [
            new("s", ["A"], _ => true, _ => Task.CompletedTask),
            new("s", ["A"], _ => true, _ => Task.CompletedTask),
        ]));
    }

    [Fact]
    public void F06_A_rule_reading_an_unregistered_sensor_type_is_rejected_at_construction()
    {
        var registry = new SensorRegistry();
        registry.Register(new TemperatureSensorSimulator(Guid.NewGuid(), 20));   // no pressure sensor

        var e = Assert.Throws<ArgumentException>(() =>
            new MachineController(registry, [Resource("A")], [new("s", ["A"], ExerciseMachine.Rule1, _ => Task.CompletedTask)]));

        Assert.Contains("Pressure", e.Message);
    }

    [Fact]
    public void F06_Registration_after_the_machine_is_built_is_rejected()
    {
        var registry = Registry();
        _ = new MachineController(registry, [Resource("A")], [new("s", ["A"], _ => true, _ => Task.CompletedTask)]);

        Assert.True(registry.IsFrozen);
        Assert.Throws<InvalidOperationException>(() => registry.Register(new TemperatureSensorSimulator(Guid.NewGuid(), 20)));
    }

    // ---------------------------------------------------------------- F07: monotonic sequences

    [Fact]
    public async Task F07_An_older_sequence_never_overwrites_a_newer_measurement()
    {
        var h = new StageHarness();
        var starts = 0;
        var stage = h.Stage([Resource("A")], work: _ => { Interlocked.Increment(ref starts); return Task.CompletedTask; });

        h.Feed(stage, 5, 40, 2);        // frame at tick 2: false
        stage.OnReading(h.Temp(25, 1)); // a replayed older temperature: dropped
        stage.OnReading(h.Pres(40, 3)); // even with a new pressure, the temperature is still 5
        await stage.CurrentRun;

        Assert.Equal(0, starts);
    }

    [Fact]
    public void F07_A_duplicate_sequence_is_dropped()
    {
        var h = new StageHarness();
        var stage = h.Stage([Resource("A")]);

        stage.OnReading(h.Temp(25, 1));
        stage.OnReading(h.Temp(25, 1));   // same sequence again: does not count as a report
        Assert.Equal(StageState.Idle, stage.State);

        stage.OnReading(h.Pres(40, 1));
        Assert.NotEqual(StageState.Idle, stage.State);
    }

    // ---------------------------------------------------------------- F08: cancellation sources are disposed

    [Fact]
    public async Task F08_Completed_runs_are_no_longer_reachable_from_the_shutdown_token()
    {
        var h = new StageHarness();
        using var parent = new CancellationTokenSource();
        var callbacks = 0;
        var stage = h.Stage([Resource("A")], work: ct =>
        {
            ct.Register(() => Interlocked.Increment(ref callbacks));
            return Task.CompletedTask;
        });

        h.Feed(stage, 25, 40, 1, parent.Token);
        await stage.CurrentRun;
        h.Feed(stage, 25, 40, 2, parent.Token);
        await stage.CurrentRun;

        parent.Cancel();
        Assert.Equal(0, callbacks);   // both runs' linked sources were disposed when the runs ended
    }

    // ---------------------------------------------------------------- hardening from the review's observations

    [Fact]
    public async Task A_release_that_throws_does_not_stop_the_other_releases()
    {
        var h = new StageHarness();
        var journal = new List<string>();
        var a = new FakeResource("A", journal) { ThrowOnRelease = true };
        var b = new FakeResource("B", journal);
        var stage = h.Stage([a, b], work: _ => Task.CompletedTask);

        h.Feed(stage, 25, 40, 1);
        await stage.CurrentRun;

        Assert.Equal(ResourceState.Idle, b.State);
        Assert.Contains("release:B", journal);
        Assert.Equal(StageState.Idle, stage.State);
    }

    [Fact]
    public async Task A_second_RunAsync_on_the_same_stage_is_rejected()
    {
        var h = new StageHarness();
        var stage = h.Stage([Resource("A")]);
        using var cts = new CancellationTokenSource();
        var first = stage.RunAsync(cts.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => stage.RunAsync(cts.Token));

        cts.Cancel();
        await first;
    }

    private static SensorRegistry Registry()
    {
        var registry = new SensorRegistry();
        registry.Register(new TemperatureSensorSimulator(Guid.NewGuid(), 20));
        registry.Register(new PressureSensorSimulator(Guid.NewGuid(), 30));
        return registry;
    }

    private sealed class EndingSensor(bool fail = false) : ISensor<SensorData>
    {
        public Guid Id { get; } = Guid.NewGuid();

        public SensorData GetSnapshot() => new(Id, 25, SensorType.Temperature, DateTime.UtcNow, 1);

        public async IAsyncEnumerable<SensorData> GetAsyncEnumerable(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (fail)
            {
                throw new InvalidOperationException("Injected sensor failure");
            }

            yield break;
        }
    }
}
