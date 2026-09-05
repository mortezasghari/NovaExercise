using Simulator;

namespace NovaTests;

public class ActiveStagesTests
{
    [Fact]
    public void Starts_empty()
    {
        Assert.Empty(new ActiveStages().Current);
    }

    [Fact]
    public void Set_and_clear()
    {
        var a = new ActiveStages();

        a.Set(Stage.Stage_2, true);
        Assert.Equal(new HashSet<Stage> { Stage.Stage_2 }, a.Current);

        a.Set(Stage.Stage_2, false);
        Assert.Empty(a.Current);
    }

    [Fact]
    public void Clearing_a_stage_that_is_not_active_is_harmless()
    {
        var a = new ActiveStages();

        a.Set(Stage.Stage_1, false);

        Assert.Empty(a.Current);
    }

    [Fact]
    public void A_snapshot_never_changes_after_it_was_taken()
    {
        var a = new ActiveStages();
        a.Set(Stage.Stage_1, true);

        var snapshot = a.Current;
        a.Set(Stage.Stage_2, true);
        a.Set(Stage.Stage_1, false);

        Assert.Equal(new HashSet<Stage> { Stage.Stage_1 }, snapshot);   // atomic swap: readers are never torn
        Assert.Equal(new HashSet<Stage> { Stage.Stage_2 }, a.Current);
    }

    [Fact]
    public void Concurrent_updates_are_all_applied()
    {
        var a = new ActiveStages();

        Parallel.For(0, 1000, i =>
        {
            var stage = (Stage)(i % 3);
            a.Set(stage, true);
            a.Set(stage, false);
            a.Set(stage, true);
        });

        Assert.Equal(new HashSet<Stage> { Stage.Stage_1, Stage.Stage_2, Stage.Stage_3 }, a.Current);
    }
}

public class ClockServiceTests
{
    [Fact]
    public void Rejects_a_non_positive_period()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClockService([], new ActiveStages(), TimeSpan.Zero));
    }

    [Fact]
    public void Every_tickable_gets_the_same_timestamp_and_the_same_stage_snapshot()
    {
        var active = new ActiveStages();
        var a = new RecordingTickable();
        var b = new RecordingTickable();
        var clock = new ClockService([a, b], active);

        active.Set(Stage.Stage_3, true);
        clock.Tick();

        Assert.Single(a.Ticks);
        Assert.Single(b.Ticks);
        Assert.Equal(a.Ticks[0].Timestamp, b.Ticks[0].Timestamp);
        Assert.Same(a.Ticks[0].Active, b.Ticks[0].Active);
        Assert.Equal(new HashSet<Stage> { Stage.Stage_3 }, a.Ticks[0].Active);
    }

    [Fact]
    public void A_tick_sees_the_stage_set_as_it_was_when_the_tick_started()
    {
        var active = new ActiveStages();
        var clock = new ClockService([new SetDuringTick(active)], active);

        clock.Tick();

        // The tickable mutated the set mid-tick, but the snapshot it received was the one taken at tick start.
        Assert.Equal(new HashSet<Stage> { Stage.Stage_1 }, active.Current);
    }

    [Fact]
    public void Tick_count_increments()
    {
        var clock = new ClockService([], new ActiveStages());

        clock.Tick();
        clock.Tick();

        Assert.Equal(2, clock.TickCount);
    }

    [Fact]
    public async Task Runs_on_its_period_and_stops_on_cancellation()
    {
        var tickable = new RecordingTickable();
        var clock = new ClockService([tickable], new ActiveStages(), TimeSpan.FromMilliseconds(10));
        using var cts = new CancellationTokenSource();

        var run = clock.RunAsync(cts.Token);
        await Wait.Until(() => clock.TickCount >= 5);
        cts.Cancel();
        await run;

        var countAtStop = clock.TickCount;
        await Task.Delay(50);
        Assert.Equal(countAtStop, clock.TickCount);   // nothing ticks after stop
    }

    [Fact]
    public async Task Independent_clocks_drive_independent_tickables()
    {
        var a = new RecordingTickable();
        var b = new RecordingTickable();
        var fast = new ClockService([a], new ActiveStages(), TimeSpan.FromMilliseconds(5));
        var slow = new ClockService([b], new ActiveStages(), TimeSpan.FromMilliseconds(40));
        using var cts = new CancellationTokenSource();

        var runs = Task.WhenAll(fast.RunAsync(cts.Token), slow.RunAsync(cts.Token));
        await Wait.Until(() => slow.TickCount >= 2);
        cts.Cancel();
        await runs;

        Assert.True(fast.TickCount > slow.TickCount);
    }

    private sealed class SetDuringTick(ActiveStages active) : ITickable
    {
        public void Tick(DateTime timestamp, IReadOnlySet<Stage> activeStages)
        {
            Assert.Empty(activeStages);
            active.Set(Stage.Stage_1, true);
            Assert.Empty(activeStages);
        }
    }
}
