using System.Collections.Concurrent;

namespace AsciiVision.Rendering.Preview;

/// <summary>A rasterized preview image travelling from the render thread to the UI thread.</summary>
public sealed class PreviewBuffer
{
    private readonly PreviewBufferPool _pool;

    internal PreviewBuffer(PreviewBufferPool pool, byte[] pixels, int width, int height)
    {
        _pool = pool;
        Pixels = pixels;
        Width = width;
        Height = height;
    }

    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;
    public double DpiScale { get; internal set; } = 1.0;

    public void Release() => _pool.Return(Pixels);
}

/// <summary>Recycles pixel arrays between the render thread and the UI (at most a few in flight).</summary>
public sealed class PreviewBufferPool
{
    private readonly ConcurrentBag<byte[]> _free = new();
    private int _count;

    public PreviewBuffer Rent(int width, int height)
    {
        int needed = width * height * 4;
        while (_free.TryTake(out var array))
        {
            Interlocked.Decrement(ref _count);
            if (array.Length >= needed && array.Length <= needed * 2)
            {
                return new PreviewBuffer(this, array, width, height);
            }
        }

        return new PreviewBuffer(this, GC.AllocateUninitializedArray<byte>(needed), width, height);
    }

    internal void Return(byte[] pixels)
    {
        if (Interlocked.Increment(ref _count) <= 3)
        {
            _free.Add(pixels);
        }
        else
        {
            Interlocked.Decrement(ref _count);
        }
    }
}
