namespace Resources;

public enum ResourceState : byte
{
    Idle,
    Busy,
    Error
}

/// <summary>
/// An exclusive resource. At most one holder at a time: Idle -> Busy on acquire, Busy -> Idle on release.
/// A resource may enter Error at any time; acquiring an Error resource fails immediately rather than waiting,
/// and a holder is expected to release and abandon its work when it observes Error.
/// </summary>
public interface IResource
{
    string Name { get; }

    ResourceState State { get; }

    /// <summary>Acquires the resource if it is Idle, without waiting. Returns false if Busy or Error.</summary>
    bool TryAcquire();

    /// <summary>Waits until the resource is Idle and acquires it.</summary>
    /// <exception cref="ResourceErrorException">The resource is, or became, Error before it could be acquired.</exception>
    Task AcquireAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns a held resource to Idle (or leaves it in Error if it failed while held).</summary>
    /// <exception cref="InvalidOperationException">The resource is not held.</exception>
    void Release();
}

public sealed class ResourceErrorException(string resourceName)
    : Exception($"Resource '{resourceName}' is in the Error state.")
{
    public string ResourceName { get; } = resourceName;
}
