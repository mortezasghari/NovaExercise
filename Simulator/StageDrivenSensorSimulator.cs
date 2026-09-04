using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sensor;

namespace Simulator;

/// <summary>
/// Base for sensors whose value is driven by the set of active stages. The clock calls <see cref="Tick"/>;
/// subclasses only supply the per-stage influence via <see cref="StageDelta"/>.
/// Each consumer of <see cref="GetAsyncEnumerable"/> gets its own one-slot buffer holding the latest reading,
/// so a slow consumer never blocks the simulation or the other consumers.
/// </summary>
public abstract class StageDrivenSensorSimulator(
    Guid id,
    SensorType type,
    PhysicalModel model,
    double initialValue,
    Random? random = null) : ISensor<SensorData>, ITickable
{
    // One lock guards the latest reading and the subscriber list.
    private readonly Lock _lock = new();
    private readonly List<ChannelWriter<SensorData>> _subscribers = [];
    private readonly Random _random = random ?? Random.Shared;

    private SensorData _latest = new(id, Math.Clamp(initialValue, model.Min, model.Max), type, DateTime.UtcNow);

    public Guid Id => id;
    public SensorType Type => type;
    public PhysicalModel Model => model;

    /// <summary>Change in value per tick contributed by one active stage.</summary>
    protected abstract double StageDelta(Stage activeStage);

    public SensorData GetSnapshot()
    {
        lock (_lock)
        {
            return _latest;
        }
    }

    public void Tick(DateTime timestamp, IReadOnlySet<Stage> activeStages)
    {
        lock (_lock)
        {
            double noise = (_random.NextDouble() * 2.0 - 1.0) * model.NoiseAmplitude;
            _latest = _latest with
            {
                Value = ComputeNext(_latest.Value, activeStages, noise),
                Timestamp = timestamp,
                Sequence = _latest.Sequence + 1,
            };

            // TryWrite never blocks and runs no consumer code (a full one-slot buffer just drops its old
            // value), so publishing under the lock cannot deadlock against a consumer calling GetSnapshot.
            foreach (var subscriber in _subscribers)
            {
                subscriber.TryWrite(_latest);
            }
        }
    }

    /// <summary>
    /// Streams every reading published after subscription. The stream ends when the caller cancels;
    /// the simulator itself has no lifetime of its own.
    /// </summary>
    public async IAsyncEnumerable<SensorData> GetAsyncEnumerable(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<SensorData>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        lock (_lock)
        {
            _subscribers.Add(channel.Writer);
        }

        try
        {
            await foreach (var reading in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return reading;
            }
        }
        finally
        {
            lock (_lock)
            {
                _subscribers.Remove(channel.Writer);
            }
        }
    }

    /// <summary>
    /// Pure physics step: touches no mutable state, so tests can assert exact values without a clock or randomness.
    /// </summary>
    public double ComputeNext(double current, IReadOnlySet<Stage> activeStages, double noise)
    {
        // Natural decay towards ambient plus the feedback of every active stage
        double delta = (model.Ambient - current) * model.DecayRate + activeStages.Sum(StageDelta);

        // Noise and physical limits
        return Math.Clamp(current + delta + noise, model.Min, model.Max);
    }
}
