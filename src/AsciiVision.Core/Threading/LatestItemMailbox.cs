namespace AsciiVision.Core.Threading;

/// <summary>
/// A single-slot buffer that always holds the newest item. Posting replaces (and disposes)
/// any item the consumer has not taken yet, which bounds latency for live video: a slow
/// consumer sees fewer frames instead of increasingly stale ones.
/// </summary>
public sealed class LatestItemMailbox<T> : IDisposable where T : class, IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly Action? _onDropped;
    private T? _item;
    private long _dropped;
    private bool _disposed;

    public LatestItemMailbox(Action? onDropped = null)
    {
        _onDropped = onDropped;
    }

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public bool HasItem => Volatile.Read(ref _item) is not null;

    public void Post(T item)
    {
        if (Volatile.Read(ref _disposed))
        {
            item.Dispose();
            return;
        }

        var previous = Interlocked.Exchange(ref _item, item);
        if (previous is not null)
        {
            previous.Dispose();
            Interlocked.Increment(ref _dropped);
            _onDropped?.Invoke();
        }

        Signal();

        // Close the race with Dispose(): if disposal happened meanwhile, reclaim the item.
        if (Volatile.Read(ref _disposed))
        {
            Interlocked.Exchange(ref _item, null)?.Dispose();
        }
    }

    /// <summary>Wakes the consumer without posting an item (e.g. to re-render after a settings change).</summary>
    public void Signal()
    {
        try
        {
            if (_signal.CurrentCount == 0)
            {
                _signal.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // Another thread signalled first; one wake-up is enough.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Waits until an item is posted or <see cref="Signal"/> is called.</summary>
    public Task WaitAsync(CancellationToken cancellationToken) => _signal.WaitAsync(cancellationToken);

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _signal.WaitAsync(timeout, cancellationToken);

    public bool Wait(TimeSpan timeout, CancellationToken cancellationToken) => _signal.Wait(timeout, cancellationToken);

    /// <summary>Takes the current item (caller becomes owner) or returns null.</summary>
    public T? TryTake() => Interlocked.Exchange(ref _item, null);

    public void Clear() => Interlocked.Exchange(ref _item, null)?.Dispose();

    public void Dispose()
    {
        Volatile.Write(ref _disposed, true);
        Clear();
        Signal();
    }
}
