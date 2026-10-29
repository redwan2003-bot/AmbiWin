namespace AmbiWin.Core.Threading;

/// <summary>
/// Holds the newest value of a snapshot that one thread writes and another reads, with no locks.
/// Writers replace the value whole; readers always see a complete value, never a torn one.
/// </summary>
public sealed class Latest<T>
{
    private Box _box;
    private readonly WakeSignal? _wake;

    public Latest(T initial, WakeSignal? wake = null)
    {
        _box = new Box(initial);
        _wake = wake;
    }

    public T Value => Volatile.Read(ref _box).Value;

    /// <summary>Publishes a new value and wakes the reader if it is parked.</summary>
    public void Publish(T value)
    {
        Volatile.Write(ref _box, new Box(value));
        _wake?.Set();
    }

    private sealed class Box(T value)
    {
        public readonly T Value = value;
    }
}

/// <summary>Lets writers wake the render thread when it is parked with nothing to draw.</summary>
public sealed class WakeSignal : IDisposable
{
    private readonly AutoResetEvent _event = new(initialState: false);

    public void Set() => _event.Set();

    /// <summary>Blocks until <see cref="Set"/> is called or the timeout passes. Returns true if woken.</summary>
    public bool Wait(TimeSpan timeout) => _event.WaitOne(timeout);

    public void Dispose() => _event.Dispose();
}
