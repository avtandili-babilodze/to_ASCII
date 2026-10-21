using System.Collections.Concurrent;

namespace AsciiVision.Core.Ascii;

/// <summary>
/// Recycles <see cref="AsciiFrame"/> instances so steady-state conversion allocates nothing.
/// Arrays for large grids can exceed the LOH threshold, so reuse matters at 60 FPS.
/// </summary>
public sealed class AsciiFramePool
{
    private readonly ConcurrentBag<AsciiFrame> _free = new();
    private readonly int _maxRetained;
    private int _retained;
    private long _created;

    public AsciiFramePool(int maxRetained = 24)
    {
        _maxRetained = maxRetained;
    }

    public long CreatedCount => Interlocked.Read(ref _created);

    public int AvailableCount => Volatile.Read(ref _retained);

    public AsciiFrame Rent(int columns, int rows)
    {
        if (columns <= 0 || rows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(columns), $"Invalid grid {columns}x{rows}.");
        }

        if (_free.TryTake(out var frame))
        {
            Interlocked.Decrement(ref _retained);
        }
        else
        {
            frame = new AsciiFrame(this, columns * rows);
            Interlocked.Increment(ref _created);
        }

        frame.Reset(columns, rows);
        return frame;
    }

    internal void Return(AsciiFrame frame)
    {
        if (Interlocked.Increment(ref _retained) <= _maxRetained)
        {
            _free.Add(frame);
        }
        else
        {
            Interlocked.Decrement(ref _retained);
        }
    }
}
