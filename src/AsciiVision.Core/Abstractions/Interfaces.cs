using AsciiVision.Core.Ascii;
using AsciiVision.Core.Configuration;
using AsciiVision.Core.Imaging;

namespace AsciiVision.Core.Abstractions;

/// <summary>Receives frames from a source. Takes ownership of each frame passed in.</summary>
public interface IFrameSink
{
    void OnFrame(VideoFrame frame);
}

/// <summary>
/// A producer of BGRA frames (monitor, window, video file, test pattern).
/// Sources deliver frames on their own threads and never block on the consumer.
/// </summary>
public interface IFrameSource : IAsyncDisposable
{
    string DisplayName { get; }

    SourceKind Kind { get; }

    /// <summary>Live sources prefer the newest frame; file sources follow their own timeline.</summary>
    bool IsLive { get; }

    /// <summary>Raised once when the source stops by itself (end of file, window closed, failure).</summary>
    event EventHandler<SourceEndedEventArgs>? Ended;

    Task StartAsync(IFrameSink sink, CancellationToken cancellationToken);

    Task StopAsync();
}

/// <summary>Transport controls for seekable sources.</summary>
public interface IPlaybackControl
{
    TimeSpan Duration { get; }

    TimeSpan Position { get; }

    double FrameRate { get; }

    bool IsPaused { get; }

    bool Loop { get; set; }

    void Pause();

    void Resume();

    void Seek(TimeSpan position);
}

public sealed class SourceEndedEventArgs : EventArgs
{
    public SourceEndedEventArgs(SourceEndReason reason, string? message = null, Exception? error = null)
    {
        Reason = reason;
        Message = message;
        Error = error;
    }

    public SourceEndReason Reason { get; }

    /// <summary>Human-readable explanation suitable for the UI.</summary>
    public string? Message { get; }

    public Exception? Error { get; }
}

public enum SourceEndReason
{
    Completed,
    SourceClosed,
    Failed,
}

/// <summary>Converts a source frame into characters. Instances are not thread-safe; use one per worker.</summary>
public interface IAsciiProcessor : IDisposable
{
    AsciiFrame Process(VideoFrame frame, AsciiSettings settings, GridSize grid);
}

public interface IAsciiProcessorFactory
{
    IAsciiProcessor Create();
}

/// <summary>
/// A destination for converted frames. <see cref="Submit"/> transfers one reference: the renderer
/// must dispose the frame when it no longer needs it. Implementations must return quickly.
/// </summary>
public interface IAsciiRenderer
{
    string Name { get; }

    /// <summary>Whether the renderer currently wants frames.</summary>
    bool IsActive { get; }

    void Submit(AsciiFrame frame);
}

/// <summary>A renderer bound to a terminal whose visible size constrains the grid.</summary>
public interface ITerminalRenderer : IAsciiRenderer
{
    /// <summary>Visible terminal area in cells, when known.</summary>
    GridSize? TerminalSize { get; }

    /// <summary>Shows a status line (e.g. "Stopped") in place of frames.</summary>
    void ShowStatus(string message);
}

/// <summary>Measures glyph shapes of a font for shape-matched (Detailed) conversion.</summary>
public interface IGlyphShapeProvider
{
    /// <summary>
    /// Coverage of the given (printable ASCII) characters sampled on a <paramref name="sampleColumns"/> × <paramref name="sampleRows"/>
    /// grid over their character cell.
    /// </summary>
    GlyphShapeSet GetShapes(string fontFamily, int sampleColumns, int sampleRows, string characters);
}

/// <summary>Provides the character-cell geometry of the preview font (needed to size the grid).</summary>
public interface ICellGeometryProvider
{
    /// <summary>Cell height divided by cell width for the given font.</summary>
    double GetCellAspect(string fontFamily);
}

public interface ISettingsService
{
    AsciiSettings Current { get; }

    event EventHandler<AsciiSettings>? Changed;

    void Update(AsciiSettings settings);

    void Update(Func<AsciiSettings, AsciiSettings> change);

    Task SaveAsync();

    Task SavePresetAsync(string path);

    /// <summary>Loads, validates and applies a preset. Returns warnings about clamped values.</summary>
    Task<IReadOnlyList<string>> LoadPresetAsync(string path);

    void ResetToDefaults();
}

public sealed record RecordingOptions(string OutputPath, EncodingQuality Quality, int FramesPerSecond);

public sealed record RecordingResult(string OutputPath, TimeSpan Duration, long FramesWritten, int Width, int Height);

public interface IRecordingService
{
    bool IsRecording { get; }

    TimeSpan Elapsed { get; }

    string? OutputPath { get; }

    /// <summary>Raised when recording stops due to an encoder failure.</summary>
    event EventHandler<string>? Failed;

    Task StartAsync(RecordingOptions options, CancellationToken cancellationToken = default);

    Task<RecordingResult?> StopAsync();
}

public sealed record ExportOptions
{
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }

    /// <summary>Target frame size; null renders at the native character raster size.</summary>
    public (int Width, int Height)? Resolution { get; init; }

    /// <summary>Output frame rate; null keeps the source rate.</summary>
    public double? FramesPerSecond { get; init; }

    public EncodingQuality Quality { get; init; } = EncodingQuality.High;

    public bool IncludeAudio { get; init; } = true;
}

public sealed record ExportProgress(long FramesDone, long FramesTotal, TimeSpan Elapsed, double FramesPerSecond)
{
    public double Fraction => FramesTotal > 0 ? Math.Clamp((double)FramesDone / FramesTotal, 0, 1) : 0;
}

public sealed record ExportResult(string OutputPath, long Frames, int Width, int Height, bool HasAudio, TimeSpan Elapsed);

public interface IVideoExporter
{
    Task<ExportResult> ExportAsync(ExportOptions options, AsciiSettings settings, IProgress<ExportProgress>? progress, CancellationToken cancellationToken);
}
