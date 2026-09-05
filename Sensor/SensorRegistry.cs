using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sensor;

/// <summary>
/// Knows which sensors exist and merges their streams into one. Sensors are registered at startup, before any
/// stream is opened; adding or replacing a sensor is a <see cref="Register"/> call and no consumer changes.
/// Every consumer that opens <see cref="ReadAllAsync"/> gets its own independent stream.
/// </summary>
public sealed class SensorRegistry(ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly Dictionary<Guid, ISensor<SensorData>> _sensors = [];

    public IReadOnlyCollection<ISensor<SensorData>> Sensors => _sensors.Values;

    public void Register(ISensor<SensorData> sensor)
    {
        if (!_sensors.TryAdd(sensor.Id, sensor))
        {
            throw new InvalidOperationException($"Sensor {sensor.Id} is already registered.");
        }

        _logger.LogInformation("Sensor {SensorId} ({SensorType}) registered", sensor.Id, sensor.GetSnapshot().Type);
    }

    /// <summary>True once the given readings cover every registered sensor.</summary>
    public bool Covers(IReadOnlyDictionary<Guid, SensorData> latest) =>
        _sensors.Keys.All(latest.ContainsKey);

    /// <summary>
    /// One stream carrying every registered sensor's readings in arrival order. Each sensor is pumped by its
    /// own task into a small shared buffer, so sensors on different clocks interleave naturally. The stream
    /// ends when the caller cancels.
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

    private static async Task PumpAsync(ISensor<SensorData> sensor, ChannelWriter<SensorData> writer, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var reading in sensor.GetAsyncEnumerable(cancellationToken))
            {
                writer.TryWrite(reading);
            }
        }
        catch (OperationCanceledException)
        {
            // Stream closed by the consumer
        }
    }
}
