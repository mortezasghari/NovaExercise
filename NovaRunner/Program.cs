using Controller;
using Microsoft.Extensions.Logging;
using Sensor;
using Simulator;

// ---- Knobs ---------------------------------------------------------------------------------------------------
var clockPeriod = TimeSpan.FromMilliseconds(100);   // the exercise: sensors update every 100 ms
var stageDuration = TimeSpan.FromSeconds(10);        // simulated work per stage run
var resourceFailureProbabilityPerTick = 0.002;      // roughly one fault per resource every 50 s
var resourceRecoveryTicks = 30;                     // 3 s at the default period
var ordering = args.Contains("--as-listed") ? ResourceOrdering.AsListed : ResourceOrdering.ByName;
var logLevel = args.Contains("--debug") ? LogLevel.Debug : LogLevel.Information;

// ---- Logging -------------------------------------------------------------------------------------------------
using var loggerFactory = LoggerFactory.Create(builder => builder
    .SetMinimumLevel(logLevel)
    .AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss.fff ";
    }));
var log = loggerFactory.CreateLogger("Runner");

// ---- The outside world (simulated) ---------------------------------------------------------------------------
// Sensors and resources stand in for real devices. Swap these for real adapters and nothing below changes.
var temperature = new TemperatureSensorSimulator(Guid.NewGuid(), initialTemperature: 20.0, logger: loggerFactory.CreateLogger("Sensor.Temperature"));
var pressure = new PressureSensorSimulator(Guid.NewGuid(), initialPressure: 30.0, logger: loggerFactory.CreateLogger("Sensor.Pressure"));

var resources = new[] { ExerciseMachine.R_A, ExerciseMachine.R_B, ExerciseMachine.R_C }
    .Select(name => new ResourceSimulator(name, resourceFailureProbabilityPerTick, resourceRecoveryTicks, logger: loggerFactory.CreateLogger($"Resource.{name}")))
    .ToList();

// The simulated environment reacts to which stages are running; this map is the only glue between the two sides.
var activeStages = new ActiveStages();
var stageOf = new Dictionary<string, Stage>
{
    [ExerciseMachine.Stage1] = Stage.Stage_1,
    [ExerciseMachine.Stage2] = Stage.Stage_2,
    [ExerciseMachine.Stage3] = Stage.Stage_3,
};

// ---- The control system --------------------------------------------------------------------------------------
var sensors = new SensorRegistry(loggerFactory.CreateLogger<SensorRegistry>());
sensors.Register(temperature);
sensors.Register(pressure);

var controller = new MachineController(
    sensors,
    resources,
    ExerciseMachine.Stages(stageDuration),
    ordering,
    stageStateChanged: (stage, state) => activeStages.Set(stageOf[stage], state == StageState.Running),
    loggerFactory);

// ---- Time ----------------------------------------------------------------------------------------------------
var clock = new ClockService([temperature, pressure, .. resources], activeStages, clockPeriod, logger: loggerFactory.CreateLogger<ClockService>());

// ---- Run until Ctrl+C ----------------------------------------------------------------------------------------
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // let the loops finish instead of killing the process
    log.LogInformation("Shutdown requested");
    shutdown.Cancel();
};

log.LogInformation("Nova machine starting (ordering {Ordering}, stage duration {StageDuration}). Press Ctrl+C to stop.", ordering, stageDuration);

await Task.WhenAll(
    clock.RunAsync(shutdown.Token),
    controller.RunAsync(shutdown.Token));

log.LogInformation("Final state: {Stages}; resources {Resources}",
    controller.Snapshot().Select(kv => $"{kv.Key}={kv.Value}"),
    resources.Select(r => $"{r.Name}={r.State}"));
