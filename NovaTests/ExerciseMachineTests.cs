using Controller;
using Sensor;

namespace NovaTests;

public class ExerciseMachineTests
{
    private static IReadOnlyDictionary<SensorType, double> Values(double temperature, double pressure) =>
        new Dictionary<SensorType, double> { [SensorType.Temperature] = temperature, [SensorType.Pressure] = pressure };

    [Theory]
    [InlineData(10.0, 50.0, false)]    // temperature exactly at the limit is not "more than"
    [InlineData(10.01, 50.0, true)]
    [InlineData(50.0, 100.0, false)]   // pressure exactly at the limit is not "less than"
    [InlineData(50.0, 99.99, true)]
    public void Rule1_boundaries(double t, double p, bool expected)
    {
        Assert.Equal(expected, ExerciseMachine.Rule1(Values(t, p)));
    }

    [Theory]
    [InlineData(5.0, 40.0, false)]
    [InlineData(5.01, 40.0, true)]
    [InlineData(20.0, 50.0, false)]
    [InlineData(20.0, 49.99, true)]
    public void Rule2_boundaries(double t, double p, bool expected)
    {
        Assert.Equal(expected, ExerciseMachine.Rule2(Values(t, p)));
    }

    [Theory]
    [InlineData(20.0, 50.0, false)]
    [InlineData(20.01, 50.0, true)]
    [InlineData(30.0, 100.0, false)]
    [InlineData(30.0, 99.99, true)]
    public void Rule3_boundaries(double t, double p, bool expected)
    {
        Assert.Equal(expected, ExerciseMachine.Rule3(Values(t, p)));
    }

    [Fact]
    public void Stage_map_matches_the_exercise()
    {
        var stages = ExerciseMachine.Stages(TimeSpan.FromMilliseconds(1));

        Assert.Equal(["stage_1", "stage_2", "stage_3"], stages.Select(s => s.Name));
        Assert.Equal(["R_A", "R_B"], stages[0].Resources);
        Assert.Equal(["R_C", "R_B"], stages[1].Resources);
        Assert.Equal(["R_A", "R_C"], stages[2].Resources);
    }

    [Theory]
    [InlineData(25.0, 40.0, true, true, true)]     // all three rules hold: every stage wants to run
    [InlineData(15.0, 70.0, true, true, false)]    // rule 1 only
    [InlineData(8.0, 40.0, false, true, true)]     // rule 2 only
    [InlineData(25.0, 70.0, true, true, true)]     // rules 1 and 3: stage_2 through rule 1
    [InlineData(3.0, 40.0, false, false, false)]   // nothing holds
    [InlineData(25.0, 150.0, false, false, false)] // pressure too high for every rule
    public void Each_stage_runs_when_any_rule_that_names_it_holds(double t, double p, bool s1, bool s2, bool s3)
    {
        var stages = ExerciseMachine.Stages(TimeSpan.FromMilliseconds(1));
        var v = Values(t, p);

        Assert.Equal(s1, stages[0].Rule(v));
        Assert.Equal(s2, stages[1].Rule(v));
        Assert.Equal(s3, stages[2].Rule(v));
    }

    [Fact]
    public void A_rule_that_references_a_missing_sensor_type_throws()
    {
        var onlyTemperature = new Dictionary<SensorType, double> { [SensorType.Temperature] = 25.0 };

        Assert.Throws<KeyNotFoundException>(() => ExerciseMachine.Rule1(onlyTemperature));
    }

    [Fact]
    public void Custom_work_per_stage_is_wired_by_name()
    {
        var calls = new List<string>();
        var stages = ExerciseMachine.Stages(name => ct => { calls.Add(name); return Task.CompletedTask; });

        foreach (var stage in stages)
        {
            stage.Work(CancellationToken.None);
        }

        Assert.Equal(["stage_1", "stage_2", "stage_3"], calls);
    }
}
