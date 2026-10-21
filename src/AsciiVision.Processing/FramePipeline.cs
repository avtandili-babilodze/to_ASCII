using System.Diagnostics;
using AsciiVision.Core.Abstractions;
using AsciiVision.Core.Ascii;
using AsciiVision.Core.Configuration;
using AsciiVision.Core.Diagnostics;
using AsciiVision.Core.Imaging;
using AsciiVision.Core.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AsciiVision.Processing;

/// <summary>
/// The real-time pipeline: source → latest-frame mailbox → one conversion → fan-out to renderers.
///
/// <list type="bullet">
/// <item>Each source frame is acquired and converted once. The preview and recorder share that
/// <see cref="AsciiFrame"/> by reference counting. The console reuses it as well unless it needs
/// a different grid (terminal-fit or custom columns); then the already-acquired frame is converted
/// a second time at the console grid on a separate processor. Decoding/capture is never duplicated.</item>
/// <item>The input mailbox holds one frame: if conversion falls behind, stale frames are dropped
/// (and counted) instead of accumulating latency.</item>
/// <item>Every session gets a generation number; frames that arrive from a previous source after a
/// switch are discarded, so switching sources cannot mix frames.</item>
/// </list>
/// </summary>
public sealed class FramePipeline : IAsyncDisposable
{
    private readonly IAsciiProcessorFactory _processorFactory;
    private readonly ISettingsService _settings;
    private readonly ICellGeometryProvider _geometry;
    private readonly PerformanceMetrics _metrics;
    private readonly AdaptiveQualityController _adaptive;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _switchLock = new(1, 1);
    private readonly object _latestGate = new();

    private LatestItemMailbox<VideoFrame>? _mailbox;
    private CancellationTokenSource? _sessionCts;
    private Task? _loopTask;
    private IFrameSource? _source;
    private long _generation;
    private volatile bool _paused;
    private volatile bool _refreshRequested;
    private AsciiFrame? _latest;

    public FramePipeline(
        IAsciiProcessorFactory processorFactory,
        ISettingsService settings,
        ICellGeometryProvider geometry,
        PerformanceMetrics metrics,
        AdaptiveQualityController adaptive,
        ILogger<FramePipeline>? logger = null)
    {
        _processorFactory = processorFactory;
        _settings = settings;
        _geometry = geometry;
        _metrics = metrics;
        _adaptive = adaptive;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _settings.Changed += (_, _) => RequestRefresh();
    }

    public IAsciiRenderer? Preview { get; set; }

    public ITerminalRenderer? Console { get; set; }

    public IAsciiRenderer? Recorder { get; set; }

    public IFrameSource? CurrentSource => _source;

    public bool IsRunning => _loopTask is { IsCompleted: false };

    public bool IsPaused => _paused;

    /// <summary>Stopwatch timestamp of the last frame delivered by the source (0 if none yet).</summary>
    public long LastSourceFrameTimestamp => Interlocked.Read(ref _lastSourceFrameTicks);

    private long _lastSourceFrameTicks;

    /// <summary>Raised (on a worker thread) when the current source ends by itself.</summary>
    public event EventHandler<SourceEndedEventArgs>? SourceEnded;

    /// <summary>Raised (on a worker thread) when conversion fails unexpectedly.</summary>
    public event EventHandler<Exception>? ProcessingFailed;

    public async Task StartAsync(IFrameSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await _switchLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);

            long generation = Interlocked.Increment(ref _generation);
            _metrics.Reset();
            _adaptive.Reset();
            _paused = false;
            Interlocked.Exchange(ref _lastSourceFrameTicks, 0);

            var cts = new CancellationTokenSource();
            var mailbox = new LatestItemMailbox<VideoFrame>(() => _metrics.AddDropped());
            _sessionCts = cts;
            _mailbox = mailbox;
            _source = source;
            source.Ended += OnSourceEnded;
            _loopTask = Task.Factory.StartNew(() => RunLoopAsync(mailbox, cts.Token), cts.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

            try
            {
                await source.StartAsync(new GenerationSink(this, generation, mailbox), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await StopCoreAsync().ConfigureAwait(false);
                throw;
            }

            _logger.LogInformation("Started source {Source} ({Kind})", source.DisplayName, source.Kind);
        }
        finally
        {
            _switchLock.Release();
        }
    }

    public async Task StopAsync()
    {
        await _switchLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _switchLock.Release();
        }
    }

    public void Pause()
    {
        _paused = true;
        if (_source is IPlaybackControl playback)
        {
            playback.Pause();
        }
    }

    public void Resume()
    {
        _paused = false;
        if (_source is IPlaybackControl playback)
        {
            playback.Resume();
        }
    }

    /// <summary>Re-converts the last source frame (e.g. after a settings change while paused).</summary>
    public void RequestRefresh()
    {
        _refreshRequested = true;
        _mailbox?.Signal();
    }

    /// <summary>Returns a retained reference to the newest converted frame, or null. Caller must dispose it.</summary>
    public AsciiFrame? AcquireLatestFrame()
    {
        lock (_latestGate)
        {
            return _latest?.Retain();
        }
    }

    private async Task StopCoreAsync()
    {
        var source = _source;
        var cts = _sessionCts;
        var loop = _loopTask;
        var mailbox = _mailbox;
        _source = null;
        _sessionCts = null;
        _loopTask = null;
        _mailbox = null;
        Interlocked.Increment(ref _generation);

        if (source is not null)
        {
            source.Ended -= OnSourceEnded;
            try
            {
                await source.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stopping source {Source} failed", source.DisplayName);
            }

            try
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Disposing source {Source} failed", source.DisplayName);
            }
        }

        if (cts is not null)
        {
            cts.Cancel();
            if (loop is not null)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Pipeline loop ended with an error");
                }
            }

            cts.Dispose();
        }

        mailbox?.Dispose();
        if (source is not null)
        {
            Console?.ShowStatus("Stopped");
        }
    }

    private void OnSourceEnded(object? sender, SourceEndedEventArgs e)
    {
        if (!ReferenceEquals(sender, _source))
        {
            return;
        }

        _logger.LogInformation("Source ended: {Reason} {Message}", e.Reason, e.Message);
        SourceEnded?.Invoke(this, e);
    }

    private async Task RunLoopAsync(LatestItemMailbox<VideoFrame> mailbox, CancellationToken ct)
    {
        using var mainProcessor = _processorFactory.Create();
        using var consoleProcessor = _processorFactory.Create();
        VideoFrame? lastFrame = null;
        VideoFrame? deferred = null;
        long nextDue = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (deferred is not null)
                {
                    // A frame arrived early for the FPS cap; process it when due unless a newer one arrives.
                    var wait = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), nextDue);
                    if (wait > TimeSpan.Zero)
                    {
                        await mailbox.WaitAsync(wait, ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    await mailbox.WaitAsync(ct).ConfigureAwait(false);
                }

                var frame = mailbox.TryTake();
                if (frame is not null)
                {
                    deferred?.Dispose();
                    deferred = null;
                }
                else if (deferred is not null && Stopwatch.GetTimestamp() >= nextDue)
                {
                    frame = deferred;
                    deferred = null;
                }

                var settings = _settings.Current;
                if (frame is not null)
                {
                    long now = Stopwatch.GetTimestamp();
                    long interval = Stopwatch.Frequency / Math.Max(1, settings.TargetFps);
                    if (now < nextDue - interval / 8)
                    {
                        deferred = frame;
                        continue;
                    }

                    // Advance the schedule by whole intervals so the average rate matches the target.
                    nextDue = Math.Max(nextDue + interval, now - interval);
                    lastFrame?.Dispose();
                    lastFrame = frame;
                    _refreshRequested = false;
                    ProcessFrame(frame, settings, mainProcessor, consoleProcessor);
                }
                else if (_refreshRequested && lastFrame is not null)
                {
                    _refreshRequested = false;
                    ProcessFrame(lastFrame, settings, mainProcessor, consoleProcessor);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Frame processing failed");
            ProcessingFailed?.Invoke(this, ex);
        }
        finally
        {
            deferred?.Dispose();
            lastFrame?.Dispose();
        }
    }

    private void ProcessFrame(VideoFrame frame, AsciiSettings settings, IAsciiProcessor mainProcessor, IAsciiProcessor consoleProcessor)
    {
        var preview = Preview;
        var console = Console;
        var recorder = Recorder;
        bool previewWanted = settings.DisplayMode != DisplayMode.Console && preview?.IsActive == true;
        bool consoleWanted = settings.DisplayMode != DisplayMode.App && console?.IsActive == true;
        bool recordWanted = recorder?.IsActive == true;

        double aspect = settings.AutoCharacterAspect ? _geometry.GetCellAspect(settings.FontFamily) : settings.CharacterAspect;
        int columns = settings.AdaptiveQuality ? _adaptive.EffectiveColumns(settings.EffectiveColumns) : settings.EffectiveColumns;
        var grid = GridCalculator.Compute(frame.Width, frame.Height, columns, aspect);

        long start = Stopwatch.GetTimestamp();

        // The main conversion always runs: it feeds the preview and recorder and backs screenshots.
        var main = mainProcessor.Process(frame, settings, grid);
        AsciiFrame? consoleFrame = null;
        if (consoleWanted)
        {
            var consoleGrid = ResolveConsoleGrid(settings, frame, grid, console!);
            consoleFrame = consoleGrid == grid ? main.Retain() : consoleProcessor.Process(frame, settings, consoleGrid);
            _metrics.ConsoleGrid = consoleGrid;
        }

        double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _metrics.ProcessingMilliseconds.Add(elapsedMs);
        _metrics.ProcessingRate.Tick();
        _metrics.Grid = grid;

        if (previewWanted)
        {
            preview!.Submit(main.Retain());
        }

        if (consoleFrame is not null)
        {
            console!.Submit(consoleFrame);
        }

        if (recordWanted)
        {
            recorder!.Submit(main.Retain());
        }

        AsciiFrame? previous;
        lock (_latestGate)
        {
            previous = _latest;
            _latest = main;
        }

        previous?.Dispose();

        if (settings.AdaptiveQuality)
        {
            double cost = Math.Max(elapsedMs, previewWanted ? _metrics.PreviewRenderMilliseconds.Value : 0);
            _adaptive.Report(cost, settings.TargetFps);
        }
    }

    internal static GridSize ResolveConsoleGrid(AsciiSettings settings, VideoFrame frame, GridSize previewGrid, ITerminalRenderer console)
    {
        switch (settings.ConsoleSizeMode)
        {
            case ConsoleSizeMode.MatchPreview:
                return previewGrid;
            case ConsoleSizeMode.Custom:
                return GridCalculator.Compute(frame.Width, frame.Height, settings.ConsoleColumns, settings.CharacterAspect);
            default:
                var terminal = console.TerminalSize ?? new GridSize(120, 30);
                // Leave the last row free: writing the bottom-right cell can scroll some terminals.
                return GridCalculator.FitWithin(frame.Width, frame.Height, terminal.Columns, Math.Max(2, terminal.Rows - 1), settings.CharacterAspect);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        lock (_latestGate)
        {
            _latest?.Dispose();
            _latest = null;
        }

        _switchLock.Dispose();
    }

    /// <summary>Tags frames with the session they belong to and drops late arrivals from old sessions.</summary>
    private sealed class GenerationSink : IFrameSink
    {
        private readonly FramePipeline _owner;
        private readonly long _generation;
        private readonly LatestItemMailbox<VideoFrame> _mailbox;

        public GenerationSink(FramePipeline owner, long generation, LatestItemMailbox<VideoFrame> mailbox)
        {
            _owner = owner;
            _generation = generation;
            _mailbox = mailbox;
        }

        public void OnFrame(VideoFrame frame)
        {
            if (Interlocked.Read(ref _owner._generation) != _generation)
            {
                frame.Dispose();
                return;
            }

            Interlocked.Exchange(ref _owner._lastSourceFrameTicks, Stopwatch.GetTimestamp());
            _owner._metrics.CaptureRate.Tick();

            // Live sources keep running while paused (instant resume); their frames are discarded.
            if (_owner._paused && _owner._source is { IsLive: true })
            {
                frame.Dispose();
                return;
            }

            _mailbox.Post(frame);
        }
    }
}
