using Sensor;

namespace Controller;

/// <summary>
/// The machine described by the exercise: three stages, three resources, three rules. Kept as data so the host
/// and the tests build the same machine, and so the rules read the same way the exercise states them.
/// </summary>
public static class ExerciseMachine
{
    public const string Stage1 = "stage_1";
    public const string Stage2 = "stage_2";
    public const string Stage3 = "stage_3";

    public const string R_A = "R_A";
    public const string R_B = "R_B";
    public const string R_C = "R_C";

    /// <summary>Temperature above 10 and pressure below 100: stage_1 and stage_2.</summary>
    public static bool Rule1(IReadOnlyDictionary<SensorType, double> v) =>
        v[SensorType.Temperature] > 10.0 && v[SensorType.Pressure] < 100.0;

    /// <summary>Temperature above 5 and pressure below 50: stage_3 and stage_2.</summary>
    public static bool Rule2(IReadOnlyDictionary<SensorType, double> v) =>
        v[SensorType.Temperature] > 5.0 && v[SensorType.Pressure] < 50.0;

    /// <summary>Temperature above 20 and pressure below 100: stage_1 and stage_3.</summary>
    public static bool Rule3(IReadOnlyDictionary<SensorType, double> v) =>
        v[SensorType.Temperature] > 20.0 && v[SensorType.Pressure] < 100.0;

    /// <summary>
    /// The stage map from the exercise, resources in the order the exercise lists them. Each stage runs when any
    /// rule that names it holds, so the overlapping rules need no priority: they simply become one condition per stage.
    /// </summary>
    public static IReadOnlyList<StageDefinition> Stages(Func<string, Func<CancellationToken, Task>> workFor) =>
    [
        new(Stage1, [R_A, R_B], v => Rule1(v) || Rule3(v), workFor(Stage1)),
        new(Stage2, [R_C, R_B], v => Rule1(v) || Rule2(v), workFor(Stage2)),
        new(Stage3, [R_A, R_C], v => Rule2(v) || Rule3(v), workFor(Stage3)),
    ];

    /// <summary>The stage map with every stage doing the same timed work.</summary>
    public static IReadOnlyList<StageDefinition> Stages(TimeSpan stageDuration) =>
        Stages(_ => StageManager.Delay(stageDuration));
}
