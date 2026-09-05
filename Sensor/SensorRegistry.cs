using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sensor;

/// <summary>A sensor's stream ended or threw. Streams are expected to run for the life of the machine, so both are faults.</summary>
public sealed class SensorStreamException(Guid sensorId, string message, Exception? inner = null)
    : Exception($"Sensor {sensorId}: {message}", inner)
{
    public Guid SensorId { get; } = sensorId;
}

/// <summary>
/// Knows which sensors exist and merges their streams into one. Sensors are registered at startup; the registry
/// is frozen when the machine starts, so a late registration fails loudly instead of being silently ignored by
/// streams that were already open. Every consumer that opens <see cref="ReadAllAsync"/> gets its own independent
/// stream, and a sensor that stops or throws ends that stream with a <see cref="SensorStreamException"/>.
/// </summary>
public sealed class SensorRegistry(ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly Dictionary<Guid, ISensor<SensorData>> _sensors = [];
    private volatile bool _frozen;

    public IReadOnlyCollection<ISensor<SensorData>> Sensors => _sensors.Values;

    public IReadOnlyCollection<Guid> Ids => _sensors.Keys;

    public bool IsFrozen => _frozen;

    public void Register(ISensor<SensorData> sensor)
    {
        if (_frozen)
        {
            throw new InvalidOperationException("The sensor registry is frozen: sensors are registered before the machine starts.");
        }

        if (!_sensors.TryAdd(sensor.Id, sensor))
        {
            throw new InvalidOperationException($"Sensor {sensor.Id} is already registered.");
        }

        _logger.LogInformation("Sensor {SensorId} ({SensorType}) registered", sensor.Id, sensor.GetSnapshot().Type);
    }

    /// <summary>Ends registration. Called by the machine when it starts.</summary>
    public void Freeze()
    {
        _frozen = true;
        _logger.LogDebug("Sensor registry frozen with {SensorCount} sensors", _sensors.Count);
    }

    /// <summary>True once the given set of sensor ids covers every registered sensor.</summary>
    public bool Covers(IReadOnlySet<Guid> sensorIds) => _sensors.Keys.All(sensorIds.Contains);

    /// <summary>True once the given readings cover every registered sensor.</summary>
    public bool Covers(IReadOnlyDictionary<Guid, SensorData> latest) => _sensors.Keys.All(latest.ContainsKey);

    /// <summary>
    /// One stream carrying every registered sensor's readings in arrival order. Each sensor is pumped by its
    /// own task into a small shared buffer, so sensors on different clocks interleave naturally. The stream ends
    /// when the caller cancels, or with a <see cref="SensorStreamException"/> as soon as any sensor's own stream
    /// ends or throws, so a consumer learns about a dead sensor immediately instead of waiting forever.
    /// </summary>
    public async IAsyncEnumerable<SensorData> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var merged = Channel.CreateBounded<SensorData>(new BoundedChannelOptions(Math.Max(1, _sensors.Count * 4))
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        using var pumpsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pumps = _sensors.Values.Select(sensor => PumpAsync(sensor, merged.Writer, pumpsCts.Token)).ToArray();
        _logger.LogDebug("Merged stream opened over {SensorCount} sensors", pumps.Length);

        try
        {
            await foreach (var reading in merged.Reader.ReadAllAsync(cancellationToken))
            {
                yield return reading;
            }
        }
        finally
        {
            pumpsCts.Cancel();
            await Task.WhenAll(pumps);
            _logger.LogDebug("Merged stream closed");
        }
    }

    private async Task PumpAsync(ISensor<SensorData> sensor, ChannelWriter<SensorData> writer, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var reading in sensor.GetAsyncEnumerable(cancellationToken))
            {
                writer.TryWrite(reading);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError("Sensor {SensorId} stream ended", sensor.Id);
                writer.TryComplete(new SensorStreamException(sensor.Id, "stream ended"));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stream closed by the consumer
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Sensor {SensorId} stream failed", sensor.Id);
            writer.TryComplete(new SensorStreamException(sensor.Id, "stream failed", e));
        }
    }
}
