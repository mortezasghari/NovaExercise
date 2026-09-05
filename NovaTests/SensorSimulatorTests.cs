using Sensor;
using Simulator;

namespace NovaTests;

public class SensorSimulatorTests
{
    private static readonly IReadOnlySet<Stage> None = new HashSet<Stage>();
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Initial_reading_has_sequence_zero_meaning_never_measured()
    {
        var s = new TemperatureSensorSimulator(Guid.NewGuid(), 20.0);

        Assert.Equal(0, s.GetSnapshot().Sequence);
    }

    [Fact]
    public void Initial_value_is_clamped_to_the_physical_range()
    {
        Assert.Equal(100.0, new TemperatureSensorSimulator(Guid.NewGuid(), 500.0).GetSnapshot().Value);
        Assert.Equal(0.0, new PressureSensorSimulator(Guid.NewGuid(), -5.0).GetSnapshot().Value);
    }

    [Fact]
    public void Each_tick_increments_the_sequence_and_stamps_the_clock_time()
    {
        var s = new TemperatureSensorSimulator(Guid.NewGuid(), 20.0);

        s.Tick(T0, None);
        s.Tick(T0.AddMilliseconds(100), None);

        var reading = s.GetSnapshot();
        Assert.Equal(2, reading.Sequence);
        Assert.Equal(T0.AddMilliseconds(100), reading.Timestamp);
    }

    [Fact]
    public void Physics_decays_towards_ambient_when_nothing_is_active()
    {
        var s = new TemperatureSensorSimulator(Guid.NewGuid(), 20.0);

        Assert.Equal(98.4, s.ComputeNext(100.0, None, noise: 0.0), precision: 10);   // 100 + (20-100)*0.02
        Assert.Equal(20.0, s.ComputeNext(20.0, None, noise: 0.0), precision: 10);    // already at ambient
    }

    [Fact]
    public void Physics_adds_the_delta_of_every_active_stage()
    {
        var s = new PressureSensorSimulator(Guid.NewGuid(), 30.0);
        var active = new HashSet<Stage> { Stage.Stage_1, Stage.Stage_2 };

        Assert.Equal(36.0, s.ComputeNext(30.0, active, noise: 0.0), precision: 10);  // +2.0 +4.0 at ambient
    }

    [Fact]
    public void Physics_clamps_at_both_limits()
    {
        var s = new TemperatureSensorSimulator(Guid.NewGuid(), 20.0);

        Assert.Equal(0.0, s.ComputeNext(0.5, new HashSet<Stage> { Stage.Stage_3 }, noise: 0.0));    // 0.5 + 0.39 - 2.0 < 0
        Assert.Equal(100.0, s.ComputeNext(99.9, new HashSet<Stage> { Stage.Stage_1 }, noise: 5.0)); // 99.9 - 1.6 + 1.5 + 5 > 100
    }

    [Fact]
    public void Physics_applies_noise_verbatim()
    {
        var s = new TemperatureSensorSimulator(Guid.NewGuid(), 20.0);

        Assert.Equal(20.2, s.ComputeNext(20.0, None, noise: 0.2), precision: 10);
    }

    [Fact]
    public void Seeded_random_makes_ticks_reproducible()
    {
        var a = new TemperatureSensorSimulator(Guid.NewGuid(), 20.0, new Random(7));
        var b = new TemperatureSensorSimulator(Guid.NewGuid(), 20.0, new Random(7));
        var active = new HashSet<Stage> { Stage.Stage_1 };

        for (var i = 0; i < 50; i++)
        {
            a.Tick(T0, active);
            b.Tick(T0, active);
        }

        Assert.Equal(a.GetSnapshot().Value, b.GetSnapshot().Value);
    }

    [Fact]
    public async Task Consumer_receives_readings_published_after_it_subscribed()
    {
        var s = new TemperatureSensorSimulator(Guid.NewGuid(), 20.0);
        using var cts = new CancellationTokenSource();
        await using var readings = s.GetAsyncEnumerable(cts.Token).GetAsyncEnumerator();

        var first = readings.MoveNextAsync();
        s.Tick(T0, None);

        Assert.True(await first);
        Assert.Equal(1, readings.Current.Sequence);
    }

    [Fact]
    public async Task Slow_consumer_sees_only_the_latest_reading_and_never_blocks_the_sensor()
    {
        var s = new TemperatureSensorSimulator(Guid.NewGuid(), 20.0);
        using var cts = new CancellationTokenSource();
        await using var readings = s.GetAsyncEnumerable(cts.Token).GetAsyncEnumerator();

        var first = readings.MoveNextAsync();
        s.Tick(T0, None);
        await first;

        // Three ticks while the consumer is not reading: the one-slot buffer keeps the newest.
        s.Tick(T0, None);
        s.Tick(T0, None);
        s.Tick(T0, None);

        Assert.True(await readings.MoveNextAsync());
        Assert.Equal(4, readings.Current.Sequence);

        var nothingMore = readings.MoveNextAsync().AsTask();
        Assert.False(await Wait.Completes(nothingMore, 50));

        // An async enumerator cannot be disposed while a read is pending: end the read first.
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => nothingMore);
    }

    [Fact]
    public async Task Two_consumers_are_independent()
    {
        var s = new TemperatureSensorSimulator(Guid.NewGuid(), 20.0);
        using var cts = new CancellationTokenSource();
        await using var fast = s.GetAsyncEnumerable(cts.Token).GetAsyncEnumerator();
        await using var slow = s.GetAsyncEnumerable(cts.Token).GetAsyncEnumerator();

        var fastFirst = fast.MoveNextAsync();
        var slowFirst = slow.MoveNextAsync();
        s.Tick(T0, None);
        await fastFirst;
        await slowFirst;

        s.Tick(T0, None);
        s.Tick(T0, None);
        Assert.True(await fast.MoveNextAsync());
        Assert.Equal(3, fast.Current.Sequence);

        s.Tick(T0, None);
        Assert.True(await slow.MoveNextAsync());
        Assert.Equal(4, slow.Current.Sequence);   // its own buffer, its own latest
    }

    [Fact]
    public async Task Stream_ends_with_cancellation_and_the_sensor_keeps_ticking()
    {
        var s = new TemperatureSensorSimulator(Guid.NewGuid(), 20.0);
        using var cts = new CancellationTokenSource();
        var consumer = Task.Run(async () =>
        {
            await foreach (var _ in s.GetAsyncEnumerable(cts.Token))
            {
            }
        });

        s.Tick(T0, None);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer);
        s.Tick(T0, None);
        Assert.Equal(2, s.GetSnapshot().Sequence);
    }

    [Fact]
    public void Snapshot_is_readable_while_ticks_happen_on_another_thread()
    {
        var s = new PressureSensorSimulator(Guid.NewGuid(), 30.0);
        using var stop = new CancellationTokenSource();
        var ticker = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                s.Tick(T0, None);
            }
        });

        for (var i = 0; i < 10_000; i++)
        {
            var reading = s.GetSnapshot();
            Assert.InRange(reading.Value, 0.0, 200.0);   // never a torn or out-of-range value
        }

        stop.Cancel();
        ticker.Wait();
    }
}
