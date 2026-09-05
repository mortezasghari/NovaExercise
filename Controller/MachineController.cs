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
/// makes no decisions of its own; it validates the configuration, resolves resource names, applies the resource
/// ordering, forwards stage state changes to whoever wants them, and owns start-up and shutdown.
/// </summary>
public sealed class MachineController
{
    private readonly ILogger _logger;
    private readonly List<StageManager> _stages = [];
    private int _started;

    public MachineController(
        SensorRegistry sensors,
        IReadOnlyList<IResource> resources,
        IReadOnlyList<StageDefinition> stages,
        ResourceOrdering ordering = ResourceOrdering.ByName,
        Action<string, StageState>? stageStateChanged = null,
        ILoggerFactory? loggerFactory = null,
        StageOptions? stageOptions = null)
    {
        var factory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = factory.CreateLogger<MachineController>();
        Ordering = ordering;

        Validate(sensors, resources, stages);
        sensors.Freeze();

        var byName = resources.ToDictionary(r => r.Name);

        foreach (var definition in stages)
        {
            IEnumerable<string> names = ordering == ResourceOrdering.ByName
                ? definition.Resources.Order(StringComparer.Ordinal)
                : definition.Resources;

            _stages.Add(new StageManager(
                definition.Name,
                names.Select(name => byName[name]).ToList(),
                sensors,
                definition.Rule,
                definition.Work,
                state => stageStateChanged?.Invoke(definition.Name, state),
                factory.CreateLogger<StageManager>(),
                stageOptions));
        }
    }

    public ResourceOrdering Ordering { get; }

    public IReadOnlyList<IStageManager> Stages => _stages;

    /// <summary>Current state of every stage, by name. A collection of per-stage reads, not one atomic picture.</summary>
    public IReadOnlyDictionary<string, StageState> Snapshot() =>
        _stages.ToDictionary(s => s.Name, s => s.State);

    /// <summary>Names of the stages whose data alarm is raised.</summary>
    public IReadOnlyList<string> DataAlarms() =>
        _stages.Where(s => s.DataAlarm).Select(s => s.Name).ToList();

    /// <summary>
    /// Runs every stage in parallel until cancelled. Each stage is started on its own thread-pool task so no
    /// stage's loop shares a call chain with another's; a stage that crashes is logged and the rest keep running.
    /// Completes when every stage has stopped. One call per instance.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            throw new InvalidOperationException("The machine is already running.");
        }

        _logger.LogInformation("Machine starting {StageCount} stages with {Ordering} resource ordering: {Stages}",
            _stages.Count, Ordering, _stages.Select(s => s.Name));

        var runs = _stages.Select(stage => Task.Run(() => RunStageAsync(stage, cancellationToken), CancellationToken.None)).ToArray();
        await Task.WhenAll(runs);

        _logger.LogInformation("Machine stopped");
    }

    /// <summary>
    /// Fails fast on configuration that would misbehave at runtime: unknown or duplicated resources (a stage
    /// waiting on a resource it already holds deadlocks with itself, whatever the ordering), duplicated stage
    /// names, and rules that read a sensor type no registered sensor provides.
    /// </summary>
    private static void Validate(SensorRegistry sensors, IReadOnlyList<IResource> resources, IReadOnlyList<StageDefinition> stages)
    {
        var known = resources.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        if (known.Count != resources.Count)
        {
            throw new ArgumentException("Resource names must be unique.", nameof(resources));
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var probes = RuleProbes(sensors.Sensors.Select(s => s.GetSnapshot().Type).Distinct().ToList());

        foreach (var stage in stages)
        {
            if (!names.Add(stage.Name))
            {
                throw new ArgumentException($"Stage name '{stage.Name}' is used more than once.", nameof(stages));
            }

            if (stage.Resources.Count == 0)
            {
                throw new ArgumentException($"Stage '{stage.Name}' needs at least one resource.", nameof(stages));
            }

            var unknown = stage.Resources.FirstOrDefault(name => !known.Contains(name));
            if (unknown is not null)
            {
                throw new ArgumentException($"Stage '{stage.Name}' needs unknown resource '{unknown}'.", nameof(stages));
            }

            if (stage.Resources.Distinct(StringComparer.Ordinal).Count() != stage.Resources.Count)
            {
                throw new ArgumentException($"Stage '{stage.Name}' lists a resource more than once.", nameof(stages));
            }

            foreach (var probe in probes)
            {
                try
                {
                    stage.Rule(probe);
                }
                catch (KeyNotFoundException e)
                {
                    throw new ArgumentException($"Stage '{stage.Name}' has a rule that reads a sensor type no registered sensor provides: {e.Message}", nameof(stages), e);
                }
                catch (Exception)
                {
                    // Probing only looks for missing sensor types. Anything else a rule does with extreme values is its own business.
                }
            }
        }
    }

    /// <summary>
    /// Every combination of a very low and a very high value per registered sensor type. A rule that is a
    /// conjunction of comparisons short-circuits, so a single probe could miss a branch that reads an unknown
    /// type; the corners exercise both sides of every comparison.
    /// </summary>
    private static List<IReadOnlyDictionary<SensorType, double>> RuleProbes(IReadOnlyList<SensorType> types)
    {
        var probes = new List<IReadOnlyDictionary<SensorType, double>>();
        var corners = 1 << types.Count;

        for (var mask = 0; mask < corners; mask++)
        {
            var probe = new Dictionary<SensorType, double>();
            for (var i = 0; i < types.Count; i++)
            {
                probe[types[i]] = (mask & (1 << i)) == 0 ? -1e9 : 1e9;
            }

            probes.Add(probe);
        }

        return probes;
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
