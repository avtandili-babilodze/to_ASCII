using System.Diagnostics;
using AsciiVision.Core.Abstractions;
using AsciiVision.Core.Ascii;
using AsciiVision.Core.Configuration;
using AsciiVision.Core.Media;
using AsciiVision.Core.Threading;
using AsciiVision.Rendering.Glyphs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AsciiVision.Export;

/// <summary>
/// Records the live ASCII output (not the source image) to MP4.
///
/// Live frames arrive irregularly (a static screen produces almost none), but MP4 here is
/// constant-frame-rate. The recorder therefore writes output frame <c>n</c> at wall time
/// <c>n / fps</c> using the newest ASCII frame available at that moment, duplicating frames
/// when the source is idle. If the encoder falls more than a second behind, it skips ahead
/// instead of building latency. The output size is fixed by the first frame; later frames
/// with a different grid (e.g. adaptive quality) are fitted into that size.
/// Video only: no audio is captured.
/// </summary>
public sealed class RecordingService : IRecordingService, IAsciiRenderer, IAsyncDisposable
{
    private const int MaxWidth = 3840;
    private const int MaxHeight = 2160;

    private readonly FfmpegLocator _locator;
    private readonly GlyphAtlasCache _atlases;
    private readonly ISettingsService _settings;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private LatestItemMailbox<AsciiFrame>? _mailbox;
    private CancellationTokenSource? _cts;
    private Task<RecordingResult?>? _worker;
    private long _startTimestamp;
    private volatile bool _recording;

    public RecordingService(FfmpegLocator locator, GlyphAtlasCache atlases, ISettingsService settings, ILogger<RecordingService>? logger = null)
    {
        _locator = locator;
        _atlases = atlases;
        _settings = settings;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public string Name => "Recorder";

    public bool IsActive => _recording;

    public bool IsRecording => _recording;

    public string? OutputPath { get; private set; }

    public TimeSpan Elapsed => _recording ? Stopwatch.GetElapsedTime(_startTimestamp) : TimeSpan.Zero;

    public event EventHandler<string>? Failed;

    public async Task StartAsync(RecordingOptions options, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_recording)
            {
                throw new InvalidOperationException("A recording is already in progress.");
            }

            var ffmpeg = _locator.Locate(_settings.Current.FfmpegPath) ?? throw new FfmpegNotFoundException();
            if (!ffmpeg.Capabilities.CanEncodeH264)
            {
                throw new FfmpegException("This FFmpeg build has no H.264 encoder (libx264 or h264_mf).");
            }

            OutputPath = Path.GetFullPath(options.OutputPath);
            _mailbox = new LatestItemMailbox<AsciiFrame>();
            _cts = new CancellationTokenSource();
            _startTimestamp = Stopwatch.GetTimestamp();
            _recording = true;
            var mailbox = _mailbox;
            var token = _cts.Token;
            _worker = Task.Factory.StartNew(() => RunAsync(ffmpeg, options, mailbox, token), TaskCreationOptions.LongRunning).Unwrap();
            _logger.LogInformation("Recording started: {Path}", OutputPath);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void Submit(AsciiFrame frame)
    {
        var mailbox = _mailbox;
        if (!_recording || mailbox is null)
        {
            frame.Dispose();
            return;
        }

        mailbox.Post(frame);
    }

    public async Task<RecordingResult?> StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_worker is null)
            {
                return null;
            }

            _recording = false;
            _cts!.Cancel();
            RecordingResult? result = null;
            try
            {
                result = await _worker.ConfigureAwait(false);
            }
            finally
            {
                _worker = null;
                _cts.Dispose();
                _cts = null;
                _mailbox?.Dispose();
                _mailbox = null;
            }

            _logger.LogInformation("Recording stopped: {Frames} frames", result?.FramesWritten ?? 0);
            return result;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task<RecordingResult?> RunAsync(FfmpegInstallation ffmpeg, RecordingOptions options, LatestItemMailbox<AsciiFrame> mailbox, CancellationToken stopToken)
    {
        var compositor = new FrameCompositor(_atlases);
        FfmpegVideoEncoder? encoder = null;
        byte[]? buffer = null;
        int width = 0, height = 0;
        double maxFont = 0;
        double fps = Math.Clamp(options.FramesPerSecond, 1, 120);
        long origin = 0;
        long written = 0;

        try
        {
            while (true)
            {
                TimeSpan wait;
                if (encoder is null)
                {
                    wait = TimeSpan.FromMilliseconds(250);
                }
                else
                {
                    var due = TimeSpan.FromSeconds(written / fps) - Stopwatch.GetElapsedTime(origin);
                    wait = due > TimeSpan.Zero ? due : TimeSpan.Zero;
                }

                bool stopping = stopToken.IsCancellationRequested;
                if (!stopping && wait > TimeSpan.Zero)
                {
                    try
                    {
                        await mailbox.WaitAsync(wait, stopToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        stopping = true;
                    }
                }

                using (var frame = mailbox.TryTake())
                {
                    if (frame is not null)
                    {
                        var settings = _settings.Current;
                        if (encoder is null)
                        {
                            (width, height, maxFont) = ChooseOutputSize(compositor, frame, settings);
                            buffer = new byte[width * height * 4];
                            encoder = FfmpegVideoEncoder.Start(ffmpeg, new FfmpegArguments.EncoderSettings(width, height, fps, options.Quality, options.OutputPath));
                            origin = Stopwatch.GetTimestamp();
                        }

                        compositor.Compose(frame, settings, buffer, width, height, maxFont);
                    }
                }

                if (encoder is not null)
                {
                    long target = (long)Math.Floor(Stopwatch.GetElapsedTime(origin).TotalSeconds * fps) + 1;
                    if (target - written > fps)
                    {
                        // More than a second behind (slow encoder): skip rather than accumulate delay.
                        written = target - 1;
                    }

                    while (written < target)
                    {
                        await encoder.WriteFrameAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                        written++;
                        if (stopping)
                        {
                            break;
                        }
                    }
                }

                if (stopping)
                {
                    break;
                }
            }

            if (encoder is null)
            {
                return null; // stopped before any frame arrived
            }

            using var finalize = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await encoder.CompleteAsync(finalize.Token).ConfigureAwait(false);
            return new RecordingResult(options.OutputPath, TimeSpan.FromSeconds(encoder.FramesWritten / fps), encoder.FramesWritten, width, height);
        }
        catch (Exception ex)
        {
            _recording = false;
            _logger.LogError(ex, "Recording failed");
            Failed?.Invoke(this, ex.Message);
            return null;
        }
        finally
        {
            if (encoder is not null)
            {
                await encoder.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static (int Width, int Height, double MaxFont) ChooseOutputSize(FrameCompositor compositor, AsciiFrame frame, AsciiSettings settings)
    {
        var (w, h) = compositor.NativeSize(frame.Grid, settings);
        if (w <= MaxWidth && h <= MaxHeight)
        {
            return (w, h, settings.FontSize);
        }

        double scale = Math.Min(MaxWidth / (double)w, MaxHeight / (double)h);
        int sw = (int)(w * scale) & ~1;
        int sh = (int)(h * scale) & ~1;
        return (sw, sh, settings.FontSize * scale);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }
}
