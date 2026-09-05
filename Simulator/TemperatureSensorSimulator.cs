using Microsoft.Extensions.Logging;
using Sensor;

namespace Simulator;

/// <summary>Temperature in degrees Celsius. Ambient 20, clamped to 0..100.</summary>
public class TemperatureSensorSimulator(Guid id, double initialTemperature, Random? random = null, ILogger? logger = null)
    : StageDrivenSensorSimulator(id, SensorType.Temperature, DefaultModel, initialTemperature, random, logger)
{
    public static readonly PhysicalModel DefaultModel = new(Ambient: 20.0, Min: 0.0, Max: 100.0);

    protected override double StageDelta(Stage activeStage) => activeStage switch
    {
        Stage.Stage_1 => 1.5,   // high heat
        Stage.Stage_2 => -0.3,  // mild cooling under compression
        Stage.Stage_3 => -2.0,  // active cooling
        _ => 0.0,
    };
}
