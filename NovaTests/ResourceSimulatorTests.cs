using Resources;
using Simulator;

namespace NovaTests;

public class ResourceSimulatorTests
{
    private static readonly IReadOnlySet<Stage> NoStages = new HashSet<Stage>();

    private static ResourceSimulator Resource(int recoveryTicks = 50, double failureProbability = 0.0, Random? random = null) =>
        new("R", failureProbability, recoveryTicks, TimeSpan.Zero, random);

    [Fact]
    public void Starts_idle_and_becomes_busy_on_acquire()
    {
        var r = Resource();

        Assert.Equal(ResourceState.Idle, r.State);
        Assert.True(r.TryAcquire());
        Assert.Equal(ResourceState.Busy, r.State);
    }

    [Fact]
    public void Second_try_acquire_fails_while_held()
    {
        var r = Resource();
        r.TryAcquire();

        Assert.False(r.TryAcquire());
    }

    [Fact]
    public void Release_without_holding_is_an_order_violation_and_throws()
    {
        var r = Resource();

        Assert.Throws<InvalidOperationException>(r.Release);
    }

    [Fact]
    public void Release_twice_throws_on_the_second()
    {
        var r = Resource();
        r.TryAcquire();
        r.Release();

        Assert.Throws<InvalidOperationException>(r.Release);
    }

    [Fact]
    public async Task Async_acquire_parks_until_the_holder_releases()
    {
        var r = Resource();
        r.TryAcquire();

        var waiter = r.AcquireAsync();
        Assert.False(await Wait.Completes(waiter, 100));

        r.Release();
        await waiter;
        Assert.Equal(ResourceState.Busy, r.State);
    }

    [Fact]
    public async Task Cancelling_a_parked_waiter_leaves_the_holder_untouched()
    {
        var r = Resource();
        r.TryAcquire();
        using var cts = new CancellationTokenSource();

        var waiter = r.AcquireAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.Equal(ResourceState.Busy, r.State);
        r.Release();
        Assert.True(r.TryAcquire());
    }

    [Fact]
    public async Task Cancelling_during_the_handshake_does_not_strand_the_slot()
    {
        var r = new ResourceSimulator("R", acquisitionLatency: TimeSpan.FromMilliseconds(500));
        using var cts = new CancellationTokenSource(20);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.AcquireAsync(cts.Token));

        Assert.Equal(ResourceState.Idle, r.State);
        Assert.True(r.TryAcquire());
    }

    [Fact]
    public void Fail_while_held_reports_error_and_holder_keeps_the_slot()
    {
        var r = Resource();
        r.TryAcquire();

        r.Fail();

        Assert.Equal(ResourceState.Error, r.State);
        Assert.False(r.TryAcquire());
    }

    [Fact]
    public void Releasing_a_failed_resource_keeps_it_in_error()
    {
        var r = Resource();
        r.TryAcquire();
        r.Fail();

        r.Release();

        Assert.Equal(ResourceState.Error, r.State);
        Assert.False(r.TryAcquire());
    }

    [Fact]
    public async Task Acquire_on_a_failed_resource_fails_immediately_instead_of_waiting()
    {
        var r = Resource();
        r.Fail();

        var e = await Assert.ThrowsAsync<ResourceErrorException>(() => r.AcquireAsync());
        Assert.Equal("R", e.ResourceName);
    }

    [Fact]
    public async Task Waiter_is_refused_when_the_resource_fails_while_it_waits()
    {
        var r = Resource();
        r.TryAcquire();
        var waiter = r.AcquireAsync();

        r.Fail();
        r.Release();

        await Assert.ThrowsAsync<ResourceErrorException>(() => waiter);
        Assert.Equal(ResourceState.Error, r.State);

        // The refused waiter gave the slot back: after recovery anyone can take it.
        r.Recover();
        Assert.True(r.TryAcquire());
    }

    [Fact]
    public void Recover_while_held_returns_to_busy_not_idle()
    {
        var r = Resource();
        r.TryAcquire();
        r.Fail();

        r.Recover();

        Assert.Equal(ResourceState.Busy, r.State);
    }

    [Fact]
    public void Recovers_after_exactly_the_configured_number_of_ticks()
    {
        var r = Resource(recoveryTicks: 3);
        r.Fail();

        r.Tick(DateTime.UtcNow, NoStages);
        r.Tick(DateTime.UtcNow, NoStages);
        Assert.Equal(ResourceState.Error, r.State);

        r.Tick(DateTime.UtcNow, NoStages);
        Assert.Equal(ResourceState.Idle, r.State);
    }

    [Fact]
    public void Never_fails_on_its_own_when_probability_is_zero()
    {
        var r = Resource(failureProbability: 0.0);

        for (var i = 0; i < 10_000; i++)
        {
            r.Tick(DateTime.UtcNow, NoStages);
        }

        Assert.Equal(ResourceState.Idle, r.State);
    }

    [Fact]
    public void Fails_on_the_first_tick_when_probability_is_one()
    {
        var r = Resource(failureProbability: 1.0, recoveryTicks: 5);

        r.Tick(DateTime.UtcNow, NoStages);

        Assert.Equal(ResourceState.Error, r.State);
    }

    [Fact]
    public void Seeded_random_makes_faults_reproducible()
    {
        var a = Resource(failureProbability: 0.1, recoveryTicks: 2, random: new Random(42));
        var b = Resource(failureProbability: 0.1, recoveryTicks: 2, random: new Random(42));

        var statesA = new List<ResourceState>();
        var statesB = new List<ResourceState>();
        for (var i = 0; i < 200; i++)
        {
            a.Tick(DateTime.UtcNow, NoStages);
            b.Tick(DateTime.UtcNow, NoStages);
            statesA.Add(a.State);
            statesB.Add(b.State);
        }

        Assert.Equal(statesA, statesB);
        Assert.Contains(ResourceState.Error, statesA);
    }

    [Fact]
    public async Task Many_concurrent_acquirers_never_hold_it_at_the_same_time()
    {
        var r = Resource();
        var concurrentHolders = 0;
        var maxConcurrentHolders = 0;

        var tasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            await r.AcquireAsync();
            var now = Interlocked.Increment(ref concurrentHolders);
            InterlockedMax(ref maxConcurrentHolders, now);
            await Task.Yield();
            Interlocked.Decrement(ref concurrentHolders);
            r.Release();
        });

        await Task.WhenAll(tasks);

        Assert.Equal(1, maxConcurrentHolders);
        Assert.Equal(ResourceState.Idle, r.State);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
