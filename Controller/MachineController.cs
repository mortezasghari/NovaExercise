using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Resources;
using Sensor;

namespace Controller;

/// <summary>What a stage needs and when it runs. Resources are named; the controller resolves them.</summary>
public sealed record StageDefinition(
    string Name,
    IReadOnlyList<string> Resources,
    Func<IReadOnlyDictionary<SensorType, double>, bool> Rule,
    Func<CancellationToken, Task> Work);

/// <summary>
/// The locking protocol of the machine. Stages acquire resources one after another, so the order decides
/// whether a circular wait can form between stages running in parallel.
/// </summary>
public enum ResourceOrdering : byte
{
    /// <summary>Acquire in the order each stage lists its resources. Safe only if the listed orders never form a cycle.</summary>
    AsListed,

    /// <summary>Acquire in one global order (by resource name) whatever the stage lists. Breaks circular wait, so no deadlock is possible.</summary>
    ByName,
}

/// <summary>
/// Wires and runs the machine: one <see cref="StageManager"/> per stage definition, all listening to the same
/// sensor registry and contending for the same resources, each running as its own parallel task. The controller
/// makes no decisions of its own; it resolves resource names, applies the resource ordering, forwards stage
/// state changes to whoever wants them, and owns start-up and shutdown.
/// </summary>
public sealed class MachineController
{
    private readonly ILogger _logger;
    private readonly List<StageManager> _stages = [];

    public MachineController(
        SensorRegistry sensors,
        IReadOnlyList<IResource> resources,
        IReadOnlyList<StageDefinition> stages,
        ResourceOrdering ordering = ResourceOrdering.ByName,
        Action<string, StageState>? stageStateChanged = null,
        ILoggerFactory? loggerFactory = null)
    {
        var factory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = factory.CreateLogger<MachineController>();
        Ordering = ordering;

        var byName = resources.ToDictionary(r => r.Name);

        foreach (var definition in stages)
        {
            IEnumerable<string> names = ordering == ResourceOrdering.ByName
                ? definition.Resources.Order(StringComparer.Ordinal)
                : definition.Resources;

            var resolved = names.Select(name => byName.TryGetValue(name, out var resource)
                    ? resource
                    : throw new ArgumentException($"Stage '{definition.Name}' needs unknown resource '{name}'.", nameof(stages)))
                .ToList();

            _stages.Add(new StageManager(
                definition.Name,
                resolved,
                sensors,
                definition.Rule,
                definition.Work,
                state => stageStateChanged?.Invoke(definition.Name, state),
                factory.CreateLogger<StageManager>()));
        }
    }

    public ResourceOrdering Ordering { get; }

    public IReadOnlyList<IStageManager> Stages => _stages;

    /// <summary>Current state of every stage, by name.</summary>
    public IReadOnlyDictionary<string, StageState> Snapshot() =>
        _stages.ToDictionary(s => s.Name, s => s.State);

    /// <summary>
    /// Runs every stage in parallel until cancelled. Each stage is started on its own thread-pool task so no
    /// stage's loop shares a call chain with another's; a stage that crashes is logged and the rest keep running.
    /// Completes when every stage has stopped.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Machine starting {StageCount} stages with {Ordering} resource ordering: {Stages}",
            _stages.Count, Ordering, _stages.Select(s => s.Name));

        var runs = _stages.Select(stage => Task.Run(() => RunStageAsync(stage, cancellationToken), CancellationToken.None)).ToArray();
        await Task.WhenAll(runs);

        _logger.LogInformation("Machine stopped");
    }

    private async Task RunStageAsync(StageManager stage, CancellationToken cancellationToken)
    {
        try
        {
            await stage.RunAsync(cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "{Stage} crashed; the other stages keep running", stage.Name);
        }
    }
}
