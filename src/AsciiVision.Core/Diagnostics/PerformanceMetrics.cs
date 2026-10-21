using AsciiVision.Core.Ascii;

namespace AsciiVision.Core.Diagnostics;

/// <summary>Shared, thread-safe measurements reported by every pipeline stage.</summary>
public sealed class PerformanceMetrics
{
    private long _droppedFrames;
    private GridSize _grid;
    private GridSize _consoleGrid;

    public RateCounter CaptureRate { get; } = new();
    public RateCounter ProcessingRate { get; } = new();
    public RateCounter PreviewRate { get; } = new();
    public RateCounter ConsoleRate { get; } = new();

    /// <summary>Conversion time per frame (all grids for that frame), milliseconds.</summary>
    public MovingAverage ProcessingMilliseconds { get; } = new(0.1);

    /// <summary>Glyph rasterization time of the WPF preview, milliseconds.</summary>
    public MovingAverage PreviewRenderMilliseconds { get; } = new(0.1);

    public MovingAverage ConsoleEncodeMilliseconds { get; } = new(0.1);

    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    public GridSize Grid
    {
        get { lock (this) { return _grid; } }
        set { lock (this) { _grid = value; } }
    }

    public GridSize ConsoleGrid
    {
        get { lock (this) { return _consoleGrid; } }
        set { lock (this) { _consoleGrid = value; } }
    }

    public long LastFrameTimestamp { get; set; }

    public void AddDropped(long count = 1) => Interlocked.Add(ref _droppedFrames, count);

    public void Reset()
    {
        Interlocked.Exchange(ref _droppedFrames, 0);
        CaptureRate.Reset();
        ProcessingRate.Reset();
        PreviewRate.Reset();
        ConsoleRate.Reset();
        ProcessingMilliseconds.Reset();
        PreviewRenderMilliseconds.Reset();
        ConsoleEncodeMilliseconds.Reset();
        Grid = default;
        ConsoleGrid = default;
    }

    public MetricsSnapshot Snapshot() => new(
        CaptureRate.Rate,
        ProcessingRate.Rate,
        PreviewRate.Rate,
        ConsoleRate.Rate,
        ProcessingMilliseconds.Value,
        PreviewRenderMilliseconds.Value,
        DroppedFrames,
        Grid,
        ConsoleGrid);
}

public readonly record struct MetricsSnapshot(
    double CaptureFps,
    double ProcessingFps,
    double PreviewFps,
    double ConsoleFps,
    double ProcessingMs,
    double PreviewRenderMs,
    long DroppedFrames,
    GridSize Grid,
    GridSize ConsoleGrid);
