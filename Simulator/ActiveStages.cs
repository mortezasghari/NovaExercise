using System.Collections.Immutable;

namespace Simulator;

/// <summary>
/// The set of stages currently running, shared between the stage executor (writer) and the clock (reader).
/// The set is immutable and swapped atomically, so a reader always gets one consistent snapshot: a tick can
/// never observe a stage half added or half removed.
/// </summary>
public sealed class ActiveStages
{
    private ImmutableHashSet<Stage> _current = ImmutableHashSet<Stage>.Empty;
    public IReadOnlySet<Stage> Current => Volatile.Read(ref _current);

    public void Set(Stage stage, bool isActive)
    {
        ImmutableInterlocked.Update(ref _current, set => isActive ? set.Add(stage) : set.Remove(stage));
    }
}
