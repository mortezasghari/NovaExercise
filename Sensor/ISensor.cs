namespace Sensor;

public enum SensorType : byte
{
    Temperature,
    Pressure
}

public record SensorData(Guid SensorId, double Value, SensorType Type, DateTime Timestamp);

public interface ISensor<out T> where T : SensorData
{
    T GetSnapshot();
    IAsyncEnumerable<T> GetAsyncEnumerable(CancellationToken cancellationToken = default);
}