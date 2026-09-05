using Controller;
using Microsoft.Extensions.Logging.Abstractions;
using Resources;
using Sensor;
using Simulator;

namespace NovaTests;

/// <summary>
/// The concurrency problems the exercise asks for, in the vocabulary of OSTEP chapter 32: one deadlock
/// (circular wait between stages) and two non-deadlock bugs (atomicity violation, order violation), each shown
/// as the bug and as the mechanism that prevents it. The schedules are forced with gates, never with sleeps.
/// </summary>
public class ConcurrencyExhibitTests
{
    private static readonly IReadOnlySet<Stage> None = new HashSet<Stage>();

    // ---------------------------------------------------------------- deadlock: circular wait

    [Fact]
    public async Task Deadlock_ring_ordered_acquisition_leaves_every_stage_holding_one_and_waiting_for_the_next()
    {
        var h = new StageHarness();
        var shared = new[] { "R_A", "R_B", "R_C" }.ToDictionary(n => n, n => (IResource)new FakeResource(n));
        var secondAcquisitions = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        // Ring: each stage's first resource is the next stage's second.
        var stages = new[]
        {
            RingStage(h, "stage_1", ["R_A", "R_B"], shared, secondAcquisitions),
            RingStage(h, "stage_2", ["R_B", "R_C"], shared, secondAcquisitions),
            RingStage(h, "stage_3", ["R_C", "R_A"], shared, secondAcquisitions),
        };

        try
        {
            foreach (var stage in stages)
            {
                h.Feed(stage, 25, 40, 1, cts.Token);
            }

            // Every stage now holds exactly its first resource and is parked before its second request.
            await Wait.Until(() => shared.Values.All(r => r.State == ResourceState.Busy));
            secondAcquisitions.SetResult();

            // Each requests a resource held by the next: nobody can ever proceed.
            Assert.False(await Wait.Completes(Task.WhenAny(stages.Select(s => s.CurrentRun)), 300));
            Assert.All(stages, s => Assert.Equal(StageState.Acquiring, s.State));
            Assert.All(shared.Values, r => Assert.Equal(ResourceState.Busy, r.State));
        }
        finally
        {
            cts.Cancel();
            await Task.WhenAll(stages.Select(s => s.CurrentRun));
        }
    }

    [Fact]
    public async Task Prevention_the_controllers_global_order_lets_the_same_ring_complete()
    {
        var (registry, temperature, pressure) = Sensors();
        var resources = new[] { "R_A", "R_B", "R_C" }.Select(n => new ResourceSimulator(n, acquisitionLatency: TimeSpan.FromMilliseconds(5))).ToList();
        var completed = new HashSet<string>();
        var controller = new MachineController(registry, resources, ExerciseMachine.RingStages(TimeSpan.FromMilliseconds(10)),
            ResourceOrdering.ByName,
            stageStateChanged: (name, state) => { if (state == StageState.Idle) lock (completed) completed.Add(name); });
        using var cts = new CancellationTokenSource();
        var run = controller.RunAsync(cts.Token);

        try
        {
            await Wait.Until(() =>
            {
                temperature.Tick(DateTime.UtcNow, None);
                pressure.Tick(DateTime.UtcNow, None);
                lock (completed) return completed.Count == 3;
            }, timeoutMs: 5000);
        }
        finally
        {
            cts.Cancel();
            await run;
        }

        Assert.All(resources, r => Assert.Equal(ResourceState.Idle, r.State));
    }

    [Fact]
    public async Task The_exercise_map_as_listed_happens_to_be_cycle_free()
    {
        // stage_1 [A,B], stage_2 [C,B], stage_3 [A,C]: every pair is ordered consistently (A < C < B), so
        // listed-order acquisition cannot deadlock on this map. It is safe by luck, not by design.
        var (registry, temperature, pressure) = Sensors();
        var resources = new[] { "R_A", "R_B", "R_C" }.Select(n => new ResourceSimulator(n, acquisitionLatency: TimeSpan.FromMilliseconds(5))).ToList();
        var completed = new HashSet<string>();
        var controller = new MachineController(registry, resources, ExerciseMachine.Stages(TimeSpan.FromMilliseconds(10)),
            ResourceOrdering.AsListed,
            stageStateChanged: (name, state) => { if (state == StageState.Idle) lock (completed) completed.Add(name); });
        using var cts = new CancellationTokenSource();
        var run = controller.RunAsync(cts.Token);

        try
        {
            await Wait.Until(() =>
            {
                temperature.Tick(DateTime.UtcNow, None);
                pressure.Tick(DateTime.UtcNow, None);
                lock (completed) return completed.Count == 3;
            }, timeoutMs: 5000);
        }
        finally
        {
            cts.Cancel();
            await run;
        }
    }

    // ---------------------------------------------------------------- atomicity violation

    [Fact]
    public async Task Atomicity_a_rule_is_only_ever_evaluated_on_a_complete_frame()
    {
        // The chapter's bug: check one thing, act on another, with a change in between. Here the check would be
        // the new temperature and the act would use the old pressure. The frame rule makes that impossible.
        var h = new StageHarness();
        var starts = 0;
        var stage = h.Stage([new FakeResource("A")], work: _ => { Interlocked.Increment(ref starts); return Task.CompletedTask; });

        h.Feed(stage, 5, 40, 1);         // false
        stage.OnReading(h.Temp(25, 2));  // would be true against the stale pressure
        stage.OnReading(h.Pres(150, 2)); // false against the pressure that belongs with it
        await stage.CurrentRun;

        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task Atomicity_the_stage_set_is_swapped_whole_so_a_reader_never_sees_a_half_update()
    {
        var active = new ActiveStages();
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                active.Set(Stage.Stage_1, true);
                active.Set(Stage.Stage_2, true);
                active.Set(Stage.Stage_1, false);
                active.Set(Stage.Stage_2, false);
            }
        });

        await Task.Yield();
        for (var i = 0; i < 100_000; i++)
        {
            var snapshot = active.Current;
            Assert.Equal(snapshot.Count, snapshot.Count);          // a mutable set edited underneath us would differ
            Assert.True(snapshot.Count <= 2);
        }

        stop.Cancel();
        await writer;
    }

    // ---------------------------------------------------------------- order violation

    [Fact]
    public void Order_a_stage_does_not_act_before_its_sensors_have_ever_measured()
    {
        // The constructor value is a real number with a real timestamp, but nothing measured it.
        var h = new StageHarness();
        var stage = h.Stage([new FakeResource("A")]);
        var temperature = h.Registry.Sensors.First(s => s.Id == h.TemperatureId).GetSnapshot();
        var pressure = h.Registry.Sensors.First(s => s.Id == h.PressureId).GetSnapshot();

        stage.OnReading(temperature with { Value = 25 });
        stage.OnReading(pressure with { Value = 40 });

        Assert.Equal(0, temperature.Sequence);
        Assert.Equal(StageState.Idle, stage.State);
    }

    [Fact]
    public async Task Order_a_stage_is_announced_active_only_after_it_holds_every_resource()
    {
        var h = new StageHarness();
        var journal = new List<string>();
        var a = new FakeResource("A", journal);
        var b = new FakeResource("B", journal) { BlockAcquire = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var stage = new StageManager("s", [a, b], h.Registry, StageHarness.Rule1, ct => Task.CompletedTask,
            state => { lock (journal) journal.Add($"state:{state}"); }, NullLogger<StageManager>.Instance, h.Options());

        h.Feed(stage, 25, 40, 1);
        await Wait.Until(() => { lock (journal) return journal.Contains("acquire:B"); });
        lock (journal)
        {
            Assert.DoesNotContain("state:Running", journal);   // holds A, waiting for B: not active yet
        }

        b.BlockAcquire.SetResult();
        await stage.CurrentRun;
        lock (journal)
        {
            Assert.True(journal.IndexOf("hold:B") < journal.IndexOf("state:Running"));
        }
    }

    [Fact]
    public void Order_releasing_before_acquiring_is_rejected_rather_than_corrupting_the_resource()
    {
        var r = new ResourceSimulator("A", acquisitionLatency: TimeSpan.Zero);

        Assert.Throws<InvalidOperationException>(r.Release);
        Assert.True(r.TryAcquire());   // the semaphore count was not pushed above one
        Assert.False(r.TryAcquire());
    }

    // ---------------------------------------------------------------- helpers

    private static StageManager RingStage(StageHarness h, string name, string[] order, IReadOnlyDictionary<string, IResource> shared, TaskCompletionSource secondAcquisitions)
    {
        var gate = new StageGate(secondAcquisitions); // one per stage: its first acquisition passes, its second waits
        return new StageManager(name, order.Select(n => (IResource)new StageView(shared[n], gate)).ToList(), h.Registry,
            _ => true, ct => Task.Delay(10, ct), _ => { }, NullLogger<StageManager>.Instance, h.Options());
    }

    private static (SensorRegistry Registry, TemperatureSensorSimulator Temperature, PressureSensorSimulator Pressure) Sensors()
    {
        var registry = new SensorRegistry();
        var temperature = new TemperatureSensorSimulator(Guid.NewGuid(), 25.0);
        var pressure = new PressureSensorSimulator(Guid.NewGuid(), 40.0);
        registry.Register(temperature);
        registry.Register(pressure);
        return (registry, temperature, pressure);
    }
}
