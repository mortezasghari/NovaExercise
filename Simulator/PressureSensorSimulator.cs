using Sensor;

namespace Simulator;

/// <summary>Pressure in arbitrary units. Ambient 30, clamped to 0..200.</summary>
public class PressureSensorSimulator(Guid id, double initialPressure, Random? random = null)
    : StageDrivenSensorSimulator(id, SensorType.Pressure, DefaultModel, initialPressure, random)
{
    public static readonly PhysicalModel DefaultModel = new(Ambient: 30.0, Min: 0.0, Max: 200.0);

    protected override double StageDelta(Stage activeStage) => activeStage switch
    {
        Stage.Stage_1 => 2.0,   // pressure build-up while heating
        Stage.Stage_2 => 4.0,   // heavy compression
        Stage.Stage_3 => -1.0,  // depressurisation
        _ => 0.0,
    };
}
