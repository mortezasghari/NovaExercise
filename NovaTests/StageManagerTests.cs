using Controller;
using Resources;
using Sensor;
using Simulator;

namespace NovaTests;

public class StageManagerTests
{
    private static ResourceSimulator Resource(string name) => new(name, acquisitionLatency: TimeSpan.Zero);

    // ---------------------------------------------------------------- readiness: the atomic view

    [Fact]
    public void Does_not_fire_until_every_registered_sensor_has_reported()
    {
        var h = new StageHarness();
        var stage = h.Stage([Resource("A")]);

        stage.OnReading(h.Temp(25, 1));

        Assert.Equal(StageState.Idle, stage.State);
        Assert.Empty(h.StatesSoFar());
    }

    [Fact]
    public void Does_not_fire_on_a_reading_that_was_never_measured()
    {
        var h = new StageHarness();
        var stage = h.Stage([Resource("A")]);

        stage.OnReading(h.Temp(25, 1));
        stage.OnReading(h.Pres(40, 0));   // sequence 0: the constructor value, not a measurement

        Assert.Equal(StageState.Idle, stage.State);
    }

    [Fact]
    public void Does_not_fire_when_the_readings_are_too_far_apart_in_time()
    {
        var h = new StageHarness();
        var stage = h.Stage([Resource("A")], maxSkew: TimeSpan.FromMilliseconds(250));

        stage.OnReading(h.Temp(25, 1));
        stage.OnReading(h.Pres(40, 4));   // 300 ms later than the temperature reading

        Assert.Equal(StageState.Idle, stage.State);
    }

    [Fact]
    public void Fires_when_the_readings_are_within_the_skew_window()
    {
        var h = new StageHarness();
        var stage = h.Stage([Resource("A")], maxSkew: TimeSpan.FromMilliseconds(250));

        stage.OnReading(h.Temp(25, 1));
        stage.OnReading(h.Pres(40, 3));   // 200 ms apart: still one consistent state

        Assert.NotEqual(StageState.Idle, stage.State);
    }

    [Fact]
    public async Task A_late_reading_can_complete_a_consistent_pair()
    {
        var h = new StageHarness();
        var stage = h.Stage([Resource("A")]);

        stage.OnReading(h.Temp(25, 1));
        stage.OnReading(h.Pres(40, 5));   // too far from tick 1
        Assert.Equal(StageState.Idle, stage.State);

        stage.OnReading(h.Temp(25, 5));   // now both are from tick 5
        await stage.CurrentRun;
        Assert.Contains(StageState.Running, h.StatesSoFar());
    }

    [Fact]
    public void Does_not_fire_when_the_rule_is_false()
    {
        var h = new StageHarness();
        var stage = h.Stage([Resource("A")]);

        h.Feed(stage, temperature: 5, pressure: 40, tick: 1);

        Assert.Equal(StageState.Idle, stage.State);
    }

    [Fact]
    public async Task With_two_sensors_of_the_same_type_the_newest_wins()
    {
        var h = new StageHarness();
        var secondThermometer = new TemperatureSensorSimulator(Guid.NewGuid(), 20.0);
        h.Registry.Register(secondThermometer);
        var stage = h.Stage([Resource("A")]);

        stage.OnReading(h.Temp(5, 1));                                                              // old: rule false
        stage.OnReading(new SensorData(secondThermometer.Id, 25, SensorType.Temperature, h.Temp(0, 2).Timestamp, 2)); // newer: rule true
        stage.OnReading(h.Pres(40, 2));

        await stage.CurrentRun;
        Assert.Contains(StageState.Running, h.StatesSoFar());
    }

    // ---------------------------------------------------------------- the run

    [Fact]
    public async Task A_run_goes_idle_acquiring_running_idle_and_releases_everything()
    {
        var h = new StageHarness();
        var a = Resource("A");
        var b = Resource("B");
        var stage = h.Stage([a, b]);

        h.Feed(stage, 25, 40, 1);
        await stage.CurrentRun;

        Assert.Equal([StageState.Acquiring, StageState.Running, StageState.Idle], h.StatesSoFar());
        Assert.Equal(ResourceState.Idle, a.State);
        Assert.Equal(ResourceState.Idle, b.State);
    }

    [Fact]
    public async Task Resources_are_acquired_in_the_order_given_and_running_is_announced_only_after_the_last_one()
    {
        var h = new StageHarness();
        var journal = new List<string>();
        var a = new FakeResource("A", journal);
        var b = new FakeResource("B", journal);
        var stage = new StageManager("s", [b, a], h.Registry, StageHarness.Rule1, ct => Task.CompletedTask,
            state => { lock (journal) journal.Add($"state:{state}"); }, Microsoft.Extensions.Logging.Abstractions.NullLogger<StageManager>.Instance, h.Options());

        h.Feed(stage, 25, 40, 1);
        await stage.CurrentRun;

        Assert.Equal(
            ["state:Acquiring", "acquire:B", "hold:B", "acquire:A", "hold:A", "state:Running", "release:B", "release:A", "state:Idle"],
            journal);
    }

    [Fact]
    public async Task Readings_during_a_run_do_not_start_a_second_run()
    {
        var h = new StageHarness();
        var gate = new TaskCompletionSource();
        var stage = h.Stage([Resource("A")], work: ct => gate.Task.WaitAsync(ct));

        h.Feed(stage, 25, 40, 1);
        var run = stage.CurrentRun;
        h.Feed(stage, 25, 40, 2);
        h.Feed(stage, 25, 40, 3);

        Assert.Same(run, stage.CurrentRun);
        gate.SetResult();
        await run;
        Assert.Equal(1, h.StatesSoFar().Count(s => s == StageState.Running));
    }

    [Fact]
    public async Task Fires_again_after_completing_when_the_rule_still_holds()
    {
        var h = new StageHarness();
        var stage = h.Stage([Resource("A")]);

        h.Feed(stage, 25, 40, 1);
        await stage.CurrentRun;
        h.Feed(stage, 25, 40, 2);
        await stage.CurrentRun;

        Assert.Equal(2, h.StatesSoFar().Count(s => s == StageState.Running));
    }

    [Fact]
    public async Task Work_that_throws_releases_resources_and_leaves_the_stage_usable()
    {
        var h = new StageHarness();
        var a = Resource("A");
        var stage = h.Stage([a], work: _ => throw new InvalidOperationException("boom"));

        h.Feed(stage, 25, 40, 1);
        await stage.CurrentRun;

        Assert.Equal(StageState.Idle, stage.State);
        Assert.Equal(ResourceState.Idle, a.State);

        h.Feed(stage, 25, 40, 2);
        await stage.CurrentRun;
        Assert.Equal(2, h.StatesSoFar().Count(s => s == StageState.Running));
    }

    [Fact]
    public void A_rule_that_throws_propagates_out_of_the_reading()
    {
        var h = new StageHarness();
        var stage = h.Stage([Resource("A")], rule: _ => throw new InvalidOperationException("bad rule"));

        stage.OnReading(h.Temp(25, 1));
        Assert.Throws<InvalidOperationException>(() => stage.OnReading(h.Pres(40, 1)));
    }

    // ---------------------------------------------------------------- abort: rule no longer holds

    [Fact]
    public async Task Rule_going_false_while_running_cancels_the_work_and_releases()
    {
        var h = new StageHarness();
        var a = Resource("A");
        var gate = new TaskCompletionSource();
        var stage = h.Stage([a], work: ct => gate.Task.WaitAsync(ct));

        h.Feed(stage, 25, 40, 1);
        await Wait.Until(() => stage.State == StageState.Running);
        h.Feed(stage, 5, 40, 2);
        await stage.CurrentRun;

        Assert.Equal(StageState.Idle, stage.State);
        Assert.Equal(ResourceState.Idle, a.State);
        Assert.False(gate.Task.IsCompleted);   // the work was cancelled, not finished
    }

    [Fact]
    public async Task Rule_going_false_while_acquiring_releases_the_partial_hold()
    {
        var h = new StageHarness();
        var a = Resource("A");
        var b = Resource("B");
        b.TryAcquire();   // someone else has B: the stage will hold A and wait
        var stage = h.Stage([a, b]);

        h.Feed(stage, 25, 40, 1);
        await Wait.Until(() => a.State == ResourceState.Busy);
        h.Feed(stage, 5, 40, 2);
        await stage.CurrentRun;

        Assert.Equal(StageState.Idle, stage.State);
        Assert.Equal(ResourceState.Idle, a.State);
        Assert.Equal(ResourceState.Busy, b.State);   // still held by the other party, untouched
    }

    [Fact]
    public async Task A_lone_reading_is_not_a_frame_and_cannot_abort_a_run()
    {
        var h = new StageHarness();
        var gate = new TaskCompletionSource();
        var stage = h.Stage([Resource("A")], work: ct => gate.Task.WaitAsync(ct));

        h.Feed(stage, 25, 40, 1);
        await Wait.Until(() => stage.State == StageState.Running);
        stage.OnReading(h.Temp(5, 2));   // rule would be false against the old pressure, but that pair is not a frame

        Assert.Equal(StageState.Running, stage.State);
        gate.SetResult();
        await stage.CurrentRun;
    }

    [Fact]
    public async Task Rule_flapping_at_the_boundary_starts_and_aborts_repeatedly()
    {
        var h = new StageHarness();
        var gate = new TaskCompletionSource();
        var stage = h.Stage([Resource("A")], work: ct => gate.Task.WaitAsync(ct));

        for (var tick = 1; tick <= 3; tick++)
        {
            h.Feed(stage, 10.1, 40, tick * 2 - 1);   // just inside rule 1
            await Wait.Until(() => stage.State == StageState.Running);
            h.Feed(stage, 9.9, 40, tick * 2);        // just outside
            await stage.CurrentRun;
        }

        Assert.Equal(3, h.StatesSoFar().Count(s => s == StageState.Running));
        Assert.Equal(StageState.Idle, stage.State);
    }

    // ---------------------------------------------------------------- abort: resource faults

    [Fact]
    public async Task Held_resource_failing_while_running_aborts_into_faulted()
    {
        var h = new StageHarness();
        var a = Resource("A");
        var gate = new TaskCompletionSource();
        var stage = h.Stage([a], work: ct => gate.Task.WaitAsync(ct));

        h.Feed(stage, 25, 40, 1);
        await Wait.Until(() => stage.State == StageState.Running);
        a.Fail();
        h.Feed(stage, 25, 40, 2);   // the scan cycle notices
        await stage.CurrentRun;

        Assert.Equal(StageState.Faulted, stage.State);
        Assert.Equal(ResourceState.Error, a.State);   // released, still in error
    }

    [Fact]
    public async Task Held_resource_failing_while_acquiring_aborts_into_faulted_and_releases()
    {
        var h = new StageHarness();
        var a = Resource("A");
        var b = Resource("B");
        b.TryAcquire();
        var stage = h.Stage([a, b]);

        h.Feed(stage, 25, 40, 1);
        await Wait.Until(() => a.State == ResourceState.Busy);
        a.Fail();
        h.Feed(stage, 25, 40, 2);
        await stage.CurrentRun;

        Assert.Equal(StageState.Faulted, stage.State);
        Assert.Equal(ResourceState.Error, a.State);
        a.Recover();
        Assert.True(a.TryAcquire());   // it was released
    }

    [Fact]
    public async Task Resource_refusing_acquisition_puts_the_stage_in_faulted_and_releases_earlier_holds()
    {
        var h = new StageHarness();
        var a = Resource("A");
        var b = new FakeResource("B") { ThrowOnAcquire = true };
        var stage = h.Stage([a, b]);

        h.Feed(stage, 25, 40, 1);
        await stage.CurrentRun;

        Assert.Equal(StageState.Faulted, stage.State);
        Assert.Equal(ResourceState.Idle, a.State);
        Assert.DoesNotContain(StageState.Running, h.StatesSoFar());
    }

    [Fact]
    public void Does_not_start_while_any_of_its_resources_is_in_error()
    {
        var h = new StageHarness();
        var a = Resource("A");
        a.Fail();
        var stage = h.Stage([a]);

        h.Feed(stage, 25, 40, 1);

        Assert.Equal(StageState.Idle, stage.State);
        Assert.Empty(h.StatesSoFar());
    }

    [Fact]
    public async Task Faulted_stage_starts_again_once_the_resource_has_recovered()
    {
        var h = new StageHarness();
        var a = Resource("A");
        var gate = new TaskCompletionSource();
        var stage = h.Stage([a], work: ct => gate.Task.WaitAsync(ct));

        h.Feed(stage, 25, 40, 1);
        await Wait.Until(() => stage.State == StageState.Running);
        a.Fail();
        h.Feed(stage, 25, 40, 2);
        await stage.CurrentRun;
        Assert.Equal(StageState.Faulted, stage.State);

        h.Feed(stage, 25, 40, 3);
        Assert.Equal(StageState.Faulted, stage.State);   // still blocked

        a.Recover();
        gate.SetResult();
        h.Feed(stage, 25, 40, 4);
        await stage.CurrentRun;
        Assert.Equal(StageState.Idle, stage.State);
        Assert.Equal(2, h.StatesSoFar().Count(s => s == StageState.Running));
    }

    // ---------------------------------------------------------------- lifetime

    [Fact]
    public async Task Shutdown_cancels_a_running_stage_and_releases_its_resources()
    {
        var h = new StageHarness();
        var a = Resource("A");
        var gate = new TaskCompletionSource();
        var stage = h.Stage([a], work: ct => gate.Task.WaitAsync(ct));
        using var cts = new CancellationTokenSource();

        h.Feed(stage, 25, 40, 1, cts.Token);
        await Wait.Until(() => stage.State == StageState.Running);
        cts.Cancel();
        await stage.CurrentRun;

        Assert.Equal(StageState.Idle, stage.State);
        Assert.Equal(ResourceState.Idle, a.State);
    }

    [Fact]
    public async Task RunAsync_stops_on_cancellation_and_waits_for_its_run_to_end()
    {
        var h = new StageHarness();
        var a = Resource("A");
        var stage = h.Stage([a], rule: _ => true, work: ct => Task.Delay(Timeout.InfiniteTimeSpan, ct));
        using var cts = new CancellationTokenSource();
        var run = stage.RunAsync(cts.Token);

        var temperature = (TemperatureSensorSimulator)h.Registry.Sensors.First(s => s.Id == h.TemperatureId);
        var pressure = (PressureSensorSimulator)h.Registry.Sensors.First(s => s.Id == h.PressureId);
        var t0 = new DateTime(2026, 1, 1);
        await Wait.Until(() =>
        {
            temperature.Tick(t0, new HashSet<Stage>());
            pressure.Tick(t0, new HashSet<Stage>());
            return stage.State == StageState.Running;
        });

        cts.Cancel();
        await run;

        Assert.Equal(StageState.Idle, stage.State);
        Assert.Equal(ResourceState.Idle, a.State);
    }
}
