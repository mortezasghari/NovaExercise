using Controller;
using Microsoft.Extensions.Logging.Abstractions;
using Resources;
using Sensor;
using Simulator;

namespace NovaTests;

/// <summary>
/// The concurrency problems the exercise asks for, in the vocabulary of OSTEP chapter 32: one deadlock
/// (circular wait between stages) and two non-deadlock bugs (atomicity violation, order violation), each
/// shown as the bug and as the mechanism that prevents it.
/// </summary>
public class ConcurrencyExhibitTests
{
    private static readonly TimeSpan Latency = TimeSpan.FromMilliseconds(5);

    // ---------------------------------------------------------------- deadlock: circular wait

    [Fact]
    public async Task Deadlock_ring_ordered_acquisition_leaves_every_stage_waiting_forever()
    {
        var (stages, resources, cts) = RingMachine(ResourceOrdering.AsListed);

        await Task.Delay(400);

        Assert.All(stages, s => Assert.Equal(StageState.Acquiring, s.State));
        Assert.All(resources, r => Assert.Equal(ResourceState.Busy, r.State));   // each holds one, waits for the next
        cts.Cancel();
        await Task.WhenAll(stages.Select(s => s.CurrentRun));
    }

    [Fact]
    public async Task Prevention_global_lock_order_lets_the_same_stages_all_complete()
    {
        var (stages, resources, cts) = RingMachine(ResourceOrdering.ByName);

        await Task.WhenAll(stages.Select(s => s.CurrentRun)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(stages, s => Assert.Equal(StageState.Idle, s.State));
        Assert.All(resources, r => Assert.Equal(ResourceState.Idle, r.State));
        cts.Cancel();
    }

    [Fact]
    public async Task The_exercise_map_as_listed_happens_to_be_cycle_free()
    {
        // stage_1 [A,B], stage_2 [C,B], stage_3 [A,C]: every pair is ordered consistently (A < C < B), so
        // listed-order acquisition cannot deadlock on this map. It is safe by luck, not by design.
        var (stages, resources, cts) = Machine(ResourceOrdering.AsListed,
            ("stage_1", ["R_A", "R_B"]), ("stage_2", ["R_C", "R_B"]), ("stage_3", ["R_A", "R_C"]));

        await Task.WhenAll(stages.Select(s => s.CurrentRun)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(resources, r => Assert.Equal(ResourceState.Idle, r.State));
        cts.Cancel();
    }

    // ---------------------------------------------------------------- atomicity violation

    [Fact]
    public async Task Atomicity_a_stage_never_acts_on_readings_from_two_different_moments()
    {
        // Temperature from tick 1 satisfies the rule together with the OLD pressure, and the new pressure from
        // tick 5 satisfies it together with the OLD temperature. Neither pair is a state the machine was ever in.
        var h = new StageHarness();
        var stage = h.Stage([new ResourceSimulator("A", acquisitionLatency: TimeSpan.Zero)], maxSkew: TimeSpan.FromMilliseconds(250));

        stage.OnReading(h.Temp(25, 1));
        stage.OnReading(h.Pres(40, 1));   // consistent and true: fires (this is the control)
        await stage.CurrentRun;
        Assert.Equal(1, h.StatesSoFar().Count(s => s == StageState.Running));

        stage.OnReading(h.Temp(25, 6));   // 500 ms newer than the pressure reading it would be paired with
        Assert.Equal(StageState.Idle, stage.State);
        Assert.Equal(1, h.StatesSoFar().Count(s => s == StageState.Running));
    }

    [Fact]
    public void Atomicity_the_stage_set_is_swapped_whole_so_a_tick_never_sees_a_half_update()
    {
        var active = new ActiveStages();
        var torn = 0;
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

        for (var i = 0; i < 100_000; i++)
        {
            var snapshot = active.Current;
            var count = snapshot.Count;
            if (count != snapshot.Count)   // a mutable set being edited underneath us would change size mid-read
            {
                torn++;
            }
        }

        stop.Cancel();
        writer.Wait();
        Assert.Equal(0, torn);
    }

    // ---------------------------------------------------------------- order violation

    [Fact]
    public void Order_a_stage_does_not_act_before_its_sensors_have_ever_measured()
    {
        // The constructor value is a real number with a real timestamp, but nothing measured it.
        var h = new StageHarness();
        var stage = h.Stage([new ResourceSimulator("A", acquisitionLatency: TimeSpan.Zero)]);
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
        var b = new FakeResource("B", journal) { BlockAcquire = new TaskCompletionSource() };
        var stage = new StageManager("s", [a, b], h.Registry, StageHarness.Rule1, ct => Task.CompletedTask,
            state => { lock (journal) journal.Add($"state:{state}"); }, NullLogger<StageManager>.Instance);

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

    private static (IReadOnlyList<StageManager> Stages, IReadOnlyList<ResourceSimulator> Resources, CancellationTokenSource Cts) RingMachine(ResourceOrdering ordering) =>
        Machine(ordering, ("stage_1", ["R_A", "R_B"]), ("stage_2", ["R_B", "R_C"]), ("stage_3", ["R_C", "R_A"]));

    /// <summary>
    /// Three stages with real (slow) resources, fed the same readings so all fire at once. Uses the same
    /// ordering rule as <see cref="MachineController"/> but drives the stages by hand for determinism.
    /// </summary>
    private static (IReadOnlyList<StageManager> Stages, IReadOnlyList<ResourceSimulator> Resources, CancellationTokenSource Cts) Machine(
        ResourceOrdering ordering, params (string Name, string[] Resources)[] definitions)
    {
        var h = new StageHarness();
        var resources = new[] { "R_A", "R_B", "R_C" }.Select(n => new ResourceSimulator(n, acquisitionLatency: Latency)).ToList();
        var byName = resources.ToDictionary(r => r.Name, r => (IResource)r);
        var cts = new CancellationTokenSource();

        var stages = definitions.Select(d =>
        {
            IEnumerable<string> names = ordering == ResourceOrdering.ByName ? d.Resources.Order(StringComparer.Ordinal) : d.Resources;
            return new StageManager(d.Name, names.Select(n => byName[n]).ToList(), h.Registry, _ => true,
                ct => Task.Delay(10, ct), _ => { }, NullLogger<StageManager>.Instance);
        }).ToList();

        foreach (var stage in stages)
        {
            h.Feed(stage, 25, 40, 1, cts.Token);
        }

        return (stages, resources, cts);
    }
}
