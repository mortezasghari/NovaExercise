namespace Simulator;

public enum Stage : byte
{
    Stage_1,
    Stage_2,
    Stage_3,
}

/// <summary>
/// Physical envelope of a simulated quantity: where it settles when nothing acts on it, how fast it
/// returns there, its hard limits and how much random noise each tick carries.
/// </summary>
public record PhysicalModel(
    double Ambient,
    double Min,
    double Max,
    double DecayRate = 0.02,        // fraction of the gap to ambient closed per tick
    double NoiseAmplitude = 0.2);   // +/- units of random noise per tick
