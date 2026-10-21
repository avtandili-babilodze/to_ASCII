using System.Buffers;

namespace AsciiVision.Core.Imaging;

/// <summary>
/// A single BGRA32 source frame backed by a pooled buffer.
/// Ownership is exclusive: whoever holds the frame must dispose it exactly once,
/// after which the buffer goes back to the pool and must not be touched.
/// </summary>
public sealed class VideoFrame : IDisposable
{
    public const int BytesPerPixel = 4;

    private byte[]? _buffer;

    private VideoFrame(byte[] buffer, int width, int height, int stride, TimeSpan timestamp, long index)
    {
        _buffer = buffer;
        Width = width;
        Height = height;
        Stride = stride;
        Timestamp = timestamp;
        Index = index;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }

    /// <summary>Presentation timestamp relative to the start of the source.</summary>
    public TimeSpan Timestamp { get; }

    /// <summary>Monotonic frame number assigned by the source.</summary>
    public long Index { get; }

    public int ByteLength => Stride * Height;

    public bool IsDisposed => _buffer is null;

    /// <summary>The underlying pooled array. It may be larger than <see cref="ByteLength"/>.</summary>
    public byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(VideoFrame));

    public Span<byte> Data => Buffer.AsSpan(0, ByteLength);

    public Span<byte> GetRow(int y) => Buffer.AsSpan(y * Stride, Width * BytesPerPixel);

    public static VideoFrame Rent(int width, int height, TimeSpan timestamp, long index = 0)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"Invalid frame size {width}x{height}.");
        }

        int stride = width * BytesPerPixel;
        var buffer = ArrayPool<byte>.Shared.Rent(stride * height);
        return new VideoFrame(buffer, width, height, stride, timestamp, index);
    }

    /// <summary>Creates a deep copy (used where a frame must outlive its producer, e.g. tests).</summary>
    public VideoFrame Clone()
    {
        var copy = Rent(Width, Height, Timestamp, Index);
        Data.CopyTo(copy.Data);
        return copy;
    }

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
