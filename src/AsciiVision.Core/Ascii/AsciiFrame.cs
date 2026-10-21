using System.Text;

namespace AsciiVision.Core.Ascii;

/// <summary>
/// A converted character frame. Cells are stored row-major in parallel arrays.
/// Colors are 0xAARRGGBB, which matches BGRA byte order in memory on little-endian machines.
///
/// Frames are reference counted so a single conversion can be shared by the preview,
/// console and recorder without copies: every consumer calls <see cref="Retain"/> before
/// keeping a frame and <see cref="Dispose"/> when done. The last release returns the
/// arrays to the owning pool. Content must not be modified after the frame is published.
/// </summary>
public sealed class AsciiFrame : IDisposable
{
    private readonly AsciiFramePool? _pool;
    private char[] _chars;
    private uint[] _foreground;
    private uint[] _background;
    private int _refCount;

    internal AsciiFrame(AsciiFramePool? pool, int capacity)
    {
        _pool = pool;
        _chars = new char[capacity];
        _foreground = new uint[capacity];
        _background = new uint[capacity];
    }

    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public int CellCount => Columns * Rows;
    public GridSize Grid => new(Columns, Rows);

    /// <summary>True when <see cref="Background"/> holds per-cell colors; otherwise use <see cref="DefaultBackground"/>.</summary>
    public bool HasCellBackground { get; set; }

    /// <summary>True when every foreground cell shares one color (lets the console skip color codes).</summary>
    public bool IsMonochrome { get; set; }

    public uint DefaultBackground { get; set; }

    public TimeSpan Timestamp { get; set; }

    public long Sequence { get; set; }

    /// <summary>Source frame size the conversion came from.</summary>
    public int SourceWidth { get; set; }
    public int SourceHeight { get; set; }

    internal int Capacity => _chars.Length;

    internal int RefCount => Volatile.Read(ref _refCount);

    public ReadOnlySpan<char> Chars => _chars.AsSpan(0, CellCount);
    public ReadOnlySpan<uint> Foreground => _foreground.AsSpan(0, CellCount);
    public ReadOnlySpan<uint> Background => _background.AsSpan(0, CellCount);

    /// <summary>Writable views for the producer. Only valid before the frame is shared.</summary>
    public Span<char> WritableChars => _chars.AsSpan(0, CellCount);
    public Span<uint> WritableForeground => _foreground.AsSpan(0, CellCount);
    public Span<uint> WritableBackground => _background.AsSpan(0, CellCount);

    public ReadOnlySpan<char> GetRow(int row) => _chars.AsSpan(row * Columns, Columns);

    public static AsciiFrame CreateUnpooled(int columns, int rows)
    {
        var frame = new AsciiFrame(null, Math.Max(1, columns * rows));
        frame.Reset(columns, rows);
        return frame;
    }

    internal void Reset(int columns, int rows)
    {
        int needed = columns * rows;
        if (needed > _chars.Length)
        {
            _chars = new char[needed];
            _foreground = new uint[needed];
            _background = new uint[needed];
        }

        Columns = columns;
        Rows = rows;
        HasCellBackground = false;
        IsMonochrome = false;
        DefaultBackground = 0xFF000000;
        Timestamp = TimeSpan.Zero;
        Sequence = 0;
        SourceWidth = 0;
        SourceHeight = 0;
        _refCount = 1;
    }

    /// <summary>Adds a reference. Returns this frame so calls can be chained.</summary>
    public AsciiFrame Retain()
    {
        int value;
        do
        {
            value = Volatile.Read(ref _refCount);
            if (value <= 0)
            {
                throw new ObjectDisposedException(nameof(AsciiFrame), "Cannot retain a released ASCII frame.");
            }
        }
        while (Interlocked.CompareExchange(ref _refCount, value + 1, value) != value);

        return this;
    }

    public void Dispose()
    {
        int remaining = Interlocked.Decrement(ref _refCount);
        if (remaining == 0)
        {
            _pool?.Return(this);
        }
        else if (remaining < 0)
        {
            // Over-release is a bug in a consumer; clamp so the pool is never corrupted.
            Interlocked.Exchange(ref _refCount, 0);
        }
    }

    /// <summary>Deep copy that is not tied to a pool (safe to keep indefinitely).</summary>
    public AsciiFrame Clone()
    {
        var copy = CreateUnpooled(Columns, Rows);
        CopyContentTo(copy);
        return copy;
    }

    internal void CopyContentTo(AsciiFrame target)
    {
        Chars.CopyTo(target.WritableChars);
        Foreground.CopyTo(target.WritableForeground);
        Background.CopyTo(target.WritableBackground);
        target.HasCellBackground = HasCellBackground;
        target.IsMonochrome = IsMonochrome;
        target.DefaultBackground = DefaultBackground;
        target.Timestamp = Timestamp;
        target.Sequence = Sequence;
        target.SourceWidth = SourceWidth;
        target.SourceHeight = SourceHeight;
    }

    /// <summary>Plain text with one line per row (trailing spaces kept so columns stay aligned).</summary>
    public string ToText(string newLine = "\r\n")
    {
        var sb = new StringBuilder(CellCount + Rows * newLine.Length);
        for (int r = 0; r < Rows; r++)
        {
            sb.Append(GetRow(r));
            if (r < Rows - 1)
            {
                sb.Append(newLine);
            }
        }

        return sb.ToString();
    }
}
