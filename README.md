# Nova exercise

A control system for a simulated industrial machine: two sensors publish every 100 ms, three processing stages
decide for themselves when to run, and three exclusive resources are contended for in parallel. The exercise is
in [SRE_Evaluation_Ver.2.0.md](SRE_Evaluation_Ver.2.0.md); the design, its assumptions and the concurrency
analysis are in [DESIGN.md](DESIGN.md).

## Prerequisites

- .NET SDK 10.0 (the solution file is `.slnx`, which needs SDK 9.0.200 or newer). Check with `dotnet --version`.
- Any OS. No other tooling, services or containers are involved.

## Build, test, run

```sh
dotnet build NovaExercise.slnx
dotnet test  NovaExercise.slnx
dotnet run --project NovaRunner
```

The runner prints a log and runs until Ctrl+C. Flags go after `--`:

```sh
dotnet run --project NovaRunner -- --help
dotnet run --project NovaRunner -- --debug              # scheduler and stream detail
dotnet run --project NovaRunner -- --stage-seconds 10   # longer stage work: expect aborts as the physics drift
dotnet run --project NovaRunner -- --deadlock           # ring-ordered map acquired as listed: the stages deadlock
dotnet run --project NovaRunner -- --drop-sensor 3      # pressure sensor link down for 8 s after 3 s
```

## What you will see

With no flags: the sensors register, the clock starts, and within one tick all three stage rules fire because
the ambient temperature and pressure satisfy the exercise's rules. The stages contend for the resources, so they
run one at a time; each logs `Acquiring`, `Running`, `completed`, `Idle`, and the next one takes over. Every few
tens of seconds a resource fails at random: the stage holding it aborts into `Faulted`, and after three seconds
the resource recovers and the stage is eligible again. Stage work that drives a sensor out of its own rule's
band aborts itself with `rule no longer holds`.

`--deadlock`: the three stages each take their first resource and log `holds R_x`; none ever reaches `Running`.
Ctrl+C cancels them cleanly. The same map with the default ordering completes, which is the point.

`--drop-sensor 3`: after three seconds the pressure sensor goes silent. Half a second later every stage warns that
its data is stale; work already in progress finishes; after five seconds each stage raises its data alarm as an
error; when the link returns the alarms clear and the machine resumes. Nothing new starts while blind.

## Layout

| Project | Role |
| --- | --- |
| `Sensor` | The sensor port: `ISensor<T>`, `SensorData`, and `SensorRegistry` (registration and one merged stream per consumer). |
| `Resources` | The resource port: `IResource` with Idle, Busy and Error. |
| `Controller` | The control system: `StageManager` (one stage as an independent process), `MachineController` (validation, wiring, lifetime), `ExerciseMachine` (the exercise's rules and stage map as data). Depends only on the two ports. |
| `Simulator` | The outside world, faked: sensor and resource simulators driven by `ClockService`, with the environment reacting to which stages run. A real deployment replaces this project with device adapters. |
| `NovaRunner` | Console host that wires everything together. |
| `NovaTests` | 133 xUnit tests: unit tests per component, the concurrency exhibits, and regressions for the external code review. |
| `Review` | An external code review of an earlier commit, kept as a record, with a resolution table at the top. |

## Tests

```sh
dotnet test NovaExercise.slnx
DOTNET_PROCESSOR_COUNT=1 dotnet test NovaExercise.slnx   # the suite also passes on a single core
```

The concurrency exhibits are in `NovaTests/ConcurrencyExhibitTests.cs`: a forced circular wait between three
stages, its prevention by the controller's global lock order, an atomicity violation prevented by frame-based
evaluation, and order violations prevented by sequence numbers and acquire-before-active. Schedules are forced
with gates, never with sleeps. `NovaTests/ReviewRegressionTests.cs` pins every finding of the code review.

## Assumptions in one paragraph

Rules are triggers evaluated only on complete sensor frames; a stage runs when any rule that names it holds,
so overlapping rules need no priority. Resources are exclusive; a stage acquires all of its resources one after
another in the controller's global order and releases them when its work ends. A stage aborts its run when a
held resource fails or when its rule stops holding. A silent sensor does not abort work: the stage warns, then
raises an alarm after a budget, and starts nothing new until data returns. Everything runs in one process with
no external infrastructure. The full list, with the reasoning, is in [DESIGN.md](DESIGN.md).
