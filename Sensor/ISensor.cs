namespace Sensor;

public enum SensorType : byte
{
    Temperature,
    Pressure
}

/// <summary>
/// One reading. <paramref name="Sequence"/> increases by one on every measurement the sensor writes, independently
/// of any clock, so a reader can tell "new since I last looked" even when timestamps collide or come from
/// different clocks. Sequence 0 means no measurement has been taken yet.
/// </summary>
public record SensorData(Guid SensorId, double Value, SensorType Type, DateTime Timestamp, long Sequence = 0);

public interface ISensor<out T> where T : SensorData
{
    T GetSnapshot();
    IAsyncEnumerable<T> GetAsyncEnumerable(CancellationToken cancellationToken = default);
}