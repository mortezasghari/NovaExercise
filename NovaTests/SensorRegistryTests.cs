using Sensor;
using Simulator;

namespace NovaTests;

public class SensorRegistryTests
{
    private static readonly IReadOnlySet<Stage> None = new HashSet<Stage>();
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Registers_and_lists_sensors()
    {
        var registry = new SensorRegistry();
        var t = new TemperatureSensorSimulator(Guid.NewGuid(), 20.0);

        registry.Register(t);

        Assert.Single(registry.Sensors);
        Assert.Contains(t, registry.Sensors);
    }

    [Fact]
    public void Registering_the_same_sensor_twice_throws()
    {
        var registry = new SensorRegistry();
        var t = new TemperatureSensorSimulator(Guid.NewGuid(), 20.0);
        registry.Register(t);

        Assert.Throws<InvalidOperationException>(() => registry.Register(t));
    }

    [Fact]
    public void Covers_requires_a_reading_from_every_registered_sensor()
    {
        var registry = new SensorRegistry();
        var t = new TemperatureSensorSimulator(Guid.NewGuid(), 20.0);
        var p = new PressureSensorSimulator(Guid.NewGuid(), 30.0);
        registry.Register(t);
        registry.Register(p);

        var onlyTemperature = new Dictionary<Guid, SensorData> { [t.Id] = t.GetSnapshot() };
        var both = new Dictionary<Guid, SensorData> { [t.Id] = t.GetSnapshot(), [p.Id] = p.GetSnapshot() };
        var stranger = new Dictionary<Guid, SensorData> { [t.Id] = t.GetSnapshot(), [Guid.NewGuid()] = p.GetSnapshot() };

        Assert.False(registry.Covers(onlyTemperature));
        Assert.True(registry.Covers(both));
        Assert.False(registry.Covers(stranger));
    }

    [Fact]
    public void Freezing_rejects_further_registration()
    {
        var registry = new SensorRegistry();
        registry.Register(new TemperatureSensorSimulator(Guid.NewGuid(), 20.0));

        registry.Freeze();

        Assert.True(registry.IsFrozen);
        Assert.Throws<InvalidOperationException>(() => registry.Register(new PressureSensorSimulator(Guid.NewGuid(), 30.0)));
        Assert.Single(registry.Sensors);
    }

    [Fact]
    public void Covers_by_id_set_requires_every_registered_sensor()
    {
        var registry = new SensorRegistry();
        var t = new TemperatureSensorSimulator(Guid.NewGuid(), 20.0);
        var p = new PressureSensorSimulator(Guid.NewGuid(), 30.0);
        registry.Register(t);
        registry.Register(p);

        Assert.False(registry.Covers(new HashSet<Guid> { t.Id }));
        Assert.True(registry.Covers(new HashSet<Guid> { t.Id, p.Id }));
    }

    [Fact]
    public void Empty_registry_is_covered_by_anything()
    {
        Assert.True(new SensorRegistry().Covers(new Dictionary<Guid, SensorData>()));
    }

    [Fact]
    public async Task Merged_stream_carries_readings_from_every_sensor()
    {
        var registry = new SensorRegistry();
        var t = new TemperatureSensorSimulator(Guid.NewGuid(), 20.0);
        var p = new PressureSensorSimulator(Guid.NewGuid(), 30.0);
        registry.Register(t);
        registry.Register(p);
        using var cts = new CancellationTokenSource();
        var seen = new List<SensorData>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var reading in registry.ReadAllAsync(cts.Token))
            {
                lock (seen) seen.Add(reading);
            }
        });

        // Tick until both sensors have been seen: the consumer's subscriptions are established asynchronously.
        await Wait.Until(() =>
        {
            t.Tick(T0, None);
            p.Tick(T0, None);
            lock (seen) return seen.Any(r => r.SensorId == t.Id) && seen.Any(r => r.SensorId == p.Id);
        });

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer);
    }

    [Fact]
    public async Task Each_consumer_gets_its_own_independent_stream()
    {
        var registry = new SensorRegistry();
        var t = new TemperatureSensorSimulator(Guid.NewGuid(), 20.0);
        registry.Register(t);
        using var cts = new CancellationTokenSource();
        var countA = 0;
        var countB = 0;
        var a = Task.Run(async () => { await foreach (var _ in registry.ReadAllAsync(cts.Token)) Interlocked.Increment(ref countA); });
        var b = Task.Run(async () => { await foreach (var _ in registry.ReadAllAsync(cts.Token)) Interlocked.Increment(ref countB); });

        await Wait.Until(() =>
        {
            t.Tick(T0, None);
            return Volatile.Read(ref countA) >= 3 && Volatile.Read(ref countB) >= 3;
        });

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(a, b));
    }

    [Fact]
    public async Task Cancelling_the_consumer_stops_the_pumps()
    {
        var registry = new SensorRegistry();
        var t = new TemperatureSensorSimulator(Guid.NewGuid(), 20.0);
        registry.Register(t);
        using var cts = new CancellationTokenSource();
        var consumer = Task.Run(async () => { await foreach (var _ in registry.ReadAllAsync(cts.Token)) { } });

        await Wait.Until(() => { t.Tick(T0, None); return t.GetSnapshot().Sequence >= 3; });
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer);

        // Ticking after the pumps are gone must not throw or block.
        t.Tick(T0, None);
        Assert.True(t.GetSnapshot().Sequence >= 4);
    }
}
