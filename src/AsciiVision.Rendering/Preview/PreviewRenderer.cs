using System.Diagnostics;
using AsciiVision.Core.Abstractions;
using AsciiVision.Core.Ascii;
using AsciiVision.Core.Configuration;
using AsciiVision.Core.Diagnostics;
using AsciiVision.Core.Threading;
using AsciiVision.Rendering.Glyphs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AsciiVision.Rendering.Preview;

/// <summary>Where the preview renderer delivers finished images (implemented by <see cref="AsciiPreviewControl"/>).</summary>
public interface IPreviewSurface
{
    /// <summary>Viewport size in device pixels and the DPI scale (thread-safe snapshot).</summary>
    (int Width, int Height, double DpiScale) Viewport { get; }

    /// <summary>Hands over a finished image; the surface must call <see cref="PreviewBuffer.Release"/> when done.</summary>
    void Present(PreviewBuffer buffer);

    void Clear();
}

/// <summary>
/// The WPF preview output. Glyph rasterization happens on this renderer's own thread; the UI thread
/// only copies the finished bitmap (one WritePixels per frame). With a single-slot mailbox on input
/// and a single pending image on output, a busy UI thread skips frames rather than queueing them.
/// </summary>
public sealed class PreviewRenderer : IAsciiRenderer, IDisposable
{
    private const double MinFontPx = 3;
    private const double MaxFontPx = 64;

    private readonly GlyphAtlasCache _atlases;
    private readonly ISettingsService _settings;
    private readonly PerformanceMetrics _metrics;
    private readonly ILogger _logger;
    private readonly LatestItemMailbox<AsciiFrame> _mailbox;
    private readonly PreviewBufferPool _buffers = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private volatile IPreviewSurface? _surface;
    private AsciiFrame? _lastFrame;

    public PreviewRenderer(GlyphAtlasCache atlases, ISettingsService settings, PerformanceMetrics metrics, ILogger<PreviewRenderer>? logger = null)
    {
        _atlases = atlases;
        _settings = settings;
        _metrics = metrics;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _mailbox = new LatestItemMailbox<AsciiFrame>(() => _metrics.AddDropped());
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "ASCII Vision preview renderer" };
        _thread.Start();
    }

    public string Name => "Preview";

    public bool IsActive => _surface is not null;

    public void Attach(IPreviewSurface? surface)
    {
        _surface = surface;
    }

    public void Submit(AsciiFrame frame)
    {
        if (_surface is null)
        {
            frame.Dispose();
            return;
        }

        _mailbox.Post(frame);
    }

    /// <summary>Re-renders the last frame (viewport resized, fit mode changed).</summary>
    public void Invalidate() => _mailbox.Signal();

    public void Clear()
    {
        _mailbox.Clear();
        _surface?.Clear();
    }

    private void RenderLoop()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                _mailbox.Wait(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var frame = _mailbox.TryTake();
            if (frame is not null)
            {
                _lastFrame?.Dispose();
                _lastFrame = frame;
            }
            else if (_lastFrame is null)
            {
                continue;
            }

            try
            {
                RenderFrame(_lastFrame);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Preview rendering failed");
            }
        }

        _lastFrame?.Dispose();
        _lastFrame = null;
    }

    private void RenderFrame(AsciiFrame frame)
    {
        var surface = _surface;
        if (surface is null)
        {
            return;
        }

        var (viewW, viewH, dpi) = surface.Viewport;
        if (viewW <= 0 || viewH <= 0)
        {
            return; // minimized or collapsed
        }

        var settings = _settings.Current;
        var metrics = _atlases.GetMetrics(settings.FontFamily);
        double sizePx = settings.PreviewFit == PreviewFit.Fit
            ? metrics.FitFontSize(frame.Columns, frame.Rows, viewW, viewH, MinFontPx, MaxFontPx)
            : Math.Clamp(settings.FontSize * dpi, MinFontPx, MaxFontPx * 2);

        long start = Stopwatch.GetTimestamp();
        var atlas = _atlases.GetAtlas(settings.FontFamily, sizePx, settings.EffectiveRamp);
        var (width, height) = AsciiRasterizer.Measure(frame.Grid, atlas);
        var buffer = _buffers.Rent(width, height);
        buffer.DpiScale = dpi;
        AsciiRasterizer.Render(frame, atlas, buffer.Pixels, width, height, buffer.Stride);
        _metrics.PreviewRenderMilliseconds.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);

        surface.Present(buffer);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _mailbox.Signal();
        _thread.Join(TimeSpan.FromSeconds(2));
        _mailbox.Dispose();
        _cts.Dispose();
    }
}
