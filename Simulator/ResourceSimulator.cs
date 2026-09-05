using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Resources;

namespace Simulator;

/// <summary>
/// In-memory fake of an exclusive resource with Idle, Busy and Error states.
/// Mutual exclusion is a one-slot semaphore, so waiting acquirers park asynchronously instead of spinning.
/// Driven by the clock: on every tick a healthy resource fails with <paramref name="failureProbabilityPerTick"/>,
/// and a failed one recovers on its own after <paramref name="recoveryTicks"/> ticks.
/// <see cref="Fail"/> and <see cref="Recover"/> inject the same transitions by hand for tests and demos.
/// </summary>
public sealed class ResourceSimulator(
    string name,
    double failureProbabilityPerTick = 0.0,
    int recoveryTicks = 50,
    Random? random = null,
    ILogger? logger = null) : IResource, ITickable
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Random _random = random ?? Random.Shared;

    // Guards _held, _faulted and _ticksUntilRecovery. The semaphore handles waiting; the lock keeps the flags consistent.
    private readonly Lock _lock = new();
    private bool _held;
    private bool _faulted;
    private int _ticksUntilRecovery;

    public string Name => name;

    public ResourceState State
    {
        get
        {
            lock (_lock)
            {
                return _faulted ? ResourceState.Error
                     : _held    ? ResourceState.Busy
                     : ResourceState.Idle;
            }
        }
    }

    public bool TryAcquire()
    {
        lock (_lock)
        {
            // The semaphore may already be taken by an AcquireAsync waiter that has not yet set _held,
            // so ask the semaphore rather than trusting the flag alone.
            if (_faulted || _held || !_gate.Wait(0))
            {
                return false;
            }

            _held = true;
            _logger.LogTrace("{Resource} acquired", name);
            return true;
        }
    }

    public async Task AcquireAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfFaulted();

        await _gate.WaitAsync(cancellationToken);
        await Task.Delay(100, cancellationToken);
        lock (_lock)
        {
            // The resource may have failed while we were waiting. Give the slot back and report it.
            if (_faulted)
            {
                _gate.Release();
                throw new ResourceErrorException(name);
            }

            _held = true;
            _logger.LogTrace("{Resource} acquired after waiting", name);
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            if (!_held)
            {
                throw new InvalidOperationException($"Resource '{name}' is not held.");
            }

            _held = false;
            _gate.Release();
            _logger.LogTrace("{Resource} released{Faulted}", name, _faulted ? " (still in Error)" : "");
        }
    }

    /// <summary>Random fault injection and timed recovery. Stage activity does not influence resource health.</summary>
    public void Tick(DateTime timestamp, IReadOnlySet<Stage> activeStages)
    {
        lock (_lock)
        {
            if (_faulted)
            {
                if (--_ticksUntilRecovery <= 0)
                {
                    _faulted = false;
                    _logger.LogInformation("{Resource} recovered, now {State}", name, _held ? ResourceState.Busy : ResourceState.Idle);
                }
            }
            else if (_random.NextDouble() < failureProbabilityPerTick)
            {
                _faulted = true;
                _ticksUntilRecovery = recoveryTicks;
                _logger.LogWarning("{Resource} failed at random{Held}, recovery in {RecoveryTicks} ticks", name, _held ? " while held" : "", recoveryTicks);
            }
        }
    }

    /// <summary>Puts the resource into Error. A current holder keeps the slot until it releases.</summary>
    public void Fail()
    {
        lock (_lock)
        {
            _faulted = true;
            _ticksUntilRecovery = recoveryTicks;
            _logger.LogWarning("{Resource} failed (injected){Held}", name, _held ? " while held" : "");
        }
    }

    /// <summary>Clears the Error state. The resource becomes Idle, or Busy if still held.</summary>
    public void Recover()
    {
        lock (_lock)
        {
            _faulted = false;
            _logger.LogInformation("{Resource} recovered (injected)", name);
        }
    }

    private void ThrowIfFaulted()
    {
        lock (_lock)
        {
            if (_faulted)
            {
                throw new ResourceErrorException(name);
            }
        }
    }
}
