using Controller;
using Resources;
using Sensor;
using Simulator;

namespace NovaTests;

public class MachineControllerTests
{
    private static readonly IReadOnlySet<Stage> None = new HashSet<Stage>();
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static StageDefinition Definition(string name, params string[] resources) =>
        new(name, resources, _ => true, ct => Task.Delay(10, ct));

    [Fact]
    public void Unknown_resource_name_fails_fast_at_construction()
    {
        var registry = new SensorRegistry();
        IReadOnlyList<IResource> resources = [new FakeResource("R_A")];

        var e = Assert.Throws<ArgumentException>(() =>
            new MachineController(registry, resources, [Definition("s", "R_A", "R_X")]));

        Assert.Contains("R_X", e.Message);
    }

    [Fact]
    public void Builds_one_stage_per_definition_all_idle()
    {
        var controller = new MachineController(new SensorRegistry(), [new FakeResource("R_A")],
            [Definition("s1", "R_A"), Definition("s2", "R_A")]);

        Assert.Equal(["s1", "s2"], controller.Stages.Select(s => s.Name));
        Assert.All(controller.Snapshot().Values, state => Assert.Equal(StageState.Idle, state));
    }

    [Fact]
    public async Task ByName_ordering_acquires_in_global_order_whatever_the_stage_lists()
    {
        var journal = await RunOneStageAndJournal(ResourceOrdering.ByName, "R_C", "R_A", "R_B");

        Assert.Equal(["acquire:R_A", "hold:R_A", "acquire:R_B", "hold:R_B", "acquire:R_C", "hold:R_C"], journal.Take(6));
    }

    [Fact]
    public async Task AsListed_ordering_acquires_exactly_as_the_stage_lists()
    {
        var journal = await RunOneStageAndJournal(ResourceOrdering.AsListed, "R_C", "R_A", "R_B");

        Assert.Equal(["acquire:R_C", "hold:R_C", "acquire:R_A", "hold:R_A", "acquire:R_B", "hold:R_B"], journal.Take(6));
    }

    [Fact]
    public async Task Stage_state_changes_are_forwarded_with_the_stage_name()
    {
        var (registry, temperature, pressure) = Sensors();
        var events = new List<(string, StageState)>();
        var controller = new MachineController(registry, [new FakeResource("R_A")], [Definition("s1", "R_A")],
            stageStateChanged: (name, state) => { lock (events) events.Add((name, state)); });
        using var cts = new CancellationTokenSource();
        var run = controller.RunAsync(cts.Token);

        await Wait.Until(() =>
        {
            temperature.Tick(T0, None);
            pressure.Tick(T0, None);
            lock (events) return events.Contains(("s1", StageState.Idle));
        });

        cts.Cancel();
        await run;
        lock (events)
        {
            Assert.Equal(("s1", StageState.Acquiring), events[0]);
            Assert.Equal(("s1", StageState.Running), events[1]);
        }
    }

    [Fact]
    public async Task Stages_with_disjoint_resources_run_at_the_same_time()
    {
        var (registry, temperature, pressure) = Sensors();
        var started = 0;
        var gate = new TaskCompletionSource();
        Func<CancellationToken, Task> work = async ct =>
        {
            Interlocked.Increment(ref started);
            await gate.Task.WaitAsync(ct);
        };
        var controller = new MachineController(registry,
            [new FakeResource("R_A"), new FakeResource("R_B"), new FakeResource("R_C")],
            [new("s1", ["R_A"], _ => true, work), new("s2", ["R_B"], _ => true, work), new("s3", ["R_C"], _ => true, work)]);
        using var cts = new CancellationTokenSource();
        var run = controller.RunAsync(cts.Token);

        // Simultaneous progress is the claim, not distinct threads: the pool may multiplex three stages onto fewer.
        await Wait.Until(() =>
        {
            temperature.Tick(T0, None);
            pressure.Tick(T0, None);
            return Volatile.Read(ref started) == 3;
        });
        Assert.All(controller.Snapshot().Values, state => Assert.Equal(StageState.Running, state));

        gate.SetResult();
        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task A_second_RunAsync_on_the_same_machine_is_rejected()
    {
        var (registry, _, _) = Sensors();
        var controller = new MachineController(registry, [new FakeResource("R_A")], [Definition("s", "R_A")]);
        using var cts = new CancellationTokenSource();
        var first = controller.RunAsync(cts.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RunAsync(cts.Token));

        cts.Cancel();
        await first;
    }

    [Fact]
    public async Task A_crashing_stage_does_not_stop_the_others()
    {
        var (registry, temperature, pressure) = Sensors();
        var healthyRan = false;
        var controller = new MachineController(registry, [new FakeResource("R_A"), new FakeResource("R_B")],
        [
            new("bad", ["R_A"], v => v[SensorType.Temperature] is > 0 and < 1000 && Crash(), ct => Task.CompletedTask),
            new("good", ["R_B"], _ => true, ct => { healthyRan = true; return Task.CompletedTask; }),
        ]);
        using var cts = new CancellationTokenSource();
        var run = controller.RunAsync(cts.Token);

        await Wait.Until(() =>
        {
            temperature.Tick(T0, None);
            pressure.Tick(T0, None);
            return Volatile.Read(ref healthyRan);
        });

        cts.Cancel();
        await run;   // completes even though one stage crashed
    }

    [Fact]
    public async Task Shutdown_leaves_every_stage_idle_and_every_resource_free()
    {
        var (registry, temperature, pressure) = Sensors();
        var resources = new[] { "R_A", "R_B", "R_C" }.Select(n => new ResourceSimulator(n, acquisitionLatency: TimeSpan.Zero)).ToList();
        var controller = new MachineController(registry, resources, ExerciseMachine.Stages(_ => ct => Task.Delay(Timeout.InfiniteTimeSpan, ct)));
        using var cts = new CancellationTokenSource();
        var run = controller.RunAsync(cts.Token);

        await Wait.Until(() =>
        {
            temperature.Tick(T0, None);
            pressure.Tick(T0, None);
            return controller.Snapshot().Values.Contains(StageState.Running);
        });

        cts.Cancel();
        await run;

        Assert.All(controller.Snapshot().Values, state => Assert.Equal(StageState.Idle, state));
        Assert.All(resources, r => Assert.Equal(ResourceState.Idle, r.State));
    }

    private static bool Crash() => throw new InvalidOperationException("bad rule at runtime");

    private static (SensorRegistry Registry, TemperatureSensorSimulator Temperature, PressureSensorSimulator Pressure) Sensors()
    {
        var registry = new SensorRegistry();
        var temperature = new TemperatureSensorSimulator(Guid.NewGuid(), 25.0);
        var pressure = new PressureSensorSimulator(Guid.NewGuid(), 40.0);
        registry.Register(temperature);
        registry.Register(pressure);
        return (registry, temperature, pressure);
    }

    private static async Task<List<string>> RunOneStageAndJournal(ResourceOrdering ordering, params string[] listed)
    {
        var (registry, temperature, pressure) = Sensors();
        var journal = new List<string>();
        var resources = new[] { "R_A", "R_B", "R_C" }.Select(n => (IResource)new FakeResource(n, journal)).ToList();
        var done = new TaskCompletionSource();
        var controller = new MachineController(registry, resources, [Definition("s", listed)], ordering,
            stageStateChanged: (_, state) => { if (state == StageState.Idle) done.TrySetResult(); });
        using var cts = new CancellationTokenSource();
        var run = controller.RunAsync(cts.Token);

        await Wait.Until(() =>
        {
            temperature.Tick(T0, None);
            pressure.Tick(T0, None);
            return done.Task.IsCompleted;
        });

        cts.Cancel();
        await run;
        lock (journal)
        {
            return journal.ToList();
        }
    }
}
